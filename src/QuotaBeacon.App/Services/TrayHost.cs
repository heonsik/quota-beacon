using System.Windows;
using QuotaBeacon.App.Theming;
using Loc = QuotaBeacon.App.Theming.Localization;
using QuotaBeacon.Core;
using Forms = System.Windows.Forms;

namespace QuotaBeacon.App.Services;

/// <summary>
/// Owns the notification-area icon and its menu.
/// </summary>
/// <remarks>
/// This is the only place WinForms appears: WPF has no tray primitive, and adding a dependency for one
/// would cost more than the interop does. The icon is redrawn from the resolved tray state, so the
/// icon and the card always agree about which meter matters.
/// </remarks>
public sealed class TrayHost : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly IconRenderer _renderer = new();
    private readonly Theme _theme;

    private TrayState _state = new(AlertLevel.None, null, IsStale: false, IsUnavailable: true);

    public TrayHost(Theme theme)
    {
        _theme = theme;

        _icon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "QuotaBeacon",
            ContextMenuStrip = BuildMenu(),
        };

        _icon.MouseClick += OnMouseClick;
        _theme.Changed += (_, _) => Redraw();

        Redraw();
    }

    public event EventHandler? ToggleRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public void Update(TrayState state, IReadOnlyList<ProviderState> providers, AlertSettings settings)
    {
        _state = state;
        Redraw();
        _icon.Text = BuildTooltip(state, providers, settings);
    }

    /// <summary>
    /// Shows a threshold crossing.
    /// </summary>
    /// <remarks>
    /// Balloon tips rather than toast notifications: a toast needs a registered AppUserModelID and a
    /// packaged identity, which a portable executable does not have. The trade-off is a plainer
    /// notification in exchange for the app running with no install.
    /// </remarks>
    public void Notify(Alert alert)
    {
        var remaining = alert.Meter.Remaining is { } fraction
            ? Loc.Current.Format("Tray.Left", $"{fraction * 100:0}%")
            : alert.Meter.Amount?.ToString() ?? string.Empty;

        _icon.ShowBalloonTip(
            5000,
            Loc.Current[alert.Level == AlertLevel.Critical ? "Alert.CriticalTitle" : "Alert.LowTitle"],
            $"{alert.Provider} · {alert.Meter.Label} · {remaining}",
            alert.Level == AlertLevel.Critical ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning);
    }

    /// <summary>
    /// Rebuilds the menu after a language change.
    /// </summary>
    /// <remarks>
    /// The context menu is WinForms, so it cannot bind to the localization indexer the way the WPF
    /// card does. Rebuilding it is the equivalent, and it happens rarely enough to be free.
    /// </remarks>
    public void RefreshLanguage() => _icon.ContextMenuStrip = BuildMenu();

    public void Dispose()
    {
        var icon = _icon.Icon;
        _icon.Visible = false;
        _icon.Icon = null;
        _icon.Dispose();
        icon?.Dispose();
        _renderer.Dispose();
    }

    private void Redraw()
    {
        // The shell asks for a small icon whose pixel size follows the primary display's scaling.
        var size = (int)Math.Round(16 * (SystemParameters.PrimaryScreenHeight > 0
            ? VisualTreeHelperScale
            : 1d));

        var previous = _icon.Icon;
        _icon.Icon = _renderer.Render(_state, _theme, size);
        previous?.Dispose();
    }

    /// <summary>
    /// The primary display's scale factor, used to pick the icon's pixel size.
    /// </summary>
    private static double VisualTreeHelperScale =>
        Application.Current?.MainWindow is { } window
        && PresentationSource.FromVisual(window) is { } source
            ? source.CompositionTarget.TransformToDevice.M11
            : 1d;

    private static string BuildTooltip(
        TrayState state,
        IReadOnlyList<ProviderState> providers,
        AlertSettings settings)
    {
        var lines = new List<string>();

        if (state.IsUnavailable)
        {
            lines.Add(Loc.Current["Tray.NotReporting"]);
        }
        else if (state.Representative is { } representative)
        {
            var remaining = representative.Remaining is { } fraction
                ? Loc.Current.Format("Tray.Left", $"{fraction * 100:0}%")
                : representative.Amount?.ToString() ?? string.Empty;

            var stale = state.IsStale ? Loc.Current["Tray.Stale"] : string.Empty;

            lines.Add($"{representative.Label} · {remaining}{stale}");
        }
        else
        {
            lines.Add("QuotaBeacon");
        }

        foreach (var provider in providers.Where(p => p.HasValues))
        {
            var headline = provider.Meters
                .OrderByDescending(settings.LevelOf)
                .ThenBy(meter => meter.Remaining ?? double.PositiveInfinity)
                .First();

            var value = headline.Remaining is { } fraction
                ? $"{fraction * 100:0}%"
                : headline.Amount?.ToString() ?? "—";

            lines.Add($"{provider.Provider}: {value}");
        }

        // The shell truncates a tooltip at 127 characters, so long content is trimmed deliberately
        // rather than letting Windows cut a line mid-number.
        var text = string.Join(Environment.NewLine, lines);

        return text.Length <= 127 ? text : text[..127];
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add(Loc.Current["Tray.Open"], null, (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(Loc.Current["Tray.RefreshNow"], null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.Current["Tray.Settings"], null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(Loc.Current["Tray.Quit"], null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        return menu;
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
