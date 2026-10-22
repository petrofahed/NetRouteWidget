using System.Net;

namespace NetRoute.Core;

public enum IpFamily { IPv4, IPv6 }

public sealed record InterfaceMetricState(bool UseAutomatic, uint Metric);

public interface IInterfaceMetrics
{
    /// Null when the interface has no stack for this family (e.g. IPv6 disabled).
    InterfaceMetricState? Get(int ifIndex, IpFamily family);

    /// metric null = automatic. Throws when Windows rejects the change.
    void Set(int ifIndex, IpFamily family, uint? metric);
}

public interface IAdapterSource
{
    IReadOnlyList<AdapterInfo> GetAdapters();
}

public interface IRouteQuery
{
    /// Interface Windows would use to reach destination (IPv4 or IPv6); null when there is no route.
    int? GetBestInterfaceIndex(IPAddress destination);
}

public interface ILatencyProbe
{
    /// Round-trip in ms from the given local address; null when there is no reply.
    Task<int?> MeasureAsync(IPAddress source, CancellationToken ct);
}

public interface ISubnetRoutes
{
    /// RouteMetric of the on-link IPv4 route for `prefix` (e.g. "192.168.86.0/24") on ifIndex in ActiveStore; null when absent.
    uint? GetOnLinkMetric(int ifIndex, string prefix);
    /// Sets that route's RouteMetric in ActiveStore. Throws when Windows rejects it.
    void SetOnLinkMetric(int ifIndex, string prefix, uint metric);
}

public interface INetworkNameSource
{
    /// The Windows connection-profile name for the network this adapter is joined to (for Wi-Fi, the SSID);
    /// null when it can't be determined (not connected, lookup failed, etc.). Never throws.
    string? NameOf(AdapterInfo adapter);
}
