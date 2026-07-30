using System.Text.Json;

namespace QuotaBeacon.Providers;

/// <summary>
/// Locates a vendor CLI's credential file.
/// </summary>
/// <remarks>
/// Split out so tests can point at a fixture directory without touching the real user profile.
/// </remarks>
public interface ICredentialFileLocator
{
    /// <summary>The credential file path, or <c>null</c> when the CLI is not installed.</summary>
    string? Locate();
}

public sealed class EnvironmentCredentialFileLocator(
    string environmentVariable,
    string defaultDirectoryName,
    string fileName) : ICredentialFileLocator
{
    public string? Locate()
    {
        var overridden = Environment.GetEnvironmentVariable(environmentVariable);

        var directory = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                defaultDirectoryName)
            : overridden;

        var path = Path.Combine(directory, fileName);

        return File.Exists(path) ? path : null;
    }
}

/// <summary>
/// Reads a CLI credential file, read-only, and never writes or refreshes it.
/// </summary>
/// <remarks>
/// The file holds a refresh token shared with the vendor CLI. Refreshing it from here would race
/// the CLI and could invalidate the user's session, so an expired token is reported rather than
/// renewed. Opening with <see cref="FileShare.ReadWrite"/> means a CLI writing the file
/// concurrently does not make the read fail.
/// </remarks>
public abstract class CliAuthSource(ICredentialFileLocator locator) : IAuthSource
{
    public AuthSourceKind Kind => AuthSourceKind.Cli;

    public async Task<AuthCredential?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (locator.Locate() is not { } path)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable or malformed file is indistinguishable from "not signed in" from the
            // user's point of view, so fall through to the next source rather than failing loudly.
            return null;
        }

        using (document)
        {
            return Extract(document.RootElement);
        }
    }

    /// <summary>Projects the parsed credential file into headers, or <c>null</c> if unusable.</summary>
    protected abstract AuthCredential? Extract(JsonElement root);

    /// <summary>
    /// Throws when a Unix-milliseconds expiry has passed, so the chain reports a renewable state
    /// instead of silently showing nothing.
    /// </summary>
    protected static void ThrowIfExpired(long? expiresAtUnixMilliseconds, string renewalHint)
    {
        if (expiresAtUnixMilliseconds is not { } expiry)
        {
            return;
        }

        if (DateTimeOffset.FromUnixTimeMilliseconds(expiry) <= DateTimeOffset.UtcNow)
        {
            throw new AuthExpiredException(renewalHint);
        }
    }
}

/// <summary>Reads <c>.claude/.credentials.json</c> as written by Claude Code.</summary>
public sealed class ClaudeCliAuthSource(ICredentialFileLocator locator) : CliAuthSource(locator)
{
    public static ClaudeCliAuthSource Default() => new(
        new EnvironmentCredentialFileLocator("CLAUDE_CONFIG_DIR", ".claude", ".credentials.json"));

    protected override AuthCredential? Extract(JsonElement root)
    {
        if (!root.TryGetProperty("claudeAiOauth", out var oauth))
        {
            return null;
        }

        if (JsonReading.String(oauth, "accessToken") is not { } accessToken)
        {
            return null;
        }

        ThrowIfExpired(
            JsonReading.Int64(oauth, "expiresAt"),
            "Claude sign-in expired. Run any Claude Code command to refresh it, or sign in from settings.");

        return new AuthCredential(
            AuthSourceKind.Cli,
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken}",
            });
    }
}

/// <summary>Reads <c>.codex/auth.json</c> as written by the Codex CLI.</summary>
public sealed class CodexCliAuthSource(ICredentialFileLocator locator) : CliAuthSource(locator)
{
    public static CodexCliAuthSource Default() => new(
        new EnvironmentCredentialFileLocator("CODEX_HOME", ".codex", "auth.json"));

    protected override AuthCredential? Extract(JsonElement root)
    {
        // Newer CLI versions nest under "tokens"; older ones are flat. Accept both.
        var tokens = root.TryGetProperty("tokens", out var nested) ? nested : root;

        if (JsonReading.String(tokens, "access_token") is not { } accessToken)
        {
            return null;
        }

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {accessToken}",
        };

        // The usage endpoint scopes to a workspace, so pass the account id when the CLI recorded it.
        if (JsonReading.String(tokens, "account_id") is { } accountId)
        {
            headers["ChatGPT-Account-Id"] = accountId;
        }

        return new AuthCredential(AuthSourceKind.Cli, headers);
    }
}
