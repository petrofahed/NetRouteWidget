namespace NetRoute.Core;

/// Windows' own byte counters for an adapter: exact, per adapter, independent of sing-box.
public interface IAdapterCounters
{
    /// Bytes sent plus received since the adapter came up; null when no adapter has that name.
    long? TotalBytes(string adapterName);

    /// One interface's own up (sent) and down (received) byte counters, by interface index (AdapterInfo.Index);
    /// null when no interface has that index or Windows cannot read it.
    Traffic? Read(int interfaceIndex);
}
