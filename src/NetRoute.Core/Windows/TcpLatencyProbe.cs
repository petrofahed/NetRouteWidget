using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Latency = TCP handshake time from a socket bound to one adapter's address.
/// Windows' strong-host model sends it out of that adapter. TCP instead of ICMP because some networks drop ICMP.
public sealed class TcpLatencyProbe(IPEndPoint target, TimeSpan timeout) : ILatencyProbe
{
    public static TcpLatencyProbe Default() =>
        new(new IPEndPoint(RouteController.ProbeTargetV4, 443), TimeSpan.FromSeconds(2));

    public async Task<int?> MeasureAsync(IPAddress source, CancellationToken ct)
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
