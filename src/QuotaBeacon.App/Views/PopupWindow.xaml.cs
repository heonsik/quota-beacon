using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuotaBeacon.App.Controls;
using QuotaBeacon.App.Theming;
using QuotaBeacon.App.ViewModels;
using QuotaBeacon.Core;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using Forms = System.Windows.Forms;
using Loc = QuotaBeacon.App.Theming.Localization;

namespace QuotaBeacon.App.Views;

/// <summary>How the card is currently presented.</summary>
public enum DisplayMode
{
    /// <summary>Summoned from the tray: anchored, on top, and dismissed as soon as focus leaves.</summary>
    Popup,

    /// <summary>Left on screen as an ordinary window the user positions and minimizes.</summary>
    Pinned,
}

/// <summary>Where a pinned window was left, so it can be restored next run.</summary>
public sealed record PinnedPlacement(bool Pinned, double Left, double Top);

/// <summary>
/// The quota card shown from the tray icon.
/// </summary>
/// <remarks>
/// The window is created once and shown or hidden, rather than constructed per open: rebuilding it
/// would re-run Mica application and first-paint layout every time, which is exactly the latency the
/// user would feel when the card is only on screen for a couple of seconds.
/// </remarks>
public partial class PopupWindow : Window
{
    private const string AllTab = "All";

    private readonly Theme _theme;
    private readonly List<ToggleButton> _tabs = [];

    private PopupViewModel? _viewModel;
    private bool _micaApplied;
    private string _currentTab = AllTab;
    private DisplayMode _mode = DisplayMode.Popup;
    private bool _alwaysOnTopWhenPinned;
    private System.Windows.Threading.DispatcherTimer? _placementSaveTimer;

    public PopupWindow(Theme theme)
    {
        _theme = theme;

        InitializeComponent();

        _theme.Changed += OnThemeChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Raised when the user asks for a fresh reading.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the user asks to sign in to a provider that needs it.</summary>
    public event EventHandler<string>? SignInRequested;

    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the pinned flag or the window position changes and should be persisted.</summary>
    public event EventHandler<PinnedPlacement>? PlacementChanged;

    public DisplayMode Mode => _mode;

    /// <summary>
    /// When true the window stays open after losing focus. Used by the preview mode that renders the
    /// card for design review; the shipped popup always closes on deactivate.
    /// </summary>
    public bool KeepOpenOnDeactivate { get; init; }

    public void Update(PopupViewModel viewModel, AlertLevel level)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        BuildTabs(viewModel);
        ApplyBeacon(level);

        // Preserve the user's tab across refreshes when it still exists, so a background update does
        // not yank them back to All while they are reading a detail view.
        var desired = _tabs.Any(tab => (string)tab.Tag == _currentTab) ? _currentTab : viewModel.SelectedTab;
        SelectTab(desired, animate: false);
    }

    public void ToggleNearTray()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        UpdateLayout();

        // A pinned window reappears where the user put it; only the popup re-anchors to the tray.
        if (_mode == DisplayMode.Popup)
        {
            PositionNearTray();
        }

        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        ApplyTheme();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (ShouldDismissOnFocusLoss)
        {
            Hide();
        }
    }

    /// <summary>
    /// Whether losing focus should dismiss the card.
    /// </summary>
    /// <remarks>
    /// A pinned window is meant to be left on screen while the user works elsewhere, so it never
    /// auto-dismisses. Preview mode sets the same expectation for design review.
    /// </remarks>
    private bool ShouldDismissOnFocusLoss => !KeepOpenOnDeactivate && _mode == DisplayMode.Popup;

    /// <summary>
    /// Turns a minimize into a return to the tray.
    /// </summary>
    /// <remarks>
    /// The window is hidden and the state reset to normal, so no dead taskbar button is left behind
    /// and the next tray click restores the card at its pinned position rather than un-minimizing an
    /// invisible window.
    /// </remarks>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        WindowState = WindowState.Normal;
        Hide();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);

        if (_mode != DisplayMode.Pinned || !IsVisible)
        {
            return;
        }

        // A drag raises this continuously, so persistence is debounced rather than writing the
        // settings file on every mouse move.
        _placementSaveTimer ??= CreatePlacementTimer();
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreatePlacementTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RaisePlacement();
        };

        return timer;
    }

    private void RaisePlacement() =>
        PlacementChanged?.Invoke(this, new PinnedPlacement(_mode == DisplayMode.Pinned, Left, Top));

    /// <summary>
    /// Restores the saved mode at startup.
    /// </summary>
    public void RestoreMode(bool pinned, bool alwaysOnTop, double? left, double? top)
    {
        _alwaysOnTopWhenPinned = alwaysOnTop;

        if (!pinned)
        {
            ApplyMode(DisplayMode.Popup);
            return;
        }

        ApplyMode(DisplayMode.Pinned);
        Show();
        UpdateLayout();

        if (left is { } x && top is { } y && IsPlacementUsable(x, y))
        {
            Left = x;
            Top = y;
        }
        else
        {
            // The display it was on may be gone or rescaled; the tray anchor is always reachable.
            PositionNearTray();
        }
    }

    private bool IsPlacementUsable(double left, double top)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice
            ?? Matrix.Identity;

        var areas = Forms.Screen.AllScreens
            .Select(screen =>
            {
                var topLeft = transform.Transform(
                    new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
                var bottomRight = transform.Transform(
                    new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));

                return new Services.PlacementRect(
                    topLeft.X,
                    topLeft.Y,
                    bottomRight.X - topLeft.X,
                    bottomRight.Y - topLeft.Y);
            })
            .ToArray();

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 200;

        return Services.WindowPlacement.IsUsable(
            new Services.PlacementRect(left, top, width, height),
            areas);
    }

    /// <summary>
    /// Switches presentation mode.
    /// </summary>
    /// <remarks>
    /// Changing <see cref="Window.ShowInTaskbar"/> makes WPF recreate the native handle, which drops
    /// the rounded-corner and dark-frame attributes along with the window's z-order. Every mode change
    /// therefore goes through this one method so the chrome is reapplied afterwards; scattering these
    /// assignments across handlers is how that reset would start happening silently.
    /// </remarks>
    private void ApplyMode(DisplayMode mode)
    {
        _mode = mode;

        var pinned = mode == DisplayMode.Pinned;

        ShowInTaskbar = pinned;
        Topmost = pinned ? _alwaysOnTopWhenPinned : true;
        ResizeMode = pinned ? ResizeMode.CanMinimize : ResizeMode.NoResize;

        MinimizeButton.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;

        PinButton.ToolTip = Loc.Current[pinned ? "Tooltip.Unpin" : "Tooltip.Pin"];
        PinIcon.Opacity = pinned ? 1.0 : 0.55;

        if (IsLoaded)
        {
            ApplyTheme();
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        var pinning = _mode == DisplayMode.Popup;

        ApplyMode(pinning ? DisplayMode.Pinned : DisplayMode.Popup);

        if (!pinning)
        {
            PositionNearTray();
        }

        Activate();
        RaisePlacement();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// Hides the window without exiting.
    /// </summary>
    /// <remarks>
    /// Closing keeps the pinned state and leaves monitoring running. A monitor that quits when its
    /// window is closed stops reporting without telling anyone, so exit is only the tray menu's
    /// command.
    /// </remarks>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only a pinned window is movable; the popup is anchored to the tray by definition.
        if (_mode != DisplayMode.Pinned || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;

        if (ShouldDismissOnFocusLoss)
        {
            Hide();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void PositionNearTray()
    {
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));

        Left = Math.Max(topLeft.X + 8, bottomRight.X - ActualWidth - 8);
        Top = Math.Max(topLeft.Y + 8, bottomRight.Y - ActualHeight - 8);
    }

    private void ApplyTheme()
    {
        _micaApplied = Interop.WindowEffects.TryApplyNativeChrome(this, _theme.IsDark);

        ThemeResources.Apply(_theme, Resources, _micaApplied);

        // The beacon is painted directly rather than through a brush resource, so it has to be
        // recoloured explicitly when the accent changes.
        ApplyBeacon(_lastLevel);
    }

    private AlertLevel _lastLevel = AlertLevel.None;

    private void ApplyBeacon(AlertLevel level)
    {
        _lastLevel = level;

        var color = level switch
        {
            AlertLevel.Critical => _theme.Critical,
            AlertLevel.Warning => _theme.Warning,
            _ => _theme.Normal,
        };

        Beacon.Fill = new SolidColorBrush(color);
        BeaconGlow.Fill = new SolidColorBrush(color);
    }

    private void BuildTabs(PopupViewModel viewModel)
    {
        var names = new List<string>();

        if (viewModel.ShowAllTab)
        {
            names.Add(AllTab);
        }

        names.AddRange(viewModel.Providers.Select(provider => provider.DisplayName));

        // Rebuilding only when the set actually changed keeps the indicator from jumping on every
        // routine refresh.
        if (names.SequenceEqual(_tabs.Select(tab => (string)tab.Tag)))
        {
            return;
        }

        _tabs.Clear();
        TabButtons.Children.Clear();
        TabButtons.ColumnDefinitions.Clear();

        for (var index = 0; index < names.Count; index++)
        {
            TabButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tab = new ToggleButton
            {
                // Tag is the stable identity; Content is the translated label. Keeping them separate
                // means switching language cannot orphan the selected tab.
                Content = names[index] == AllTab ? Loc.Current["Tab.All"] : names[index],
                Tag = names[index],
                Style = (Style)FindResource("Tab"),
            };

            tab.Checked += OnTabChecked;
            Grid.SetColumn(tab, index);
            TabButtons.Children.Add(tab);
            _tabs.Add(tab);
        }

        // A single provider needs no tab strip at all; the card is already showing only that provider.
        TabStrip.Visibility = names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        UpdateIndicatorWidth();
    }

    private void OnTabChecked(object sender, RoutedEventArgs e) =>
        SelectTab((string)((ToggleButton)sender).Tag, animate: true);

    private void SelectTab(string name, bool animate)
    {
        if (_viewModel is null || _tabs.Count == 0)
        {
            return;
        }

        var index = _tabs.FindIndex(tab => (string)tab.Tag == name);

        if (index < 0)
        {
            index = 0;
            name = (string)_tabs[0].Tag;
        }

        _currentTab = name;
        _viewModel.SelectedTab = name;

        for (var i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].IsChecked = i == index;
        }

        MoveIndicator(index, animate);
        ShowPanel(name, animate);
    }

    private void ShowPanel(string name, bool animate)
    {
        var showAll = name == AllTab;

        AllPanel.Visibility = showAll ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = showAll ? Visibility.Collapsed : Visibility.Visible;

        if (!showAll)
        {
            DetailPanel.Content = _viewModel?.Providers.FirstOrDefault(p => p.DisplayName == name);
        }

        if (!animate || !Motion.ShouldAnimate)
        {
            return;
        }

        // A short rise plus fade reads as the content arriving, and is subtle enough not to draw the
        // eye away from the number the user opened the card to read.
        var panel = showAll ? (FrameworkElement)AllPanel : DetailPanel;
        var slide = new TranslateTransform();
        panel.RenderTransform = slide;

        panel.BeginAnimation(OpacityProperty, Motion.To(1, Motion.Transition));
        panel.Opacity = 0;
        panel.BeginAnimation(OpacityProperty, Motion.To(1, Motion.Transition));
        slide.BeginAnimation(TranslateTransform.YProperty, Animate(6, 0, Motion.Transition));
    }

    private static DoubleAnimation Animate(double from, double to, Duration duration) => new(from, to, duration)
    {
        EasingFunction = Motion.Ease,
    };

    private void OnTabStripSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateIndicatorWidth();

        var index = Math.Max(0, _tabs.FindIndex(tab => tab.IsChecked == true));
        MoveIndicator(index, animate: false);
    }

    private void UpdateIndicatorWidth()
    {
        if (_tabs.Count == 0)
        {
            TabIndicator.Width = 0;
            return;
        }

        // The strip's padding is inside its own bounds, so the track width is the content width.
        var available = TabStrip.ActualWidth - TabStrip.Padding.Left - TabStrip.Padding.Right - TabStrip.BorderThickness.Left * 2;

        TabIndicator.Width = available > 0 ? available / _tabs.Count : 0;
        TabIndicator.Height = Math.Max(
            0,
            TabStrip.ActualHeight - TabStrip.Padding.Top - TabStrip.Padding.Bottom);
    }

    private void MoveIndicator(int index, bool animate)
    {
        var target = TabIndicator.Width * index;

        if (!animate || !Motion.ShouldAnimate)
        {
            TabIndicatorOffset.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorOffset.X = target;
            return;
        }

        TabIndicatorOffset.BeginAnimation(
            TranslateTransform.XProperty,
            Motion.To(target, Motion.Transition));
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProviderViewModel provider)
        {
            SignInRequested?.Invoke(this, provider.DisplayName);
        }
    }
}
