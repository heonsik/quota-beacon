using QuotaBeacon.App.ViewModels;
using QuotaBeacon.Core;

namespace QuotaBeacon.App;

/// <summary>
/// Representative states for the <c>--preview</c> design mode.
/// </summary>
/// <remarks>
/// The visual work is judged against the cases that are actually hard: a healthy window, a critical
/// window, and a spend meter with no denominator that must render without a gauge. Reviewing the card
/// against a single happy path is how the awkward states end up unhandled.
/// </remarks>
internal static class SampleData
{
    public static PopupViewModel Build(PreviewScenario scenario, DateTimeOffset now)
    {
        var settings = new AlertSettings();

        return new PopupViewModel(
            [.. Providers(scenario, now).Select(state =>
                new ProviderViewModel(state.Provider.ToString(), state, settings, now))],
            now);
    }

    public static TrayState TrayFor(PreviewScenario scenario, DateTimeOffset now) =>
        TrayStateResolver.Resolve([.. Providers(scenario, now)], new AlertSettings());

    private static IEnumerable<ProviderState> Providers(PreviewScenario scenario, DateTimeOffset now) =>
        scenario switch
        {
            PreviewScenario.Seat =>
            [
                State(ProviderId.Claude, now,
                    Meter.Window("claude.session5h", "5-hour limit", 0.32, now.AddHours(2).AddMinutes(18)),
                    Meter.Window("claude.weekly", "Weekly limit", 0.19, now.AddDays(3))),
                State(ProviderId.Codex, now,
                    Meter.Window("codex.primary", "5-hour limit", 0.76, now.AddMinutes(43)),
                    Meter.Window("codex.secondary", "Weekly limit", 0.88, now.AddDays(2))),
            ],

            PreviewScenario.Spend =>
            [
                State(ProviderId.Claude, now,
                    Meter.Spend(
                        "claude.spend.period",
                        "This billing period",
                        new Money(132.50m, "USD"),
                        periodStart: new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset),
                        periodEnd: new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(1).AddDays(-1))),
                State(ProviderId.Codex, now,
                    Meter.Spend(
                        "codex.credits",
                        "Workspace credits",
                        new Money(240.75m, "USD"),
                        new Money(1000m, "USD"))),
            ],

            PreviewScenario.Mixed =>
            [
                State(ProviderId.Claude, now,
                    Meter.Window("claude.session5h", "5-hour limit", 0.94, now.AddMinutes(37)),
                    Meter.Spend("claude.spend.period", "This billing period", new Money(1_284.00m, "USD"))),
                new ProviderState(
                    ProviderId.Codex,
                    [],
                    null,
                    new ProviderError(
                        ProviderErrorKind.AuthenticationMissing,
                        "Not signed in to Codex. Sign in through settings, or use its CLI."),
                    IsStale: false),
            ],

            _ => [],
        };

    private static ProviderState State(ProviderId provider, DateTimeOffset now, params Meter[] meters) =>
        new(provider, meters, now.AddSeconds(-12), null, IsStale: false);
}

public enum PreviewScenario
{
    /// <summary>Seat-based windows: percentages, gauges, and reset countdowns.</summary>
    Seat,

    /// <summary>Consumption spend, one with a limit and one without.</summary>
    Spend,

    /// <summary>The settings window, so its chrome can be reviewed like every other surface.</summary>
    Settings,

    /// <summary>A critical window beside a limitless spend meter, plus a provider needing sign-in.</summary>
    Mixed,
}
