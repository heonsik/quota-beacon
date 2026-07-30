using QuotaBeacon.Core;

namespace QuotaBeacon.Tests;

public class TrayStateResolverTests
{
    private static readonly DateTimeOffset Fetched = DateTimeOffset.Parse("2026-07-30T12:00:00Z");
    private static readonly AlertSettings Defaults = new();

    private static ProviderState State(
        ProviderId provider,
        bool isStale = false,
        params Meter[] meters) =>
        new(provider, meters, meters.Length > 0 ? Fetched : null, null, isStale);

    [Fact]
    public void No_providers_at_all_is_unavailable()
    {
        var state = TrayStateResolver.Resolve([], Defaults);

        Assert.True(state.IsUnavailable);
        Assert.Null(state.Representative);
    }

    [Fact]
    public void Providers_with_no_values_are_unavailable()
    {
        var states = new[]
        {
            new ProviderState(
                ProviderId.Claude,
                [],
                null,
                new ProviderError(ProviderErrorKind.AuthenticationMissing, "Sign in."),
                false),
        };

        Assert.True(TrayStateResolver.Resolve(states, Defaults).IsUnavailable);
    }

    [Fact]
    public void The_worst_meter_across_providers_becomes_the_representative()
    {
        var states = new[]
        {
            State(ProviderId.Claude, false, Meter.Window("claude.session5h", "5-hour", 0.30)),
            State(ProviderId.Codex, false, Meter.Window("codex.weekly", "Weekly", 0.94)),
        };

        var state = TrayStateResolver.Resolve(states, Defaults);

        Assert.Equal(AlertLevel.Critical, state.Level);
        Assert.Equal("codex.weekly", state.Representative!.Id);
    }

    [Fact]
    public void Within_a_level_the_least_remaining_meter_wins()
    {
        var states = new[]
        {
            State(
                ProviderId.Claude,
                false,
                Meter.Window("claude.weekly", "Weekly", 0.85),
                Meter.Window("claude.session5h", "5-hour", 0.88)),
        };

        var state = TrayStateResolver.Resolve(states, Defaults);

        Assert.Equal("claude.session5h", state.Representative!.Id);
    }

    [Fact]
    public void A_spend_meter_without_a_threshold_cannot_drive_the_icon()
    {
        // This is the honesty rule: a limitless spend account gets a neutral icon and real numbers
        // in the popup, never a fabricated gauge.
        var states = new[]
        {
            State(
                ProviderId.Claude,
                false,
                Meter.Spend("claude.spend.month", "This month", new Money(4_000m, "USD"))),
        };

        var state = TrayStateResolver.Resolve(states, Defaults);

        Assert.False(state.IsUnavailable);
        Assert.Null(state.Representative);
        Assert.Equal(AlertLevel.None, state.Level);
        Assert.Null(state.RemainingFraction);
    }

    [Fact]
    public void A_spend_meter_with_a_threshold_can_drive_the_icon()
    {
        var settings = new AlertSettings
        {
            SpendThresholds = new Dictionary<string, SpendAlertThreshold>
            {
                ["claude.spend.month"] = new(Critical: new Money(100m, "USD")),
            },
        };
        var states = new[]
        {
            State(
                ProviderId.Claude,
                false,
                Meter.Spend("claude.spend.month", "This month", new Money(250m, "USD"))),
        };

        var state = TrayStateResolver.Resolve(states, settings);

        Assert.Equal(AlertLevel.Critical, state.Level);
        Assert.Equal("claude.spend.month", state.Representative!.Id);
    }

    [Fact]
    public void A_ratio_bearing_meter_outranks_a_limitless_spend_meter_at_the_same_level()
    {
        var settings = new AlertSettings
        {
            SpendThresholds = new Dictionary<string, SpendAlertThreshold>
            {
                ["claude.spend.month"] = new(Critical: new Money(100m, "USD")),
            },
        };
        var states = new[]
        {
            State(
                ProviderId.Claude,
                false,
                Meter.Spend("claude.spend.month", "This month", new Money(250m, "USD")),
                Meter.Window("claude.session5h", "5-hour", 0.97)),
        };

        var state = TrayStateResolver.Resolve(states, settings);

        Assert.Equal("claude.session5h", state.Representative!.Id);
    }

    [Fact]
    public void Stale_is_reported_only_when_every_provider_with_values_is_stale()
    {
        var mixed = new[]
        {
            State(ProviderId.Claude, true, Meter.Window("claude.session5h", "5-hour", 0.1)),
            State(ProviderId.Codex, false, Meter.Window("codex.5h", "5-hour", 0.1)),
        };
        var allStale = new[]
        {
            State(ProviderId.Claude, true, Meter.Window("claude.session5h", "5-hour", 0.1)),
            State(ProviderId.Codex, true, Meter.Window("codex.5h", "5-hour", 0.1)),
        };

        Assert.False(TrayStateResolver.Resolve(mixed, Defaults).IsStale);
        Assert.True(TrayStateResolver.Resolve(allStale, Defaults).IsStale);
    }

    [Fact]
    public void A_provider_without_values_does_not_make_the_others_look_stale()
    {
        var states = new[]
        {
            State(ProviderId.Claude, false, Meter.Window("claude.session5h", "5-hour", 0.1)),
            State(ProviderId.Codex),
        };

        Assert.False(TrayStateResolver.Resolve(states, Defaults).IsStale);
    }

    [Fact]
    public void Remaining_fraction_follows_the_representative()
    {
        var states = new[]
        {
            State(ProviderId.Claude, false, Meter.Window("claude.session5h", "5-hour", 0.25)),
        };

        var state = TrayStateResolver.Resolve(states, Defaults);

        Assert.Equal(0.75, state.RemainingFraction!.Value, precision: 10);
    }
}
