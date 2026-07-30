using System.Text.Json;
using QuotaBeacon.Core;
using QuotaBeacon.Providers;

namespace QuotaBeacon.Tests;

public class QuotaMapperTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");

    private static readonly IReadOnlyList<MeterDescriptor> ClaudeDescriptors =
    [
        new WindowMeterDescriptor(
            ClaudeProvider.SessionMeterId,
            "5-hour limit",
            ["five_hour", "primary_window", "primary"]),
        new WindowMeterDescriptor(
            ClaudeProvider.WeeklyMeterId,
            "Weekly limit",
            ["seven_day", "weekly", "secondary"]),
        new SpendMeterDescriptor(
            ClaudeProvider.SpendMeterId,
            "This billing period",
            ["spend", "current_period"]),
    ];

    private static readonly IReadOnlyList<MeterDescriptor> CodexDescriptors =
    [
        new WindowMeterDescriptor(CodexProvider.PrimaryMeterId, "5-hour limit", ["primary_window", "primary"]),
        new WindowMeterDescriptor(CodexProvider.SecondaryMeterId, "Weekly limit", ["secondary_window", "secondary"]),
        new SpendMeterDescriptor(CodexProvider.CreditMeterId, "Workspace credits", ["credits", "balance"]),
    ];

    private static IReadOnlyList<Meter> MapFixture(
        string fixtureName,
        IReadOnlyList<MeterDescriptor> descriptors)
    {
        using var document = JsonDocument.Parse(Fixture.Read(fixtureName));

        return QuotaMapper.Map(document.RootElement, descriptors, Now);
    }

    private static IReadOnlyList<Meter> MapJson(string json, IReadOnlyList<MeterDescriptor> descriptors)
    {
        using var document = JsonDocument.Parse(json);

        return QuotaMapper.Map(document.RootElement, descriptors, Now);
    }

    [Fact]
    public void A_seat_response_maps_to_two_window_meters()
    {
        var meters = MapFixture("claude-seat.json", ClaudeDescriptors);

        Assert.Equal(2, meters.Count);

        var session = meters.Single(m => m.Id == ClaudeProvider.SessionMeterId);
        Assert.Equal(MeterKind.Window, session.Kind);
        Assert.Equal(0.42, session.Ratio!.Value, precision: 6);
        Assert.Equal(DateTimeOffset.Parse("2026-07-30T18:00:00Z"), session.ResetsAt);

        Assert.Equal(0.19, meters.Single(m => m.Id == ClaudeProvider.WeeklyMeterId).Ratio!.Value, precision: 6);
    }

    [Fact]
    public void A_spend_response_with_a_limit_produces_a_ratio()
    {
        var meter = Assert.Single(MapFixture("claude-spend-with-limit.json", ClaudeDescriptors));

        Assert.Equal(MeterKind.Spend, meter.Kind);
        Assert.Equal(132.5m, meter.Amount!.Value.Amount);
        Assert.Equal("USD", meter.Amount!.Value.Currency);
        // The bare numeric limit inherits the amount's currency.
        Assert.Equal(new Money(400m, "USD"), meter.Limit);
        Assert.Equal(0.33125, meter.Ratio!.Value, precision: 6);
        Assert.Equal(DateTimeOffset.Parse("2026-07-31T23:59:59Z"), meter.PeriodEnd);
    }

    [Fact]
    public void A_spend_response_without_a_limit_produces_no_ratio()
    {
        // The consumption-Enterprise-without-a-cap case: an amount worth showing, no gauge.
        var meter = Assert.Single(MapFixture("claude-spend-no-limit.json", ClaudeDescriptors));

        Assert.False(meter.HasRatio);
        Assert.Null(meter.Limit);
        Assert.Equal(132.5m, meter.Amount!.Value.Amount);
    }

    [Fact]
    public void A_mixed_response_produces_meters_of_both_kinds()
    {
        // An account switching styles must not need a code change.
        var meters = MapFixture("claude-mixed.json", ClaudeDescriptors);

        Assert.Equal(2, meters.Count);

        var window = meters.Single(m => m.Kind == MeterKind.Window);
        Assert.Equal(0.884, window.Ratio!.Value, precision: 6);
        Assert.Equal(Now.AddSeconds(4200), window.ResetsAt);

        var spend = meters.Single(m => m.Kind == MeterKind.Spend);
        Assert.Equal(0.61, spend.Ratio!.Value, precision: 6);
    }

    [Fact]
    public void Codex_windows_map_from_a_nested_rate_limits_object()
    {
        var meters = MapFixture("codex-windows.json", CodexDescriptors);

        Assert.Equal(2, meters.Count);
        Assert.Equal(0.76, meters.Single(m => m.Id == CodexProvider.PrimaryMeterId).Ratio!.Value, precision: 6);
        Assert.Equal(
            Now.AddSeconds(1800),
            meters.Single(m => m.Id == CodexProvider.PrimaryMeterId).ResetsAt);
    }

    [Fact]
    public void Codex_credits_map_to_a_spend_meter()
    {
        var meter = Assert.Single(MapFixture("codex-credits.json", CodexDescriptors));

        Assert.Equal(CodexProvider.CreditMeterId, meter.Id);
        Assert.Equal(240.75m, meter.Amount!.Value.Amount);
        Assert.Equal(0.24075, meter.Ratio!.Value, precision: 6);
    }

    [Fact]
    public void An_unrecognized_response_maps_to_nothing()
    {
        // Zero meters is the signal the provider turns into UnrecognizedResponse.
        Assert.Empty(MapFixture("unmappable.json", ClaudeDescriptors));
    }

    [Fact]
    public void A_remaining_percentage_is_inverted_into_consumed()
    {
        var meter = Assert.Single(MapFixture("claude-remaining-style.json", ClaudeDescriptors));

        Assert.Equal(0.75, meter.Ratio!.Value, precision: 6);
        Assert.Equal(0.25, meter.Remaining!.Value, precision: 6);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785412800), meter.ResetsAt);
    }

    [Theory]
    [InlineData(42, 0.42)]
    [InlineData(0.42, 0.42)]
    [InlineData(1, 1.0)]
    public void An_unnamed_scale_is_resolved_by_magnitude(double reported, double expected)
    {
        // "utilization" states no scale, so a value above 1 can only be a percentage. At exactly 1
        // the reading is a full window rather than one percent, which is the safer error.
        var meters = MapJson(
            $"{{\"five_hour\":{{\"utilization\":{reported.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}",
            ClaudeDescriptors);

        Assert.Equal(expected, Assert.Single(meters).Ratio!.Value, precision: 6);
    }

    [Fact]
    public void Keys_are_matched_case_insensitively()
    {
        var meters = MapJson("""{"Five_Hour":{"Used_Percent":30}}""", ClaudeDescriptors);

        Assert.Equal(0.30, Assert.Single(meters).Ratio!.Value, precision: 6);
    }

    [Fact]
    public void Quoted_numbers_are_accepted()
    {
        // Providers quote numerics to survive JavaScript precision limits.
        var meters = MapJson("""{"five_hour":{"used_percent":"55.5"}}""", ClaudeDescriptors);

        Assert.Equal(0.555, Assert.Single(meters).Ratio!.Value, precision: 6);
    }

    [Fact]
    public void Unknown_keys_are_ignored_rather_than_failing_the_map()
    {
        var meters = MapJson(
            """{"five_hour":{"used_percent":10,"brand_new_field":{"nested":true}},"unrelated":42}""",
            ClaudeDescriptors);

        Assert.Equal(0.10, Assert.Single(meters).Ratio!.Value, precision: 6);
    }

    [Fact]
    public void A_shallow_match_wins_over_a_deeper_one()
    {
        var meters = MapJson(
            """{"five_hour":{"used_percent":10},"details":{"five_hour":{"used_percent":90}}}""",
            ClaudeDescriptors);

        Assert.Equal(0.10, Assert.Single(meters).Ratio!.Value, precision: 6);
    }

    [Fact]
    public void A_window_without_any_usage_figure_contributes_nothing()
    {
        // A window whose usage is unknown is not a window; emitting a zeroed gauge would be a lie.
        Assert.Empty(MapJson("""{"five_hour":{"resets_at":"2026-07-30T18:00:00Z"}}""", ClaudeDescriptors));
    }

    [Fact]
    public void A_mismatched_limit_currency_drops_the_limit_but_keeps_the_amount()
    {
        var meter = Assert.Single(MapJson(
            """{"spend":{"amount":10,"currency":"USD","limit":{"amount":50000,"currency":"KRW"}}}""",
            ClaudeDescriptors));

        Assert.False(meter.HasRatio);
        Assert.Null(meter.Limit);
        Assert.Equal(10m, meter.Amount!.Value.Amount);
    }

    [Fact]
    public void A_shape_description_names_keys_and_types_but_no_values()
    {
        using var document = JsonDocument.Parse(Fixture.Read("claude-seat.json"));

        var shape = QuotaMapper.DescribeShape(document.RootElement);

        Assert.Contains("five_hour", shape);
        Assert.Contains("number", shape);
        // The diagnostic must be safe to log: no value from the payload may appear in it.
        Assert.DoesNotContain("42", shape);
        Assert.DoesNotContain("default_claude_ai", shape);
        Assert.DoesNotContain("2026-07-30", shape);
    }
}
