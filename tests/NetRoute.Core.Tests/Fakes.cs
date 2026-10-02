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
