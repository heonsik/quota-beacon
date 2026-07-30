using QuotaBeacon.App;

namespace QuotaBeacon.Tests;

public class PreviewLaunchOptionsTests
{
    [Theory]
    [InlineData("seat", PreviewScenario.Seat)]
    [InlineData("SPEND", PreviewScenario.Spend)]
    [InlineData("mixed", PreviewScenario.Mixed)]
    public void Parses_named_preview_scenarios(string value, PreviewScenario expected)
    {
        var result = PreviewLaunchOptions.Parse(["--preview", value]);

        Assert.True(result.IsPreview);
        Assert.Equal(expected, result.Scenario);
    }

    [Fact]
    public void Bare_preview_uses_the_seat_scenario()
    {
        var result = PreviewLaunchOptions.Parse(["--preview"]);

        Assert.True(result.IsPreview);
        Assert.Equal(PreviewScenario.Seat, result.Scenario);
    }

    [Fact]
    public void Normal_launch_is_not_a_preview()
    {
        var result = PreviewLaunchOptions.Parse([]);

        Assert.False(result.IsPreview);
    }

    [Fact]
    public void Unrelated_arguments_do_not_enable_preview()
    {
        var result = PreviewLaunchOptions.Parse(["--autostart"]);

        Assert.False(result.IsPreview);
    }
}
