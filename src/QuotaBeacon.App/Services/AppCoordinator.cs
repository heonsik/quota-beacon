using System.Windows;
using QuotaBeacon.App.Theming;
using QuotaBeacon.App.ViewModels;
using QuotaBeacon.App.Views;
using QuotaBeacon.Core;
using System.Diagnostics;
using Loc = QuotaBeacon.App.Theming.Localization;

namespace QuotaBeacon.App.Services;

internal sealed class AppCoordinator : IDisposable
{
    private readonly System.Windows.Application _application;
    private AppSettings _settings;
    private readonly Theme _theme;
    private readonly HttpClient _httpClient;
    private readonly WebViewSessionStore _sessions;
    private readonly PopupWindow _popup;
    private readonly TrayHost _tray;
    private readonly RefreshScheduler _scheduler;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public AppCoordinator(System.Windows.Application application, AppSettings settings)
    {
        _application = application;
        _settings = settings;

        // Applied before any window is constructed so the first render is already in the right
        // language and nothing has to be rebuilt.
        Loc.Current.SetLanguage(settings.Language);

        _theme = new Theme();
        ThemeResources.Apply(_theme, application.Resources, micaApplied: false);

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("QuotaBeacon/0.1");

        _sessions = new WebViewSessionStore();
        var providers = ProviderFactory.Create(settings, _httpClient, _sessions);
        var alerts = new AlertEngine(settings.ToAlertSettings());

        _popup = new PopupWindow(_theme);
        _tray = new TrayHost(_theme);
        _scheduler = new RefreshScheduler(providers, alerts, settings.RefreshInterval);

        _application.MainWindow = _popup;
        WireEvents();
    }

    public void Start()
    {
        // Restored after construction so the window can lay out and report a real size, which the
        // off-screen check needs before it can judge a remembered position.
        _popup.RestoreMode(
            _settings.Pinned,
            _settings.PinnedAlwaysOnTop,
            _settings.PinnedLeft,
            _settings.PinnedTop);

        _ = RunSchedulerAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _scheduler.Dispose();
        _tray.Dispose();
        _popup.Close();
        _sessions.Dispose();
        _httpClient.Dispose();
        _theme.Dispose();
        _shutdown.Dispose();
    }

    private void WireEvents()
    {
        _scheduler.Refreshed += OnRefreshed;
        _tray.ToggleRequested += (_, _) => _popup.ToggleNearTray();
        _tray.RefreshRequested += (_, _) => _ = RefreshNowAsync();
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.ExitRequested += (_, _) => _application.Shutdown();
        _popup.RefreshRequested += (_, _) => _ = RefreshNowAsync();
        _popup.SettingsRequested += (_, _) => OpenSettings();
        _popup.SignInRequested += (_, provider) => SignIn(provider);
        _popup.PlacementChanged += (_, placement) => PersistPlacement(placement);
    }

    private async Task RunSchedulerAsync()
    {
        try
        {
            await _scheduler.RunAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _application.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    Loc.Current.Format("Dialog.MonitoringStopped", exception.Message),
                    "QuotaBeacon",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error));
        }
    }

    private async Task RefreshNowAsync()
    {
        try
        {
            await _scheduler.RefreshAsync(force: true, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void OnRefreshed(object? sender, RefreshResult result)
    {
        _ = _application.Dispatcher.InvokeAsync(() =>
        {
            var now = DateTimeOffset.Now;
            var alertSettings = _settings.ToAlertSettings();

            var viewModel = new PopupViewModel(
            [
                .. result.States.Select(state =>
                    new ProviderViewModel(state.Provider.ToString(), state, alertSettings, now)),
            ],
                now);

            _popup.Update(viewModel, result.TrayState.Level);
            _tray.Update(result.TrayState, result.States, alertSettings);

            foreach (var alert in result.Alerts)
            {
                _tray.Notify(alert);
            }
        });
    }

    private void SignIn(string displayName)
    {
        var provider = displayName.Equals("Claude", StringComparison.OrdinalIgnoreCase)
            ? ProviderId.Claude
            : ProviderId.Codex;

        var window = new WebSignInWindow(provider, displayName)
        {
            Owner = _popup.IsVisible ? _popup : null,
        };

        window.ShowDialog();

        if (window.SignedIn)
        {
            _ = RefreshNowAsync();
        }
    }

    /// <summary>Saves the pinned flag and position without disturbing anything else.</summary>
    private void PersistPlacement(PinnedPlacement placement)
    {
        _settings = _settings with
        {
            Pinned = placement.Pinned,
            PinnedLeft = placement.Left,
            PinnedTop = placement.Top,
        };

        _settings.Save();
    }

    private void OpenSettings()
    {
        var previous = _settings;

        var window = new SettingsWindow(_settings, _sessions)
        {
            Owner = _popup.IsVisible ? _popup : null,
        };

        if (window.ShowDialog() != true || window.Result is not { } updated)
        {
            return;
        }

        _settings = updated;
        updated.Save();

        // The WinForms tray menu cannot bind to the localization indexer, so it is rebuilt by hand.
        _tray.RefreshLanguage();

        if (!StartupRegistration.TrySetEnabled(updated.RunAtStartup))
        {
            MessageBox.Show(
                Loc.Current["Dialog.StartupFailed"],
                "QuotaBeacon",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // Only the provider set and the polling interval are baked into the scheduler. Thresholds,
        // language, and window behaviour take effect on the next refresh, so asking for a restart
        // when only those changed would be a prompt the user cannot act on usefully.
        var needsRestart =
            updated.ClaudeEnabled != previous.ClaudeEnabled
            || updated.CodexEnabled != previous.CodexEnabled
            || updated.RefreshMinutes != previous.RefreshMinutes;

        if (!needsRestart)
        {
            _ = RefreshNowAsync();
            return;
        }

        var restart = MessageBox.Show(
            Loc.Current["Dialog.RestartPrompt"],
            "QuotaBeacon",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (restart == MessageBoxResult.Yes && Environment.ProcessPath is { } executable)
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            _application.Shutdown();
        }
    }
}
