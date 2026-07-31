using QuotaBeacon.Providers;

namespace QuotaBeacon.App.Services;

/// <summary>Issues provider requests from inside the signed-in embedded browser.</summary>
public sealed class BrowserResponseSource(IWebSessionStore sessionStore) : IResponseSource
{
    public Task<FetchResult> GetAsync(
        Uri uri,
        AuthCredential credential,
        CancellationToken cancellationToken) =>
        // The browser carries its own session, so the credential's headers are not needed here; they
        // exist to prove a session is available at all.
        sessionStore.GetFromBrowserAsync(uri, cancellationToken);
}
