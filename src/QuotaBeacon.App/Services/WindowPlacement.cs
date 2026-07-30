namespace QuotaBeacon.App.Services;

/// <summary>A rectangle in device-independent pixels.</summary>
public readonly record struct PlacementRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>
/// Decides whether a remembered window position is still usable.
/// </summary>
/// <remarks>
/// Kept free of WPF types so the rule is directly testable. A display can be unplugged, rearranged,
/// or rescaled between runs, and restoring a window to coordinates that no longer exist puts it
/// somewhere the user cannot reach — with no title bar to drag it back by, that state is
/// unrecoverable without editing the settings file.
/// </remarks>
public static class WindowPlacement
{
    /// <summary>
    /// How much of the window must remain inside the work area for the position to be kept.
    /// </summary>
    /// <remarks>
    /// A strict "fully contained" rule would reject a window the user deliberately nudged off the
    /// edge. Requiring a grabbable portion instead preserves intent while still catching a monitor
    /// that has gone away.
    /// </remarks>
    private const double RequiredVisibleFraction = 0.5;

    /// <summary>
    /// Whether <paramref name="window"/> is visible enough within any of <paramref name="workAreas"/>.
    /// </summary>
    public static bool IsUsable(PlacementRect window, IReadOnlyList<PlacementRect> workAreas)
    {
        if (window.Width <= 0 || window.Height <= 0 || workAreas.Count == 0)
        {
            return false;
        }

        var area = window.Width * window.Height;

        // Overlap is measured per display rather than summed: a window straddling two monitors is
        // reachable on either one, and summing could pass a window that is mostly in the gap between
        // two non-adjacent displays.
        return workAreas.Any(work => Overlap(window, work) >= area * RequiredVisibleFraction);
    }

    private static double Overlap(PlacementRect a, PlacementRect b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);

        return width <= 0 || height <= 0 ? 0 : width * height;
    }
}
