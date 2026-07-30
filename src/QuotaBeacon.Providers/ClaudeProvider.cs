using QuotaBeacon.Core;

namespace QuotaBeacon.Providers;

/// <summary>
/// Claude quota, covering both seat-based rolling windows and consumption-based spend.
/// </summary>
/// <remarks>
/// Claude rate limits are shared across claude.ai, Claude Code, and the IDE extensions, so a single
/// figure is meaningful regardless of which surface the user works in.
/// </remarks>
public sealed class ClaudeProvider(
    HttpClient httpClient,
    AuthChain authChain,
    IReadOnlyList<QuotaSource>? sources = null)
    : HttpQuotaProvider(httpClient, authChain, sources ?? DefaultSources)
{
    public const string SessionMeterId = "claude.session5h";
    public const string WeeklyMeterId = "claude.weekly";
    public const string SpendMeterId = "claude.spend.period";

    /// <summary>
    /// Candidate endpoints in preference order: the seat-based usage endpoint first, since it is the
    /// common case and cheap to read, then the organization usage endpoint that consumption accounts
    /// report spend through.
    /// </summary>
    public static IReadOnlyList<QuotaSource> DefaultSources { get; } =
    [
        new QuotaSource(
            "oauth-usage",
            new Uri("https://api.anthropic.com/api/oauth/usage"),
            SeatDescriptors,
            [AuthSourceKind.Cli]),

        new QuotaSource(
            "claude-ai-usage",
            new Uri("https://claude.ai/api/usage"),
            [.. SeatDescriptors, .. SpendDescriptors],
            [AuthSourceKind.Web]),
    ];

    private static IReadOnlyList<MeterDescriptor> SeatDescriptors =>
    [
        new WindowMeterDescriptor(
            SessionMeterId,
            "5-hour limit",
            ["five_hour", "five_hour_limit", "session", "primary_window", "primary"]),

        new WindowMeterDescriptor(
            WeeklyMeterId,
            "Weekly limit",
            ["seven_day", "weekly", "weekly_limit", "secondary_window", "secondary"]),
    ];

    private static IReadOnlyList<MeterDescriptor> SpendDescriptors =>
    [
        new SpendMeterDescriptor(
            SpendMeterId,
            "This billing period",
            ["spend", "usage_cost", "current_period", "billing_period", "cost"]),
    ];

    public override ProviderId Id => ProviderId.Claude;

    public override string DisplayName => "Claude";
}
