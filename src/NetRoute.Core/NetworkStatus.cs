namespace NetRoute.Core;

public sealed record NetworkStatus(
    RoutingMode Mode,
    DetectionResult Adapters,
    InternetPath ActivePath,
    InternetPath Ipv6Path,
    IReadOnlyDictionary<ConnectionId, int?> LatencyMs,
    bool CanModify,
    string? Error,
    bool IsHealing = false,
    bool IsHealSticky = false)
{
    /// The connection healing currently targets as the backup; null when not healing.
    public ConnectionId? HealTarget { get; init; }
    public ConnectionId? Carrier { get; init; }
    public string? WifiNetwork { get; init; }

    public int? PhoneLatencyMs => LatencyMs.TryGetValue(ConnectionId.Phone, out var ms) ? ms : null;
    public int? LanLatencyMs => LatencyMs.TryGetValue(ConnectionId.Lan, out var ms) ? ms : null;
    public int? WifiLatencyMs => LatencyMs.TryGetValue(ConnectionId.Wifi, out var ms) ? ms : null;

    public NetworkStatus WithLatency(ConnectionId id, int? ms) =>
        this with { LatencyMs = new Dictionary<ConnectionId, int?>(LatencyMs) { [id] = ms } };

    /// The preferred adapter is not the one carrying internet.
    public bool IsFallback =>
        Connections.Of(Mode) is { } main && Connections.Of(ActivePath) is { } path && path != main;

    public static NetworkStatus Initial(RoutingMode mode, bool canModify) =>
        new(mode, DetectionResult.Empty, InternetPath.None, InternetPath.None, new Dictionary<ConnectionId, int?>(), canModify, null);

    /// Loops over the present adapters and returns the path of whichever one's Index matches.
    public static InternetPath ResolvePath(int? bestInterfaceIndex, DetectionResult adapters)
    {
        if (bestInterfaceIndex is null) return InternetPath.None;
        foreach (var (id, adapter) in adapters.Present)
            if (adapter.Index == bestInterfaceIndex) return Connections.PathOf(id);
        return InternetPath.Other;
    }

    /// The connection actually carrying internet: the main (healTarget, or the mode's pin) if it
    /// qualifies, else the first of the rest of the fallback order that does. Auto gives null.
    public static ConnectionId? CarrierFor(RoutingMode mode, ConnectionId? healTarget, DetectionResult adapters)
    {
        if ((healTarget ?? Connections.Of(mode)) is not { } main) return null;

        var order = new[] { main }.Concat(Connections.FallbackOrder.Where(id => id != main));
        foreach (var id in order)
            if (adapters.Adapter(id) is { HasGateway: true, IPv4: not null })
                return id;
        return null;
    }
}
