using QuotaBeacon.App.Theming;
using QuotaBeacon.Core;

namespace QuotaBeacon.App.ViewModels;

/// <summary>
/// One provider's tab content.
/// </summary>
/// <remarks>
/// A provider is renderable in three distinct situations, and conflating them is what makes a status
/// UI untrustworthy: it has values, it has stale values plus a reason, or it has nothing and needs the
/// user to act. <see cref="NeedsSignIn"/> and <see cref="HasValues"/> keep those separate.
/// </remarks>
public sealed class ProviderViewModel
{
    public ProviderViewModel(
        string displayName,
        ProviderState state,
        AlertSettings settings,
        DateTimeOffset now)
    {
        DisplayName = displayName;
        IsStale = state.IsStale;

        Meters =
        [
            .. state.Meters
                .OrderByDescending(settings.LevelOf)
                .ThenBy(meter => meter.Remaining ?? double.PositiveInfinity)
                .Select(meter => new MeterViewModel(meter, settings.LevelOf(meter), now)),
        ];

        // The headline is the meter the user needs to act on, which is the worst one — the same rule
        // the tray icon uses, so the icon and the card never disagree.
        Headline = Meters.FirstOrDefault();

        NeedsSignIn = state.Error?.Kind
            is ProviderErrorKind.AuthenticationMissing
            or ProviderErrorKind.AuthenticationExpired;

        // Provider messages stay in English by design: they are diagnostics. "Not signed in" is the
        // exception, because it is the error users hit most and it reads as guidance rather than as a
        // diagnostic, so the UI phrases that one itself from the error kind.
        ErrorText = state.Error is null
            ? string.Empty
            : state.Error.Kind == ProviderErrorKind.AuthenticationMissing
                ? Localization.Current.Format("Error.NotSignedIn", displayName)
                : state.Error.Message;

        LastSuccessText = state.LastSuccessAt is { } at
            ? DescribeAge(now - at)
            : string.Empty;
    }

    public string DisplayName { get; }

    public IReadOnlyList<MeterViewModel> Meters { get; }

    public MeterViewModel? Headline { get; }

    public bool HasValues => Meters.Count > 0;

    public bool IsStale { get; }

    public string ErrorText { get; }

    public bool HasError => ErrorText.Length > 0;

    public bool NeedsSignIn { get; }

    public string LastSuccessText { get; }

    /// <summary>
    /// The remaining meters once the headline is taken, shown as detail rows beneath it.
    /// </summary>
    public IReadOnlyList<MeterViewModel> SupportingMeters => Meters.Count > 1 ? Meters.Skip(1).ToArray() : [];

    private static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromSeconds(45))
        {
            return Localization.Current["Status.UpdatedJustNow"];
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Localization.Current.Format("Status.UpdatedMinutes", (int)age.TotalMinutes);
        }

        return age < TimeSpan.FromDays(1)
            ? Localization.Current.Format("Status.UpdatedHours", (int)age.TotalHours)
            : Localization.Current.Format("Status.UpdatedDays", (int)age.TotalDays);
    }
}
