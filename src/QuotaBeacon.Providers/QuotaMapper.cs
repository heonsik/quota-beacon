using System.Text.Json;
using QuotaBeacon.Core;

namespace QuotaBeacon.Providers;

/// <summary>Describes one meter to look for in a response.</summary>
/// <param name="MeterId">The stable meter id to emit.</param>
/// <param name="Label">Display label.</param>
/// <param name="ContainerKeys">
/// Candidate object keys that may hold this meter, in preference order. Aliases exist because the
/// same quota appears under different names across providers and versions
/// (<c>primary_window</c>, <c>five_hour_limit</c>, <c>five_hour</c>).
/// </param>
public abstract record MeterDescriptor(
    string MeterId,
    string Label,
    IReadOnlyList<string> ContainerKeys);

public sealed record WindowMeterDescriptor(
    string MeterId,
    string Label,
    IReadOnlyList<string> ContainerKeys) : MeterDescriptor(MeterId, Label, ContainerKeys);

public sealed record SpendMeterDescriptor(
    string MeterId,
    string Label,
    IReadOnlyList<string> ContainerKeys) : MeterDescriptor(MeterId, Label, ContainerKeys);

/// <summary>
/// Projects an undocumented JSON response onto <see cref="Meter"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Mapping is additive and never requires a field it does not use: unknown keys are ignored, and a
/// descriptor that finds nothing simply contributes no meter. A response carrying both window and
/// spend information yields meters of both kinds, which is what lets one account style change to
/// another without a code change.
/// </para>
/// <para>
/// When nothing maps, the caller reports <see cref="ProviderErrorKind.UnrecognizedResponse"/>
/// rather than an empty success, so a shape change surfaces as an actionable error instead of a
/// confident blank.
/// </para>
/// </remarks>
public static class QuotaMapper
{
    /// <summary>Fields stating a consumed percentage on a 0-100 scale.</summary>
    private static readonly string[] ConsumedPercentKeys =
        ["used_percent", "usage_percent", "percent_used", "utilization_percent", "percent"];

    /// <summary>Fields stating a consumed fraction on a 0-1 scale.</summary>
    private static readonly string[] ConsumedFractionKeys =
        ["used_fraction", "fraction_used", "consumed_fraction", "ratio"];

    /// <summary>Fields stating a remaining percentage on a 0-100 scale.</summary>
    private static readonly string[] RemainingPercentKeys =
        ["remaining_percent", "percent_remaining"];

    /// <summary>
    /// Fields whose scale is not stated by the name. Resolved by magnitude: a value above 1 can only
    /// be a percentage, and at or below 1 it is read as a fraction.
    /// </summary>
    private static readonly string[] AmbiguousConsumedKeys = ["utilization", "used", "consumed"];

    private static readonly string[] AbsoluteResetKeys =
        ["resets_at", "reset_at", "resets_at_unix", "reset_time", "next_reset"];

    private static readonly string[] RelativeResetSecondKeys =
        ["resets_in_seconds", "reset_after_seconds", "resets_in", "seconds_until_reset"];

    private static readonly string[] AmountKeys =
        ["amount", "total", "spend", "total_spend", "cost", "total_cost", "used_credits", "used_dollars", "used", "usage"];

    private static readonly string[] LimitKeys =
        ["monthly_limit", "limit_dollars", "limit", "spend_limit", "cap", "max", "limit_amount", "budget"];

    private static readonly string[] CurrencyKeys = ["currency", "currency_code", "unit"];

    private static readonly string[] PeriodStartKeys =
        ["period_start", "start", "starts_at", "start_date", "billing_period_start"];

    private static readonly string[] PeriodEndKeys =
        ["period_end", "end", "ends_at", "end_date", "billing_period_end"];

    /// <summary>How deep to search for a descriptor's container.</summary>
    /// <remarks>
    /// Responses nest quota objects one or two levels down (<c>rate_limits.primary</c>,
    /// <c>usage.current_period</c>). Searching deeper would start matching unrelated keys, so the
    /// depth is bounded deliberately rather than left open.
    /// </remarks>
    private const int MaxSearchDepth = 3;

    public static IReadOnlyList<Meter> Map(
        JsonElement root,
        IEnumerable<MeterDescriptor> descriptors,
        DateTimeOffset now)
    {
        var meters = new List<Meter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            if (FindContainer(root, descriptor.ContainerKeys) is not { } container)
            {
                continue;
            }

            var meter = descriptor switch
            {
                WindowMeterDescriptor window => MapWindow(window, container, now),
                SpendMeterDescriptor spend => MapSpend(spend, container),
                _ => null,
            };

            // A duplicate id would collide with alert latching, so the first match wins.
            if (meter is not null && seen.Add(meter.Id))
            {
                meters.Add(meter);
            }
        }

        return meters;
    }

    /// <summary>Describes a payload's shape for an unrecognized-response diagnostic.</summary>
    public static string DescribeShape(JsonElement root) => JsonReading.DescribeShape(root);

    private static Meter? MapWindow(
        WindowMeterDescriptor descriptor,
        JsonElement container,
        DateTimeOffset now)
    {
        if (ReadConsumedFraction(container) is not { } consumed)
        {
            return null;
        }

        return Meter.Window(
            descriptor.MeterId,
            descriptor.Label,
            consumed,
            ReadReset(container, now));
    }

    private static Meter? MapSpend(SpendMeterDescriptor descriptor, JsonElement container)
    {
        if (ReadMoney(container, AmountKeys) is not { } amount)
        {
            return null;
        }

        var limit = ReadMoney(container, LimitKeys);

        // Inherit the amount's currency when a bare numeric limit sits alongside it, which is the
        // common case; otherwise a valid limit would be discarded as a currency mismatch.
        if (limit is { } cap && string.IsNullOrEmpty(cap.Currency))
        {
            limit = new Money(cap.Amount, amount.Currency);
        }

        return Meter.Spend(
            descriptor.MeterId,
            descriptor.Label,
            amount,
            limit,
            JsonReading.Timestamp(container, PeriodStartKeys),
            JsonReading.Timestamp(container, PeriodEndKeys));
    }

    private static double? ReadConsumedFraction(JsonElement container)
    {
        foreach (var key in ConsumedPercentKeys)
        {
            if (JsonReading.Double(container, key) is { } percent)
            {
                return percent / 100d;
            }
        }

        foreach (var key in ConsumedFractionKeys)
        {
            if (JsonReading.Double(container, key) is { } fraction)
            {
                return fraction;
            }
        }

        foreach (var key in RemainingPercentKeys)
        {
            if (JsonReading.Double(container, key) is { } remaining)
            {
                return 1d - (remaining / 100d);
            }
        }

        foreach (var key in AmbiguousConsumedKeys)
        {
            if (JsonReading.Double(container, key) is { } value)
            {
                return value > 1d ? value / 100d : value;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadReset(JsonElement container, DateTimeOffset now)
    {
        if (JsonReading.Timestamp(container, AbsoluteResetKeys) is { } absolute)
        {
            return absolute;
        }

        foreach (var key in RelativeResetSecondKeys)
        {
            if (JsonReading.Double(container, key) is { } seconds && seconds >= 0)
            {
                return now.AddSeconds(seconds);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads money written either as a nested object (<c>{"amount": 12, "currency": "USD"}</c>) or
    /// as a bare number alongside a sibling currency field.
    /// </summary>
    private static Money? ReadMoney(JsonElement container, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!JsonReading.TryProperty(container, key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                if (ReadDecimal(value, AmountKeys) is { } nested)
                {
                    return new Money(nested, ReadCurrency(value) ?? ReadCurrency(container) ?? "USD");
                }

                continue;
            }

            if (JsonReading.AsDecimal(value) is { } number)
            {
                // An empty currency signals "inherit from the sibling amount" to the caller.
                return new Money(number, ReadCurrency(container) ?? string.Empty);
            }
        }

        return null;
    }

    private static decimal? ReadDecimal(JsonElement container, string[] keys)
    {
        foreach (var key in keys)
        {
            if (JsonReading.TryProperty(container, key, out var value)
                && JsonReading.AsDecimal(value) is { } number)
            {
                return number;
            }
        }

        return null;
    }

    private static string? ReadCurrency(JsonElement container)
    {
        foreach (var key in CurrencyKeys)
        {
            if (JsonReading.String(container, key) is { Length: > 0 } currency)
            {
                return currency;
            }
        }

        return null;
    }

    /// <summary>
    /// Breadth-first search for the first object matching any candidate key.
    /// </summary>
    /// <remarks>
    /// Breadth-first rather than depth-first so a shallow match wins over a deeper one, which keeps
    /// a top-level <c>weekly</c> from losing to a nested <c>details.weekly</c>. Candidate order is
    /// honored ahead of depth: every key is tried at each level before descending.
    /// </remarks>
    private static JsonElement? FindContainer(JsonElement root, IReadOnlyList<string> candidateKeys)
    {
        var level = new List<JsonElement> { root };

        for (var depth = 0; depth < MaxSearchDepth && level.Count > 0; depth++)
        {
            foreach (var key in candidateKeys)
            {
                foreach (var element in level)
                {
                    if (JsonReading.TryProperty(element, key, out var match)
                        && match.ValueKind == JsonValueKind.Object)
                    {
                        return match;
                    }
                }
            }

            level = level
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .SelectMany(element => element.EnumerateObject().Select(property => property.Value))
                .Where(value => value.ValueKind == JsonValueKind.Object)
                .ToList();
        }

        return null;
    }
}
