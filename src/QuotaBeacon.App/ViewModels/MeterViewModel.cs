using System.Globalization;
using QuotaBeacon.App.Theming;
using QuotaBeacon.Core;

namespace QuotaBeacon.App.ViewModels;

/// <summary>
/// One meter, formatted for display.
/// </summary>
/// <remarks>
/// The kind distinction from the design lives here as <see cref="IsRatio"/>: a meter with a
/// denominator gets a percentage and a gauge, and a meter without one gets an amount and no gauge.
/// No property fabricates a percentage when the underlying meter has none.
/// </remarks>
public sealed class MeterViewModel(Meter meter, AlertLevel level, DateTimeOffset now)
{
    public string Label { get; } = LocalizedLabel(meter);

    public AlertLevel Level { get; } = level;

    public bool IsRatio { get; } = meter.HasRatio;

    /// <summary>Remaining fraction for the gauge, or <c>null</c> to draw no gauge at all.</summary>
    public double? RemainingFraction { get; } = meter.Remaining;

    /// <summary>Remaining percentage for the hero figure, or <c>null</c> when there is no denominator.</summary>
    public double? RemainingPercent { get; } = meter.Remaining * 100d;

    /// <summary>The amount, for spend meters. Empty for windows.</summary>
    public string AmountText { get; } = meter.Amount is { } amount ? FormatMoney(amount) : string.Empty;

    /// <summary>The cap, phrased as a continuation of the amount. Empty when there is no cap.</summary>
    public string LimitText { get; } = meter.Limit is { } limit
        ? Localization.Current.Format("Meter.LimitOf", FormatMoney(limit))
        : string.Empty;

    /// <summary>
    /// Explains the missing gauge, so its absence reads as information rather than as a failure to
    /// load. Empty whenever a gauge is drawn.
    /// </summary>
    public string NoLimitHint { get; } = meter is { Kind: MeterKind.Spend, HasRatio: false }
        ? Localization.Current["Meter.NoSpendLimit"]
        : string.Empty;

    public string MetaLeft { get; } = LocalizedLabel(meter);

    public string MetaRight { get; } = DescribeTiming(meter, now);

    /// <summary>The severity word shown beside the figure, so severity is never color-only.</summary>
    public string LevelText { get; } = level switch
    {
        AlertLevel.Critical => Localization.Current["Level.Critical"],
        AlertLevel.Warning => Localization.Current["Level.Low"],
        _ => string.Empty,
    };

    /// <summary>
    /// Translates a known meter's label.
    /// </summary>
    /// <remarks>
    /// Providers produce English labels because they are a lower layer that knows nothing about the
    /// interface language. Mapping by the stable meter id keeps translation in the UI where it
    /// belongs, and an unrecognised id falls back to whatever the provider supplied rather than
    /// showing a blank or a raw key.
    /// </remarks>
    private static string LocalizedLabel(Meter meter)
    {
        var key = meter.Id switch
        {
            "claude.session5h" or "codex.primary" => "Meter.Window5h",
            "claude.weekly" or "codex.secondary" => "Meter.WindowWeekly",
            "claude.spend.period" => "Meter.BillingPeriod",
            "codex.credits" => "Meter.Credits",
            _ => null,
        };

        return key is null ? meter.Label : Localization.Current[key];
    }

    private static string DescribeTiming(Meter meter, DateTimeOffset now)
    {
        if (meter.Kind == MeterKind.Window)
        {
            return meter.ResetsAt is { } resetsAt
                ? DescribeCountdown(resetsAt - now)
                : string.Empty;
        }

        if (meter.PeriodEnd is not { } periodEnd)
        {
            return string.Empty;
        }

        return meter.PeriodStart is { } periodStart
            ? $"{FormatDay(periodStart)} – {FormatDay(periodEnd)}"
            : Localization.Current.Format("Meter.Through", FormatDay(periodEnd));
    }

    /// <summary>
    /// Renders a countdown at the coarsest granularity that is still useful, so the text stops
    /// changing every second once the reset is hours away.
    /// </summary>
    private static string DescribeCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return Localization.Current["Meter.Resetting"];
        }

        if (remaining.TotalDays >= 1)
        {
            return Localization.Current.Format(
                "Meter.ResetsInDays",
                (int)remaining.TotalDays,
                remaining.Hours);
        }

        if (remaining.TotalHours >= 1)
        {
            return Localization.Current.Format(
                "Meter.ResetsInHours",
                (int)remaining.TotalHours,
                remaining.Minutes);
        }

        return remaining.TotalMinutes >= 1
            ? Localization.Current.Format("Meter.ResetsInMinutes", (int)remaining.TotalMinutes)
            : Localization.Current["Meter.ResetsUnderMinute"];
    }

    private static string FormatDay(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MMM d", Localization.Current.Culture);

    /// <summary>
    /// Formats money with a symbol when the currency is a familiar one, and with the ISO code
    /// otherwise, so an unrecognized code still renders legibly instead of being dropped.
    /// </summary>
    private static string FormatMoney(Money money)
    {
        var code = money.Currency.ToUpperInvariant();

        var symbol = code switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "KRW" => "₩",
            "JPY" => "¥",
            _ => null,
        };

        // Zero-decimal currencies read as wrong with cents attached.
        var amount = code is "KRW" or "JPY"
            ? money.Amount.ToString("N0", Localization.Current.Culture)
            : money.Amount.ToString("N2", Localization.Current.Culture);

        return symbol is null ? $"{amount} {code}" : $"{symbol}{amount}";
    }
}
