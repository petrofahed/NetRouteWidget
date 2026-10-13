using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.PathMap;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class IcmpPathProbeTests
{
    static readonly IPAddress Target = IPAddress.Parse("1.1.1.1");
    static uint Addr(string ip) => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);

    [Fact]
    public void Echo_reply_struct_matches_the_native_layout_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        Assert.Equal(40, Marshal.SizeOf<IcmpNative.ICMP_ECHO_REPLY>());
        Assert.Equal(16, Marshal.SizeOf<IcmpNative.IP_OPTION_INFORMATION>());
        Assert.Equal(16, (int)Marshal.OffsetOf<IcmpNative.ICMP_ECHO_REPLY>(nameof(IcmpNative.ICMP_ECHO_REPLY.Data)));
        Assert.Equal(24, (int)Marshal.OffsetOf<IcmpNative.ICMP_ECHO_REPLY>(nameof(IcmpNative.ICMP_ECHO_REPLY.Options)));
    }

    [Fact]
    public void A_timeout_is_a_lost_check()
    {
        var r = IcmpPathProbe.Interpret(0, 0, 0, 0, lastError: 11010, Target);

        Assert.Equal(ProbeOutcome.NoReply, r.Outcome);
    }

    [Theory]
    [InlineData(87)]    // ERROR_INVALID_PARAMETER: for example the source address no longer exists
    [InlineData(1231)]  // ERROR_NETWORK_UNREACHABLE
    [InlineData(5)]     // access denied
    [InlineData(11050)] // IP_GENERAL_FAILURE
    [InlineData(0)]
    public void Any_other_failure_to_send_did_not_measure_the_network(int lastError)
    {
        Assert.Equal(ProbeOutcome.Unavailable, IcmpPathProbe.Interpret(0, 0, 0, 0, lastError, Target).Outcome);
    }

    [Fact]
    public void An_echo_reply_from_the_target_reaches_it()
    {
        var r = IcmpPathProbe.Interpret(1, 0, Addr("1.1.1.1"), 52, 0, Target);

        Assert.Equal(ProbeOutcome.Reply, r.Outcome);
        Assert.True(r.TargetReached);
        Assert.Equal(52, r.RttMs);
        Assert.Equal(Target, r.From);
    }

    [Fact]
    public void Ttl_expired_reveals_the_device_and_does_not_reach_the_target()
    {
        var r = IcmpPathProbe.Interpret(1, 11013, Addr("192.168.1.1"), 0, 0, Target);

        Assert.Equal(ProbeOutcome.Reply, r.Outcome);
        Assert.False(r.TargetReached);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), r.From);
        Assert.Equal(1, r.RttMs); // under a millisecond is shown as 1
    }

    [Theory]
    [InlineData(11010u)] // timed out
    [InlineData(11002u)] // destination net unreachable
    [InlineData(11003u)] // destination host unreachable
    [InlineData(11004u)]
    [InlineData(11005u)]
    [InlineData(11018u)] // bad destination
    public void The_network_saying_no_is_a_lost_check(uint status)
    {
        Assert.Equal(ProbeOutcome.NoReply, IcmpPathProbe.Interpret(1, status, Addr("192.168.1.1"), 3, 0, Target).Outcome);
    }

    [Theory]
    [InlineData(11001u)] // buffer too small
    [InlineData(11006u)] // no resources
    [InlineData(11007u)] // bad option
    [InlineData(11050u)] // general failure
    [InlineData(99999u)] // unknown
    public void Unexpected_statuses_did_not_measure_the_network(uint status)
    {
        Assert.Equal(ProbeOutcome.Unavailable, IcmpPathProbe.Interpret(1, status, Addr("192.168.1.1"), 3, 0, Target).Outcome);
    }

    [Fact]
    public async Task A_cancelled_check_is_unavailable_and_sends_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = await new IcmpPathProbe().EchoAsync(IPAddress.Loopback, IPAddress.Loopback, 64, TimeSpan.FromSeconds(1), cts.Token);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    public async Task IPv6_is_not_supported_and_is_unavailable()
    {
        var r = await new IcmpPathProbe().EchoAsync(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, 64, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Loopback_echo_gets_a_real_reply()
    {
        var r = await new IcmpPathProbe().EchoAsync(IPAddress.Loopback, IPAddress.Loopback, 64, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Reply, r.Outcome);
        Assert.True(r.TargetReached);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(256)]
    public async Task A_ttl_outside_1_to_255_is_unavailable_not_clamped(int ttl)
    {
        var r = await new IcmpPathProbe().EchoAsync(IPAddress.Loopback, IPAddress.Loopback, ttl, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_source_address_this_pc_does_not_have_is_unavailable_not_lost()
    {
        var r = await new IcmpPathProbe().EchoAsync(IPAddress.Parse("203.0.113.7"), IPAddress.Loopback, 64, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }
}
