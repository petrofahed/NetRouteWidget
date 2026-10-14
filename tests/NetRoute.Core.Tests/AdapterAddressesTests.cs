using System.Net;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class AdapterAddressesTests
{
    [Fact]
    public void No_adapter_has_no_addresses()
    {
        Assert.Empty(AdapterAddresses.Of(null, _ => [IPAddress.Loopback]));
    }

    [Fact]
    public void The_detected_ipv4_comes_first_and_the_lookup_adds_the_rest_without_duplicates()
    {
        var adapter = TestAdapters.Phone();
        var all = AdapterAddresses.Of(adapter, name =>
        {
            Assert.Equal(adapter.Name, name);
            return [IPAddress.Parse("192.168.42.11"), IPAddress.Parse("2001:db8::2")];
        });

        Assert.Equal([IPAddress.Parse("192.168.42.11"), IPAddress.Parse("2001:db8::2")], all);
    }

    [Fact]
    public void A_failing_lookup_still_returns_the_detected_ipv4()
    {
        var all = AdapterAddresses.Of(TestAdapters.Lan(), _ => throw new InvalidOperationException("no nic"));

        Assert.Equal([IPAddress.Parse("192.168.86.42")], all);
    }

    [Fact]
    public void An_adapter_without_an_ipv4_yet_uses_the_lookup_only()
    {
        var all = AdapterAddresses.Of(TestAdapters.Lan() with { IPv4 = null }, _ => [IPAddress.Parse("2001:db8::9")]);

        Assert.Equal([IPAddress.Parse("2001:db8::9")], all);
    }
}
