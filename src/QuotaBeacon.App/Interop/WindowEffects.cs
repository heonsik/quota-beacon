using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuotaBeacon.App.Interop;

/// <summary>
/// Applies Windows 11 window materials and keeps the popup inside the work area.
/// </summary>
/// <remarks>
/// Every attribute is applied by probing rather than by checking an OS version string: a failed
/// <c>DwmSetWindowAttribute</c> call returns a non-zero result on builds that do not support the
/// attribute, which is exactly the signal needed, and it keeps working when Microsoft back-ports or
/// renumbers something. On Windows 10 the calls fail harmlessly and the card falls back to a solid
/// themed surface with a hairline border.
/// </remarks>
internal static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;

    private const int CornerPreferenceRound = 2;

    /// <summary>
    /// Applies the native window chrome: rounded corners and the correct dark or light frame.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a system backdrop will actually be painted, meaning the caller may leave its
    /// surface transparent. Always <c>false</c> today: see the remarks.
    /// </returns>
    /// <remarks>
    /// A Mica backdrop is deliberately not requested. Setting
    /// <c>DWMWA_SYSTEMBACKDROP_TYPE</c> succeeds on Windows 11, but DWM only paints the material
    /// where the frame is extended into the client area, and extending it on a
    /// <c>WindowStyle=None</c>, non-layered WPF window makes the composited result drop the card
    /// entirely: the content still renders, as <c>PrintWindow</c> confirms, yet nothing reaches the
    /// screen and the popup reads as invisible.
    ///
    /// Reporting a backdrop that is not painted is the worst of the options, because the caller then
    /// drops its opaque fill and the card becomes see-through over whatever window is beneath. So the
    /// surface stays opaque and rounded corners plus the themed frame carry the native feel. Revisit
    /// with a <c>WindowChrome</c>-based window, where the backdrop can be verified end to end.
    /// </remarks>
    public static bool TryApplyNativeChrome(Window window, bool isDark)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return false;
        }

        SetAttribute(source.Handle, DwmwaUseImmersiveDarkMode, isDark ? 1 : 0);
        SetAttribute(source.Handle, DwmwaWindowCornerPreference, CornerPreferenceRound);

        return false;
    }

    /// <summary>
    /// Positions a popup near the notification area without covering the taskbar or leaving the
    /// monitor it was summoned on.
    /// </summary>
    /// <remarks>
    /// The anchor is the current cursor position, which is where the user just clicked the tray icon
    /// and therefore identifies the right monitor on a multi-display setup. Work-area clamping is
    /// what keeps the card from spilling off a scaled secondary display.
    /// </remarks>
    public static void PositionNearTray(Window window, double margin = 12)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
            return;

        if (!GetCursorPos(out var cursor))
            return;

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (!GetMonitorInfo(monitor, ref info))
            return;

        // Device pixels to DIPs: a 150% display reports a work area far larger than WPF's coordinates.
        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(info.WorkArea.Left, info.WorkArea.Top));
        var bottomRight = transform.Transform(new Point(info.WorkArea.Right, info.WorkArea.Bottom));
        var anchor = transform.Transform(new Point(cursor.X, cursor.Y));

        var left = Math.Clamp(
            anchor.X - (window.Width / 2),
            topLeft.X + margin,
            Math.Max(topLeft.X + margin, bottomRight.X - window.Width - margin));

        // Prefer sitting above the anchor, which is the usual bottom taskbar. If there is no room
        // above, the taskbar is at the top, so drop below it instead of hanging off-screen.
        var above = anchor.Y - window.ActualHeight - margin;
        var top = above >= topLeft.Y + margin
            ? above
            : Math.Min(anchor.Y + margin, bottomRight.Y - window.ActualHeight - margin);

        window.Left = left;
        window.Top = Math.Max(topLeft.Y + margin, top);
    }

    private static bool SetAttribute(IntPtr handle, int attribute, int value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;

    private const int MonitorDefaultToNearest = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public int Flags;
    }
}
