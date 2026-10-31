namespace NetRoute.Core.Tests;

public class SmartRoutingPathResolverTests
{
    [Theory]
    [InlineData(ConnectionId.Lan, InternetPath.Lan)]
    [InlineData(ConnectionId.Wifi, InternetPath.Wifi)]
    [InlineData(ConnectionId.Phone, InternetPath.Phone)]
    public void Only_the_active_tun_query_uses_the_applied_physical_exit(ConnectionId id, InternetPath expected)
    {
        Assert.Equal(expected, SmartRoutingPathResolver.Resolve(true, id, 90, 90));
        Assert.Null(SmartRoutingPathResolver.Resolve(false, id, 90, 90));
        Assert.Null(SmartRoutingPathResolver.Resolve(true, id, 91, 90));
        Assert.Null(SmartRoutingPathResolver.Resolve(true, id, null, null));
    }
}
