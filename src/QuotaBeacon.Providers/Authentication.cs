using QuotaBeacon.Core;
using System.Runtime.CompilerServices;

namespace QuotaBeacon.Providers;

/// <summary>Where a credential came from, shown in settings so the user can tell.</summary>
public enum AuthSourceKind
{
    Cli,
    Web,
}

/// <summary>
/// Everything needed to authenticate one request, reduced to headers.
/// </summary>
/// <remarks>
/// Reducing every source to headers keeps the providers indifferent to whether the user signed in
/// through a CLI or through the embedded browser. <see cref="ToString"/> is overridden so an
/// accidental interpolation into a log line cannot print token material.
/// </remarks>
public sealed record AuthCredential(
    AuthSourceKind Kind,
    IReadOnlyDictionary<string, string> Headers)
{
    public override string ToString() => $"AuthCredential({Kind}, {Headers.Count} headers)";
}

/// <summary>
/// One way of authenticating to a provider. Returning <c>null</c> means "not available", which is
/// how the chain knows to fall through to the next source.
/// </summary>
public interface IAuthSource
{
    AuthSourceKind Kind { get; }

    /// <summary>
    /// Produces a credential, or <c>null</c> when this source cannot supply one.
    /// </summary>
    /// <exception cref="AuthExpiredException">
    /// When a credential exists but is known to be expired. This is distinct from unavailable: the
    /// user has something that needs renewing, and the UI should say so.
    /// </exception>
    Task<AuthCredential?> TryAcquireAsync(CancellationToken cancellationToken);
}

/// <summary>Signals a present but expired credential, so the UI can offer a renewal path.</summary>
public sealed class AuthExpiredException(string message) : Exception(message);

/// <summary>
/// Walks auth sources in order and returns the first credential available.
/// </summary>
/// <remarks>
/// An expired credential in an earlier source does not abort the chain; a user whose CLI token
/// lapsed but who signed in through the app should still get values. The expiry is remembered and
/// only surfaces if no later source works.
/// </remarks>
public sealed class AuthChain(IReadOnlyList<IAuthSource> sources)
{
    public async Task<AuthCredential> AcquireAsync(CancellationToken cancellationToken)
    {
        await foreach (var credential in AcquireAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return credential;
        }

        throw new InvalidOperationException("The authentication chain completed without a result.");
    }

    /// <summary>
    /// Lazily yields credentials in preference order. Later sources are acquired only after the
    /// caller has tried and rejected an earlier credential.
    /// </summary>
    public async IAsyncEnumerable<AuthCredential> AcquireAvailableAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? expiredMessage = null;
        var yielded = false;

        foreach (var source in sources)
        {
            AuthCredential? credential = null;

            try
            {
                credential = await source.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AuthExpiredException expired)
            {
                expiredMessage ??= expired.Message;
            }

            if (credential is not null)
            {
                yielded = true;
                Log.Info("auth", $"{source.Kind} source supplied a credential ({credential.Headers.Count} headers: {string.Join(", ", credential.Headers.Keys)})");
                yield return credential;
            }
            else
            {
                Log.Debug("auth", $"{source.Kind} source has nothing available");
            }
        }

        if (!yielded)
        {
            Log.Warning("auth", expiredMessage is null
                ? "no source could authenticate"
                : "no source could authenticate; an earlier one reported an expired credential");

            throw expiredMessage is null
                ? new AuthUnavailableException(ProviderErrorKind.AuthenticationMissing)
                : new AuthUnavailableException(ProviderErrorKind.AuthenticationExpired, expiredMessage);
        }
    }
}

/// <summary>Raised when no source in the chain could authenticate.</summary>
public sealed class AuthUnavailableException(ProviderErrorKind kind, string? detail = null)
    : Exception(detail ?? "No authentication source is available.")
{
    public ProviderErrorKind Kind { get; } = kind;
}
