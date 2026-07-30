using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MediaColor = System.Windows.Media.Color;

namespace QuotaBeacon.App.Theming;

public enum ThemeMode
{
    Light,
    Dark,
}

/// <summary>
/// Supplies the palette and keeps it in step with the system.
/// </summary>
/// <remarks>
/// <para>
/// Light and dark are both first-class: nothing in the popup hardcodes a background or a text color.
/// The accent is read from the user's Windows accent so a normal, healthy quota is drawn in the color
/// their desktop already uses, which is what makes the card read as part of the system rather than as
/// a visitor.
/// </para>
/// <para>
/// Both signals come from the registry and are watched rather than polled, so switching theme or
/// accent updates the open popup without a restart.
/// </para>
/// </remarks>
public sealed class Theme : DependencyObject, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    private static readonly MediaColor FallbackAccent = MediaColor.FromRgb(0x0A, 0x84, 0xFF);

    private readonly RegistryWatcher _personalizeWatcher;
    private readonly RegistryWatcher _dwmWatcher;

    public Theme()
    {
        Reload();

        _personalizeWatcher = new RegistryWatcher(RegistryHive.CurrentUser, PersonalizeKey, Reload);
        _dwmWatcher = new RegistryWatcher(RegistryHive.CurrentUser, DwmKey, Reload);
    }

    public event EventHandler? Changed;

    public ThemeMode Mode { get; private set; }

    public MediaColor Accent { get; private set; }

    public bool IsDark => Mode == ThemeMode.Dark;

    // Surfaces. The card is only a fallback: when Mica is available the window is transparent and
    // the desktop shows through, so these values matter most on Windows 10.
    public MediaColor CardBackground => IsDark ? Rgb(0x20, 0x20, 0x20) : Rgb(0xFA, 0xFA, 0xFA);

    public MediaColor CardBorder => IsDark ? Argb(0x18, 0xFF, 0xFF, 0xFF) : Argb(0x14, 0x00, 0x00, 0x00);

    public MediaColor Hairline => IsDark ? Argb(0x14, 0xFF, 0xFF, 0xFF) : Argb(0x10, 0x00, 0x00, 0x00);

    public MediaColor Track => IsDark ? Argb(0x1F, 0xFF, 0xFF, 0xFF) : Argb(0x12, 0x00, 0x00, 0x00);

    public MediaColor Hover => IsDark ? Argb(0x14, 0xFF, 0xFF, 0xFF) : Argb(0x0C, 0x00, 0x00, 0x00);

    // Text. Muted text sits at roughly 60% so labels recede without dropping below a readable
    // contrast against either surface.
    public MediaColor Foreground => IsDark ? Rgb(0xF3, 0xF3, 0xF3) : Rgb(0x1A, 0x1A, 0x1A);

    public MediaColor Muted => IsDark ? Rgb(0x9E, 0x9E, 0x9E) : Rgb(0x6A, 0x6A, 0x6A);

    public MediaColor Subtle => IsDark ? Rgb(0x77, 0x77, 0x77) : Rgb(0x8C, 0x8C, 0x8C);

    /// <summary>Severity colors. Normal borrows the system accent; warning and amber stay fixed.</summary>
    public MediaColor Normal => Accent;

    public MediaColor Warning => IsDark ? Rgb(0xF5, 0xB1, 0x4C) : Rgb(0xC9, 0x7A, 0x11);

    public MediaColor Critical => IsDark ? Rgb(0xFF, 0x6B, 0x63) : Rgb(0xD1, 0x33, 0x2E);

    /// <summary>
    /// Lightens a severity color for the far end of a gauge gradient, so a fill has depth instead of
    /// reading as a flat block.
    /// </summary>
    public static MediaColor Lighten(MediaColor color, double amount = 0.22) => MediaColor.FromRgb(
        (byte)Math.Clamp(color.R + ((255 - color.R) * amount), 0, 255),
        (byte)Math.Clamp(color.G + ((255 - color.G) * amount), 0, 255),
        (byte)Math.Clamp(color.B + ((255 - color.B) * amount), 0, 255));

    public void Dispose()
    {
        _personalizeWatcher.Dispose();
        _dwmWatcher.Dispose();
    }

    private void Reload()
    {
        var previousMode = Mode;
        var previousAccent = Accent;

        Mode = ReadAppsUseLightTheme() ? ThemeMode.Light : ThemeMode.Dark;
        Accent = ReadAccent();

        if (Mode != previousMode || Accent != previousAccent)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool ReadAppsUseLightTheme()
    {
        // Absent on some builds; light is the documented default when the value is missing.
        var value = Registry.GetValue($@"HKEY_CURRENT_USER\{PersonalizeKey}", "AppsUseLightTheme", 1);

        return value is not int flag || flag != 0;
    }

    private static MediaColor ReadAccent()
    {
        // DWM stores the accent as a DWORD in ABGR order, not ARGB.
        if (Registry.GetValue($@"HKEY_CURRENT_USER\{DwmKey}", "AccentColor", null) is not int packed)
        {
            return FallbackAccent;
        }

        var bytes = BitConverter.GetBytes(packed);
        var accent = MediaColor.FromRgb(bytes[0], bytes[1], bytes[2]);

        // A near-black or near-white accent would read as an error state or vanish into the surface,
        // so fall back rather than drawing an unreadable gauge.
        var luminance = (0.2126 * accent.R) + (0.7152 * accent.G) + (0.0722 * accent.B);

        if (luminance is < 24 or > 232)
        {
            return FallbackAccent;
        }

        // A desaturated accent is legible but says the wrong thing: a healthy quota drawn in grey
        // reads as disabled rather than as fine, and the sign-in link stops looking clickable. Colour
        // carries meaning here, so an accent with too little of it is not usable as the normal state.
        return Saturation(accent) < 0.25 ? FallbackAccent : accent;
    }

    /// <summary>HSL saturation of a colour, in <c>[0,1]</c>.</summary>
    private static double Saturation(MediaColor color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        if (delta <= 0)
        {
            return 0;
        }

        var lightness = (max + min) / 2d;

        return delta / (1d - Math.Abs((2d * lightness) - 1d));
    }

    private static MediaColor Rgb(byte r, byte g, byte b) => MediaColor.FromRgb(r, g, b);

    private static MediaColor Argb(byte a, byte r, byte g, byte b) => MediaColor.FromArgb(a, r, g, b);
}
