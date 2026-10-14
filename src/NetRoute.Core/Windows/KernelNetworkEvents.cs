namespace NetRoute.Core.Windows;

/// Event ids of the data events of Microsoft-Windows-Kernel-Network: bytes sent and received over TCP and UDP, IPv4 and
/// IPv6. Connection set-up, teardown and retransmits are not new data and are ignored. (Ids from the provider's manifest;
/// the Task 1 probe confirms them on a real machine.)
public static class KernelNetworkEvents
{
    static readonly HashSet<int> Send = [10, 26, 42, 58];    // TCPv4, TCPv6, UDPv4, UDPv6 sent
    static readonly HashSet<int> Receive = [11, 27, 43, 59]; // TCPv4, TCPv6, UDPv4, UDPv6 received

    public static bool IsData(int id) => Send.Contains(id) || Receive.Contains(id);

    public static bool IsSend(int id) => Send.Contains(id);
}
