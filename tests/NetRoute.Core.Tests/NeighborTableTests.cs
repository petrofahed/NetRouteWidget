using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class NeighborTableTests
{
    // MIB_IPNETTABLE: a DWORD count, then MIB_IPNETROW rows (24 bytes: index, physical address length, 8 bytes of address, IPv4 address, type).
    static byte[] Table(params (uint Index, byte[] Mac, string Ip, uint Type)[] rows)
    {
        var bytes = new List<byte>(BitConverter.GetBytes((uint)rows.Length));
        foreach (var (index, mac, ip, type) in rows)
        {
            bytes.AddRange(BitConverter.GetBytes(index));
            bytes.AddRange(BitConverter.GetBytes((uint)mac.Length));
            bytes.AddRange(mac.Concat(new byte[8]).Take(8));
            bytes.AddRange(IPAddress.Parse(ip).GetAddressBytes());
            bytes.AddRange(BitConverter.GetBytes(type));
        }
        return [.. bytes];
    }

    [Fact]
    public void The_native_row_is_24_bytes_like_the_layout_the_parser_reads()
    {
        Assert.Equal(24, Marshal.SizeOf<NeighborTable.MIB_IPNETROW>());
    }

    [Fact]
    public void Rows_become_address_and_mac_pairs()
    {
        var table = Table(
            (10, [0xAA, 0xBB, 0xCC, 0x00, 0x00, 0x44], "192.168.86.44", 3),
            (10, [0xAA, 0xBB, 0xCC, 0x00, 0x00, 0x01], "192.168.86.1", 4));

        var rows = NeighborTable.Parse(table);

        Assert.Equal(2, rows.Count);
        Assert.Equal((IPAddress.Parse("192.168.86.44"), "AA-BB-CC-00-00-44"), (rows[0].Address, rows[0].Mac));
        Assert.Equal((IPAddress.Parse("192.168.86.1"), "AA-BB-CC-00-00-01"), (rows[1].Address, rows[1].Mac));
    }

    [Fact]
    public void Invalid_entries_and_rows_without_a_six_byte_address_are_skipped()
    {
        var table = Table(
            (10, [1, 2, 3, 4, 5, 6], "192.168.86.10", 2),   // invalid
            (10, [1, 2, 3, 4], "192.168.86.11", 3),         // not a MAC
            (10, [], "192.168.86.12", 3),                   // incomplete (no address yet)
            (10, [1, 2, 3, 4, 5, 6], "192.168.86.13", 3));

        var rows = NeighborTable.Parse(table);

        Assert.Equal((IPAddress.Parse("192.168.86.13"), "01-02-03-04-05-06"), (Assert.Single(rows).Address, rows[0].Mac));
    }

    [Fact]
    public void A_truncated_or_empty_buffer_gives_no_rows_instead_of_throwing()
    {
        Assert.Empty(NeighborTable.Parse([]));
        Assert.Empty(NeighborTable.Parse([1, 0, 0, 0, 9, 9])); // says one row, holds half of one
    }
}
