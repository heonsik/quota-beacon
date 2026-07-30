using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using QuotaBeacon.App.Theming;
using QuotaBeacon.Core;
using MediaColor = System.Windows.Media.Color;

namespace QuotaBeacon.App.Services;

/// <summary>
/// Draws the tray icon at runtime.
/// </summary>
/// <remarks>
/// <para>
/// Drawing beats shipping bitmaps here: the icon has to express a continuous value across three
/// severities in two themes at several DPI scales, which is far more combinations than a sensible
/// number of assets, and a generated icon is always crisp.
/// </para>
/// <para>
/// The sweep is the representative meter's <em>remaining</em> fraction. When there is no denominator
/// the ring is drawn unfilled rather than full or empty, which is the icon's version of the same
/// honesty rule the card follows.
/// </para>
/// </remarks>
public sealed class IconRenderer : IDisposable
{
    public Icon Render(TrayState state, Theme theme, int size)
    {
        // A 16px logical icon is drawn at the device size the shell asks for, so nothing is resampled.
        var diameter = Math.Max(16, size);
        var thickness = Math.Max(2f, diameter / 7f);
        var inset = thickness / 2f + 1f;
        var bounds = new RectangleF(inset, inset, diameter - (inset * 2), diameter - (inset * 2));

        using var bitmap = new Bitmap(diameter, diameter);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var opacity = state.IsStale ? 0.45f : 1f;

        using var trackPen = new Pen(Blend(theme.Track, theme.IsDark, opacity * 0.9f), thickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        graphics.DrawEllipse(trackPen, bounds);

        if (state.IsUnavailable)
        {
            // Nothing is known: an empty ring plus a dot, so it reads as "not reporting" rather than
            // as a quota that has run out.
            DrawCentreDot(graphics, diameter, ToDrawing(theme.Subtle, opacity));
            return Finalize(bitmap);
        }

        if (state.RemainingFraction is not { } remaining)
        {
            // A meter with no denominator cannot be a gauge; show the accent dot instead of an arc.
            DrawCentreDot(graphics, diameter, ToDrawing(theme.Normal, opacity));
            return Finalize(bitmap);
        }

        var color = state.Level switch
        {
            AlertLevel.Critical => theme.Critical,
            AlertLevel.Warning => theme.Warning,
            _ => theme.Normal,
        };

        var sweep = (float)(Math.Clamp(remaining, 0d, 1d) * 360d);

        if (sweep > 0.5f)
        {
            using var pen = new Pen(ToDrawing(color, opacity), thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            // Start at twelve o'clock and sweep clockwise, matching how a countdown is read.
            graphics.DrawArc(pen, bounds, -90f, sweep);
        }

        return Finalize(bitmap);
    }

    public void Dispose()
    {
    }

    private static void DrawCentreDot(Graphics graphics, int diameter, Color color)
    {
        var dot = Math.Max(3f, diameter / 5f);
        var offset = (diameter - dot) / 2f;

        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, offset, offset, dot, dot);
    }

    /// <summary>
    /// Converts the drawn bitmap into an icon, retaining the handle so it can be destroyed on dispose.
    /// </summary>
    /// <remarks>
    /// <c>Icon.FromHandle</c> does not own the handle it wraps, so releasing it is the caller's job.
    /// The icon is redrawn on every refresh, which over a long-running session would otherwise leak a
    /// GDI handle per update until the process exits.
    /// </remarks>
    private Icon Finalize(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();

        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Color ToDrawing(MediaColor color, float opacity) => Color.FromArgb(
        (int)Math.Clamp(color.A * opacity, 0, 255),
        color.R,
        color.G,
        color.B);

    /// <summary>
    /// Flattens a translucent track color against the notification area's own backdrop.
    /// </summary>
    /// <remarks>
    /// The tray composites the icon over a taskbar whose color is unknown, so a low-alpha track would
    /// disappear on some backgrounds. Blending toward the theme's own extreme keeps the ring visible
    /// in both light and dark shells.
    /// </remarks>
    private static Color Blend(MediaColor color, bool isDark, float opacity)
    {
        var target = isDark ? 255 : 0;
        var weight = color.A / 255f;

        byte Mix(byte channel) =>
            (byte)Math.Clamp((channel * weight) + (target * (1 - weight)), 0, 255);

        return Color.FromArgb(
            (int)Math.Clamp(140 * opacity, 0, 255),
            Mix(color.R),
            Mix(color.G),
            Mix(color.B));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
