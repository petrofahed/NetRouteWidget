using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.PathMap;

public sealed record ScanResult(IPAddress Address, string? Mac, string? Name, bool AnsweredPing);

/// The PC's IPv4 neighbour (ARP) table: addresses it has talked to recently, with their MACs.
public interface INeighborTable
{
    IReadOnlyList<(IPAddress Address, string Mac)> Read();
}

/// Finds the devices on the LAN adapter's own subnet: a ping sweep plus the neighbour table (which also lists devices that do not
/// answer pings), then a best-effort name for each. Never more than MaxHosts addresses; throws OperationCanceledException when cancelled.
public sealed class NetworkScanner(IPathProbe probe, INeighborTable neighbors, Func<IPAddress, CancellationToken, Task<string?>> reverseName)
{
    public const int MaxHosts = 254;
    public const int PingConcurrency = 64;
    public const int NameConcurrency = 8;
    public static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(500);
    const int Ttl = 64;

    /// The host addresses to scan: the subnet of the address (a subnet larger than a /24 is cut down to the /24 around the address),
    /// without the network and broadcast addresses and without this PC. Empty for a /31 or /32.
    public static IReadOnlyList<IPAddress> HostsOf(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength >= 31) return [];
        var prefix = Math.Clamp(prefixLength, 24, 30);
        var self = ToUInt(address);
        var mask = uint.MaxValue << (32 - prefix);
        var network = self & mask;
        var broadcast = network | ~mask;
        var hosts = new List<IPAddress>();
        for (var ip = network + 1; ip < broadcast; ip++)
            if (ip != self) hosts.Add(FromUInt(ip));
        return hosts;
    }

    public async Task<IReadOnlyList<ScanResult>> ScanAsync(IPAddress source, int prefixLength, IProgress<int>? progress, CancellationToken ct)
    {
        var hosts = HostsOf(source, prefixLength);
        var answered = new ConcurrentDictionary<IPAddress, bool>();
        var done = 0;
        using (var gate = new SemaphoreSlim(PingConcurrency))
        {
            await Task.WhenAll(hosts.Select(async host =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var reply = await probe.EchoAsync(source, host, Ttl, PingTimeout, ct);
                    if (reply.Outcome == ProbeOutcome.Reply && reply.TargetReached) answered[host] = true;
                }
                finally
                {
                    gate.Release();
                    progress?.Report(Interlocked.Increment(ref done));
                }
            }));
        }
        ct.ThrowIfCancellationRequested();

        var wanted = hosts.ToHashSet();
        var macs = new Dictionary<IPAddress, string>();
        foreach (var (address, mac) in neighbors.Read())
            if (wanted.Contains(address) && IsRealMac(mac)) macs[address] = mac.ToUpperInvariant();

        var found = hosts.Where(h => answered.ContainsKey(h) || macs.ContainsKey(h)).ToList(); // hosts is in address order
        var names = new string?[found.Count];
        using (var gate = new SemaphoreSlim(NameConcurrency))
        {
            await Task.WhenAll(found.Select(async (host, i) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    names[i] = await reverseName(host, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    names[i] = null; // a name is a convenience: never fail the scan for it
                }
                finally { gate.Release(); }
            }));
        }
        ct.ThrowIfCancellationRequested();

        return found.Select((host, i) => new ScanResult(host, macs.GetValueOrDefault(host), names[i], answered.ContainsKey(host))).ToList();
    }

    static bool IsRealMac(string mac)
    {
        var digits = mac.Where(Uri.IsHexDigit).ToArray();
        return digits.Length == 12 && digits.Any(c => c != '0') && digits.Any(c => c is not ('f' or 'F'));
    }

    static uint ToUInt(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray(), 0);

    static IPAddress FromUInt(uint value) => new(BitConverter.GetBytes(value).Reverse().ToArray());
}
