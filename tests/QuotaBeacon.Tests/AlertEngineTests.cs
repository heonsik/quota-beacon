using QuotaBeacon.Core;

namespace QuotaBeacon.Tests;

public class AlertEngineTests
{
    private static readonly DateTimeOffset Reset = DateTimeOffset.Parse("2026-07-30T18:00:00Z");

    private static ProviderState State(params Meter[] meters) =>
        new(ProviderId.Claude, meters, DateTimeOffset.Parse("2026-07-30T12:00:00Z"), null, false);

    private static Meter Window(double remaining, DateTimeOffset? resetsAt = null) =>
        Meter.Window("claude.session5h", "5-hour", 1d - remaining, resetsAt ?? Reset);

    [Fact]
    public void No_alert_while_remaining_is_healthy()
    {
        var engine = new AlertEngine(new AlertSettings());

        Assert.Empty(engine.Evaluate([State(Window(remaining: 0.55))]));
    }

    [Fact]
    public void Warning_fires_once_and_stays_silent_on_repeated_refreshes()
    {
        var engine = new AlertEngine(new AlertSettings());

        var first = engine.Evaluate([State(Window(remaining: 0.15))]);
        var second = engine.Evaluate([State(Window(remaining: 0.14))]);
        var third = engine.Evaluate([State(Window(remaining: 0.13))]);

        Assert.Equal(AlertLevel.Warning, Assert.Single(first).Level);
        Assert.Empty(second);
        Assert.Empty(third);
    }

    [Fact]
    public void Critical_fires_after_a_warning_has_already_fired()
    {
        var engine = new AlertEngine(new AlertSettings());

        engine.Evaluate([State(Window(remaining: 0.15))]);
        var escalation = engine.Evaluate([State(Window(remaining: 0.05))]);

        Assert.Equal(AlertLevel.Critical, Assert.Single(escalation).Level);
    }

    [Fact]
    public void Dropping_straight_to_critical_reports_critical_only()
    {
        var engine = new AlertEngine(new AlertSettings());

        var alerts = engine.Evaluate([State(Window(remaining: 0.02))]);

        Assert.Equal(AlertLevel.Critical, Assert.Single(alerts).Level);
    }

    [Fact]
    public void Easing_from_critical_back_into_the_warning_band_is_silent()
    {
        // Recovering is not a new problem; announcing a warning here would report a worsening
        // that did not happen.
        var engine = new AlertEngine(new AlertSettings());
        engine.Evaluate([State(Window(remaining: 0.02))]);

        Assert.Empty(engine.Evaluate([State(Window(remaining: 0.15))]));
    }

    [Fact]
    public void Falling_back_into_critical_after_recovering_alerts_again()
    {
        var engine = new AlertEngine(new AlertSettings());
        engine.Evaluate([State(Window(remaining: 0.02))]);
        engine.Evaluate([State(Window(remaining: 0.15))]);

        var again = engine.Evaluate([State(Window(remaining: 0.03))]);

        Assert.Equal(AlertLevel.Critical, Assert.Single(again).Level);
    }

    [Fact]
    public void Full_recovery_re_arms_the_warning()
    {
        var engine = new AlertEngine(new AlertSettings());
        engine.Evaluate([State(Window(remaining: 0.15))]);
        engine.Evaluate([State(Window(remaining: 0.80))]);

        var again = engine.Evaluate([State(Window(remaining: 0.12))]);

        Assert.Equal(AlertLevel.Warning, Assert.Single(again).Level);
    }

    [Fact]
    public void Window_rollover_re_arms_the_same_meter()
    {
        // The latch is keyed on reset time, so a new window alerts without any extra bookkeeping.
        var engine = new AlertEngine(new AlertSettings());
        engine.Evaluate([State(Window(remaining: 0.05, Reset))]);

        var nextWindow = engine.Evaluate([State(Window(remaining: 0.05, Reset.AddHours(5)))]);

        Assert.Equal(AlertLevel.Critical, Assert.Single(nextWindow).Level);
    }

    [Fact]
    public void A_spend_meter_without_a_configured_threshold_never_alerts()
    {
        // No defensible default exists for absolute spend, so silence is correct.
        var engine = new AlertEngine(new AlertSettings());
        var meter = Meter.Spend("claude.spend.month", "This month", new Money(9_999m, "USD"));

        Assert.Empty(engine.Evaluate([State(meter)]));
    }

    [Fact]
    public void A_spend_meter_alerts_once_a_threshold_is_configured()
    {
        var settings = new AlertSettings
        {
            SpendThresholds = new Dictionary<string, SpendAlertThreshold>
            {
                ["claude.spend.month"] = new(
                    Warning: new Money(50m, "USD"),
                    Critical: new Money(80m, "USD")),
            },
        };
        var engine = new AlertEngine(settings);

        var warning = engine.Evaluate(
            [State(Meter.Spend("claude.spend.month", "This month", new Money(55m, "USD")))]);
        var critical = engine.Evaluate(
            [State(Meter.Spend("claude.spend.month", "This month", new Money(95m, "USD")))]);

        Assert.Equal(AlertLevel.Warning, Assert.Single(warning).Level);
        Assert.Equal(AlertLevel.Critical, Assert.Single(critical).Level);
    }

    [Fact]
    public void A_spend_threshold_in_another_currency_is_ignored()
    {
        var settings = new AlertSettings
        {
            SpendThresholds = new Dictionary<string, SpendAlertThreshold>
            {
                ["s"] = new(Warning: new Money(10m, "KRW")),
            },
        };
        var engine = new AlertEngine(settings);

        Assert.Empty(engine.Evaluate([State(Meter.Spend("s", "S", new Money(500m, "USD")))]));
    }

    [Fact]
    public void Each_meter_latches_independently()
    {
        var engine = new AlertEngine(new AlertSettings());

        var alerts = engine.Evaluate(
        [
            State(
                Meter.Window("claude.session5h", "5-hour", 0.95, Reset),
                Meter.Window("claude.weekly", "Weekly", 0.85, Reset.AddDays(3))),
        ]);

        Assert.Equal(2, alerts.Count);
        Assert.Equal(AlertLevel.Critical, alerts.Single(a => a.Meter.Id == "claude.session5h").Level);
        Assert.Equal(AlertLevel.Warning, alerts.Single(a => a.Meter.Id == "claude.weekly").Level);
    }

    [Fact]
    public void Custom_thresholds_are_respected()
    {
        var engine = new AlertEngine(new AlertSettings
        {
            WarningRemaining = 0.50,
            CriticalRemaining = 0.40,
        });

        var alerts = engine.Evaluate([State(Window(remaining: 0.45))]);

        Assert.Equal(AlertLevel.Warning, Assert.Single(alerts).Level);
    }
}
