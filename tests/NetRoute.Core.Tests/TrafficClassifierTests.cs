using System.Net;

namespace NetRoute.Core.Tests;

public class TrafficClassifierTests
{
    static IPAddress Ip(string text) => IPAddress.Parse(text);

    static readonly IPAddress Phone4 = Ip("192.168.42.11");
    static readonly IPAddress Lan4 = Ip("192.168.86.42");
    static readonly IPAddress Phone6 = Ip("2001:db8:1::2");
    static readonly IPAddress Lan6 = Ip("2001:db8:2::9");
    static readonly TrafficClassifier Classifier = new([Phone4, Phone6], [Lan4, Lan6]);

    [Fact]
    public void An_internet_packet_from_the_phone_counts_for_the_phone_whichever_address_comes_first()
    {
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Phone4, Ip("1.1.1.1")));
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Ip("1.1.1.1"), Phone4));
    }

    [Fact]
    public void An_internet_packet_on_the_lan_counts_for_the_lan()
    {
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Lan4, Ip("8.8.8.8")));
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Ip("8.8.8.8"), Lan4));
    }

    [Fact]
    public void Ipv6_addresses_of_the_adapter_count_too()
    {
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Phone6, Ip("2606:4700:4700::1111")));
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Ip("2606:4700:4700::1111"), Lan6));
    }

    [Fact]
    public void An_ipv4_mapped_ipv6_address_is_read_as_the_ipv4_one()
    {
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Ip("::ffff:192.168.42.11"), Ip("1.1.1.1")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.3.4")]
    [InlineData("224.0.0.251")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::5")]
    [InlineData("ff02::fb")]
    public void Traffic_to_the_home_network_loopback_and_multicast_is_ignored(string remote)
    {
        Assert.Null(Classifier.ExitOf(Lan4, Ip(remote)));
        Assert.Null(Classifier.ExitOf(Ip(remote), Phone4));
    }

    [Fact]
    public void A_public_address_next_to_the_private_172_block_still_counts()
    {
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Lan4, Ip("172.32.0.1")));
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Lan4, Ip("172.15.255.255")));
    }

    [Fact]
    public void A_public_172_64_address_is_not_mistaken_for_the_private_172_16_block()
    {
        // 172.64.148.235 is a Cloudflare address seen in the probe on the user's machine.
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Phone4, Ip("172.64.148.235")));
    }

    [Fact]
    public void Packets_of_other_adapters_are_ignored()
    {
        Assert.Null(Classifier.ExitOf(Ip("10.8.0.2"), Ip("8.8.8.8"))); // a VPN or virtual adapter
    }

    [Fact]
    public void Traffic_between_the_two_adapters_or_to_ourselves_is_ignored()
    {
        Assert.Null(Classifier.ExitOf(Phone4, Lan4));
        Assert.Null(Classifier.ExitOf(Phone4, Phone4));
        Assert.Null(Classifier.ExitOf(Phone4, Phone6));
    }

    [Fact]
    public void Nothing_counts_before_the_adapters_are_known()
    {
        Assert.Null(TrafficClassifier.None.ExitOf(Phone4, Ip("1.1.1.1")));
    }

    [Fact]
    public void A_phone_with_a_public_address_does_not_count_traffic_to_its_own_other_address()
    {
        var c = new TrafficClassifier([Ip("100.64.0.5"), Ip("203.0.113.7")], []);
        Assert.Null(c.ExitOf(Ip("100.64.0.5"), Ip("203.0.113.7")));
        Assert.Equal(RouteExit.Phone, c.ExitOf(Ip("203.0.113.7"), Ip("1.1.1.1")));
    }
}
