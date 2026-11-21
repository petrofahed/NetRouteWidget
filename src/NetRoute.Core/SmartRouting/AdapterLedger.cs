namespace NetRoute.Core;

/// One slot's adapter name and its raw Windows byte counters (since the adapter came up).
public sealed record AdapterReading(string Name, Traffic Bytes);

/// One local day of the ledger: bytes per connection, and the local time of that day's first reading
/// (null when nothing was read that day).
public sealed record LedgerDay(IReadOnlyDictionary<ConnectionId, Traffic> Totals, TimeOnly? Since);

/// Exact bytes per adapter per local day, from the Windows counters. Independent of UsageCounter and of Smart routing.
///   - First reading of a slot, or a changed adapter name for it: a baseline only, nothing is counted (the counter is
///     "since the adapter came up").
///   - Same name, reading not lower than the last: the difference is counted.
///   - Same name, either direction lower than the last (adapter reset or replug): the whole slot is treated as reset
///     and the NEW readings are counted as growth, like UsageCounter.Grow. Judged on the pair so a reset is never
///     half-counted.
///   - A slot absent from a tick keeps its baseline and counts nothing.
///   - Rates (the card's live speed): the bytes counted in the last UsageCounter.RateWindow (same in-window rule and jitter
///     margin as UsageCounter), per connection, up + down, divided by the window seconds; a connection below
///     UsageCounter.ActiveBytesPerSecond reads 0. Refreshed by every Update (an empty tick lets them fade), never persisted.
/// Not thread-safe: callers must serialize all calls.
public sealed class AdapterLedger
{
    sealed class DayData
    {
        public readonly Dictionary<ConnectionId, Traffic> Totals = new();
        public TimeOnly? Since;
    }

    readonly SortedDictionary<DateOnly, DayData> _days = new();
    readonly Dictionary<ConnectionId, AdapterReading> _baselines = new();
    readonly Dictionary<ConnectionId, Queue<(DateTimeOffset At, long Bytes)>> _recent = new();
    DateOnly _today;
    bool _dirty;

    /// Bytes per second per connection right now, over the rate window. Every connection has a key (0 when idle).
    public IReadOnlyDictionary<ConnectionId, long> Rates { get; private set; } = IdleRates();

    public AdapterLedger(IReadOnlyDictionary<DateOnly, LedgerDay>? restored, DateOnly today)
    {
        _today = today;
        if (restored is not null)
        {
            foreach (var (day, data) in restored)
            {
                var d = new DayData { Since = data.Since };
                foreach (var (id, traffic) in data.Totals) d.Totals[id] = traffic;
                _days[day] = d;
            }
        }
        Prune();
    }

    /// Slots absent from readings had no adapter this tick. Now is the LOCAL time: its date is the day the bytes belong to.
    public void Update(IReadOnlyDictionary<ConnectionId, AdapterReading> readings, DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.DateTime);
        if (day != _today)
        {
            _today = day;
            Prune();
        }
        if (readings.Count == 0)
        {
            RebuildRates(now);
            return;
        }

        var dayData = DayFor(day);
        if (dayData.Since is null)
        {
            dayData.Since = TimeOnly.FromDateTime(now.DateTime);
            _dirty = true;
        }

        foreach (var (id, reading) in readings)
        {
            var bytes = new Traffic(Math.Max(0, reading.Bytes.Up), Math.Max(0, reading.Bytes.Down));
            var current = reading with { Bytes = bytes };
            var grown = _baselines.TryGetValue(id, out var last) && last.Name == reading.Name
                ? Grow(bytes, last.Bytes)
                : default;
            _baselines[id] = current;
            if (grown.Total <= 0) continue;
            if (!_recent.TryGetValue(id, out var queue)) _recent[id] = queue = new();
            queue.Enqueue((now, grown.Total));
            dayData.Totals[id] = dayData.Totals.GetValueOrDefault(id) + grown;
            _dirty = true;
        }
        RebuildRates(now);
    }

    /// Today's total for a connection (today = the day of the last Update, else the constructor's day).
    public Traffic Today(ConnectionId id) =>
        _days.TryGetValue(_today, out var data) ? data.Totals.GetValueOrDefault(id) : default;

    /// An immutable copy of every stored day, including a day that has only a Since.
    public IReadOnlyDictionary<DateOnly, LedgerDay> Snapshot()
    {
        var days = new Dictionary<DateOnly, LedgerDay>();
        foreach (var (day, data) in _days)
            days[day] = new LedgerDay(new Dictionary<ConnectionId, Traffic>(data.Totals), data.Since);
        return days;
    }

    /// True once after any change since the last call: the signal to save the file.
    public bool TakeDirty()
    {
        var dirty = _dirty;
        _dirty = false;
        return dirty;
    }

    /// Drops every stored day. The baselines stay, so the next tick does not re-add the since-boot counter; that tick
    /// recreates today with Since = its own time.
    public void Clear()
    {
        _days.Clear();
        _dirty = true;
    }

    /// Growth of a slot. Judged on the pair: any direction going backwards means the adapter was reset, and the new
    /// readings count as growth.
    static Traffic Grow(Traffic now, Traffic last) =>
        now.Up < last.Up || now.Down < last.Down ? now : new Traffic(now.Up - last.Up, now.Down - last.Down);

    static Dictionary<ConnectionId, long> IdleRates() => Connections.All.ToDictionary(id => id, _ => 0L);

    void RebuildRates(DateTimeOffset now)
    {
        var rates = IdleRates();
        var seconds = (long)UsageCounter.RateWindow.TotalSeconds;
        foreach (var (id, queue) in _recent.ToList())
        {
            if (queue.Any(e => !UsageCounter.InWindow(e.At, now)))
            {
                var fresh = queue.Where(e => UsageCounter.InWindow(e.At, now)).ToList();
                queue.Clear();
                foreach (var e in fresh) queue.Enqueue(e);
            }
            if (queue.Count == 0)
            {
                _recent.Remove(id);
                continue;
            }
            var rate = queue.Sum(e => e.Bytes) / seconds;
            rates[id] = rate < UsageCounter.ActiveBytesPerSecond ? 0 : rate;
        }
        Rates = rates;
    }

    DayData DayFor(DateOnly day)
    {
        if (!_days.TryGetValue(day, out var data)) _days[day] = data = new DayData();
        return data;
    }

    void Prune()
    {
        var cutoff = _today.AddDays(-(UsageSnapshot.RetentionDays - 1));
        foreach (var old in _days.Keys.Where(d => d < cutoff).ToList()) _days.Remove(old);
    }
}
