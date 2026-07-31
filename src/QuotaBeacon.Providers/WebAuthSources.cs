using System.Text.Json;

namespace QuotaBeacon.Providers;

/// <summary>
/// Supplies cookies from QuotaBeacon's own embedded browser profile.
/// </summary>
/// <remarks>
/// Implemented in the app layer over WebView2 so this assembly stays free of a UI dependency. The
/// contract is deliberately narrow: it can only read cookies from a profile QuotaBeacon owns and
/// the user signed into. Reading an installed browser's cookie store is prohibited — that would
/// mean handling credentials the user never presented to this application.
/// </remarks>
public interface IWebSessionStore
{
    /// <summary>
    /// The <c>Cookie</c> header value for <paramref name="uri"/>, or <c>null</c> when the user has
    /// not signed in to that site inside QuotaBeacon.
    /// </summary>
    Task<string?> GetCookieHeaderAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a GET from inside the signed-in browser and returns the status and body.
    /// </summary>
    /// <remarks>
    /// Needed because some sites answer 403 to anything that is not a browser. Running the request in
    /// the browser satisfies that honestly, rather than by pretending to be one.
    /// </remarks>
    Task<FetchResult> GetFromBrowserAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>
/// Authenticates with the cookies of an embedded sign-in, for APIs that accept cookie auth.
/// </summary>
public sealed class WebCookieAuthSource(IWebSessionStore sessionStore, Uri siteUri) : IAuthSource
{
    public AuthSourceKind Kind => AuthSourceKind.Web;

    public async Task<AuthCredential?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var cookieHeader = await sessionStore
            .GetCookieHeaderAsync(siteUri, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        return new AuthCredential(
            AuthSourceKind.Web,
            new Dictionary<string, string> { ["Cookie"] = cookieHeader });
    }
}

/// <summary>
/// Exchanges embedded-sign-in cookies for a bearer token, for APIs that require one.
/// </summary>
/// <remarks>
/// ChatGPT's backend expects a bearer token rather than raw cookies, and the browser obtains one
/// from a session endpoint. Mirroring that exchange keeps the web path working without the app ever
/// asking the user for a password or reading a foreign cookie store.
/// </remarks>
public sealed class WebBearerExchangeAuthSource(
    IWebSessionStore sessionStore,
    HttpClient httpClient,
    Uri siteUri,
    Uri sessionEndpoint,
    string tokenPropertyName = "accessToken") : IAuthSource
{
    public AuthSourceKind Kind => AuthSourceKind.Web;

    public async Task<AuthCredential?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var cookieHeader = await sessionStore
            .GetCookieHeaderAsync(siteUri, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sessionEndpoint);
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            using var response = await httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A signed-out or lapsed session is "not available" rather than an error: the chain
                // should keep looking, and the UI already knows how to offer a fresh sign-in.
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (JsonReading.String(document.RootElement, tokenPropertyName) is not { Length: > 0 } token)
            {
                return null;
            }

            return new AuthCredential(
                AuthSourceKind.Web,
                new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }
}
