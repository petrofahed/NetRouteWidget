using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core;

/// Bytes one process moved through one exit since the previous Drain. ProcessName is the image file name ("chrome.exe"),
/// or null when the process could not be identified (it counts under "Other").
public sealed record MeasuredTraffic(string? ProcessName, RouteExit Exit, Traffic Traffic);

/// Per-application traffic that works without sing-box (Windows kernel tracing). Used by SmartRoutingController while
/// sing-box is not running; every call comes from under the controller's gate except Dispose.
public interface ITrafficSource : IDisposable
{
    bool IsRunning { get; }

    /// Starts the source when it is not running. Null = running (or just started); otherwise the reason it could not.
    /// Cheap to call every second: a failed start is not retried before the source's own retry delay.
    string? EnsureRunning();

    /// The adapters whose packets count. Called on every apply, so a replugged phone with a new address is followed.
    void SetAdapters(AdapterInfo? phone, AdapterInfo? lan);

    /// What was measured since the previous call (per process and exit), and forgets it.
    IReadOnlyList<MeasuredTraffic> Drain();

    /// Ends the source when it is running, and forgets what it counted but did not drain. Cheap when it is not running.
    void Stop();
}

/// Decides which exit a packet belongs to from its two addresses. Pure.
///   - one address must be an address of the Phone adapter (-> Phone) or of the LAN adapter (-> LAN); both addresses are
///     checked because the local/remote order differs between event types;
///   - the OTHER address (the remote) must be a real internet address: loopback, private, link-local, multicast and
///     broadcast addresses, and our own addresses, are ignored (traffic to the home network is not usage);
///   - packets of any other adapter (VPN, virtual switch, the sing-box TUN) match neither set and are ignored.
public sealed class TrafficClassifier
{
    public static readonly TrafficClassifier None = new([], []);

    readonly HashSet<IPAddress> _phone;
    readonly HashSet<IPAddress> _lan;

    public TrafficClassifier(IReadOnlyCollection<IPAddress> phone, IReadOnlyCollection<IPAddress> lan)
    {
        _phone = [.. phone.Select(Plain)];
        _lan = [.. lan.Select(Plain)];
    }

    public RouteExit? ExitOf(IPAddress a, IPAddress b)
    {
        a = Plain(a);
        b = Plain(b);
        var onPhone = _phone.Contains(a) || _phone.Contains(b);
        var onLan = _lan.Contains(a) || _lan.Contains(b);
        if (onPhone == onLan) return null; // neither adapter, or both (traffic between our own two adapters)

        var ours = onPhone ? _phone : _lan;
        var remote = ours.Contains(a) ? b : a;
        if (_phone.Contains(remote) || _lan.Contains(remote) || IsLocal(remote)) return null;
        return onPhone ? RouteExit.Phone : RouteExit.Lan;
    }

    /// Loopback, private, link-local, multicast, broadcast and unspecified addresses: not internet traffic.
    public static bool IsLocal(IPAddress ip)
    {
        ip = Plain(ip);
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] >= 224; // multicast, reserved and the broadcast address
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique local
    }

    static IPAddress Plain(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
