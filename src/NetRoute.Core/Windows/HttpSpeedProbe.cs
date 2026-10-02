using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Downloads a test file through one adapter (socket bound to its address; strong-host routing keeps it there).
public sealed class HttpSpeedProbe(Uri url, TimeSpan timeout) : ISpeedProbe
{
    public static HttpSpeedProbe Default() =>
        new(new Uri("https://speed.cloudflare.com/__down?bytes=5000000"), TimeSpan.FromSeconds(30));

    public async Task<double?> MeasureMbpsAsync(IPAddress source, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
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
        using var client = new HttpClient(handler) { Timeout = timeout };
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0) total += read;
            var seconds = stopwatch.Elapsed.TotalSeconds;
            return total == 0 || seconds <= 0 ? null : total * 8 / seconds / 1_000_000;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && ex is HttpRequestException or TaskCanceledException or SocketException or IOException)
        {
            return null;
        }
    }
}
