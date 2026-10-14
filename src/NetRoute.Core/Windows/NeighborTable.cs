using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

/// Windows' IPv4 neighbour (ARP) table through GetIpNetTable: the addresses this PC has talked to recently and their MACs.
public sealed class NeighborTable : INeighborTable
{
    const int ErrorInsufficientBuffer = 122;
    const uint TypeInvalid = 2;
    const int RowSize = 24;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IPNETROW
    {
        public uint Index;
        public uint PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PhysAddr;
        public uint Addr;
        public uint Type;
    }

    [DllImport("iphlpapi.dll")]
    static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    public IReadOnlyList<(IPAddress Address, string Mac)> Read()
    {
        // The table can grow between the size query and the read: ask again a few times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = 0;
            if (GetIpNetTable(IntPtr.Zero, ref size, false) != ErrorInsufficientBuffer || size <= 0) return [];
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetIpNetTable(buffer, ref size, false);
                if (result == ErrorInsufficientBuffer) continue;
                if (result != 0) return [];
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return Parse(bytes);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    /// Pure, so a handcrafted buffer tests it. Skips invalid entries and rows whose physical address is not 6 bytes (incomplete entries).
    internal static IReadOnlyList<(IPAddress Address, string Mac)> Parse(byte[] buffer)
    {
        var rows = new List<(IPAddress, string)>();
        if (buffer.Length < 4) return rows;
        var count = BitConverter.ToUInt32(buffer, 0);
        for (var i = 0; i < count; i++)
        {
            var at = 4 + i * RowSize;
            if (at + RowSize > buffer.Length) break;
            var macLength = BitConverter.ToUInt32(buffer, at + 4);
            var type = BitConverter.ToUInt32(buffer, at + 20);
            if (type == TypeInvalid || macLength != 6) continue;
            var mac = string.Join("-", buffer.Skip(at + 8).Take(6).Select(b => b.ToString("X2")));
            rows.Add((new IPAddress(buffer.AsSpan(at + 16, 4)), mac));
        }
        return rows;
    }
}
