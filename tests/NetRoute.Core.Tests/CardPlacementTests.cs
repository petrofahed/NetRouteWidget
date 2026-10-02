namespace NetRoute.Core.Tests;

public class CardPlacementTests
{
    static readonly Bounds Screen = new(0, 0, 1920, 1080);
    static readonly Bounds Work = new(0, 0, 1920, 1040);

    [Fact]
    public void No_saved_position_goes_bottom_right_of_work_area()
    {
        Assert.Equal((1614d, 874d), CardPlacement.Resolve(null, null, 290, 150, Screen, Work));
    }

    [Fact]
    public void Saved_position_on_screen_is_kept()
    {
        Assert.Equal((100d, 200d), CardPlacement.Resolve(100, 200, 290, 150, Screen, Work));
    }

    [Fact]
    public void Position_on_an_unplugged_monitor_falls_back_to_bottom_right()
    {
        Assert.Equal((1614d, 874d), CardPlacement.Resolve(2500, 300, 290, 150, Screen, Work));
    }

    [Fact]
    public void Partially_off_screen_position_falls_back_to_bottom_right()
    {
        Assert.Equal((1614d, 874d), CardPlacement.Resolve(1800, 1000, 290, 150, Screen, Work));
    }

    [Fact]
    public void Position_on_a_monitor_left_of_the_primary_is_kept()
    {
        var twoMonitors = new Bounds(-1920, 0, 3840, 1080);

        Assert.Equal((-1500d, 100d), CardPlacement.Resolve(-1500, 100, 290, 150, twoMonitors, Work));
    }

    // ---- the waiting popup must not cover the card ----

    [Fact]
    public void Popup_sits_above_the_visible_card_aligned_to_its_left()
    {
        var card = new Bounds(1614, 874, 290, 150);

        Assert.Equal((1540d, 874d - 120 - 8), CardPlacement.PopupAboveCard(card, 380, 120, Work)); // clamped: 1614+380 > 1920
        Assert.Equal((100d, 600d - 120 - 8), CardPlacement.PopupAboveCard(new Bounds(100, 600, 290, 150), 380, 120, Work));
    }

    [Fact]
    public void Popup_falls_back_to_bottom_right_when_the_card_is_hidden_or_there_is_no_room_above()
    {
        Assert.Equal((1920d - 380 - 16, 1040d - 120 - 16), CardPlacement.PopupAboveCard(null, 380, 120, Work));
        Assert.Equal((1920d - 380 - 16, 1040d - 120 - 16), CardPlacement.PopupAboveCard(new Bounds(100, 50, 290, 150), 380, 120, Work));
    }
}
