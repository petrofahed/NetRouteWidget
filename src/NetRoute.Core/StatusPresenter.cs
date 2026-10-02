namespace NetRoute.Core;

public enum Dot { Green, Amber, Gray }

public enum TrayColor { Green, Blue, Gray }

public sealed record AdapterRow(string Label, string Address, string Latency, Dot Dot, bool IsActive);

public sealed record CardView(
    string Header, bool HeaderWarning, string? Note,
    AdapterRow Phone, AdapterRow Lan,
    string Tooltip, TrayColor TrayColor, bool TrayBadge);

/// Pure mapping from status to what the card, tray icon and toasts show.
public static class StatusPresenter
{
    public static CardView Present(NetworkStatus s)
    {
        var noInternet = s.ActivePath == InternetPath.None;
        return new CardView(
            HeaderText(s),
            HeaderWarning: s.Error is not null || s.IsFallback || noInternet,
            NoteText(s),
            Row("Phone", s.Adapters.Phone, s.Adapters.PhoneIssue, s.PhoneLatencyMs, s.ActivePath == InternetPath.Phone),
            Row("LAN", s.Adapters.Lan, s.Adapters.LanIssue, s.LanLatencyMs, s.ActivePath == InternetPath.Lan),
            TooltipText(s),
            s.Mode switch { RoutingMode.Phone => TrayColor.Green, RoutingMode.Lan => TrayColor.Blue, _ => TrayColor.Gray },
            TrayBadge: s.IsFallback || noInternet);
    }

    /// Message for an automatic (not user-initiated) change of internet path; null when the path did not change.
    public static string? ToastFor(NetworkStatus before, NetworkStatus after)
    {
        if (before.ActivePath == after.ActivePath) return null;
        return after.ActivePath switch
        {
            InternetPath.None => "No internet connection",
            InternetPath.Other => "Internet now via another adapter (VPN?)",
            InternetPath.Lan when after.IsFallback =>
                $"Phone {(after.Adapters.Phone is null ? "disconnected" : "lost internet")} — internet via LAN",
            InternetPath.Phone when after.IsFallback =>
                $"LAN {(after.Adapters.Lan is null ? "disconnected" : "lost internet")} — internet via Phone",
            InternetPath.Phone => after.Mode == RoutingMode.Phone ? "Phone back — internet via Phone" : "Internet now via Phone",
            _ => after.Mode == RoutingMode.Lan ? "LAN back — internet via LAN" : "Internet now via LAN",
        };
    }

    static string HeaderText(NetworkStatus s)
    {
        if (s.Error is { } error) return error;
        return s.ActivePath switch
        {
            InternetPath.None => "No internet",
            InternetPath.Other => "Internet via other adapter",
            InternetPath.Phone when s.IsFallback => $"Internet via PHONE ({Reason(s.Adapters.Lan, "LAN")})",
            InternetPath.Lan when s.IsFallback => $"Internet via LAN ({Reason(s.Adapters.Phone, "phone")})",
            InternetPath.Phone => "Internet via PHONE",
            _ => "Internet via LAN",
        };
    }

    static string Reason(AdapterInfo? preferred, string name) =>
        preferred is null ? $"{name} offline" : $"{name} not routing";

    static string? NoteText(NetworkStatus s) =>
        s.Ipv6Path != InternetPath.None && s.ActivePath != InternetPath.None && s.Ipv6Path != s.ActivePath
            ? $"IPv6 traffic goes via {PathName(s.Ipv6Path)}"
            : null;

    static AdapterRow Row(string label, AdapterInfo? adapter, DetectionIssue issue, int? latencyMs, bool active)
    {
        if (adapter is null)
            return new(label, issue == DetectionIssue.Ambiguous ? "choose adapter…" : "not connected", "", Dot.Gray, false);
        var address = adapter.IPv4 ?? "no IPv4";
        return latencyMs is { } ms
            ? new(label, address, $"{ms} ms", Dot.Green, active)
            : new(label, address, "no reply", Dot.Amber, active);
    }

    static string TooltipText(NetworkStatus s)
    {
        if (s.Error is { } error) return $"NetRoute: {error}";
        var path = s.ActivePath switch
        {
            InternetPath.Phone => $"Phone{Ms(s.PhoneLatencyMs)}",
            InternetPath.Lan => $"LAN{Ms(s.LanLatencyMs)}",
            InternetPath.Other => "other adapter",
            _ => "none",
        };
        var backup = s.ActivePath == InternetPath.Lan
            ? $"Phone {(s.Adapters.Phone is null ? "offline" : "ready")}"
            : $"LAN {(s.Adapters.Lan is null ? "offline" : "ready")}";
        return $"Internet: {path} · {backup}";
    }

    static string Ms(int? ms) => ms is { } v ? $" ({v} ms)" : "";

    static string PathName(InternetPath path) => path switch
    {
        InternetPath.Phone => "Phone",
        InternetPath.Lan => "LAN",
        _ => "another adapter",
    };
}
