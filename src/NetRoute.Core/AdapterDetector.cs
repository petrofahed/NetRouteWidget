using System.Text.RegularExpressions;

namespace NetRoute.Core;

/// TurnedOff: the user switched a chosen connection off on the card. It counts as Not used everywhere except the card row.
public enum DetectionIssue { None, NotFound, Ambiguous, NotUsed, TurnedOff }

/// User choices that win over auto-detection. Phone is matched by description
/// (tethering MACs can be randomised), the LAN and Wi-Fi by MAC address.
/// "none" (see NotUsed) marks a slot the user has turned off.
public sealed record AdapterOverrides(string? PhoneDescription, string? LanMac, string? WifiMac = null)
{
    public const string NotUsed = "none";

    public static readonly AdapterOverrides None = new(null, null);
}

/// One adapter (or lack of one) per connection slot.
public sealed record DetectionResult(IReadOnlyDictionary<ConnectionId, (AdapterInfo? Adapter, DetectionIssue Issue)> Slots)
{
    public static readonly DetectionResult Empty = new(Connections.All.ToDictionary(
        id => id,
        id => (default(AdapterInfo), id == ConnectionId.Wifi ? DetectionIssue.NotUsed : DetectionIssue.NotFound)));

    public static DetectionResult Of(AdapterInfo? phone, DetectionIssue phoneIssue, AdapterInfo? lan, DetectionIssue lanIssue,
        AdapterInfo? wifi = null, DetectionIssue wifiIssue = DetectionIssue.NotUsed) =>
        new(new Dictionary<ConnectionId, (AdapterInfo?, DetectionIssue)>
        {
            [ConnectionId.Phone] = (phone, phoneIssue),
            [ConnectionId.Lan] = (lan, lanIssue),
            [ConnectionId.Wifi] = (wifi, wifiIssue),
        });

    /// The adapters of turned-off slots, which their slots no longer hold. Only used to name the path when Windows
    /// still routes through one of them (it keeps its automatic metric).
    public IReadOnlyDictionary<ConnectionId, AdapterInfo> OffAdapters { get; init; } = new Dictionary<ConnectionId, AdapterInfo>();

    public AdapterInfo? Adapter(ConnectionId id) => Slots[id].Adapter;
    public DetectionIssue Issue(ConnectionId id) => Slots[id].Issue;
    public bool IsUsed(ConnectionId id) => Issue(id) is not (DetectionIssue.NotUsed or DetectionIssue.TurnedOff);
    public bool IsTurnedOff(ConnectionId id) => Issue(id) == DetectionIssue.TurnedOff;

    /// All detected adapters, in Connections.All order.
    public IEnumerable<(ConnectionId Id, AdapterInfo Adapter)> Present =>
        Connections.All.Where(id => Adapter(id) is not null).Select(id => (id, Adapter(id)!));

    public AdapterInfo? Phone => Adapter(ConnectionId.Phone);
    public DetectionIssue PhoneIssue => Issue(ConnectionId.Phone);
    public AdapterInfo? Lan => Adapter(ConnectionId.Lan);
    public DetectionIssue LanIssue => Issue(ConnectionId.Lan);
    public AdapterInfo? Wifi => Adapter(ConnectionId.Wifi);
    public DetectionIssue WifiIssue => Issue(ConnectionId.Wifi);

    /// The default record equality compares Slots by reference (it's a dictionary); compare by
    /// value instead, slot by slot, so two results with the same picks are equal.
    public bool Equals(DetectionResult? other) =>
        other is not null && Connections.All.All(id => Slots[id] == other.Slots[id]
            && Equals(OffAdapters.GetValueOrDefault(id), other.OffAdapters.GetValueOrDefault(id)));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in Connections.All)
        {
            hash.Add(Slots[id]);
            hash.Add(OffAdapters.GetValueOrDefault(id));
        }
        return hash.ToHashCode();
    }
}

public static partial class AdapterDetector
{
    static readonly string[] PhoneMarkers = ["Remote NDIS", "Apple Mobile Device Ethernet"];

    static readonly string[] VirtualMarkers =
    [
        "VMware", "Hyper-V", "Virtual", "vEthernet", "VirtualBox", "WireGuard", "NordLynx",
        "TAP-", "Wintun", "Tunnel", "VPN", "Loopback", "Bluetooth",
        "NetRoute", "sing-tun", // the Smart routing sing-box TUN
    ];

    public static bool IsPhone(AdapterInfo a) => ContainsAny(a.Description, PhoneMarkers);

    public static bool IsVirtual(AdapterInfo a) =>
        ContainsAny(a.Description, VirtualMarkers) || ContainsAny(a.Name, VirtualMarkers);

    /// Adapters a user may pick manually: real network hardware, up or down.
    public static IEnumerable<AdapterInfo> Candidates(IEnumerable<AdapterInfo> adapters) =>
        adapters.Where(a => !IsVirtual(a) && (a.Kind != AdapterKind.Other || IsPhone(a)));

    /// A connection in turnedOff is detected as usual (so the others still exclude its candidates), then its slot is
    /// emptied and marked TurnedOff. Not used wins: a Not-used slot stays NotUsed.
    public static DetectionResult Detect(IReadOnlyList<AdapterInfo> adapters, AdapterOverrides overrides, IReadOnlyCollection<ConnectionId>? turnedOff = null)
    {
        var up = adapters.Where(a => a.IsUp && !IsVirtual(a)).ToList();

        var (phoneCandidates, phone, phoneIssue) = DetectPhone(up, overrides);
        var (wifiCandidates, wifi, wifiIssue) = DetectWifi(adapters, overrides, phoneCandidates);
        var (lan, lanIssue) = DetectLan(up, overrides, phoneCandidates, wifiCandidates);

        var result = DetectionResult.Of(phone, phoneIssue, lan, lanIssue, wifi, wifiIssue);
        if (turnedOff is null || turnedOff.Count == 0) return result;

        var slots = new Dictionary<ConnectionId, (AdapterInfo?, DetectionIssue)>(result.Slots);
        var off = new Dictionary<ConnectionId, AdapterInfo>();
        foreach (var id in turnedOff.Distinct())
        {
            if (result.Issue(id) == DetectionIssue.NotUsed) continue;
            if (result.Adapter(id) is { } adapter) off[id] = adapter;
            slots[id] = (null, DetectionIssue.TurnedOff);
        }
        return new DetectionResult(slots) { OffAdapters = off };
    }

    static (List<AdapterInfo> Candidates, AdapterInfo? Adapter, DetectionIssue Issue) DetectPhone(List<AdapterInfo> up, AdapterOverrides overrides)
    {
        if (overrides.PhoneDescription == AdapterOverrides.NotUsed)
            return ([], null, DetectionIssue.NotUsed);

        var phones = overrides.PhoneDescription is { } description
            ? up.Where(a => NormalizeDescription(a.Description) == NormalizeDescription(description)).ToList()
            : up.Where(IsPhone).ToList();

        var (phone, issue) = Pick(phones);
        return (phones, phone, issue);
    }

    static (List<AdapterInfo> Candidates, AdapterInfo? Adapter, DetectionIssue Issue) DetectWifi(
        IReadOnlyList<AdapterInfo> adapters, AdapterOverrides overrides, List<AdapterInfo> phoneCandidates)
    {
        if (overrides.WifiMac is null or AdapterOverrides.NotUsed)
            return ([], null, DetectionIssue.NotUsed);

        var wifis = adapters
            .Where(a => a.IsUp && !IsVirtual(a) && string.Equals(a.Mac, overrides.WifiMac, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // An adapter chosen (or merely a candidate) for the phone can never also be the Wi-Fi.
        wifis.RemoveAll(w => phoneCandidates.Any(p => p.Index == w.Index));

        var (wifi, issue) = Pick(wifis);
        return (wifis, wifi, issue);
    }

    static (AdapterInfo?, DetectionIssue) DetectLan(
        List<AdapterInfo> up, AdapterOverrides overrides, List<AdapterInfo> phoneCandidates, List<AdapterInfo> wifiCandidates)
    {
        if (overrides.LanMac == AdapterOverrides.NotUsed)
            return (null, DetectionIssue.NotUsed);

        var lans = overrides.LanMac is { } mac
            ? up.Where(a => string.Equals(a.Mac, mac, StringComparison.OrdinalIgnoreCase)).ToList()
            : up.Where(a => a.Kind == AdapterKind.Ethernet && a.HasGateway && !IsPhone(a)).ToList();

        // An adapter chosen (or merely a candidate) for the phone or Wi-Fi can never also be the LAN.
        lans.RemoveAll(l => phoneCandidates.Any(p => p.Index == l.Index) || wifiCandidates.Any(w => w.Index == l.Index));

        return Pick(lans);
    }

    /// What Detect would pick with no overrides, as concrete override values. A slot with zero
    /// or several candidates is reported as "none" rather than guessed.
    public static AdapterOverrides Suggest(IReadOnlyList<AdapterInfo> adapters)
    {
        var up = adapters.Where(a => a.IsUp && !IsVirtual(a)).ToList();

        var phones = up.Where(IsPhone).ToList();
        var (phone, _) = Pick(phones);

        var wireless = adapters.Where(a => !IsVirtual(a) && a.Kind == AdapterKind.Wireless).ToList();
        var (wifi, _) = Pick(wireless);

        var lans = up.Where(a => a.Kind == AdapterKind.Ethernet && a.HasGateway && !IsPhone(a)).ToList();
        var (lan, _) = Pick(lans);

        return new AdapterOverrides(
            phone?.Description ?? AdapterOverrides.NotUsed,
            lan?.Mac ?? AdapterOverrides.NotUsed,
            wifi?.Mac ?? AdapterOverrides.NotUsed);
    }

    /// A human-readable summary of an adapter for the chosen slot, e.g.
    /// "192.168.86.42 · gateway 192.168.86.1 · 1 Gbps". A down adapter shows just "not connected".
    public static string DetailLine(AdapterInfo adapter, ConnectionId slot)
    {
        if (!adapter.IsUp) return "not connected";

        var parts = new List<string> { adapter.IPv4 ?? "no IPv4" };
        if (adapter.Gateway is { } gateway) parts.Add("gateway " + gateway);

        if (slot == ConnectionId.Phone) parts.Add("USB tethering");
        else if (adapter.SpeedBps is { } speed && SpeedLabel(speed) is { } label) parts.Add(label);

        return string.Join(" · ", parts);
    }

    static string? SpeedLabel(long speedBps) => speedBps switch
    {
        >= 1_000_000_000 => $"{Round(speedBps, 1_000_000_000)} Gbps",
        >= 1_000_000 => $"{Round(speedBps, 1_000_000)} Mbps",
        _ => null,
    };

    static long Round(long value, long unit) => (long)Math.Round(value / (double)unit, MidpointRounding.AwayFromZero);

    /// Windows appends " #2", " #3"… when the same device is re-plugged.
    internal static string NormalizeDescription(string description) =>
        NumberSuffix().Replace(description.Trim(), "").ToUpperInvariant();

    static (AdapterInfo?, DetectionIssue) Pick(List<AdapterInfo> found) => found.Count switch
    {
        0 => (null, DetectionIssue.NotFound),
        1 => (found[0], DetectionIssue.None),
        _ => (null, DetectionIssue.Ambiguous),
    };

    static bool ContainsAny(string text, string[] markers) =>
        markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\s+#\d+$")]
    private static partial Regex NumberSuffix();
}
