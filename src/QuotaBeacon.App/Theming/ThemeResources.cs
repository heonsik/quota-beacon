using System.Windows;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace QuotaBeacon.App.Theming;

/// <summary>
/// Publishes the palette as application resources so XAML can bind with <c>DynamicResource</c>.
/// </summary>
/// <remarks>
/// Going through the resource dictionary rather than view-model properties means a theme or accent
/// change repaints every open window without rebuilding a single view model, and no control needs to
/// know that themes exist.
/// </remarks>
internal static class ThemeResources
{
    public const string Surface = "Brush.Surface";
    public const string Elevated = "Brush.Elevated";
    public const string CardBorder = "Brush.CardBorder";
    public const string Hairline = "Brush.Hairline";
    public const string Track = "Brush.Track";
    public const string Hover = "Brush.Hover";
    public const string Foreground = "Brush.Foreground";
    public const string Muted = "Brush.Muted";
    public const string Subtle = "Brush.Subtle";
    public const string FillNormal = "Brush.Fill.Normal";
    public const string FillWarning = "Brush.Fill.Warning";
    public const string FillCritical = "Brush.Fill.Critical";
    public const string TextNormal = "Brush.Text.Normal";
    public const string TextWarning = "Brush.Text.Warning";
    public const string TextCritical = "Brush.Text.Critical";

    /// <summary>
    /// Writes the palette into <paramref name="resources"/>.
    /// </summary>
    /// <param name="micaApplied">
    /// When Mica is in effect the card must stay transparent so the desktop material shows through.
    /// On Windows 10, where the backdrop is unavailable, the same slot carries a solid surface so the
    /// card is still legible.
    /// </param>
    public static void Apply(Theme theme, ResourceDictionary resources, bool micaApplied)
    {
        resources[Surface] = micaApplied
            ? Brushes.Transparent
            : Solid(theme.CardBackground);

        // The selected-tab pill sits above the surface, so it needs an opaque fill even under Mica;
        // a translucent pill would let the desktop read through and lose the sense of elevation.
        resources[Elevated] = Solid(theme.IsDark
            ? MediaColor.FromRgb(0x32, 0x32, 0x32)
            : MediaColor.FromRgb(0xFF, 0xFF, 0xFF));

        resources[CardBorder] = Solid(theme.CardBorder);
        resources[Hairline] = Solid(theme.Hairline);
        resources[Track] = Solid(theme.Track);
        resources[Hover] = Solid(theme.Hover);
        resources[Foreground] = Solid(theme.Foreground);
        resources[Muted] = Solid(theme.Muted);
        resources[Subtle] = Solid(theme.Subtle);

        // Gauge fills carry a slight gradient so a bar has depth instead of reading as a flat block.
        resources[FillNormal] = Gradient(theme.Normal);
        resources[FillWarning] = Gradient(theme.Warning);
        resources[FillCritical] = Gradient(theme.Critical);

        // Text reuses the flat severity color: a gradient on small glyphs only muddies them.
        resources[TextNormal] = Solid(theme.Normal);
        resources[TextWarning] = Solid(theme.Warning);
        resources[TextCritical] = Solid(theme.Critical);
    }

    private static SolidColorBrush Solid(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        // Frozen brushes are cheaper to render and safe to share across windows.
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(MediaColor color)
    {
        var brush = new LinearGradientBrush(
            Theme.Lighten(color),
            color,
            new Point(0, 0),
            new Point(1, 0));

        brush.Freeze();
        return brush;
    }
}
