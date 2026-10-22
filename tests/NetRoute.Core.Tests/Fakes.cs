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

sealed class FakeSubnetRoutes : ISubnetRoutes
{
    public Dictionary<(int, string), uint> State { get; } = new();
    public List<(int IfIndex, string Prefix, uint Metric)> SetCalls { get; } = new();
    public bool FailOnSet { get; set; }

    /// Test-only convenience so fixtures can seed/inspect state as `routes[(10, "192.168.86.0/24")]`.
    public uint this[(int IfIndex, string Prefix) key]
    {
        get => State[key];
        set => State[key] = value;
    }

    public uint? GetOnLinkMetric(int ifIndex, string prefix) => State.TryGetValue((ifIndex, prefix), out var m) ? m : null;

    public void SetOnLinkMetric(int ifIndex, string prefix, uint metric)
    {
        SetCalls.Add((ifIndex, prefix, metric));
        if (FailOnSet) throw new InvalidOperationException("Access is denied.");
        State[(ifIndex, prefix)] = metric;
    }
}

sealed class FakeNetworkNameSource : INetworkNameSource
{
    public string? Result { get; set; }
    /// When set, consumed one value per call (in order) instead of `Result` - for tests that need the
    /// lookup to return different values on successive calls (e.g. null, then a real name).
    public Queue<string?>? Results { get; set; }
    public int CallCount { get; private set; }

    public string? NameOf(AdapterInfo adapter)
    {
        CallCount++;
        return Results is { Count: > 0 } queue ? queue.Dequeue() : Result;
    }
}

sealed class BlockingLatencyProbe : ILatencyProbe
{
    readonly TaskCompletionSource _gate = new();
    readonly Dictionary<string, int?> _bySource = new();

    public void SetLatency(string source, int? ms) => _bySource[source] = ms;
    public void Unblock() => _gate.TrySetResult();

    public async Task<int?> MeasureAsync(IPAddress source, CancellationToken ct)
    {
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return _bySource.TryGetValue(source.ToString(), out var ms) ? ms : null;
    }
}
