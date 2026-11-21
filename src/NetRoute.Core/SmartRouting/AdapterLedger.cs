namespace NetRoute.Core;

/// One slot's adapter name and its raw Windows byte counters (since the adapter came up).
public sealed record AdapterReading(string Name, Traffic Bytes);

/// One local day of the ledger: bytes per connection, and the local time of that day's first reading
/// (null when nothing was read that day, or after a Clear).
public sealed record LedgerDay(IReadOnlyDictionary<ConnectionId, Traffic> Totals, TimeOnly? Since);

/// Exact bytes per adapter per local day, from the Windows counters. Independent of UsageCounter and of Smart routing.
///   - First reading of a slot, or a changed adapter name for it: a baseline only, nothing is counted (the counter is
///     "since the adapter came up").
///   - Same name, reading not lower than the last: the difference is counted.
///   - Same name, either direction lower than the last (adapter reset or replug): the whole slot is treated as reset
///     and the NEW readings are counted as growth, like UsageCounter.Grow. Judged on the pair so a reset is never
///     half-counted.
///   - A slot absent from a tick keeps its baseline and counts nothing.
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
    DateOnly _today;
    bool _dirty;

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
        if (readings.Count == 0) return;

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
            dayData.Totals[id] = dayData.Totals.GetValueOrDefault(id) + grown;
            _dirty = true;
        }
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

    /// Zeroes every stored day and drops its Since. The baselines stay, so the next tick does not re-add the since-boot counter.
    public void Clear()
    {
        foreach (var data in _days.Values)
        {
            foreach (var id in data.Totals.Keys.ToList()) data.Totals[id] = default;
            data.Since = null;
        }
        _dirty = true;
    }

    /// Growth of a slot. Judged on the pair: any direction going backwards means the adapter was reset, and the new
    /// readings count as growth.
    static Traffic Grow(Traffic now, Traffic last) =>
        now.Up < last.Up || now.Down < last.Down ? now : new Traffic(now.Up - last.Up, now.Down - last.Down);

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
