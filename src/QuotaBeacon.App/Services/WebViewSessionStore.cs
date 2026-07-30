using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuotaBeacon.Core;
using QuotaBeacon.Providers;
using System.Windows;

namespace QuotaBeacon.App.Services;

/// <summary>
/// Reads cookies from QuotaBeacon's own embedded browser profiles.
/// </summary>
/// <remarks>
/// <para>
/// Each provider gets an isolated WebView2 user-data folder under
/// <c>%LOCALAPPDATA%\QuotaBeacon\WebView2\&lt;provider&gt;</c>. WebView2 owns that storage, so
/// QuotaBeacon never persists a credential itself: signing out is a directory delete.
/// </para>
/// <para>
/// This deliberately cannot reach an installed browser's cookie store. The user signs in here, in a
/// window this app owns, and only what they presented to this app is readable.
/// </para>
/// </remarks>
public sealed class WebViewSessionStore : IWebSessionStore, IDisposable
{
    private readonly Dictionary<ProviderId, WebView2> _hosts = [];
    private readonly Dictionary<ProviderId, Window> _windows = [];

    public static string ProfileDirectory(ProviderId provider) => Path.Combine(
        AppSettings.Directory,
        "WebView2",
        provider.ToString().ToLowerInvariant());

    /// <summary>
    /// Creates or returns the hidden WebView2 bound to a provider's profile.
    /// </summary>
    /// <remarks>
    /// WebView2's managed API needs a hosted control to reach a profile's cookie manager, so a
    /// zero-sized off-screen window stands in for a headless environment. It is created lazily, so a
    /// user who never uses the web fallback never pays for a browser process.
    /// </remarks>
    public async Task<WebView2> GetOrCreateHostAsync(ProviderId provider)
    {
        if (_hosts.TryGetValue(provider, out var existing))
        {
            return existing;
        }

        var view = new WebView2();

        var window = new Window
        {
            Width = 0,
            Height = 0,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            Content = view,
        };

        window.Show();

        var environment = await CoreWebView2Environment
            .CreateAsync(userDataFolder: ProfileDirectory(provider))
            .ConfigureAwait(true);

        await view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        _hosts[provider] = view;
        _windows[provider] = window;

        return view;
    }

    public async Task<string?> GetCookieHeaderAsync(Uri uri, CancellationToken cancellationToken)
    {
        var provider = ProviderFor(uri);

        if (provider is null || !HasProfile(provider.Value))
        {
            // No profile means the user never signed in here. That is "unavailable", not an error, and
            // creating a browser process just to confirm emptiness would be wasteful.
            return null;
        }

        try
        {
            var view = await GetOrCreateHostAsync(provider.Value).ConfigureAwait(true);
            var cookies = await view.CoreWebView2.CookieManager
                .GetCookiesAsync(uri.GetLeftPart(UriPartial.Authority))
                .ConfigureAwait(true);

            if (cookies.Count == 0)
            {
                return null;
            }

            return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A missing WebView2 runtime or a locked profile leaves the chain to fall through to its
            // remaining sources, which is better than failing the whole refresh.
            return null;
        }
    }

    public static bool HasProfile(ProviderId provider) => Directory.Exists(ProfileDirectory(provider));

    /// <summary>Signs out by deleting the provider's profile.</summary>
    public bool TrySignOut(ProviderId provider)
    {
        if (_windows.Remove(provider, out var window))
        {
            window.Close();
        }

        _hosts.Remove(provider);

        try
        {
            // The browser process holds the profile open briefly after the window closes; a failure
            // here means the user has to retry, which is better than throwing from a settings toggle.
            if (Directory.Exists(ProfileDirectory(provider)))
            {
                Directory.Delete(ProfileDirectory(provider), recursive: true);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var window in _windows.Values)
        {
            window.Close();
        }

        _windows.Clear();
        _hosts.Clear();
    }

    private static ProviderId? ProviderFor(Uri uri) => uri.Host switch
    {
        var host when host.EndsWith("claude.ai", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase) => ProviderId.Claude,

        var host when host.EndsWith("chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase) => ProviderId.Codex,

        _ => null,
    };
}
