using System.Text.Json;
using QuotaBeacon.Core;

namespace QuotaBeacon.Providers;

/// <summary>
/// Works out the real usage URL when it cannot be written down in advance.
/// </summary>
/// <remarks>
/// Some usage endpoints are scoped to an account-specific identifier that only the server knows —
/// an organization id, a workspace — so the address has to be discovered per account before it can
/// be called.
/// </remarks>
public interface IEndpointResolver
{
    /// <summary>The URL to request, or <c>null</c> when it cannot be determined.</summary>
    Task<Uri?> ResolveAsync(
        HttpClient httpClient,
        AuthCredential credential,
        CancellationToken cancellationToken);
}

/// <summary>
/// Finds the caller's Claude organization and builds the usage URL from it.
/// </summary>
/// <remarks>
/// <para>
/// claude.ai scopes usage to an organization, so <c>/api/usage</c> is not addressable on its own — it
/// answers 403. The organization list is fetched once and the result reused for the rest of the
/// session, because it does not change while the app is running.
/// </para>
/// <para>
/// The first organization is taken when several are present. The API returns the caller's active
/// organization first, which matches what the user sees on the site.
/// </para>
/// </remarks>
public sealed class ClaudeOrganizationResolver(IResponseSource responseSource) : IEndpointResolver
{
    private static readonly Uri Organizations = new("https://claude.ai/api/organizations");

    private Uri? _resolved;

    public async Task<Uri?> ResolveAsync(
        HttpClient httpClient,
        AuthCredential credential,
        CancellationToken cancellationToken)
    {
        if (_resolved is not null)
        {
            return _resolved;
        }

        // Discovery goes through the same channel as the usage call itself, because claude.ai
        // refuses both alike when they do not come from the browser.
        var result = await responseSource
            .GetAsync(Organizations, credential, cancellationToken)
            .ConfigureAwait(false);

        Log.Debug("Claude", $"GET {Organizations} -> {result.Status}");

        if (result.Status is < 200 or > 299 || string.IsNullOrEmpty(result.Body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.Body);

        if (FindOrganizationId(document.RootElement) is not { } id)
        {
            Log.Warning(
                "Claude",
                $"organization list had no usable id; shape={JsonReading.DescribeShape(document.RootElement)}");

            return null;
        }

        _resolved = new Uri($"https://claude.ai/api/organizations/{id}/usage");
        Log.Info("Claude", "resolved the organization-scoped usage endpoint");

        return _resolved;
    }

    private static string? FindOrganizationId(JsonElement root)
    {
        // A single object is accepted as well as a list, in case the response shape changes.
        IEnumerable<JsonElement> candidates = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object => EnumerateSingle(root),
            _ => [],
        };

        foreach (var organization in candidates)
        {
            if (JsonReading.String(organization, "uuid") is { Length: > 0 } uuid)
            {
                return uuid;
            }

            if (JsonReading.String(organization, "id") is { Length: > 0 } id)
            {
                return id;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateSingle(JsonElement element)
    {
        yield return element;
    }
}
