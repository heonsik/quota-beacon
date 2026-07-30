using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaBeacon.App.Theming;
using QuotaBeacon.Core;

namespace QuotaBeacon.App.Services;

/// <summary>
/// User settings, persisted as JSON under <c>%LOCALAPPDATA%\QuotaBeacon</c>.
/// </summary>
/// <remarks>
/// No secret is stored here. The first release keeps no credential of its own: CLI tokens stay in the
/// vendor's file and the embedded sign-in keeps its cookies in its own WebView2 profile, so this file
/// is safe to read, diff, and back up.
/// </remarks>
public sealed record AppSettings
{
    public static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBeacon");

    private static readonly string FilePath = Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool ClaudeEnabled { get; init; } = true;

    public bool CodexEnabled { get; init; } = true;

    /// <summary>
    /// Minutes between refreshes. Floored at one minute: these are undocumented endpoints shared with
    /// the vendor's own clients, and polling harder than the data changes only invites throttling.
    /// </summary>
    public int RefreshMinutes { get; init; } = 5;

    public double WarningRemaining { get; init; } = 0.20;

    public double CriticalRemaining { get; init; } = 0.10;

    /// <summary>Absolute spend thresholds by meter id. Empty by default; see <see cref="SpendAlertThreshold"/>.</summary>
    public Dictionary<string, SpendThresholdSetting> SpendThresholds { get; init; } = [];

    public bool RunAtStartup { get; init; }

    /// <summary>Interface language. <see cref="AppLanguage.System"/> follows Windows.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AppLanguage>))]
    public AppLanguage Language { get; init; } = AppLanguage.System;

    /// <summary>Whether the card is kept on screen as an ordinary window rather than a tray popup.</summary>
    public bool Pinned { get; init; }

    /// <summary>Whether a pinned window stays above other windows. Off by default: always-on-top is
    /// useful for a glanceable widget but obtrusive when it is not wanted, and it cannot be
    /// discovered or escaped from the window itself.</summary>
    public bool PinnedAlwaysOnTop { get; init; }

    /// <summary>Where the user left the pinned window, in device-independent pixels.</summary>
    public double? PinnedLeft { get; init; }

    public double? PinnedTop { get; init; }

    [JsonIgnore]
    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(RefreshMinutes, 1, 120));

    [JsonIgnore]
    public IReadOnlyList<ProviderId> EnabledProviders =>
    [
        .. ClaudeEnabled ? new[] { ProviderId.Claude } : [],
        .. CodexEnabled ? new[] { ProviderId.Codex } : [],
    ];

    public AlertSettings ToAlertSettings() => new()
    {
        WarningRemaining = Math.Clamp(WarningRemaining, 0.01, 0.9),
        CriticalRemaining = Math.Clamp(CriticalRemaining, 0.01, 0.9),
        SpendThresholds = SpendThresholds.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToThreshold(),
            StringComparer.Ordinal),
    };

    public AppSettings Normalize()
    {
        var warning = Math.Clamp(WarningRemaining, 0.01, 0.90);
        var critical = Math.Clamp(CriticalRemaining, 0.01, warning);

        return this with
        {
            RefreshMinutes = Math.Clamp(RefreshMinutes, 1, 120),
            WarningRemaining = warning,
            CriticalRemaining = critical,
        };
    }

    /// <summary>
    /// Loads settings, falling back to defaults when the file is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// A corrupt settings file must not stop the app from starting: the worst outcome of ignoring it is
    /// that the user re-enters preferences, whereas a startup crash leaves them with no monitor at all.
    /// </remarks>
    public static AppSettings Load()
    {
        try
        {
            return (File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), SerializerOptions) ?? new AppSettings()
                : new AppSettings()).Normalize();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Normalize(), SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing a preference is not worth taking the app down for.
        }
    }
}

/// <summary>Serializable form of an absolute spend threshold.</summary>
public sealed record SpendThresholdSetting
{
    public decimal? Warning { get; init; }

    public decimal? Critical { get; init; }

    public string Currency { get; init; } = "USD";

    public SpendAlertThreshold ToThreshold() => new(
        Warning is { } warning ? new Money(warning, Currency) : null,
        Critical is { } critical ? new Money(critical, Currency) : null);
}
