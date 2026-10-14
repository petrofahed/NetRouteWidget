using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Name lookups with a short timeout: a home router's DNS can be slow, and a lookup must never hold the tab up.
public static class HostResolver
{
    static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan ReverseTimeout = TimeSpan.FromSeconds(1);

    /// The IPv4 addresses of a name; empty when it cannot be found.
    public static async Task<IReadOnlyList<IPAddress>> ResolveIPv4Async(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(ForwardTimeout);
            return await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return [];
        }
    }

    /// The name of an address, or null when it has none (or the lookup took too long). Cancelling the caller's token throws.
    public static async Task<string?> ReverseAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReverseTimeout);
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cts.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) || entry.HostName == address.ToString() ? null : entry.HostName;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }
}
