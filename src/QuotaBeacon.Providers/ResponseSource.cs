namespace QuotaBeacon.Providers;

/// <summary>A response reduced to what mapping needs.</summary>
public sealed record FetchResult(int Status, string? Body);

/// <summary>
/// Performs the usage request.
/// </summary>
/// <remarks>
/// <para>
/// The default is an ordinary HTTP client, but claude.ai rejects requests that do not come from a
/// browser: it answers 403 to a plain client even with a valid session cookie. Disguising the client
/// as a browser would mean defeating a protection the site deliberately enabled, so instead the
/// request can be issued <em>by</em> the browser the user already signed in to — a genuine
/// first-party request from a real engine with the real session, which is what that protection is
/// there to require.
/// </para>
/// <para>
/// This is the seam that lets a provider choose between the two without either knowing about the
/// other.
/// </para>
/// </remarks>
public interface IResponseSource
{
    Task<FetchResult> GetAsync(Uri uri, AuthCredential credential, CancellationToken cancellationToken);
}
