using System.Net;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class EtwPayloadTests
{
    [Fact]
    public void An_ip_address_passes_through()
    {
        var ip = IPAddress.Parse("1.2.3.4");
        Assert.Same(ip, EtwPayload.ToAddress(ip));
    }

    [Fact]
    public void An_ipv4_address_read_as_a_little_endian_number_is_the_dotted_address()
    {
        Assert.Equal(IPAddress.Parse("127.0.0.1"), EtwPayload.ToAddress(0x0100007Fu));
        Assert.Equal(IPAddress.Parse("192.168.42.11"), EtwPayload.ToAddress(unchecked((int)0x0B2AA8C0)));
    }

    [Fact]
    public void Real_values_from_the_probe_on_the_users_machine_decode_to_the_expected_addresses()
    {
        // Int32 payloads recorded by tools/EtwProbe (2026-10-14): the same connection seen as a send and as a receive.
        Assert.Equal(IPAddress.Parse("172.19.0.1"), EtwPayload.ToAddress(16782252));
        Assert.Equal(IPAddress.Parse("172.19.0.2"), EtwPayload.ToAddress(33559468));
        Assert.Equal(IPAddress.Parse("192.168.42.11"), EtwPayload.ToAddress(187345088));   // the phone adapter, local side of a receive
        Assert.Equal(IPAddress.Parse("172.64.148.235"), EtwPayload.ToAddress(-342605652)); // a public Cloudflare address, remote side
        Assert.Equal(IPAddress.Parse("127.0.0.1"), EtwPayload.ToAddress(16777343));
    }

    [Fact]
    public void Byte_arrays_and_text_become_addresses()
    {
        Assert.Equal(IPAddress.Parse("1.1.1.1"), EtwPayload.ToAddress(new byte[] { 1, 1, 1, 1 }));
        Assert.Equal(IPAddress.Parse("2001:db8::1"), EtwPayload.ToAddress(IPAddress.Parse("2001:db8::1").GetAddressBytes()));
        Assert.Equal(IPAddress.Parse("::1"), EtwPayload.ToAddress("::1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not an address")]
    [InlineData(2.5)]
    public void Anything_else_is_not_an_address(object? value)
    {
        Assert.Null(EtwPayload.ToAddress(value));
    }

    [Fact]
    public void Numbers_of_any_width_become_longs()
    {
        Assert.Equal(5, EtwPayload.ToLong(5));
        Assert.Equal(4_000_000_000L, EtwPayload.ToLong(4_000_000_000u));
        Assert.Equal(7, EtwPayload.ToLong((ushort)7));
        Assert.Equal(long.MaxValue, EtwPayload.ToLong(ulong.MaxValue));
        Assert.Equal(0, EtwPayload.ToLong("x"));
        Assert.Equal(0, EtwPayload.ToLong(null));
    }
}
