namespace QuotaBeacon.Core;

/// <summary>
/// What the tray icon should look like right now.
/// </summary>
/// <param name="Level">Severity driving the icon color.</param>
/// <param name="Representative">
/// The meter the icon speaks for, or <c>null</c> when no meter can express danger.
/// </param>
/// <param name="IsStale">Every provider with values is showing stale data.</param>
/// <param name="IsUnavailable">No provider has any values to show.</param>
public sealed record TrayState(
    AlertLevel Level,
    Meter? Representative,
    bool IsStale,
    bool IsUnavailable)
{
    /// <summary>
    /// The sweep to draw on the icon ring, or <c>null</c> to draw an unfilled ring. Absent whenever
    /// the representative meter has no denominator, which keeps a limitless spend account from
    /// being drawn as a gauge.
    /// </summary>
    public double? RemainingFraction => Representative?.Remaining;
}

/// <summary>
/// Collapses every meter across every enabled provider into the single state one icon can carry.
/// </summary>
public static class TrayStateResolver
{
    public static TrayState Resolve(IReadOnlyList<ProviderState> states, AlertSettings settings)
    {
        if (states.Count == 0 || states.All(s => s.IsEmpty))
        {
            return new TrayState(AlertLevel.None, null, IsStale: false, IsUnavailable: true);
        }

        // Stale wins over severity: a red icon computed from hours-old numbers is worse than an
        // honest "I don't know". Only providers that actually have values get a say.
        var withValues = states.Where(s => s.HasValues).ToArray();
        var isStale = withValues.All(s => s.IsStale);

        var representative = withValues
            .SelectMany(state => state.Meters)
            .Where(settings.IsEligibleForSeverity)
            .OrderByDescending(meter => settings.LevelOf(meter))
            // Least remaining first. A meter without a denominator sorts last within its level,
            // since it cannot be compared on proportion.
            .ThenBy(meter => meter.Remaining ?? double.PositiveInfinity)
            .ThenBy(meter => meter.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        var level = representative is null ? AlertLevel.None : settings.LevelOf(representative);

        return new TrayState(level, representative, isStale, IsUnavailable: false);
    }
}
