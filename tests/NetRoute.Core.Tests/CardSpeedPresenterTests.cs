namespace NetRoute.Core.Tests;

public class CardSpeedPresenterTests
{
    [Fact]
    public void A_busy_connection_shows_a_speed_and_an_idle_one_is_blank()
    {
        var labels = CardSpeedPresenter.Labels(new Dictionary<ConnectionId, long> { [ConnectionId.Lan] = 2_000_000, [ConnectionId.Wifi] = 0 });
        Assert.Equal(UsageReport.NowText(2_000_000), labels[ConnectionId.Lan]);
        Assert.Equal("", labels[ConnectionId.Wifi]);
        Assert.Equal("", labels[ConnectionId.Phone]); // absent from the rates
        Assert.Equal(Connections.All.OrderBy(i => i), labels.Keys.OrderBy(i => i));
    }
}
