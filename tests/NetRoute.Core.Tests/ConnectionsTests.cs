namespace NetRoute.Core.Tests;

public class ConnectionsTests
{
    [Fact]
    public void Orders_are_fixed()
    {
        Assert.Equal([ConnectionId.Lan, ConnectionId.Wifi, ConnectionId.Phone], Connections.FallbackOrder);
        Assert.Equal([ConnectionId.Phone, ConnectionId.Lan, ConnectionId.Wifi], Connections.CardOrder);
    }

    [Theory, MemberData(nameof(Every))]
    public void Every_connection_round_trips_through_mode_and_path(ConnectionId id)
    {
        Assert.Equal(id, Connections.Of(Connections.ModeOf(id)));
        Assert.Equal(id, Connections.Of(Connections.PathOf(id)));
    }

    [Fact]
    public void Only_the_phone_is_metered() =>
        Assert.Equal([ConnectionId.Phone], Connections.All.Where(Connections.IsMetered));

    [Fact]
    public void Auto_none_and_other_map_to_no_connection()
    {
        Assert.Null(Connections.Of(RoutingMode.Auto));
        Assert.Null(Connections.Of(InternetPath.None));
        Assert.Null(Connections.Of(InternetPath.Other));
    }

    [Fact]
    public void Labels() =>
        Assert.Equal(["Phone", "LAN", "Wi-Fi"], Connections.CardOrder.Select(Connections.Label));

    public static TheoryData<ConnectionId> Every => new(Enum.GetValues<ConnectionId>());
}
