using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using QuotaBeacon.App.Services;
using QuotaBeacon.App.Theming;
using QuotaBeacon.Core;
using Loc = QuotaBeacon.App.Theming.Localization;

namespace QuotaBeacon.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly WebViewSessionStore _sessions;
    private readonly AppLanguage _languageOnOpen;

    private bool _loaded;

    public SettingsWindow(AppSettings settings, WebViewSessionStore sessions)
    {
        _original = settings;
        _sessions = sessions;
        _languageOnOpen = settings.Language;

        InitializeComponent();

        ClaudeEnabled.IsChecked = settings.ClaudeEnabled;
        CodexEnabled.IsChecked = settings.CodexEnabled;
        RefreshMinutes.Text = settings.RefreshMinutes.ToString(CultureInfo.InvariantCulture);
        WarningPercent.Text = (settings.WarningRemaining * 100).ToString("0", CultureInfo.InvariantCulture);
        CriticalPercent.Text = (settings.CriticalRemaining * 100).ToString("0", CultureInfo.InvariantCulture);
        RunAtStartup.IsChecked = settings.RunAtStartup;
        AlwaysOnTop.IsChecked = settings.PinnedAlwaysOnTop;

        BuildLanguageChoices(settings.Language);
        RefreshSessionStatus();

        _loaded = true;
    }

    public AppSettings? Result { get; private set; }

    private void BuildLanguageChoices(AppLanguage selected)
    {
        // Language names are shown in their own language, which is the one label a user cannot read
        // in the wrong language.
        LanguageChoice.Items.Add(new LanguageOption(AppLanguage.System, Loc.Current["Settings.LanguageSystem"]));
        LanguageChoice.Items.Add(new LanguageOption(AppLanguage.English, "English"));
        LanguageChoice.Items.Add(new LanguageOption(AppLanguage.Korean, "한국어"));

        LanguageChoice.SelectedItem = LanguageChoice.Items
            .Cast<LanguageOption>()
            .First(option => option.Language == selected);
    }

    /// <summary>
    /// Applies the language immediately so the effect of the choice is visible in this very window.
    /// </summary>
    /// <remarks>
    /// If the user cancels, the language is put back: a discarded dialog must not leave a setting
    /// applied.
    /// </remarks>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || LanguageChoice.SelectedItem is not LanguageOption option)
        {
            return;
        }

        Loc.Current.SetLanguage(option.Language);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (Result is null)
        {
            Loc.Current.SetLanguage(_languageOnOpen);
        }
    }

    private void OnClaudeSignIn(object sender, RoutedEventArgs e) => SignIn(ProviderId.Claude, "Claude");

    private void OnCodexSignIn(object sender, RoutedEventArgs e) => SignIn(ProviderId.Codex, "Codex");

    private void OnClaudeSignOut(object sender, RoutedEventArgs e) => SignOut(ProviderId.Claude);

    private void OnCodexSignOut(object sender, RoutedEventArgs e) => SignOut(ProviderId.Codex);

    private void SignIn(ProviderId provider, string displayName)
    {
        new WebSignInWindow(provider, displayName) { Owner = this }.ShowDialog();
        RefreshSessionStatus();
    }

    private void SignOut(ProviderId provider)
    {
        if (!_sessions.TrySignOut(provider))
        {
            ValidationMessage.Text = Loc.Current["Validation.SessionBusy"];
            return;
        }

        ValidationMessage.Text = string.Empty;
        RefreshSessionStatus();
    }

    private void RefreshSessionStatus()
    {
        ClaudeSession.Text = Describe(ProviderId.Claude);
        CodexSession.Text = Describe(ProviderId.Codex);

        static string Describe(ProviderId provider) => WebViewSessionStore.HasProfile(provider)
            ? Loc.Current["Settings.WebSessionAvailable"]
            : Loc.Current["Settings.NoWebSession"];
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RefreshMinutes.Text, out var refresh)
            || !double.TryParse(WarningPercent.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var warning)
            || !double.TryParse(CriticalPercent.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var critical))
        {
            ValidationMessage.Text = Loc.Current["Validation.Numeric"];
            return;
        }

        if (refresh is < 1 or > 120 || warning is < 1 or > 90 || critical is < 1 || critical > warning)
        {
            ValidationMessage.Text = Loc.Current["Validation.Range"];
            return;
        }

        if (ClaudeEnabled.IsChecked != true && CodexEnabled.IsChecked != true)
        {
            ValidationMessage.Text = Loc.Current["Validation.OneProvider"];
            return;
        }

        Result = _original with
        {
            ClaudeEnabled = ClaudeEnabled.IsChecked == true,
            CodexEnabled = CodexEnabled.IsChecked == true,
            RefreshMinutes = refresh,
            WarningRemaining = warning / 100,
            CriticalRemaining = critical / 100,
            RunAtStartup = RunAtStartup.IsChecked == true,
            PinnedAlwaysOnTop = AlwaysOnTop.IsChecked == true,
            Language = (LanguageChoice.SelectedItem as LanguageOption)?.Language ?? AppLanguage.System,
        };

        DialogResult = true;
    }

    private sealed record LanguageOption(AppLanguage Language, string Display)
    {
        public override string ToString() => Display;
    }
}
