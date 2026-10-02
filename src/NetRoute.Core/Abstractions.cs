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
