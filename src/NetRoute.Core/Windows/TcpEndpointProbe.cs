using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

/// The path map's internet check: a TCP connection from one adapter's address, with a three-way outcome. Unlike the
/// auto-heal probe (which treats every socket error as "no internet" and is left untouched), a local problem such as
/// exhausted ports or a vanished adapter address is Unavailable, not a lost check.
public sealed class TcpEndpointProbe(IReadOnlyList<IPEndPoint> targets, TimeSpan timeout) : IEndpointProbe
{
    public static TcpEndpointProbe Default() => new(
        [new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443)],
        TimeSpan.FromSeconds(2));

    /// Any target answering is a Reply. Otherwise, if any attempt could not be measured the check is Unavailable
    /// (we cannot claim the network said no); only when every attempt really got no answer is it NoReply.
    public async Task<EndpointResult> CheckAsync(IPAddress source, CancellationToken ct)
    {
        var sawUnavailable = false;
        foreach (var target in targets)
        {
            if (ct.IsCancellationRequested) return new EndpointResult(ProbeOutcome.Unavailable);
            var result = await ConnectAsync(source, target, ct);
            if (result.Outcome == ProbeOutcome.Reply) return result;
            if (result.Outcome == ProbeOutcome.Unavailable) sawUnavailable = true;
        }
        return new EndpointResult(sawUnavailable ? ProbeOutcome.Unavailable : ProbeOutcome.NoReply);
    }

    async Task<EndpointResult> ConnectAsync(IPAddress source, IPEndPoint target, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        Socket? socket = null;
        try
        {
            // Created inside the try: exhausted sockets and unsupported families can fail at construction, not only at connect.
            socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(source, 0));
            var stopwatch = Stopwatch.StartNew();
            await socket.ConnectAsync(target, timeoutCts.Token);
            return new EndpointResult(ProbeOutcome.Reply, (int)Math.Max(1, stopwatch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled: the check did not finish, which says nothing. Our own timeout firing is a real non-answer.
            return new EndpointResult(ct.IsCancellationRequested ? ProbeOutcome.Unavailable : ProbeOutcome.NoReply);
        }
        catch (SocketException e)
        {
            return new EndpointResult(Classify(e.SocketErrorCode));
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or ArgumentException
                                      or NotSupportedException or PlatformNotSupportedException)
        {
            return new EndpointResult(ProbeOutcome.Unavailable);
        }
        finally
        {
            socket?.Dispose();
        }
    }

    /// The network (or the stack's routing table) said no, or nothing came back: NoReply. Anything that points at this PC
    /// (ports or buffers exhausted, the source address gone, access denied) or is unknown: Unavailable.
    public static ProbeOutcome Classify(SocketError error) => error switch
    {
        SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
            or SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted
            or SocketError.HostDown => ProbeOutcome.NoReply,
        _ => ProbeOutcome.Unavailable,
    };
}
