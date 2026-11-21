namespace NetRoute.Core;

/// Windows' own byte counters for an adapter: exact, per adapter, independent of sing-box.
public interface IAdapterCounters
{
    /// Bytes sent plus received since the adapter came up; null when no adapter has that name.
    long? TotalBytes(string adapterName);

    /// Every adapter's own up and down byte counters, keyed by adapter name (case-insensitive), from one
    /// enumeration. Throws NetworkInformationException when Windows cannot enumerate the adapters.
    IReadOnlyDictionary<string, Traffic> ReadAll();
}
