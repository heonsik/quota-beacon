using System.ComponentModel;
using QuotaBeacon.App.Theming;
using QuotaBeacon.Core;

namespace QuotaBeacon.App.ViewModels;

/// <summary>
/// The whole card: which tabs exist, which is selected, and what the footer says.
/// </summary>
/// <remarks>
/// Rebuilt wholesale on each refresh rather than mutated in place. The card is small and read for a
/// couple of seconds at a time, so a fresh immutable projection is simpler to reason about than
/// change tracking, and it makes an inconsistent intermediate state impossible.
/// </remarks>
public sealed class PopupViewModel : INotifyPropertyChanged
{
    private string _selectedTab;

    public PopupViewModel(IReadOnlyList<ProviderViewModel> providers, DateTimeOffset now)
    {
        Providers = providers;
        _selectedTab = DefaultTab(providers);
        FooterText = BuildFooter(providers, now);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ProviderViewModel> Providers { get; }

    /// <summary>The All tab only earns its place when there is more than one provider to compare.</summary>
    public bool ShowAllTab => Providers.Count > 1;

    public string FooterText { get; }

    /// <summary>Either "All" or a provider display name.</summary>
    public string SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value)
            {
                return;
            }

            _selectedTab = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTab)));
        }
    }

    /// <summary>
    /// Opens on the comparison when there is something to compare, and straight into the single
    /// provider's detail otherwise, so a Claude-only user never sees a one-row "All" tab.
    /// </summary>
    private static string DefaultTab(IReadOnlyList<ProviderViewModel> providers) =>
        providers.Count > 1 ? "All" : providers.FirstOrDefault()?.DisplayName ?? "All";

    private static string BuildFooter(IReadOnlyList<ProviderViewModel> providers, DateTimeOffset now)
    {
        if (providers.Count == 0)
        {
            return Localization.Current["Status.NoProviders"];
        }

        var withValues = providers.Where(p => p.HasValues).ToArray();

        if (withValues.Length == 0)
        {
            return Localization.Current["Status.WaitingFirst"];
        }

        // Stale is worth saying out loud, and the freshest timestamp is the honest one to report:
        // claiming the oldest would understate data the user can actually see.
        var stale = withValues.Where(p => p.IsStale).Select(p => p.DisplayName).ToArray();

        return stale.Length == withValues.Length
            ? Localization.Current.Format("Status.StaleAll", withValues[0].LastSuccessText)
            : stale.Length > 0
                ? Localization.Current.Format(
                    "Status.StaleSome",
                    withValues.First(p => !p.IsStale).LastSuccessText,
                    string.Join(", ", stale))
                : withValues.First(p => !p.IsStale).LastSuccessText;
    }
}
