namespace NetRoute.Core;

public enum RoutingMode { Phone, Lan, Auto }

public enum AdapterKind { Ethernet, Wireless, Other }

/// Which detected adapter Windows would use for internet traffic right now.
public enum InternetPath { None, Phone, Lan, Other }

public sealed record AdapterInfo(
    int Index,
    string Name,
    string Description,
    AdapterKind Kind,
    bool IsUp,
    bool HasGateway,
    string? IPv4,
    string Mac,
    string? DnsServer = null);
