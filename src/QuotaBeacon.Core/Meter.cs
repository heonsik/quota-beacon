namespace QuotaBeacon.Core;

public enum MeterKind
{
    /// <summary>A quota that resets on a rolling schedule and reports a percentage.</summary>
    Window,

    /// <summary>An amount accumulated over a billing period, with an optional cap.</summary>
    Spend,
}

/// <summary>
/// One independently resetting or accumulating quota quantity.
/// </summary>
/// <remarks>
/// <para>
/// This is the single abstraction that lets seat-based and consumption-based accounts render
/// through the same code. Seat accounts produce <see cref="MeterKind.Window"/> meters; Enterprise
/// consumption accounts produce <see cref="MeterKind.Spend"/> meters.
/// </para>
/// <para>
/// <see cref="Ratio"/> is the field consumers branch on, and it is deliberately nullable: a spend
/// meter with no configured limit has no denominator, so it has no percentage. Callers must render
/// that case as an amount without a gauge rather than substituting 0 or 1.
/// </para>
/// <para>
/// <see cref="Ratio"/> always expresses the fraction <em>consumed</em>. Remaining is presentation
/// (<c>1 - Ratio</c>); storing a single direction removes a class of inversion bugs.
/// </para>
/// </remarks>
public sealed record Meter
{
    private Meter(string id, string label, MeterKind kind)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Meter id must be a stable non-empty key.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Meter label must be non-empty.", nameof(label));
        }

        Id = id;
        Label = label;
        Kind = kind;
    }

    /// <summary>A stable key such as <c>claude.session5h</c>. Used for alert latching.</summary>
    public string Id { get; }

    public string Label { get; }

    public MeterKind Kind { get; }

    /// <summary>Fraction consumed in <c>[0,1]</c>, or <c>null</c> when there is no denominator.</summary>
    public double? Ratio { get; private init; }

    /// <summary>When a <see cref="MeterKind.Window"/> meter next resets.</summary>
    public DateTimeOffset? ResetsAt { get; private init; }

    /// <summary>Amount consumed, for <see cref="MeterKind.Spend"/> meters.</summary>
    public Money? Amount { get; private init; }

    /// <summary>The spend cap, when the organization configured one.</summary>
    public Money? Limit { get; private init; }

    public DateTimeOffset? PeriodStart { get; private init; }

    public DateTimeOffset? PeriodEnd { get; private init; }

    /// <summary>
    /// Whether this meter can be expressed as a proportion, and therefore drawn as a gauge and
    /// evaluated against percentage thresholds.
    /// </summary>
    public bool HasRatio => Ratio.HasValue;

    /// <summary>Fraction remaining, or <c>null</c> when there is no denominator.</summary>
    public double? Remaining => Ratio is { } consumed ? 1d - consumed : null;

    /// <summary>
    /// The identity of the current window or billing period. Alert latches are keyed on this so a
    /// meter becomes eligible to alert again once it rolls over.
    /// </summary>
    public DateTimeOffset? WindowIdentity => Kind == MeterKind.Window ? ResetsAt : PeriodEnd;

    /// <summary>
    /// Creates a rolling-window meter.
    /// </summary>
    /// <param name="consumedRatio">
    /// Fraction consumed. Clamped into <c>[0,1]</c> because providers have been observed to report
    /// slightly over 1.0 once a limit is exceeded.
    /// </param>
    /// <exception cref="ArgumentException">If the ratio is not a finite number.</exception>
    public static Meter Window(
        string id,
        string label,
        double consumedRatio,
        DateTimeOffset? resetsAt = null) =>
        new(id, label, MeterKind.Window)
        {
            Ratio = Clamp(consumedRatio, nameof(consumedRatio)),
            ResetsAt = resetsAt,
        };

    /// <summary>
    /// Creates a spend meter. When <paramref name="limit"/> is absent, in a different currency, or
    /// not positive, the meter has no ratio and must render without a gauge.
    /// </summary>
    public static Meter Spend(
        string id,
        string label,
        Money amount,
        Money? limit = null,
        DateTimeOffset? periodStart = null,
        DateTimeOffset? periodEnd = null)
    {
        var ratio = amount.FractionOf(limit);

        return new Meter(id, label, MeterKind.Spend)
        {
            Amount = amount,
            // Drop a limit we cannot compare against, so no consumer can later derive a
            // percentage from a mismatched currency.
            Limit = ratio.HasValue ? limit : null,
            Ratio = ratio.HasValue ? Clamp(ratio.Value, nameof(limit)) : null,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
        };
    }

    private static double Clamp(double value, string paramName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException("Ratio must be a finite number.", paramName);
        }

        return Math.Clamp(value, 0d, 1d);
    }
}
