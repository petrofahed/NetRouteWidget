namespace NetRoute.Core;

/// Home is logical LAN-or-Wi-Fi; the other targets name literal connections.
public enum RuleTarget { Home, Lan, Wifi, Phone }

/// Pure selector decisions. Callers own execution, answering probes and override recovery timing.
public static class SmartRoutingPolicy
{
    public static RuleTarget TargetOf(ConnectionId id) => id switch
    {
        ConnectionId.Lan => RuleTarget.Lan,
        ConnectionId.Wifi => RuleTarget.Wifi,
        ConnectionId.Phone => RuleTarget.Phone,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    public static string Tag(ConnectionId id) => id switch
    {
        ConnectionId.Lan => "lan",
        ConnectionId.Wifi => "wifi",
        ConnectionId.Phone => "phone",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    public static string SelectorTag(RuleTarget target) => target switch
    {
        RuleTarget.Home => "only-home",
        RuleTarget.Lan => "only-lan",
        RuleTarget.Wifi => "only-wifi",
        RuleTarget.Phone => "only-phone",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    /// Auto/null uses the existing paused editor fallback; this does not permit execution.
    public static ConnectionId ProfileOf(NetworkStatus? net) =>
        net is null ? ConnectionId.Phone : Connections.Of(net.Mode) ?? ConnectionId.Phone;

    public static ConnectionId? WantedExit(NetworkStatus net)
    {
        if (Connections.Of(net.Mode) is null) return null;
        // ActivePath may describe our own TUN; use the qualified physical carrier instead.
        return net.Carrier is { } carrier && net.Adapters.IsUsed(carrier)
            && net.Adapters.Adapter(carrier) is { HasGateway: true, IPv4: not null }
            ? carrier
            : NetworkStatus.CarrierFor(net.Mode, net.HealTarget, net.Adapters);
    }

    public static IReadOnlyList<ConnectionId> Candidates(RuleTarget target, ConnectionId profile,
        ConnectionId currentPath, DetectionResult adapters, bool useMain)
    {
        // Reject unknown identities even in branches that do not otherwise need both inputs.
        _ = TargetOf(profile);
        _ = TargetOf(currentPath);
        var home = Connections.FallbackOrder.Where(id => !Connections.IsMetered(id));
        IEnumerable<ConnectionId> order = target switch
        {
            RuleTarget.Home => Connections.IsMetered(profile) ? home : new[] { profile }.Concat(home),
            RuleTarget.Lan => new[] { ConnectionId.Lan }.Concat(home),
            RuleTarget.Wifi => new[] { ConnectionId.Wifi }.Concat(home),
            RuleTarget.Phone => [ConnectionId.Phone, currentPath],
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };
        // Non-metered targets can reach mobile data only through this explicit user override.
        if (useMain) order = order.Append(currentPath);
        return order.Where(adapters.IsUsed).Distinct().ToList();
    }

    public static ConnectionId? Select(RuleTarget target, ConnectionId profile, ConnectionId currentPath,
        DetectionResult adapters, IReadOnlySet<ConnectionId> answering, bool useMain)
    {
        var candidates = Candidates(target, profile, currentPath, adapters, useMain);
        if (useMain && candidates.Contains(currentPath) && answering.Contains(currentPath)) return currentPath;
        return candidates.Where(answering.Contains).Select(id => (ConnectionId?)id).FirstOrDefault();
    }
}
