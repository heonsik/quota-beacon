using QuotaBeacon.Core;

namespace QuotaBeacon.Tests;

public class MeterTests
{
    [Fact]
    public void Window_exposes_consumed_and_remaining_as_complements()
    {
        var meter = Meter.Window("claude.session5h", "5-hour limit", consumedRatio: 0.32);

        Assert.True(meter.HasRatio);
        Assert.Equal(0.32, meter.Ratio!.Value, precision: 10);
        Assert.Equal(0.68, meter.Remaining!.Value, precision: 10);
    }

    [Theory]
    [InlineData(1.4, 1.0)]
    [InlineData(-0.2, 0.0)]
    public void Window_clamps_out_of_range_ratios(double reported, double expected)
    {
        // Providers have been observed reporting above 1.0 after a limit is exceeded.
        var meter = Meter.Window("codex.weekly", "Weekly", reported);

        Assert.Equal(expected, meter.Ratio!.Value, precision: 10);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Window_rejects_non_finite_ratios(double reported)
    {
        Assert.Throws<ArgumentException>(() => Meter.Window("x", "X", reported));
    }

    [Fact]
    public void Spend_with_a_limit_derives_a_ratio()
    {
        var meter = Meter.Spend(
            "claude.spend.month",
            "This month",
            new Money(32m, "USD"),
            new Money(100m, "USD"));

        Assert.True(meter.HasRatio);
        Assert.Equal(0.32, meter.Ratio!.Value, precision: 10);
        Assert.Equal(new Money(100m, "USD"), meter.Limit);
    }

    [Fact]
    public void Spend_without_a_limit_has_no_ratio()
    {
        // The defining case: consumption Enterprise with no spend limit configured. There is no
        // denominator, so there must be no percentage and no gauge.
        var meter = Meter.Spend("claude.spend.month", "This month", new Money(32m, "USD"));

        Assert.False(meter.HasRatio);
        Assert.Null(meter.Ratio);
        Assert.Null(meter.Remaining);
        Assert.Equal(new Money(32m, "USD"), meter.Amount);
    }

    [Fact]
    public void Spend_over_its_limit_clamps_the_ratio_but_keeps_the_true_amount()
    {
        var meter = Meter.Spend(
            "claude.spend.month",
            "This month",
            new Money(150m, "USD"),
            new Money(100m, "USD"));

        Assert.Equal(1.0, meter.Ratio!.Value, precision: 10);
        Assert.Equal(150m, meter.Amount!.Value.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Spend_treats_a_non_positive_limit_as_no_denominator(decimal limit)
    {
        // A zero limit is an unknown denominator, not a full bar.
        var meter = Meter.Spend("s", "S", new Money(10m, "USD"), new Money(limit, "USD"));

        Assert.False(meter.HasRatio);
        Assert.Null(meter.Limit);
    }

    [Fact]
    public void Spend_drops_a_limit_in_a_different_currency()
    {
        var meter = Meter.Spend("s", "S", new Money(10m, "USD"), new Money(100m, "KRW"));

        Assert.False(meter.HasRatio);
        Assert.Null(meter.Limit);
    }

    [Fact]
    public void Spend_matches_currency_case_insensitively()
    {
        var meter = Meter.Spend("s", "S", new Money(25m, "usd"), new Money(100m, "USD"));

        Assert.Equal(0.25, meter.Ratio!.Value, precision: 10);
    }

    [Fact]
    public void Window_identity_is_the_reset_time_and_spend_identity_is_the_period_end()
    {
        var reset = DateTimeOffset.Parse("2026-07-30T18:00:00Z");
        var periodEnd = DateTimeOffset.Parse("2026-07-31T23:59:59Z");

        var window = Meter.Window("w", "W", 0.5, reset);
        var spend = Meter.Spend("s", "S", new Money(1m, "USD"), periodEnd: periodEnd);

        Assert.Equal(reset, window.WindowIdentity);
        Assert.Equal(periodEnd, spend.WindowIdentity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Meter_requires_a_stable_id(string id)
    {
        Assert.Throws<ArgumentException>(() => Meter.Window(id, "Label", 0.5));
    }

    [Fact]
    public void Meter_requires_a_label()
    {
        Assert.Throws<ArgumentException>(() => Meter.Window("id", " ", 0.5));
    }
}
