using System.Windows;
using Microsoft.Web.WebView2.Core;
using QuotaBeacon.App.Services;
using QuotaBeacon.Core;
using Loc = QuotaBeacon.App.Theming.Localization;

namespace QuotaBeacon.App.Views;

/// <summary>
/// Hosts a provider's own sign-in page in a window QuotaBeacon owns.
/// </summary>
/// <remarks>
/// <para>
/// This is the supported alternative to reading a system browser's cookies. The user completes the
/// provider's real sign-in, including SSO and MFA, in a real browser engine; conditional access
/// applies exactly as it would in Edge. QuotaBeacon never sees a password, and the resulting session
/// stays inside its own WebView2 profile.
/// </para>
/// <para>
/// Navigation is confined to the provider's own domains. Following an arbitrary link out of a sign-in
/// flow inside an app-owned window is how a convincing credential-phishing surface gets built, so
/// off-domain navigation is cancelled and handed to the user's real browser instead.
/// </para>
/// </remarks>
public partial class WebSignInWindow : Window
{
    private readonly ProviderId _provider;
    private readonly Uri _signInUri;
    private readonly string[] _providerHostSuffixes;
    private readonly string _validationScript;

    public WebSignInWindow(ProviderId provider, string displayName)
    {
        _provider = provider;

        InitializeComponent();

        (_signInUri, _providerHostSuffixes, _validationScript) = provider switch
        {
            ProviderId.Claude => (
                new Uri("https://claude.ai/login"),
                new[] { "claude.ai", "anthropic.com" },
                "fetch('/api/usage',{credentials:'include'}).then(r=>r.ok).catch(()=>false)"),
            _ => (
                new Uri("https://chatgpt.com/auth/login"),
                new[] { "chatgpt.com", "openai.com" },
                "fetch('/api/auth/session',{credentials:'include'}).then(async r=>r.ok&&Boolean((await r.json()).accessToken)).catch(()=>false)"),
        };

        Title = Loc.Current.Format("SignIn.Title", displayName);
        Heading.Text = Title;
        Explanation.Text = Loc.Current.Format("SignIn.Explanation", _signInUri.Host);

        Loaded += OnLoaded;
    }

    /// <summary>True once the provider's site set cookies, meaning a session now exists.</summary>
    public bool SignedIn { get; private set; }

    /// <summary>
    /// Starts the embedded browser.
    /// </summary>
    /// <remarks>
    /// An <c>async void</c> handler that throws takes the process down with it, so everything here is
    /// guarded. The realistic failure is a locked profile: WebView2 user-data folders are
    /// single-process, so a second copy of QuotaBeacon makes this fail, and the user needs to be told
    /// that rather than watching a blank window.
    /// </remarks>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: WebViewSessionStore.ProfileDirectory(_provider));

            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            // A popped-out window would escape the domain restriction above, so new windows are
            // refused and reopened in the user's real browser where the address bar is visible.
            Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            Browser.CoreWebView2.Navigate(_signInUri.ToString());
        }
        catch (Exception exception)
        {
            Explanation.Text = Loc.Current["SignIn.Failed"];
            CurrentHost.Text = exception.Message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // WPF does not dispose a XAML-declared control when its window closes, so without this the
        // browser is left to the finalizer — fatal if it never finished initializing.
        Services.WebViewLifetime.Discard(Browser);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = uri.Scheme != Uri.UriSchemeHttps;

        if (!e.Cancel)
        {
            CurrentHost.Text = uri.Host;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            e.Handled = true;
            Browser.CoreWebView2.Navigate(uri.ToString());
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            return;
        }

        try
        {
            if (Browser.Source is not { } source || !IsProviderHost(source))
            {
                return;
            }

            var result = await Browser.CoreWebView2.ExecuteScriptAsync(_validationScript);
            SignedIn = string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);

            if (SignedIn)
            {
                DialogResult = true;
            }
        }
        catch (Exception)
        {
            // Closing or navigating away mid-check tears the browser down underneath us. This is
            // another async void: it must not throw, and a failed check just means "not yet".
        }
    }

    private bool IsProviderHost(Uri uri) =>
        _providerHostSuffixes.Any(suffix =>
            uri.Host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase));
}
