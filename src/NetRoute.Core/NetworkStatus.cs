namespace NetRoute.Core;

public sealed record NetworkStatus(
    RoutingMode Mode,
    DetectionResult Adapters,
    InternetPath ActivePath,
    InternetPath Ipv6Path,
    int? PhoneLatencyMs,
    int? LanLatencyMs,
    bool CanModify,
    string? Error,
    bool IsHealing = false)
{
    /// The preferred adapter is not the one carrying internet.
    public bool IsFallback =>
        (Mode == RoutingMode.Phone && ActivePath == InternetPath.Lan) ||
        (Mode == RoutingMode.Lan && ActivePath == InternetPath.Phone);

    public static NetworkStatus Initial(RoutingMode mode, bool canModify) =>
        new(mode, DetectionResult.Empty, InternetPath.None, InternetPath.None, null, null, canModify, null);

    public static InternetPath ResolvePath(int? bestInterfaceIndex, DetectionResult adapters) => bestInterfaceIndex switch
    {
        null => InternetPath.None,
        var i when adapters.Phone?.Index == i => InternetPath.Phone,
        var i when adapters.Lan?.Index == i => InternetPath.Lan,
        _ => InternetPath.Other,
    };
}
