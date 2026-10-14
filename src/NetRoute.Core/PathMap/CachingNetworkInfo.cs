using System.Net;

namespace NetRoute.Core.PathMap;

/// Remembers what Windows said for a short while. The Local network tab asks about the adapter several times a second on the UI
/// thread, and every real read lists all the PC's adapters (many on a PC with VMs and VPNs): the card's statistics are read at most
/// once a second, the gateway and prefix (which only change when the network does) once every ten seconds. A missing answer is
/// remembered too, so a failing read is not repeated at the tab's refresh rate.
public sealed class CachingNetworkInfo(INetworkInfo inner, TimeProvider time) : INetworkInfo
{
    public static readonly TimeSpan NicLifetime = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LayoutLifetime = TimeSpan.FromSeconds(10);

    readonly Lock _gate = new();
    readonly Dictionary<string, (DateTimeOffset At, object? Value)> _cache = new();

    public NicStats? ReadNic(string adapterName) =>
        Cached("nic|" + adapterName, NicLifetime, () => inner.ReadNic(adapterName));

    public IPAddress? GatewayV4(string adapterName) =>
        Cached("gw|" + adapterName, LayoutLifetime, () => inner.GatewayV4(adapterName));

    public int? PrefixLengthV4(string adapterName, IPAddress address) =>
        Cached("prefix|" + adapterName + "|" + address, LayoutLifetime, () => inner.PrefixLengthV4(adapterName, address));

    T? Cached<T>(string key, TimeSpan lifetime, Func<T?> read)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit) && now - hit.At < lifetime) return (T?)hit.Value;
        }
        var value = read(); // outside the lock: a real read takes a while
        lock (_gate) _cache[key] = (now, value);
        return value;
    }
}
