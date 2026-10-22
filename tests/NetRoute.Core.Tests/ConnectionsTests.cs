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

    [Theory]
    [InlineData("192.168.86.42", 24, "192.168.86.0/24")]
    [InlineData("10.5.0.2", 8, "10.0.0.0/8")]
    [InlineData("172.19.0.1", 30, "172.19.0.0/30")]
    public void Subnet_of_an_address(string ip, int prefix, string expected) =>
        Assert.Equal(expected, AdapterInfo.SubnetOf(ip, prefix));

    [Theory]
    [InlineData(-1)]
    [InlineData(33)]
    public void Subnet_of_rejects_an_out_of_range_prefix_length(int prefix) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AdapterInfo.SubnetOf("192.168.86.42", prefix));

    public static TheoryData<ConnectionId> Every => new(Enum.GetValues<ConnectionId>());
}
