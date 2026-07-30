using QuotaBeacon.Core;

namespace QuotaBeacon.Providers;

/// <summary>
/// OpenAI Codex quota.
/// </summary>
/// <remarks>
/// This covers Codex only. General ChatGPT chat message limits are deliberately absent: OpenAI does
/// not expose a remaining count, so any figure shown for them would be an estimate presented as
/// fact. Enterprise workspaces with flexible pricing report credits, which map to a spend meter.
/// </remarks>
public sealed class CodexProvider(
    HttpClient httpClient,
    AuthChain authChain,
    IReadOnlyList<QuotaSource>? sources = null)
    : HttpQuotaProvider(httpClient, authChain, sources ?? DefaultSources)
{
    public const string PrimaryMeterId = "codex.primary";
    public const string SecondaryMeterId = "codex.secondary";
    public const string CreditMeterId = "codex.credits";

    public static IReadOnlyList<QuotaSource> DefaultSources { get; } =
    [
        new QuotaSource(
            "wham-usage",
            new Uri("https://chatgpt.com/backend-api/wham/usage"),
            Descriptors),
    ];

    private static IReadOnlyList<MeterDescriptor> Descriptors =>
    [
        new WindowMeterDescriptor(
            PrimaryMeterId,
            "5-hour limit",
            ["primary_window", "primary", "five_hour_limit", "five_hour"]),

        new WindowMeterDescriptor(
            SecondaryMeterId,
            "Weekly limit",
            ["secondary_window", "secondary", "weekly_limit", "weekly"]),

        new SpendMeterDescriptor(
            CreditMeterId,
            "Workspace credits",
            ["credits", "credit_balance", "workspace_credits", "balance"]),
    ];

    public override ProviderId Id => ProviderId.Codex;

    public override string DisplayName => "Codex";
}
