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
    /// <summary>
    /// Builds the candidate list, given how web requests should be issued.
    /// </summary>
    /// <remarks>
    /// The web endpoint needs a response source because claude.ai refuses non-browser callers, and it
    /// needs a resolver because usage is scoped to an organization that has to be discovered first.
    /// </remarks>
    public static IReadOnlyList<QuotaSource> BuildSources(IResponseSource browser) =>
    [
        new QuotaSource(
            "oauth-usage",
            new Uri("https://api.anthropic.com/api/oauth/usage"),
            SeatDescriptors,
            [AuthSourceKind.Cli]),

        new QuotaSource(
            "claude-ai-usage",
            new Uri("https://claude.ai/api/organizations"),
            [.. SeatDescriptors, .. SpendDescriptors],
            [AuthSourceKind.Web],
            new ClaudeOrganizationResolver(browser),
            browser),
    ];

    /// <summary>
    /// Candidates available without a browser: the CLI endpoint only.
    /// </summary>
    /// <remarks>
    /// The web endpoint is deliberately absent. It cannot work without a response source that issues
    /// the request from the signed-in browser, so offering it here would only produce 403s. Callers
    /// that have a browser use <see cref="BuildSources"/>.
    /// </remarks>
    public static IReadOnlyList<QuotaSource> DefaultSources { get; } =
    [
        new QuotaSource(
            "oauth-usage",
            new Uri("https://api.anthropic.com/api/oauth/usage"),
            SeatDescriptors,
            [AuthSourceKind.Cli]),
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
