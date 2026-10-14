using System.Net;
using Microsoft.Extensions.Time.Testing;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public class CachingNetworkInfoTests
{
    sealed class CountingInfo : INetworkInfo
    {
        public int NicReads, GatewayReads, PrefixReads;
        public NicStats? Nic { get; set; } = new("Ethernet", 1_000_000_000, true, 0, 0, 0, 0);
        public IPAddress? Gateway { get; set; } = IPAddress.Parse("192.168.86.1");

        public NicStats? ReadNic(string adapterName) { NicReads++; return Nic; }
        public IPAddress? GatewayV4(string adapterName) { GatewayReads++; return Gateway; }
        public int? PrefixLengthV4(string adapterName, IPAddress address) { PrefixReads++; return 24; }
    }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    readonly CountingInfo _inner = new();

    CachingNetworkInfo Create() => new(_inner, _time);

    [Fact]
    public void Repeated_reads_inside_the_lifetime_hit_the_system_once()
    {
        var info = Create();

        for (var i = 0; i < 6; i++)
        {
            info.ReadNic("Ethernet");
            info.GatewayV4("Ethernet");
            info.PrefixLengthV4("Ethernet", IPAddress.Parse("192.168.86.42"));
            _time.Advance(TimeSpan.FromMilliseconds(150)); // the tab asks about six times a second
        }

        Assert.Equal((1, 1, 1), (_inner.NicReads, _inner.GatewayReads, _inner.PrefixReads));
    }

    [Fact]
    public void The_card_statistics_are_read_again_after_a_second_and_the_layout_after_ten()
    {
        var info = Create();
        info.ReadNic("Ethernet");
        info.GatewayV4("Ethernet");

        _time.Advance(CachingNetworkInfo.NicLifetime);
        info.ReadNic("Ethernet");
        info.GatewayV4("Ethernet");
        Assert.Equal((2, 1), (_inner.NicReads, _inner.GatewayReads));

        _time.Advance(CachingNetworkInfo.LayoutLifetime);
        info.GatewayV4("Ethernet");
        Assert.Equal(2, _inner.GatewayReads);
    }

    [Fact]
    public void Another_adapter_or_address_is_a_separate_entry()
    {
        var info = Create();

        info.ReadNic("Ethernet");
        info.ReadNic("Ethernet 5");
        info.PrefixLengthV4("Ethernet", IPAddress.Parse("192.168.86.42"));
        info.PrefixLengthV4("Ethernet", IPAddress.Parse("192.168.86.77"));

        Assert.Equal((2, 2), (_inner.NicReads, _inner.PrefixReads));
    }

    [Fact]
    public void A_missing_answer_is_cached_too_so_a_failing_read_is_not_hammered()
    {
        _inner.Nic = null;
        _inner.Gateway = null;
        var info = Create();

        Assert.Null(info.ReadNic("Ethernet"));
        Assert.Null(info.ReadNic("Ethernet"));
        Assert.Null(info.GatewayV4("Ethernet"));
        Assert.Null(info.GatewayV4("Ethernet"));

        Assert.Equal((1, 1), (_inner.NicReads, _inner.GatewayReads));
    }

    [Fact]
    public void A_cached_answer_is_what_the_system_gave()
    {
        var info = Create();

        Assert.Equal(_inner.Nic, info.ReadNic("Ethernet"));
        Assert.Equal(IPAddress.Parse("192.168.86.1"), info.GatewayV4("Ethernet"));
        Assert.Equal(24, info.PrefixLengthV4("Ethernet", IPAddress.Parse("192.168.86.42")));
    }
}
