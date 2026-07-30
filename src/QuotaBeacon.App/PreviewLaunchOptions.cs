namespace QuotaBeacon.App;

public sealed record PreviewLaunchOptions(bool IsPreview, PreviewScenario Scenario)
{
    public static PreviewLaunchOptions Parse(IReadOnlyList<string> args)
    {
        var index = -1;

        for (var position = 0; position < args.Count; position++)
        {
            if (args[position].Equals("--preview", StringComparison.OrdinalIgnoreCase))
            {
                index = position;
                break;
            }
        }

        if (args.Count == 0 || index < 0 || index >= args.Count)
        {
            return new PreviewLaunchOptions(false, PreviewScenario.Seat);
        }

        var scenario = index + 1 < args.Count
            ? args[index + 1].ToLowerInvariant() switch
            {
                "spend" => PreviewScenario.Spend,
                "mixed" => PreviewScenario.Mixed,
                "settings" => PreviewScenario.Settings,
                _ => PreviewScenario.Seat,
            }
            : PreviewScenario.Seat;

        return new PreviewLaunchOptions(true, scenario);
    }
}
