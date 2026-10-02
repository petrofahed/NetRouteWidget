using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Downloads a test file through one adapter (socket bound to its address; strong-host routing keeps it there).
/// The rate is timed from the moment the response headers arrive, so DNS, TCP and TLS setup are not counted.
/// The data use is bounded: at most about <see cref="MaxBytes"/> bytes are read and redirects are never followed.
public sealed class HttpSpeedProbe(Uri url, TimeSpan timeout) : ISpeedProbe
{
    const long MaxBytes = 6_000_000;
    const long MinBytesWithoutLength = 1_000_000;

    public static HttpSpeedProbe Default() =>
        new(new Uri("https://speed.cloudflare.com/__down?bytes=5000000"), TimeSpan.FromSeconds(30));

    public async Task<double?> MeasureMbpsAsync(IPAddress source, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, source.AddressFamily, token);
                var socket = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        // HttpClient.Timeout stops at the response headers; one linked token bounds the whole download.
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            var hasLength = response.Content.Headers.ContentLength is not null;
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var stopwatch = Stopwatch.StartNew();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while (total < MaxBytes && (read = await stream.ReadAsync(buffer, cts.Token)) > 0) total += read;
            var seconds = stopwatch.Elapsed.TotalSeconds;
            if (!hasLength && total < MinBytesWithoutLength) return null; // closed early with no declared length: truncated
            return total == 0 || seconds <= 0 ? null : total * 8 / seconds / 1_000_000;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && ex is OperationCanceledException or HttpRequestException or SocketException or IOException)
        {
            return null;
        }
    }
}
