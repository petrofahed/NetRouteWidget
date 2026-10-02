using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Tests;

sealed class FakeInterfaceMetrics : IInterfaceMetrics
{
    public Dictionary<(int, IpFamily), InterfaceMetricState> State { get; } = new();
    public HashSet<(int, IpFamily)> FailOnSet { get; } = new();
    public HashSet<(int, IpFamily)> IgnoreSet { get; } = new();
    public List<(int Index, IpFamily Family, uint? Metric)> SetCalls { get; } = new();

    /// Registers an interface with Windows-style automatic metric 25.
    public void Add(int index, bool ipv6 = true)
    {
        State[(index, IpFamily.IPv4)] = new(true, 25);
        if (ipv6) State[(index, IpFamily.IPv6)] = new(true, 25);
    }

    public InterfaceMetricState? Get(int ifIndex, IpFamily family) =>
        State.TryGetValue((ifIndex, family), out var s) ? s : null;

    public void Set(int ifIndex, IpFamily family, uint? metric)
    {
        SetCalls.Add((ifIndex, family, metric));
        if (FailOnSet.Contains((ifIndex, family))) throw new InvalidOperationException("Access is denied.");
        if (IgnoreSet.Contains((ifIndex, family))) return;
        State[(ifIndex, family)] = metric is { } m ? new(false, m) : new(true, 25);
    }
}

sealed class FakeAdapterSource : IAdapterSource
{
    public List<AdapterInfo> Adapters { get; } = new();
    public Exception? Throw { get; set; }

    public IReadOnlyList<AdapterInfo> GetAdapters() => Throw is { } ex ? throw ex : Adapters.ToList();
}

sealed class FakeRouteQuery : IRouteQuery
{
    public int? BestV4 { get; set; }
    public int? BestV6 { get; set; }

    public int? GetBestInterfaceIndex(IPAddress destination) =>
        destination.AddressFamily == AddressFamily.InterNetworkV6 ? BestV6 : BestV4;
}

sealed class FakeLatencyProbe : ILatencyProbe
{
    public Dictionary<string, int?> BySource { get; } = new();

    public Task<int?> MeasureAsync(IPAddress source, CancellationToken ct) =>
        Task.FromResult(BySource.TryGetValue(source.ToString(), out var ms) ? ms : null);
}

sealed class BlockingLatencyProbe : ILatencyProbe
{
    readonly TaskCompletionSource _gate = new();
    readonly Dictionary<string, int?> _bySource = new();

    public Task Release() => _gate.Task;
    public void SetResult() => _gate.TrySetResult();
    public void SetLatency(string source, int? ms) => _bySource[source] = ms;

    public async Task<int?> MeasureAsync(IPAddress source, CancellationToken ct)
    {
        await _gate.Task.ConfigureAwait(false);
        return _bySource.TryGetValue(source.ToString(), out var ms) ? ms : null;
    }
}
