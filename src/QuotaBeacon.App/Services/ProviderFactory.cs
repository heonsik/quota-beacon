using QuotaBeacon.Core;
using QuotaBeacon.Providers;

namespace QuotaBeacon.App.Services;

internal static class ProviderFactory
{
    public static IReadOnlyList<IQuotaProvider> Create(
        AppSettings settings,
        HttpClient httpClient,
        IWebSessionStore sessions)
    {
        var providers = new List<IQuotaProvider>();

        if (settings.ClaudeEnabled)
        {
            providers.Add(new ClaudeProvider(
                httpClient,
                new AuthChain(
                [
                    ClaudeCliAuthSource.Default(),
                    new WebCookieAuthSource(sessions, new Uri("https://claude.ai")),
                ])));
        }

        if (settings.CodexEnabled)
        {
            providers.Add(new CodexProvider(
                httpClient,
                new AuthChain(
                [
                    CodexCliAuthSource.Default(),
                    new WebBearerExchangeAuthSource(
                        sessions,
                        httpClient,
                        new Uri("https://chatgpt.com"),
                        new Uri("https://chatgpt.com/api/auth/session")),
                ])));
        }

        return providers;
    }
}
