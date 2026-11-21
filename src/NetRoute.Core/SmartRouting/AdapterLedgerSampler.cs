namespace NetRoute.Core;

/// Turns the slot-to-adapter mapping plus one counters enumeration into the readings AdapterLedger.Update wants.
/// Always on and independent of Smart routing: every slot is sampled, including one the user turned off on the card.
///   - The mapping is re-detected at most every MappingRefresh, or after InvalidateMapping (a network change).
///   - A slot's adapter name is remembered whenever detection finds its adapter, and used only while the adapter is
///     NotFound (unplugged, or down but still counting). Not used and Ambiguous forget it.
///   - Two slots that resolve to one adapter name are counted once: the first in Connections.All order.
///   - A slot is in the result only when it has a name AND a counters row.
/// Not thread-safe: callers must serialize all calls.
public sealed class AdapterLedgerSampler
{
    public static readonly TimeSpan MappingRefresh = TimeSpan.FromSeconds(10);

    readonly IAdapterSource _adapters;
    readonly IAdapterCounters _counters;
    readonly Func<AdapterOverrides> _overrides;
    readonly Action<string> _log;
    readonly Func<DateTimeOffset> _now;

    readonly Dictionary<ConnectionId, string> _remembered = new();
    IReadOnlyList<(ConnectionId Id, string Name)> _mapping = [];
    DateTimeOffset? _mappedAt;
    bool _invalidated;
    bool _detectFailed;
    bool _readFailed;

    public AdapterLedgerSampler(IAdapterSource adapters, IAdapterCounters counters, Func<AdapterOverrides> overrides,
        Action<string> log, Func<DateTimeOffset> now)
    {
        _adapters = adapters;
        _counters = counters;
        _overrides = overrides;
        _log = log;
        _now = now;
    }

    /// The next Sample re-detects the mapping (the app calls this on network-change events).
    public void InvalidateMapping() => _invalidated = true;

    /// A reading for every slot that has an adapter name and a counters row. Empty when Windows cannot enumerate the counters.
    public IReadOnlyDictionary<ConnectionId, AdapterReading> Sample()
    {
        RefreshMappingIfDue();

        IReadOnlyDictionary<string, Traffic> all;
        try
        {
            all = _counters.ReadAll();
            _readFailed = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_readFailed) _log($"Adapter ledger: reading the adapters' byte counters failed: {ex.Message}");
            _readFailed = true; // logged once per failure streak: this runs every second
            return new Dictionary<ConnectionId, AdapterReading>();
        }

        var readings = new Dictionary<ConnectionId, AdapterReading>();
        foreach (var (id, name) in _mapping)
        {
            if (all.TryGetValue(name, out var bytes)) readings[id] = new AdapterReading(name, bytes);
        }
        return readings;
    }

    void RefreshMappingIfDue()
    {
        var now = _now();
        if (!_invalidated && _mappedAt is { } at && now - at < MappingRefresh) return;

        _invalidated = false;
        _mappedAt = now; // a failed detection is retried after MappingRefresh too, not every second

        DetectionResult detected;
        try
        {
            detected = AdapterDetector.Detect(_adapters.GetAdapters(), _overrides());
            _detectFailed = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_detectFailed) _log($"Adapter ledger: detecting the adapters failed, keeping the previous mapping: {ex.Message}");
            _detectFailed = true;
            return;
        }

        var mapping = new List<(ConnectionId, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in Connections.All)
        {
            var name = NameFor(id, detected);
            if (name is not null && seen.Add(name)) mapping.Add((id, name));
        }
        _mapping = mapping;
    }

    string? NameFor(ConnectionId id, DetectionResult detected)
    {
        if (detected.Adapter(id) is { } adapter)
        {
            _remembered[id] = adapter.Name;
            return adapter.Name;
        }

        if (detected.Issue(id) == DetectionIssue.NotFound) return _remembered.GetValueOrDefault(id);

        _remembered.Remove(id); // Not used or Ambiguous
        return null;
    }
}
