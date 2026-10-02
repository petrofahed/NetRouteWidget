using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Latency = TCP handshake time from a socket bound to one adapter's address.
/// Windows' strong-host model sends it out of that adapter. TCP instead of ICMP because some networks drop ICMP.
/// Targets are tried in order, each with its own timeout, so one slow or blocked target is not a dead adapter.
public sealed class TcpLatencyProbe(IReadOnlyList<IPEndPoint> targets, TimeSpan timeout) : ILatencyProbe
{
    public TcpLatencyProbe(IPEndPoint target, TimeSpan timeout) : this([target], timeout) { }

    public static TcpLatencyProbe Default() =>
        new([new IPEndPoint(RouteController.ProbeTargetV4, 443), new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443)],
            TimeSpan.FromSeconds(2));

    /// The first answering target's handshake time, or null when none answers.
    public async Task<int?> MeasureAsync(IPAddress source, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            if (await MeasureOneAsync(source, target, ct) is { } ms) return ms;
        }
        return null;
    }

    async Task<int?> MeasureOneAsync(IPAddress source, IPEndPoint target, CancellationToken ct)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            socket.Bind(new IPEndPoint(source, 0));
            var stopwatch = Stopwatch.StartNew();
            await socket.ConnectAsync(target, timeoutCts.Token);
            return (int)Math.Max(1, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
