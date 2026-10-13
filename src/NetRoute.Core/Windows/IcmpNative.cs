using System.Runtime.InteropServices;

namespace NetRoute.Core.Windows;

/// iphlpapi.dll ICMP interop (icmpapi.h / ipexport.h). The reply struct is 40 bytes on x64; IcmpPathProbeTests.Echo_reply_struct_matches_the_native_layout_on_x64 guards it.
internal static class IcmpNative
{
    public const uint IP_SUCCESS = 0;
    public const uint IP_DEST_NET_UNREACHABLE = 11002;
    public const uint IP_DEST_HOST_UNREACHABLE = 11003;
    public const uint IP_DEST_PROT_UNREACHABLE = 11004;
    public const uint IP_DEST_PORT_UNREACHABLE = 11005;
    public const uint IP_REQ_TIMED_OUT = 11010;
    public const uint IP_TTL_EXPIRED_TRANSIT = 11013;
    public const uint IP_BAD_DESTINATION = 11018;

    [StructLayout(LayoutKind.Sequential)]
    public struct IP_OPTION_INFORMATION
    {
        public byte Ttl;
        public byte Tos;
        public byte Flags;
        public byte OptionsSize;
        public IntPtr OptionsData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICMP_ECHO_REPLY
    {
        public uint Address;
        public uint Status;
        public uint RoundTripTime;
        public ushort DataSize;
        public ushort Reserved;
        public IntPtr Data;
        public IP_OPTION_INFORMATION Options;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern bool IcmpCloseHandle(IntPtr handle);

    /// Synchronous when eventHandle and apcRoutine are null. Returns the number of replies, or 0 (see GetLastError).
    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint IcmpSendEcho2Ex(
        IntPtr icmpHandle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destinationAddress,
        byte[] requestData, ushort requestSize, ref IP_OPTION_INFORMATION requestOptions,
        IntPtr replyBuffer, uint replySize, uint timeout);
}
