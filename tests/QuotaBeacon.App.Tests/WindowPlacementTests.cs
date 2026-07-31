using QuotaBeacon.App.Services;

namespace QuotaBeacon.App.Tests;

public class WindowPlacementTests
{
    private static readonly PlacementRect Primary = new(0, 0, 2560, 1392);
    private static readonly PlacementRect Secondary = new(2560, 0, 1920, 1080);

    private static PlacementRect Card(double left, double top) => new(left, top, 430, 350);

    [Fact]
    public void A_window_well_inside_a_display_is_usable()
    {
        Assert.True(WindowPlacement.IsUsable(Card(400, 300), [Primary]));
    }

    [Fact]
    public void A_window_on_a_display_that_is_gone_is_not_usable()
    {
        // The saved position referred to a second monitor that has since been unplugged. Restoring
        // there would put a title-bar-less window somewhere the user cannot drag it back from.
        Assert.False(WindowPlacement.IsUsable(Card(3000, 400), [Primary]));
    }

    [Fact]
    public void The_same_position_is_usable_again_once_that_display_returns()
    {
        Assert.True(WindowPlacement.IsUsable(Card(3000, 400), [Primary, Secondary]));
    }

    [Fact]
    public void A_window_nudged_slightly_off_an_edge_is_kept()
    {
        // Deliberate placement, not a stale coordinate: most of the card is still on screen.
        Assert.True(WindowPlacement.IsUsable(Card(-100, 300), [Primary]));
    }

    [Fact]
    public void A_window_mostly_off_an_edge_is_rejected()
    {
        Assert.False(WindowPlacement.IsUsable(Card(-330, 300), [Primary]));
    }

    [Fact]
    public void A_window_below_the_work_area_is_rejected()
    {
        // Covers a resolution change that leaves the old position under the taskbar.
        Assert.False(WindowPlacement.IsUsable(Card(400, 1380), [Primary]));
    }

    [Fact]
    public void Straddling_two_displays_counts_against_each_display_separately()
    {
        // 35% on the primary and 65% on the secondary. Reachable while both are attached, but not
        // once the secondary goes away — which is why overlap is judged per display rather than
        // summed across all of them.
        var straddling = Card(2560 - 150, 300);

        Assert.True(WindowPlacement.IsUsable(straddling, [Primary, Secondary]));
        Assert.False(WindowPlacement.IsUsable(straddling, [Primary]));
    }

    [Fact]
    public void No_displays_means_no_usable_position()
    {
        Assert.False(WindowPlacement.IsUsable(Card(0, 0), []));
    }

    [Theory]
    [InlineData(0, 350)]
    [InlineData(430, 0)]
    public void A_window_with_no_area_is_rejected(double width, double height)
    {
        Assert.False(WindowPlacement.IsUsable(new PlacementRect(10, 10, width, height), [Primary]));
    }
}
