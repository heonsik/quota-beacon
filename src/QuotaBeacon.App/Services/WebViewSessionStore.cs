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
        Window? window = null;

        try
        {
            window = new Window
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
        }
        catch
        {
            // Initialization commonly fails because another QuotaBeacon instance already holds this
            // user-data folder — WebView2 profiles are single-process. Whatever the reason, the
            // half-built control must be destroyed here rather than abandoned to the garbage
            // collector; see Discard for why that distinction is fatal.
            Discard(view, window);
            throw;
        }

        _hosts[provider] = view;
        _windows[provider] = window;

        return view;
    }

    /// <summary>
    /// Destroys a host without letting its finalizer run.
    /// </summary>
    /// <remarks>
    /// <see cref="WebView2"/> dereferences state during teardown that only exists once initialization
    /// has completed. A control that failed to initialize therefore throws
    /// <see cref="NullReferenceException"/> from its own disposal — and if that disposal happens on the
    /// finalizer thread, the exception is unhandled and takes the whole process down. Suppressing
    /// finalization is the only way to guarantee that never happens, so it runs even when the explicit
    /// disposal below succeeds.
    /// </remarks>
    private static void Discard(WebView2 view, Window? window)
    {
        try
        {
            view.Dispose();
        }
        catch (Exception)
        {
            // An uninitialized control throws here. There is nothing to recover — the point of this
            // call is simply to release a control that *was* initialized.
        }
        finally
        {
            GC.SuppressFinalize(view);
        }

        if (window is null)
        {
            return;
        }

        // Detach first so closing the window cannot walk back into the dead control.
        window.Content = null;
        window.Close();
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
        _windows.Remove(provider, out var window);

        if (_hosts.Remove(provider, out var host))
        {
            Discard(host, window);
        }
        else
        {
            window?.Close();
        }

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
        // The control owns the CoreWebView2 and the browser processes behind it, so closing the window
        // alone does not release them.
        foreach (var (provider, host) in _hosts)
        {
            Discard(host, _windows.GetValueOrDefault(provider));
        }

        foreach (var (provider, window) in _windows)
        {
            if (!_hosts.ContainsKey(provider))
            {
                window.Close();
            }
        }

        _hosts.Clear();
        _windows.Clear();
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
