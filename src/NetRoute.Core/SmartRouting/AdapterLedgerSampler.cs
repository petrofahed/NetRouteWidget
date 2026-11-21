namespace NetRoute.Core;

/// Turns the slot-to-adapter mapping plus one counters read per mapped slot into the readings AdapterLedger.Update wants.
/// Always on and independent of Smart routing: every slot is sampled, including one the user turned off on the card.
///   - The mapping (each slot's adapter name and interface index) is re-detected at most every MappingRefresh, or after
///     InvalidateMapping (a network change). Each tick then reads only the mapped interfaces, by index (at most three).
///   - A slot's adapter name and index are remembered whenever detection finds its adapter, and used only while the
///     adapter is NotFound (unplugged, or down but still counting). Not used and Ambiguous forget them.
///   - Two slots that resolve to one adapter (same name or same index) are counted once: slots whose adapter was detected
///     live claim theirs first, remembered ones only what is left, each in Connections.All order.
///   - A slot is in the result only when it has a mapping AND its read returned counters this tick.
/// Not thread-safe: callers must serialize all calls.
public sealed class AdapterLedgerSampler
{
    public static readonly TimeSpan MappingRefresh = TimeSpan.FromSeconds(10);

    readonly record struct Slot(ConnectionId Id, string Name, int Index);

    readonly IAdapterSource _adapters;
    readonly IAdapterCounters _counters;
    readonly Func<AdapterOverrides> _overrides;
    readonly Action<string> _log;
    readonly Func<DateTimeOffset> _now;

    readonly Dictionary<ConnectionId, Slot> _remembered = new();
    IReadOnlyList<Slot> _mapping = [];
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

    /// A reading for every mapped slot whose counters could be read. A slot whose read fails (throws) or finds no
    /// interface (null) is left out of this tick only.
    public IReadOnlyDictionary<ConnectionId, AdapterReading> Sample()
    {
        RefreshMappingIfDue();

        var readings = new Dictionary<ConnectionId, AdapterReading>();
        var failed = false;
        foreach (var slot in _mapping)
        {
            Traffic? bytes;
            try
            {
                bytes = _counters.Read(slot.Index);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_readFailed && !failed) _log($"Adapter ledger: reading the adapters' byte counters failed: {ex.Message}");
                failed = true;
                continue;
            }
            if (bytes is { } b) readings[slot.Id] = new AdapterReading(slot.Name, b);
        }
        _readFailed = failed; // logged once per failure streak: this runs every second
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

        var resolved = Connections.All
            .Select(id => (Slot: SlotFor(id, detected, out var live), Live: live))
            .Where(r => r.Slot is not null)
            .ToList();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indices = new HashSet<int>();
        var mapping = new List<Slot>();
        foreach (var live in new[] { true, false }) // live adapters claim first: a stale remembered name never wins
        {
            foreach (var (slot, isLive) in resolved)
            {
                if (isLive != live) continue;
                var s = slot!.Value;
                if (names.Contains(s.Name) || indices.Contains(s.Index)) continue;
                names.Add(s.Name);
                indices.Add(s.Index);
                mapping.Add(s);
            }
        }
        _mapping = mapping;
    }

    Slot? SlotFor(ConnectionId id, DetectionResult detected, out bool live)
    {
        live = false;
        if (detected.Adapter(id) is { } adapter)
        {
            live = true;
            return _remembered[id] = new Slot(id, adapter.Name, adapter.Index);
        }

        if (detected.Issue(id) == DetectionIssue.NotFound) return _remembered.TryGetValue(id, out var kept) ? kept : null;

        _remembered.Remove(id); // Not used or Ambiguous
        return null;
    }
}
