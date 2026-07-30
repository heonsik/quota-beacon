namespace QuotaBeacon.Core;

public enum AlertLevel
{
    None = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// Absolute-amount thresholds for a spend meter that has no denominator.
/// </summary>
/// <remarks>
/// These have no defaults. The application cannot know whether a given monthly spend is normal for
/// an organization, so a guessed default would either cry wolf or stay silent through a real
/// overrun. Settings surface the current amount and let the user choose an informed number.
/// </remarks>
public sealed record SpendAlertThreshold(Money? Warning = null, Money? Critical = null)
{
    public bool IsConfigured => Warning.HasValue || Critical.HasValue;
}

public sealed record AlertSettings
{
    /// <summary>Fire a warning when remaining falls below this fraction.</summary>
    public double WarningRemaining { get; init; } = 0.20;

    /// <summary>Fire a critical alert when remaining falls below this fraction.</summary>
    public double CriticalRemaining { get; init; } = 0.10;

    /// <summary>Per-meter absolute thresholds, keyed by <see cref="Meter.Id"/>.</summary>
    public IReadOnlyDictionary<string, SpendAlertThreshold> SpendThresholds { get; init; } =
        new Dictionary<string, SpendAlertThreshold>(StringComparer.Ordinal);

    public SpendAlertThreshold? ThresholdFor(string meterId) =>
        SpendThresholds.TryGetValue(meterId, out var threshold) && threshold.IsConfigured
            ? threshold
            : null;

    /// <summary>
    /// Whether a meter can express danger at all, and so may drive the tray icon.
    /// </summary>
    public bool IsEligibleForSeverity(Meter meter) =>
        meter.HasRatio || ThresholdFor(meter.Id) is not null;

    /// <summary>
    /// The severity a meter is currently at, independent of any alert already delivered.
    /// </summary>
    public AlertLevel LevelOf(Meter meter)
    {
        if (meter.Remaining is { } remaining)
        {
            if (remaining < CriticalRemaining)
            {
                return AlertLevel.Critical;
            }

            return remaining < WarningRemaining ? AlertLevel.Warning : AlertLevel.None;
        }

        if (meter.Amount is not { } amount || ThresholdFor(meter.Id) is not { } threshold)
        {
            return AlertLevel.None;
        }

        // Spend rises toward danger, the opposite direction from a remaining fraction.
        if (Exceeds(amount, threshold.Critical))
        {
            return AlertLevel.Critical;
        }

        return Exceeds(amount, threshold.Warning) ? AlertLevel.Warning : AlertLevel.None;
    }

    private static bool Exceeds(Money amount, Money? threshold) =>
        threshold is { } limit
        && amount.SameCurrencyAs(limit)
        && amount.Amount >= limit.Amount;
}

/// <summary>A threshold crossing that should be delivered to the user once.</summary>
public sealed record Alert(ProviderId Provider, Meter Meter, AlertLevel Level);

/// <summary>
/// Decides which threshold crossings become notifications.
/// </summary>
/// <remarks>
/// Latches are keyed on meter id, level, and window identity. Because the identity is the reset
/// time or billing period end, a rollover naturally re-arms the meter without any scheduled
/// bookkeeping. Latches for identities no longer present are pruned on each evaluation so the
/// engine does not grow without bound over a long-running session.
/// </remarks>
public sealed class AlertEngine(AlertSettings settings)
{
    private readonly HashSet<LatchKey> _latched = [];

    public AlertSettings Settings { get; } = settings;

    /// <summary>
    /// Evaluates the current state of every provider and returns the alerts to deliver now.
    /// </summary>
    public IReadOnlyList<Alert> Evaluate(IEnumerable<ProviderState> states)
    {
        var raised = new List<Alert>();
        var live = new HashSet<LatchKey>();

        foreach (var state in states)
        {
            foreach (var meter in state.Meters)
            {
                var level = Settings.LevelOf(meter);
                var identity = meter.WindowIdentity;

                var warningKey = new LatchKey(meter.Id, AlertLevel.Warning, identity);
                var criticalKey = new LatchKey(meter.Id, AlertLevel.Critical, identity);
                live.Add(warningKey);
                live.Add(criticalKey);

                switch (level)
                {
                    case AlertLevel.None:
                        // Fully recovered: re-arm both levels for this window.
                        _latched.Remove(warningKey);
                        _latched.Remove(criticalKey);
                        break;

                    case AlertLevel.Warning:
                        // Recovering out of critical re-arms critical but must not fire a warning
                        // on the way up, which would report a worsening that did not happen.
                        _latched.Remove(criticalKey);

                        if (_latched.Add(warningKey))
                        {
                            raised.Add(new Alert(state.Provider, meter, AlertLevel.Warning));
                        }

                        break;

                    case AlertLevel.Critical:
                        if (_latched.Add(criticalKey))
                        {
                            raised.Add(new Alert(state.Provider, meter, AlertLevel.Critical));
                        }

                        // Latch warning too, so easing back into the warning band is silent.
                        _latched.Add(warningKey);
                        break;
                }
            }
        }

        _latched.RemoveWhere(key => !live.Contains(key));

        return raised;
    }

    private readonly record struct LatchKey(string MeterId, AlertLevel Level, DateTimeOffset? Identity);
}
