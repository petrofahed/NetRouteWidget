namespace NetRoute.Core;

public enum Dot { Green, Amber, Gray }

public enum TrayColor { Green, Blue, Gray, Purple }

public sealed record AdapterRow(string Label, string Address, string Latency, Dot Dot, bool IsActive);

public sealed record ConnectionRow(ConnectionId Id, AdapterRow Row);

public sealed record CardView(
    string Header, bool HeaderWarning, string? Note,
    IReadOnlyList<ConnectionRow> Rows,
    string Tooltip, TrayColor TrayColor, bool TrayBadge)
{
    /// Compatibility helpers for callers written before Wi-Fi existed: a used connection's row,
    /// or a "not connected" placeholder for one that (unexpectedly) isn't in Rows.
    public AdapterRow Row(ConnectionId id) =>
        Rows.FirstOrDefault(r => r.Id == id)?.Row ?? new AdapterRow(Connections.Label(id), "not connected", "", Dot.Gray, false);

    public AdapterRow Phone => Row(ConnectionId.Phone);
    public AdapterRow Lan => Row(ConnectionId.Lan);
}

/// Pure mapping from status to what the card, tray icon and toasts show.
public static class StatusPresenter
{
    /// A reply this slow (ms) or slower is amber: the adapter answers, but slowly (e.g. a congested link).
    public const int SlowLatencyMs = 500;

    public static CardView Present(NetworkStatus s)
    {
        var noInternet = s.ActivePath == InternetPath.None;
        var rows = Connections.CardOrder
            .Where(id => s.Adapters.IsUsed(id))
            .Select(id => new ConnectionRow(id, Row(id, s)))
            .ToList();
        return new CardView(
            HeaderText(s),
            HeaderWarning: s.Error is not null || s.IsFallback || noInternet,
            NoteText(s),
            rows,
            TooltipText(s),
            s.Mode switch
            {
                RoutingMode.Phone => TrayColor.Green,
                RoutingMode.Lan => TrayColor.Blue,
                RoutingMode.Wifi => TrayColor.Purple,
                RoutingMode.Auto => TrayColor.Gray,
                _ => throw new ArgumentOutOfRangeException(nameof(s.Mode), s.Mode, null),
            },
            TrayBadge: s.IsFallback || noInternet);
    }

    /// Message for an automatic (not user-initiated) change of internet path; null when the path did not change.
    public static string? ToastFor(NetworkStatus before, NetworkStatus after)
    {
        if (before.ActivePath == after.ActivePath) return null;
        if (after.ActivePath == InternetPath.None) return "No internet connection";
        if (Connections.Of(after.ActivePath) is not { } active) return "Internet now via another adapter (VPN?)";

        if (after.IsFallback)
        {
            var main = Connections.Of(after.Mode)!.Value;
            var verb = after.Adapters.Adapter(main) is null ? "disconnected" : "lost internet";
            return $"{Connections.Label(main)} {verb} — internet via {Connections.Label(active)}";
        }

        return after.Mode == Connections.ModeOf(active)
            ? $"{Connections.Label(active)} back — internet via {Connections.Label(active)}"
            : $"Internet now via {Connections.Label(active)}";
    }

    /// Message for the moment a heal becomes sticky: the app stops switching back on its own.
    public static string StickyHealToast(RoutingMode mode, ConnectionId target) =>
        $"{Connections.Label(Connections.Of(mode)!.Value)} keeps losing internet — staying on {Connections.Label(target)}. " +
        $"Click {Connections.Label(Connections.Of(mode)!.Value)} to retry.";

    static string HeaderText(NetworkStatus s)
    {
        if (s.Error is { } error) return error;
        if (s.ActivePath == InternetPath.None) return "No internet";
        if (Connections.Of(s.ActivePath) is not { } active) return "Internet via other adapter";

        var header = $"Internet via {Connections.HeaderName(active)}";
        if (!s.IsFallback) return header;

        var main = Connections.Of(s.Mode)!.Value;
        return $"{header} ({Reason(main, s)})";
    }

    /// Reason text for the main connection not currently carrying internet. Uses main's label,
    /// lower-cased for Phone only (today's wording), and main's normal (capitalized) button label.
    static string Reason(ConnectionId main, NetworkStatus s) =>
        s.Adapters.Adapter(main) is null ? $"{ReasonName(main)} offline"
        : s.IsHealSticky ? $"{ReasonName(main)} keeps losing internet — click {Connections.Label(main)} to retry"
        : s.IsHealing ? $"{ReasonName(main)} has no internet"
        : $"{ReasonName(main)} not routing";

    /// "phone" for Phone (today's lower-case convention in the Reason parenthetical), else the
    /// normal label ("LAN", "Wi-Fi" — neither should be force-lowercased like a plain word).
    static string ReasonName(ConnectionId id) => id == ConnectionId.Phone ? Connections.Label(id).ToLowerInvariant() : Connections.Label(id);

    static string? NoteText(NetworkStatus s) =>
        s.Ipv6Path != InternetPath.None && s.ActivePath != InternetPath.None && s.Ipv6Path != s.ActivePath
            ? $"IPv6 traffic goes via {PathName(s.Ipv6Path)}"
            : null;

    static AdapterRow Row(ConnectionId id, NetworkStatus s)
    {
        var label = id == ConnectionId.Wifi ? s.WifiNetwork ?? "Wi-Fi" : Connections.Label(id);
        var latencyMs = s.LatencyMs.TryGetValue(id, out var ms) ? ms : null;
        return Row(label, s.Adapters.Adapter(id), s.Adapters.Issue(id), latencyMs, s.ActivePath == Connections.PathOf(id));
    }

    static AdapterRow Row(string label, AdapterInfo? adapter, DetectionIssue issue, int? latencyMs, bool active)
    {
        if (adapter is null)
            return new(label, issue == DetectionIssue.Ambiguous ? "choose adapter…" : "not connected", "", Dot.Gray, false);
        var address = adapter.IPv4 ?? "no IPv4";
        return latencyMs is { } ms
            ? new(label, address, $"{ms} ms", ms >= SlowLatencyMs ? Dot.Amber : Dot.Green, active)
            : new(label, address, "no reply", Dot.Amber, active);
    }

    static string TooltipText(NetworkStatus s)
    {
        if (s.Error is { } error) return $"NetRoute: {error}";
        var active = Connections.Of(s.ActivePath);
        var path = active is { } id
            ? $"{Connections.Label(id)}{Ms(s.LatencyMs.TryGetValue(id, out var ms) ? ms : null)}"
            : s.ActivePath == InternetPath.Other ? "other adapter" : "none";

        // When there is no active connection of ours (None/Other), there's no "the rest" to
        // enumerate against; today's convention is a single line naming one backup, not every
        // used connection (the existing 2-connection fixture pins this to exactly one entry).
        // Pick the last used connection in CardOrder, instead of hardcoding LAN, so a setup
        // without a LAN (e.g. Wi-Fi as the only backup) is reported correctly.
        var others = active is { } a
            ? Connections.CardOrder.Where(id => id != a && s.Adapters.IsUsed(id))
                .Select(id => $"{Connections.Label(id)} {(s.Adapters.Adapter(id) is null ? "offline" : "ready")}")
                .ToList()
            : Connections.CardOrder.Where(id => s.Adapters.IsUsed(id)).Cast<ConnectionId?>().LastOrDefault() is { } fallback
                ? [$"{Connections.Label(fallback)} {(s.Adapters.Adapter(fallback) is null ? "offline" : "ready")}"]
                : [];

        return others.Count == 0 ? $"Internet: {path}" : $"Internet: {path} · {string.Join(" · ", others)}";
    }

    static string Ms(int? ms) => ms is { } v ? $" ({v} ms)" : "";

    static string PathName(InternetPath path) => Connections.Of(path) is { } id ? Connections.Label(id) : "another adapter";
}
