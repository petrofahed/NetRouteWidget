using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

/// ICMP echo with a chosen TTL, sent from a chosen local address (so each connection is probed through its own adapter).
/// Needs no administrator rights. A check that could not run is Unavailable, never a lost packet.
public sealed class IcmpPathProbe : IPathProbe
{
    const int PayloadSize = 32;
    const int ReplyBufferSize = 256; // ICMP_ECHO_REPLY (40) + payload + 8 for an ICMP error + IO_STATUS_BLOCK, with room to spare

    public Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(new EchoResult(ProbeOutcome.Unavailable));
        // IcmpSendEcho2Ex blocks for up to the timeout; keep it off the caller's thread.
        return Task.Run(() =>
        {
            if (ct.IsCancellationRequested) return new EchoResult(ProbeOutcome.Unavailable);
            var result = Send(source, target, ttl, timeout);
            // A check cancelled while it was in flight was superseded: its answer must not count as a loss or a reply.
            return ct.IsCancellationRequested ? new EchoResult(ProbeOutcome.Unavailable) : result;
        }, CancellationToken.None);
    }

    static EchoResult Send(IPAddress source, IPAddress target, int ttl, TimeSpan timeout)
    {
        if (source.AddressFamily != AddressFamily.InterNetwork || target.AddressFamily != AddressFamily.InterNetwork)
            return new EchoResult(ProbeOutcome.Unavailable);
        // An out-of-range TTL is a caller bug, not a measurement: it is not clamped into a different probe.
        if (ttl < 1 || ttl > 255) return new EchoResult(ProbeOutcome.Unavailable);

        var handle = IntPtr.Zero;
        var reply = IntPtr.Zero;
        try
        {
            handle = IcmpNative.IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return new EchoResult(ProbeOutcome.Unavailable);
            reply = Marshal.AllocHGlobal(ReplyBufferSize);
            var options = new IcmpNative.IP_OPTION_INFORMATION { Ttl = (byte)ttl };
            var replies = IcmpNative.IcmpSendEcho2Ex(
                handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                Address(source), Address(target),
                new byte[PayloadSize], PayloadSize, ref options,
                reply, ReplyBufferSize, (uint)Math.Max(1, timeout.TotalMilliseconds));
            var lastError = Marshal.GetLastWin32Error();
            if (replies == 0) return Interpret(0, 0, 0, 0, lastError, target);
            var r = Marshal.PtrToStructure<IcmpNative.ICMP_ECHO_REPLY>(reply);
            return Interpret(replies, r.Status, r.Address, r.RoundTripTime, 0, target);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException
                                       or OutOfMemoryException or InvalidOperationException)
        {
            // A local problem (missing iphlpapi entry point, marshalling failure) did not measure the network.
            return new EchoResult(ProbeOutcome.Unavailable);
        }
        finally
        {
            if (reply != IntPtr.Zero) Marshal.FreeHGlobal(reply);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) IcmpNative.IcmpCloseHandle(handle);
        }
    }

    static uint Address(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

    /// Maps the API's result to an outcome. Pure, so a table test pins it.
    /// Reply: a real echo reply, or "TTL expired" from a device. NoReply: a timeout, or the network answering "unreachable".
    /// Everything else (resource and general failures, parameter errors, any other Win32 error) did not measure the network.
    internal static EchoResult Interpret(uint replies, uint status, uint address, uint roundTripMs, int lastError, IPAddress target)
    {
        if (replies == 0)
            return new EchoResult(lastError == (int)IcmpNative.IP_REQ_TIMED_OUT ? ProbeOutcome.NoReply : ProbeOutcome.Unavailable);

        var from = new IPAddress(BitConverter.GetBytes(address));
        var rtt = (int)Math.Max(1, roundTripMs);
        switch (status)
        {
            case IcmpNative.IP_SUCCESS:
                return new EchoResult(ProbeOutcome.Reply, from, rtt, TargetReached: from.Equals(target));
            case IcmpNative.IP_TTL_EXPIRED_TRANSIT:
                return new EchoResult(ProbeOutcome.Reply, from, rtt);
            case IcmpNative.IP_REQ_TIMED_OUT:
            case IcmpNative.IP_DEST_NET_UNREACHABLE:
            case IcmpNative.IP_DEST_HOST_UNREACHABLE:
            case IcmpNative.IP_DEST_PROT_UNREACHABLE:
            case IcmpNative.IP_DEST_PORT_UNREACHABLE:
            case IcmpNative.IP_BAD_DESTINATION:
                return new EchoResult(ProbeOutcome.NoReply, from);
            default:
                return new EchoResult(ProbeOutcome.Unavailable);
        }
    }
}
