using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Latency = TCP handshake time from a socket bound to one adapter's address.
/// Windows' strong-host model sends it out of that adapter. TCP instead of ICMP because some networks drop ICMP.
/// All targets race at once: the first to connect gives its own handshake time, so one slow, blocked or refused
/// target is not a dead adapter. One deadline covers the whole check, so it never takes longer than the timeout.
/// Default 4 s: a congested but working link still answers in 1-2 s, and it stays under the card's 5 s poll so
/// refreshes never queue up on RouteController's gate.
public sealed class TcpLatencyProbe(IReadOnlyList<IPEndPoint> targets, TimeSpan timeout) : ILatencyProbe
{
    public TcpLatencyProbe(IPEndPoint target, TimeSpan timeout) : this([target], timeout) { }

    public static TcpLatencyProbe Default() =>
        new([new IPEndPoint(RouteController.ProbeTargetV4, 443), new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443)],
            TimeSpan.FromSeconds(4));

    /// Connects the probe's bound socket. Tests swap it: no loopback endpoint hangs on Windows (a refused SYN is retried ~2 s).
    internal Func<Socket, IPEndPoint, CancellationToken, ValueTask> Connect { get; init; } =
        static (socket, target, ct) => socket.ConnectAsync(target, ct);

    /// The first target to connect gives its handshake time; null when none connects before the deadline.
    public async Task<int?> MeasureAsync(IPAddress source, CancellationToken ct)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(timeout);
        var attempts = targets.Select(target => MeasureOneAsync(source, target, race.Token, ct)).ToList();
        try
        {
            while (attempts.Count > 0)
            {
                var done = await Task.WhenAny(attempts);
                attempts.Remove(done);
                if (await done is { } ms) return ms;
            }
            return null;
        }
        finally
        {
            // Cancel the losers and wait for them, so their sockets are closed and their outcomes observed.
            race.Cancel();
            await ((Task)Task.WhenAll(attempts))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    async Task<int?> MeasureOneAsync(IPAddress source, IPEndPoint target, CancellationToken race, CancellationToken ct)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(source, 0));
            var stopwatch = Stopwatch.StartNew();
            await Connect(socket, target, race);
            return (int)Math.Max(1, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // the deadline passed or another target won
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
