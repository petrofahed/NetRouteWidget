namespace NetRoute.Core.Tests;

public class CardTotalsPresenterTests
{
    [Fact]
    public void A_connection_with_bytes_gets_a_human_label()
    {
        var labels = CardTotalsPresenter.Labels(id => id == ConnectionId.Lan ? new Traffic(1_000_000, 2_000_000) : default);
        Assert.Equal(ByteFormat.Human(3_000_000), labels[ConnectionId.Lan]);
    }

    [Fact]
    public void A_connection_with_no_bytes_is_blank()
    {
        var labels = CardTotalsPresenter.Labels(id => id == ConnectionId.Lan ? new Traffic(1_000_000, 2_000_000) : default);
        Assert.Equal("", labels[ConnectionId.Phone]);
        Assert.Equal("", labels[ConnectionId.Wifi]);
    }

    [Fact]
    public void Every_connection_has_a_label_entry()
    {
        var labels = CardTotalsPresenter.Labels(_ => default);
        foreach (var id in Connections.All) Assert.True(labels.ContainsKey(id), $"{id} has no label entry");
        Assert.Equal(Connections.All.Count, labels.Count);
    }

    [Fact]
    public void Wifi_bytes_show_on_the_wifi_label()
    {
        var labels = CardTotalsPresenter.Labels(id => id == ConnectionId.Wifi ? new Traffic(0, 5_000_000) : default);
        Assert.Equal(ByteFormat.Human(5_000_000), labels[ConnectionId.Wifi]);
        Assert.Equal("", labels[ConnectionId.Phone]);
        Assert.Equal("", labels[ConnectionId.Lan]);
    }
}
