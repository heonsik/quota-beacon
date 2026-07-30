using QuotaBeacon.App.Services;

namespace QuotaBeacon.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Normalize_clamps_refresh_interval()
    {
        Assert.Equal(1, new AppSettings { RefreshMinutes = 0 }.Normalize().RefreshMinutes);
        Assert.Equal(120, new AppSettings { RefreshMinutes = 500 }.Normalize().RefreshMinutes);
    }

    [Fact]
    public void Normalize_keeps_critical_threshold_at_or_below_warning()
    {
        var settings = new AppSettings
        {
            WarningRemaining = 0.10,
            CriticalRemaining = 0.25,
        }.Normalize();

        Assert.Equal(0.10, settings.WarningRemaining, precision: 6);
        Assert.Equal(0.10, settings.CriticalRemaining, precision: 6);
    }
}
