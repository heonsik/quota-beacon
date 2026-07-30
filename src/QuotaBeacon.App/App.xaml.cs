using System.Windows;
using QuotaBeacon.App.Services;
using QuotaBeacon.App.Theming;
using QuotaBeacon.App.Views;

namespace QuotaBeacon.App;

public partial class App : System.Windows.Application
{
    private AppCoordinator? _coordinator;
    private Theme? _previewTheme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = PreviewLaunchOptions.Parse(e.Args);

        if (options.IsPreview)
        {
            StartPreview(options.Scenario);
            return;
        }

        _coordinator = new AppCoordinator(this, AppSettings.Load());
        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _coordinator?.Dispose();
        _previewTheme?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Routes WPF binding failures to a file.
    /// </summary>
    /// <remarks>
    /// Binding errors are silent at runtime: a mistyped path renders as a blank element rather than an
    /// exception. Preview mode exists to review the card visually, so it is exactly where a silent
    /// blank must be made loud.
    /// </remarks>
    private static void TraceBindingFailures()
    {
        var path = Path.Combine(Path.GetTempPath(), "quotabeacon-binding.log");
        System.IO.File.Delete(path);

        var listener = new System.Diagnostics.TextWriterTraceListener(path)
        {
            TraceOutputOptions = System.Diagnostics.TraceOptions.None,
        };

        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Warning;
        System.Diagnostics.PresentationTraceSources.ResourceDictionarySource.Listeners.Add(listener);
        System.Diagnostics.PresentationTraceSources.ResourceDictionarySource.Switch.Level =
            System.Diagnostics.SourceLevels.Warning;

        // Trace.AutoFlush does not apply to TraceSource listeners, and preview runs are terminated
        // rather than exited cleanly, so the listener is flushed on a timer instead.
        var flush = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };

        flush.Tick += (_, _) => listener.Flush();
        flush.Start();
    }

    private void StartPreview(PreviewScenario scenario)
    {
        TraceBindingFailures();

        Theming.Localization.Current.SetLanguage(AppSettings.Load().Language);

        _previewTheme = new Theme();
        ThemeResources.Apply(_previewTheme, Resources, micaApplied: false);

        if (scenario == PreviewScenario.Settings)
        {
            var settings = new SettingsWindow(AppSettings.Load(), new Services.WebViewSessionStore());
            MainWindow = settings;
            settings.Show();
            return;
        }

        var now = DateTimeOffset.Now;
        var popup = new PopupWindow(_previewTheme)
        {
            KeepOpenOnDeactivate = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        MainWindow = popup;
        popup.Update(SampleData.Build(scenario, now), SampleData.TrayFor(scenario, now).Level);
        popup.Show();
    }
}
