using System.Text.RegularExpressions;

namespace NetRoute.Core;

public enum DetectionIssue { None, NotFound, Ambiguous }

/// User choices that win over auto-detection. Phone is matched by description
/// (tethering MACs can be randomised), the LAN by MAC address.
public sealed record AdapterOverrides(string? PhoneDescription, string? LanMac)
{
    public static readonly AdapterOverrides None = new(null, null);
}

public sealed record DetectionResult(
    AdapterInfo? Phone, DetectionIssue PhoneIssue,
    AdapterInfo? Lan, DetectionIssue LanIssue)
{
    public static readonly DetectionResult Empty = new(null, DetectionIssue.NotFound, null, DetectionIssue.NotFound);
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

    public static DetectionResult Detect(IReadOnlyList<AdapterInfo> adapters, AdapterOverrides overrides)
    {
        var up = adapters.Where(a => a.IsUp && !IsVirtual(a)).ToList();

        var phones = overrides.PhoneDescription is { } description
            ? up.Where(a => NormalizeDescription(a.Description) == NormalizeDescription(description)).ToList()
            : up.Where(IsPhone).ToList();

        var lans = overrides.LanMac is { } mac
            ? up.Where(a => string.Equals(a.Mac, mac, StringComparison.OrdinalIgnoreCase)).ToList()
            : up.Where(a => a.Kind == AdapterKind.Ethernet && a.HasGateway && !IsPhone(a)).ToList();

        // An adapter chosen as the phone can never also be the LAN.
        lans.RemoveAll(l => phones.Any(p => p.Index == l.Index));

        var (phone, phoneIssue) = Pick(phones);
        var (lan, lanIssue) = Pick(lans);
        return new DetectionResult(phone, phoneIssue, lan, lanIssue);
    }

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
