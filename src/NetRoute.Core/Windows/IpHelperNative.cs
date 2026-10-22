using System.Runtime.InteropServices;

namespace NetRoute.Core.Windows;

/// iphlpapi.dll interop. MIB_IPINTERFACE_ROW layout follows netioapi.h exactly (168 bytes on x64);
/// BOOLEAN fields are single bytes. A layout mistake corrupts other interface settings on Set,
/// so IpHelperLayoutTests and WindowsIntegrationTests guard it.
internal static class IpHelperNative
{
    public const ushort AF_INET = 2;
    public const ushort AF_INET6 = 23;
    public const int NO_ERROR = 0;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IPINTERFACE_ROW
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public int RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public uint[] ZoneIndices;
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    [DllImport("iphlpapi.dll")]
    public static extern int GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [DllImport("iphlpapi.dll")]
    public static extern int SetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    /// destAddr is a raw SOCKADDR (sockaddr_in or sockaddr_in6).
    [DllImport("iphlpapi.dll")]
    public static extern int GetBestInterfaceEx(byte[] destAddr, out uint bestIfIndex);

    /// SOCKADDR_INET, restricted to the IPv4 (SOCKADDR_IN) shape: a union of SOCKADDR_IN (16 bytes)
    /// and SOCKADDR_IN6 (28 bytes), so its total size is 28 (the larger member). Family = AF_INET
    /// for a real IPv4 endpoint, or 0 (AF_UNSPEC) for "unspecified" (e.g. NextHop on an on-link route).
    /// Ipv4Addr holds the address in network byte order, i.e. BitConverter.ToUInt32(GetAddressBytes(), 0)
    /// with no extra byte-swap - do not run it through HostToNetworkOrder, that would double-swap.
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct SOCKADDR_INET_V4
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(2)] public ushort Port;
        [FieldOffset(4)] public uint Ipv4Addr;
    }

    /// IP_ADDRESS_PREFIX = { SOCKADDR_INET Prefix; UINT8 PrefixLength; }, padded to 32 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct IP_ADDRESS_PREFIX
    {
        [FieldOffset(0)] public SOCKADDR_INET_V4 Prefix;
        [FieldOffset(28)] public byte PrefixLength;
    }

    /// MIB_IPFORWARD_ROW2 (netioapi.h), x64 layout, 104 bytes total. Every field is pinned to its exact
    /// documented offset with FieldOffset rather than left to automatic Sequential layout, specifically so
    /// this cannot silently drift from the real native struct - Set writes this row straight into the live
    /// route table, and a wrong offset here would corrupt whichever field lands on top of it.
    /// IpHelperLayoutTests and the WindowsSubnetRoutes integration test guard this against the real machine.
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    public struct MIB_IPFORWARD_ROW2
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public IP_ADDRESS_PREFIX DestinationPrefix;
        [FieldOffset(44)] public SOCKADDR_INET_V4 NextHop;
        [FieldOffset(72)] public byte SitePrefixLength;
        [FieldOffset(76)] public uint ValidLifetime;
        [FieldOffset(80)] public uint PreferredLifetime;
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(88)] public int Protocol;
        [FieldOffset(92)] public byte Loopback;
        [FieldOffset(93)] public byte AutoconfigureAddress;
        [FieldOffset(94)] public byte Publish;
        [FieldOffset(95)] public byte Immortal;
        [FieldOffset(96)] public uint Age;
        [FieldOffset(100)] public int Origin;
    }

    /// Looks up ONE specific route row: DestinationPrefix, NextHop and InterfaceIndex (or InterfaceLuid)
    /// must be pre-filled on input to identify which row to fetch - this does not enumerate the table.
    [DllImport("iphlpapi.dll")]
    public static extern int GetIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    /// Matches on DestinationPrefix + NextHop + interface and fails with ERROR_NOT_FOUND if no such
    /// route exists. Always Get the row first, mutate only the field(s) you intend to change (e.g.
    /// Metric), and Set that same row back - anything else risks writing stale or zeroed fields into
    /// the real route table.
    [DllImport("iphlpapi.dll")]
    public static extern int SetIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);
}
