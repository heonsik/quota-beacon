using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;

namespace QuotaBeacon.App.Theming;

public enum AppLanguage
{
    /// <summary>Follow the operating system's display language.</summary>
    System,

    English,

    Korean,
}

/// <summary>
/// Supplies translated strings and switches language without a restart.
/// </summary>
/// <remarks>
/// <para>
/// Exposed as an indexer so XAML can bind with
/// <c>{Binding [Some.Key], Source={x:Static theming:Localization.Current}}</c>. Changing the
/// language raises <see cref="INotifyPropertyChanged"/> for the indexer, which re-evaluates every
/// bound string in every open window. Nothing has to be rebuilt and no window is recreated.
/// </para>
/// <para>
/// Only interface text lives here. Provider error text and log output stay in English, which costs
/// nothing to maintain because that text is produced in a different assembly
/// (<c>QuotaBeacon.Providers</c>) and never passes through this class.
/// </para>
/// </remarks>
public sealed class Localization : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources =
        new("QuotaBeacon.App.Strings", typeof(Localization).Assembly);

    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    private Localization()
    {
    }

    public static Localization Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppLanguage Language { get; private set; } = AppLanguage.System;

    /// <summary>The culture used for both lookups and for formatting dates and numbers.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>
    /// The translated string for <paramref name="key"/>, or the key itself when it is missing.
    /// </summary>
    /// <remarks>
    /// Returning the key rather than throwing keeps a missing translation to a cosmetic defect: the
    /// window still opens and every other string still reads correctly.
    /// </remarks>
    public string this[string key] => Resources.GetString(key, _culture) ?? key;

    public void SetLanguage(AppLanguage language)
    {
        var culture = Resolve(language);

        if (Language == language && Equals(culture, _culture))
        {
            return;
        }

        Language = language;
        _culture = culture;

        // Dates and money are formatted against the same culture, so a Korean interface never shows
        // an English label beside a Korean-formatted date.
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;

        // Binding.IndexerName is the wildcard that invalidates every indexer binding at once.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    /// <summary>Looks up a format string and fills it in.</summary>
    public string Format(string key, params object?[] arguments) =>
        string.Format(_culture, this[key], arguments);

    private static CultureInfo Resolve(AppLanguage language) => language switch
    {
        AppLanguage.English => CultureInfo.GetCultureInfo("en"),
        AppLanguage.Korean => CultureInfo.GetCultureInfo("ko"),
        // Anything that is not Korean falls back to the neutral English resources, since those are
        // the only two sets that exist.
        _ => IsKorean(CultureInfo.InstalledUICulture)
            ? CultureInfo.GetCultureInfo("ko")
            : CultureInfo.GetCultureInfo("en"),
    };

    private static bool IsKorean(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase);
}
