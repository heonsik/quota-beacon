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
/// <para>
/// Every method here is called from the refresh loop, which runs on the thread pool. WPF objects
/// belong to a single STA thread with a dispatcher, so all of it is marshalled to the UI thread
/// before a control or window is touched. Creating them on the calling thread instead does not merely
/// fail — it leaves a half-built control whose finalizer throws, and an unhandled exception on the
/// finalizer thread terminates the process.
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

    public static bool HasProfile(ProviderId provider) => Directory.Exists(ProfileDirectory(provider));

    public async Task<string?> GetCookieHeaderAsync(Uri uri, CancellationToken cancellationToken)
    {
        var provider = ProviderFor(uri);

        if (provider is null || !HasProfile(provider.Value))
        {
            Log.Debug("websession", $"no profile for {uri.Host}; nothing to read");
            // No profile means the user never signed in here. That is "unavailable", not an error, and
            // starting a browser process just to confirm emptiness would be wasteful.
            return null;
        }

        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            Log.Warning("websession", "no dispatcher available; cannot touch WebView2");
            return null;
        }

        try
        {
            // InvokeAsync returns a task for the delegate; the delegate itself is async, so the outer
            // task must be unwrapped to await the actual work rather than the scheduling of it.
            return await dispatcher
                .InvokeAsync(() => ReadCookieHeaderAsync(provider.Value, uri))
                .Task
                .Unwrap()
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("websession", $"reading cookies for {uri.Host} failed: {exception.GetType().Name}: {exception.Message}");
            // A missing WebView2 runtime, a profile held by another instance, or a dispatcher that is
            // shutting down all land here. The auth chain simply moves on to its remaining sources.
            return null;
        }
    }

    public async Task<FetchResult> GetFromBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        var provider = ProviderFor(uri);

        if (provider is null || !HasProfile(provider.Value))
        {
            return new FetchResult(-1, null);
        }

        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            Log.Warning("websession", "no dispatcher available; cannot use the browser");
            return new FetchResult(-1, null);
        }

        try
        {
            return await dispatcher
                .InvokeAsync(() => FetchInBrowserAsync(provider.Value, uri, cancellationToken))
                .Task
                .Unwrap()
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Error("websession", $"browser fetch of {uri} failed: {exception.GetType().Name}: {exception.Message}");
            return new FetchResult(-1, null);
        }
    }

    /// <summary>Runs on the UI thread.</summary>
    private async Task<FetchResult> FetchInBrowserAsync(
        ProviderId provider,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var view = await GetOrCreateHostAsync(provider).ConfigureAwait(true);
        var origin = uri.GetLeftPart(UriPartial.Authority);

        // fetch is only first-party if the document itself is on that origin, and a blank host is on
        // about:blank until told otherwise.
        if (view.Source is null || !string.Equals(
                view.Source.GetLeftPart(UriPartial.Authority),
                origin,
                StringComparison.OrdinalIgnoreCase))
        {
            var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                navigated.TrySetResult();

            view.CoreWebView2.NavigationCompleted += OnCompleted;

            try
            {
                view.CoreWebView2.Navigate(origin + "/");

                using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using (timer.Token.Register(() => navigated.TrySetCanceled()))
                {
                    await navigated.Task.ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Warning("websession", $"navigating the hidden host to {origin} timed out");
                return new FetchResult(-1, null);
            }
            finally
            {
                view.CoreWebView2.NavigationCompleted -= OnCompleted;
            }
        }

        return await BrowserFetch
            .GetAsync(view, uri, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Runs on the UI thread.</summary>
    private async Task<string?> ReadCookieHeaderAsync(ProviderId provider, Uri uri)
    {
        var view = await GetOrCreateHostAsync(provider).ConfigureAwait(true);

        var cookies = await view.CoreWebView2.CookieManager
            .GetCookiesAsync(uri.GetLeftPart(UriPartial.Authority))
            .ConfigureAwait(true);

        Log.Info(
            "websession",
            $"{uri.Host}: {cookies.Count} cookies in profile ({string.Join(", ", cookies.Select(c => c.Name))})");

        return cookies.Count == 0
            ? null
            : string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    /// <summary>
    /// Creates or returns the hidden WebView2 bound to a provider's profile. Must run on the UI thread.
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

        WebView2? view = null;
        Window? window = null;

        try
        {
            // Construction is inside the guard: a constructor that throws still leaves an object
            // registered for finalization, and that is precisely the object whose finalizer is fatal.
            view = new WebView2();

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
            // user-data folder — WebView2 profiles are single-process.
            WebViewLifetime.Discard(view, window);
            throw;
        }

        _hosts[provider] = view;
        _windows[provider] = window;

        return view;
    }

    /// <summary>Signs out by deleting the provider's profile.</summary>
    public bool TrySignOut(ProviderId provider)
    {
        _windows.Remove(provider, out var window);

        if (_hosts.Remove(provider, out var host))
        {
            WebViewLifetime.Discard(host, window);
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
        foreach (var (provider, host) in _hosts)
        {
            WebViewLifetime.Discard(host, _windows.GetValueOrDefault(provider));
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
