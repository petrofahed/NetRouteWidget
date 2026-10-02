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
}
