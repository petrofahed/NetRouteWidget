namespace NetRoute.Core;

/// Pins the LAN's on-link subnet route to metric 0 so local traffic stays on the cable even when
/// another connection (typically Wi-Fi) shares that subnet and would otherwise win it on interface
/// metric alone. Sing-box (Smart routing) never touches local-subnet traffic, so this pin applies
/// uniformly whether Smart routing is on or off.
///
/// Stateful: constructed once and reused across refreshes, so Sync can tell "already applied" (no
/// write needed) from "wanted but drifted back" (DHCP renewal, a Windows update, etc. - needs a
/// fresh write) by reading the real current metric back, not just trusting its own cached state.
public sealed class RoutePin(ISubnetRoutes routes, Action<string> log)
{
    public const uint PinnedMetric = 0;
    public const uint DefaultMetric = 256;

    (int IfIndex, string Prefix)? _pinned;

    /// The LAN's (index, subnet) to pin when all of: the mode is not Auto; the LAN is present with
    /// an IPv4 address and a prefix; some other present connection's IPv4 falls inside that same
    /// subnet; and that connection's metric (MetricPolicy.For(mode, carrier)) is lower than the
    /// LAN's. Otherwise null.
    public static (int IfIndex, string Prefix)? Wanted(RoutingMode mode, ConnectionId? carrier, DetectionResult adapters)
    {
        if (mode == RoutingMode.Auto) return null;
        if (adapters.Lan is not { IPv4: { } lanIp, PrefixLength: { } lanPrefix } lan) return null;

        var lanSubnet = AdapterInfo.SubnetOf(lanIp, lanPrefix);
        if (lanSubnet is null) return null;

        var metrics = MetricPolicy.For(mode, carrier);
        if (metrics.GetValueOrDefault(ConnectionId.Lan) is not { } lanMetric) return null;

        foreach (var (id, adapter) in adapters.Present)
        {
            if (id == ConnectionId.Lan || adapter.IPv4 is null || adapter.PrefixLength is null) continue;
            if (AdapterInfo.SubnetOf(adapter.IPv4, adapter.PrefixLength.Value) != lanSubnet) continue;
            if (metrics.GetValueOrDefault(id) is { } otherMetric && otherMetric < lanMetric)
                return (lan.Index, lanSubnet);
        }
        return null;
    }

    /// Applies `wanted` (pins it to PinnedMetric when its current metric isn't already 0) and
    /// reverts whatever was previously pinned back to DefaultMetric when the pin moved or is no
    /// longer wanted. Revert and apply run under separate try/catch so one failing never blocks
    /// the other (e.g. a pin that moved to a new interface/subnet still gets applied even if
    /// reverting the old one failed). Never throws: any failure from `routes` is caught and logged.
    public void Sync((int IfIndex, string Prefix)? wanted)
    {
        if (_pinned is { } previous && previous != wanted)
        {
            try
            {
                // The old route may be gone entirely (cable unplugged, adapter re-enumerated with a
                // new index): nothing to revert then, and trying would just throw ERROR_NOT_FOUND.
                if (routes.GetOnLinkMetric(previous.IfIndex, previous.Prefix) is not null)
                {
                    routes.SetOnLinkMetric(previous.IfIndex, previous.Prefix, DefaultMetric);
                    log($"Route pin: released {previous.Prefix} on interface {previous.IfIndex} (metric {DefaultMetric})");
                }
            }
            catch (Exception ex)
            {
                log($"Route pin failed: {ex.Message}");
            }
            finally
            {
                _pinned = null;
            }
        }

        if (wanted is { } w)
        {
            try
            {
                if (routes.GetOnLinkMetric(w.IfIndex, w.Prefix) != PinnedMetric)
                {
                    routes.SetOnLinkMetric(w.IfIndex, w.Prefix, PinnedMetric);
                    log($"Route pin: pinned {w.Prefix} on interface {w.IfIndex} (metric {PinnedMetric}) so local traffic stays on the cable");
                }
                _pinned = w;
            }
            catch (Exception ex)
            {
                log($"Route pin failed: {ex.Message}");
            }
        }
    }

    /// Reverts whatever is currently pinned (if anything) back to DefaultMetric.
    public void Release() => Sync(null);
}
