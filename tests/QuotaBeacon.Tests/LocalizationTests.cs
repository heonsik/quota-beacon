using QuotaBeacon.App.Theming;

namespace QuotaBeacon.Tests;

/// <summary>
/// Guards the translation set itself.
/// </summary>
/// <remarks>
/// A missing translation is silent at runtime — the indexer falls back to the key — so the only way
/// a gap gets noticed is a test that looks for it.
/// </remarks>
public class LocalizationTests : IDisposable
{
    /// <summary>Every key the application looks up. Add here when adding a string.</summary>
    private static readonly string[] Keys =
    [
        "Tab.All", "Action.Refresh", "Action.SignIn", "Action.SignOut", "Action.Save", "Action.Cancel",
        "Tooltip.Settings", "Tooltip.Pin", "Tooltip.Unpin", "Tooltip.Minimize", "Tooltip.Close",
        "Tray.Open", "Tray.RefreshNow", "Tray.Settings", "Tray.Quit", "Tray.NotReporting",
        "Tray.Left", "Tray.Stale", "Alert.LowTitle", "Alert.CriticalTitle",
        "Level.Low", "Level.Critical",
        "Meter.Window5h", "Meter.WindowWeekly", "Meter.BillingPeriod", "Meter.Credits",
        "Meter.NoSpendLimit", "Meter.LimitOf", "Meter.Resetting", "Meter.ResetsInDays",
        "Meter.ResetsInHours", "Meter.ResetsInMinutes", "Meter.ResetsUnderMinute", "Meter.Through",
        "Status.UpdatedJustNow", "Status.UpdatedMinutes", "Status.UpdatedHours", "Status.UpdatedDays",
        "Status.NoProviders", "Status.WaitingFirst", "Status.StaleAll", "Status.StaleSome",
        "Error.NotSignedIn",
        "Settings.Title", "Settings.Providers", "Settings.WebSessionAvailable", "Settings.NoWebSession",
        "Settings.RefreshInterval", "Settings.WarningAt", "Settings.CriticalAt", "Settings.RunAtStartup",
        "Settings.Language", "Settings.LanguageSystem", "Settings.AlwaysOnTop", "Settings.Author",
        "Validation.Numeric", "Validation.Range", "Validation.OneProvider", "Validation.SessionBusy",
        "SignIn.Title", "SignIn.Explanation",
        "Dialog.RestartPrompt", "Dialog.StartupFailed", "Dialog.MonitoringStopped",
    ];

    public void Dispose() => Localization.Current.SetLanguage(AppLanguage.System);

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Korean)]
    public void Every_key_resolves_in_every_language(AppLanguage language)
    {
        Localization.Current.SetLanguage(language);

        // The indexer returns the key itself when a lookup fails, so a value equal to its key is the
        // signal that a translation is missing.
        var missing = Keys.Where(key => Localization.Current[key] == key).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Korean_and_English_differ()
    {
        // Catches the satellite assembly being stripped from the build, which would leave Korean
        // silently falling back to English everywhere.
        Localization.Current.SetLanguage(AppLanguage.English);
        var english = Localization.Current["Tab.All"];

        Localization.Current.SetLanguage(AppLanguage.Korean);
        var korean = Localization.Current["Tab.All"];

        Assert.NotEqual(english, korean);
    }

    [Fact]
    public void The_author_credit_names_the_contact()
    {
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Korean })
        {
            Localization.Current.SetLanguage(language);

            Assert.Contains("heonsik.lim", Localization.Current["Settings.Author"]);
        }
    }

    [Fact]
    public void Formatting_fills_placeholders()
    {
        Localization.Current.SetLanguage(AppLanguage.English);

        Assert.Equal("Resets in 2h 18m", Localization.Current.Format("Meter.ResetsInHours", 2, 18));
    }

    [Fact]
    public void Culture_follows_the_selected_language()
    {
        Localization.Current.SetLanguage(AppLanguage.Korean);
        Assert.Equal("ko", Localization.Current.Culture.TwoLetterISOLanguageName);

        Localization.Current.SetLanguage(AppLanguage.English);
        Assert.Equal("en", Localization.Current.Culture.TwoLetterISOLanguageName);
    }
}
