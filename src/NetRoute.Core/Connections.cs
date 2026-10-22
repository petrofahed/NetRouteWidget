namespace NetRoute.Core;

/// A connection the app can route internet traffic through.
public enum ConnectionId { Lan, Wifi, Phone }

/// Fixed facts about each connection: its order, its routing mode and internet path, and its labels.
public static class Connections
{
    /// Order in which healing and backup metrics try connections.
    public static IReadOnlyList<ConnectionId> FallbackOrder { get; } = [ConnectionId.Lan, ConnectionId.Wifi, ConnectionId.Phone];

    /// Order of card rows, mode buttons and tray items.
    public static IReadOnlyList<ConnectionId> CardOrder { get; } = [ConnectionId.Phone, ConnectionId.Lan, ConnectionId.Wifi];

    public static IReadOnlyList<ConnectionId> All { get; } = [ConnectionId.Lan, ConnectionId.Wifi, ConnectionId.Phone];

    /// Only the phone's data plan is metered.
    public static bool IsMetered(ConnectionId id) => id switch
    {
        ConnectionId.Phone => true,
        ConnectionId.Lan => false,
        ConnectionId.Wifi => false,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    /// The connection a routing mode pins to; null for Auto, which pins to none.
    public static ConnectionId? Of(RoutingMode mode) => mode switch
    {
        RoutingMode.Phone => ConnectionId.Phone,
        RoutingMode.Lan => ConnectionId.Lan,
        RoutingMode.Wifi => ConnectionId.Wifi,
        RoutingMode.Auto => null,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static RoutingMode ModeOf(ConnectionId id) => id switch
    {
        ConnectionId.Phone => RoutingMode.Phone,
        ConnectionId.Lan => RoutingMode.Lan,
        ConnectionId.Wifi => RoutingMode.Wifi,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    public static InternetPath PathOf(ConnectionId id) => id switch
    {
        ConnectionId.Phone => InternetPath.Phone,
        ConnectionId.Lan => InternetPath.Lan,
        ConnectionId.Wifi => InternetPath.Wifi,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    /// The connection an internet path belongs to; null for None and Other, which are not one of ours.
    public static ConnectionId? Of(InternetPath path) => path switch
    {
        InternetPath.Phone => ConnectionId.Phone,
        InternetPath.Lan => ConnectionId.Lan,
        InternetPath.Wifi => ConnectionId.Wifi,
        InternetPath.None => null,
        InternetPath.Other => null,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
    };

    /// Short name used in labels, e.g. "Wi-Fi".
    public static string Label(ConnectionId id) => id switch
    {
        ConnectionId.Phone => "Phone",
        ConnectionId.Lan => "LAN",
        ConnectionId.Wifi => "Wi-Fi",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    /// Name used in the card header, e.g. "Wi-Fi".
    public static string HeaderName(ConnectionId id) => id switch
    {
        ConnectionId.Phone => "PHONE",
        ConnectionId.Lan => "LAN",
        ConnectionId.Wifi => "Wi-Fi",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };
}
