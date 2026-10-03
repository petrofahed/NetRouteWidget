namespace NetRoute.Core;

/// What one poll of the Clash API saw. Now is the LOCAL time: its date is the day the bytes belong to.
public sealed record UsagePoll(
    IReadOnlyList<SingBoxConnection> Connections, long? UploadTotal, long? DownloadTotal,
    long? PhoneAdapterBytes, bool PhoneIsDefault, DateTimeOffset Now);

/// Counts bytes per local day, per row, per exit.
///   - Per-connection growth since the previous poll goes to the connection's row and exit.
///   - Bytes the poll missed (connections that opened and closed between two polls) are found by comparing the Clash
///     totals with what the open connections explain, and go to the "unattributed" row, split by exit with the help of
///     the phone adapter's Windows counter (see UsageReconciler).
///   - Live rates ("Now") are the bytes seen over the last RateWindow.
/// Not thread-safe: callers must serialize all calls (the SmartRoutingController does this under its gate).
public sealed class UsageCounter
{
    public const int MaxRowsPerDay = 500;
    public const long ActiveBytesPerSecond = 1024;
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);

    sealed class Cell
    {
        public long PhoneUp, PhoneDown, LanUp, LanDown, Kept;
    }

    sealed class DayData
    {
        public readonly Dictionary<string, Cell> Rows = new();
        public long PhoneAdapterBytes;
        public int Gaps;
    }

    readonly SortedDictionary<DateOnly, DayData> _days = new();
    readonly Dictionary<string, (long Up, long Down)> _lastSeen = new();
    readonly Dictionary<string, Queue<(DateTimeOffset At, RouteExit Exit, long Bytes)>> _recent = new();
    Dictionary<string, UsageRate> _rates = new();
    long? _lastUp, _lastDown, _lastAdapter;
    DateOnly _today;
    bool _dirty;

    public UsageCounter(UsageSnapshot? restored, DateOnly today)
    {
        _today = today;
        if (restored is not null)
        {
            foreach (var (day, data) in restored.Days)
            {
                var d = new DayData { PhoneAdapterBytes = data.PhoneAdapterBytes, Gaps = data.Gaps };
                foreach (var (key, row) in data.Rows)
                {
                    d.Rows[key] = new Cell
                    {
                        PhoneUp = row.Phone.Up, PhoneDown = row.Phone.Down, LanUp = row.Lan.Up, LanDown = row.Lan.Down, Kept = row.Kept,
                    };
                }
                _days[day] = d;
            }
        }
        Prune();
    }

    public void Update(UsagePoll poll, UsageAttribution attribution)
    {
        var day = DateOnly.FromDateTime(poll.Now.DateTime);
        if (day != _today)
        {
            _today = day;
            Prune();
        }
        var dayData = DayFor(day);
        var rows = dayData.Rows;

        long seenUp = 0, seenDown = 0, seenPhone = 0;
        var alive = new HashSet<string>();
        foreach (var c in poll.Connections)
        {
            alive.Add(c.Id);
            var (lastUp, lastDown) = _lastSeen.GetValueOrDefault(c.Id);
            var up = Grow(c.Upload, lastUp);
            var down = Grow(c.Download, lastDown);
            _lastSeen[c.Id] = (c.Upload, c.Download);
            if (up + down <= 0) continue;

            var exit = c.Chains.Count > 0 && c.Chains[0] == SingBoxConfigBuilder.LanTag ? RouteExit.Lan : RouteExit.Phone;
            var key = attribution.Resolve(c.ProcessName, c.Host);
            var kept = exit == RouteExit.Lan && c.IsLanOnly && poll.PhoneIsDefault ? up + down : 0;
            Add(rows, key, exit, new Traffic(up, down), kept);
            AddRate(key, exit, up + down, poll.Now);
            seenUp += up;
            seenDown += down;
            if (exit == RouteExit.Phone) seenPhone += up + down;
        }
        foreach (var gone in _lastSeen.Keys.Where(id => !alive.Contains(id)).ToList()) _lastSeen.Remove(gone);

        long? adapterDelta = null;
        if (poll.PhoneAdapterBytes is { } adapterNow && _lastAdapter is { } adapterBefore && adapterNow >= adapterBefore)
            adapterDelta = adapterNow - adapterBefore;
        _lastAdapter = poll.PhoneAdapterBytes; // a counter that went backwards (adapter reset) is just a new baseline
        if (adapterDelta is { } counted)
        {
            dayData.PhoneAdapterBytes += counted;
            _dirty = true;
        }

        if (poll.UploadTotal is { } totalUp && poll.DownloadTotal is { } totalDown)
        {
            // The first poll after (re)start has no earlier total: everything since sing-box started counts.
            var upDelta = Grow(totalUp, _lastUp ?? 0);
            var downDelta = Grow(totalDown, _lastDown ?? 0);
            _lastUp = totalUp;
            _lastDown = totalDown;

            var split = UsageReconciler.Split(upDelta + downDelta, seenUp + seenDown, seenPhone, adapterDelta, poll.PhoneIsDefault);
            var unseenUp = Math.Max(0, upDelta - seenUp);
            var unseenDown = Math.Max(0, downDelta - seenDown);
            Add(rows, UsageAttribution.UnattributedKey, RouteExit.Phone, Spread(split.Phone, unseenUp, unseenDown), kept: 0);
            Add(rows, UsageAttribution.UnattributedKey, RouteExit.Lan, Spread(split.Lan, unseenUp, unseenDown),
                kept: poll.PhoneIsDefault ? split.Lan : 0);
        }
        // A poll without totals skips the reconciliation but keeps the baseline: only ResetBaselines (a real restart)
        // clears it, otherwise the next poll with totals would book sing-box's whole lifetime total as unattributed.

        RebuildRates(poll.Now);
    }

    /// An immutable copy of everything.
    public UsageSnapshot Snapshot()
    {
        var days = new Dictionary<DateOnly, UsageDay>();
        foreach (var (day, data) in _days)
        {
            if (data.Rows.Count == 0 && data.PhoneAdapterBytes == 0) continue;
            var rows = data.Rows.ToDictionary(
                r => r.Key,
                r => new UsageRow(new Traffic(r.Value.PhoneUp, r.Value.PhoneDown), new Traffic(r.Value.LanUp, r.Value.LanDown), r.Value.Kept));
            days[day] = new UsageDay(rows, data.PhoneAdapterBytes, data.Gaps);
        }
        return new UsageSnapshot(days, new Dictionary<string, UsageRate>(_rates));
    }

    /// Today's kept-off-4G bytes per row (the card's "kept off 4G today" figure).
    public DailyStats TodayKept(DateOnly today)
    {
        var kept = new Dictionary<string, long>();
        if (_days.TryGetValue(today, out var data))
            foreach (var (key, cell) in data.Rows)
                if (cell.Kept > 0) kept[key] = cell.Kept;
        return new DailyStats(today, kept);
    }

    /// True once after any change since the last call: the signal to save the file.
    public bool TakeDirty()
    {
        var dirty = _dirty;
        _dirty = false;
        return dirty;
    }

    /// Wipes the history. Connections that are still open are not recounted from zero (their baselines stay).
    public void Clear()
    {
        _days.Clear();
        _dirty = true;
    }

    /// A running sing-box went away (stopped, crashed, restarted): the bytes between the last poll and that moment were
    /// not recorded. The day is marked so the kept-off-4G figure can say "at least".
    public void NoteGap(DateOnly day)
    {
        DayFor(day).Gaps++;
        _dirty = true;
    }

    /// Forgets the live rates only (a failed poll must not leave rows looking busy). The baselines stay.
    public void ClearRates()
    {
        _recent.Clear();
        _rates = new Dictionary<string, UsageRate>();
    }

    /// Forgets what the next poll would be compared with (per-connection bytes, totals, the adapter reading, rates).
    /// Called when a running sing-box goes away. The history stays.
    public void ResetBaselines()
    {
        _lastSeen.Clear();
        ClearRates();
        _lastUp = _lastDown = _lastAdapter = null;
    }

    /// Growth of a counter. A first sighting counts the whole value; a counter that went backwards (a reused id, a
    /// restarted sing-box) starts over from its new value.
    static long Grow(long now, long last) => now < 0 ? 0 : now >= last ? now - last : now;

    /// Splits bytes over up and down in the proportion of the missed up/down bytes; all of it goes to down when unknown.
    static Traffic Spread(long bytes, long up, long down)
    {
        if (bytes <= 0) return default;
        var sum = up + down;
        if (sum <= 0) return new Traffic(0, bytes);
        var upShare = (long)Math.Round((double)bytes * up / sum);
        return new Traffic(upShare, bytes - upShare);
    }

    DayData DayFor(DateOnly day)
    {
        if (!_days.TryGetValue(day, out var data)) _days[day] = data = new DayData();
        return data;
    }

    void Add(Dictionary<string, Cell> rows, string key, RouteExit exit, Traffic traffic, long kept)
    {
        if (traffic.Total <= 0 && kept <= 0) return;
        // A new row beyond the cap folds into "Other"; the two special rows are always allowed.
        if (!rows.ContainsKey(key) && rows.Count >= MaxRowsPerDay
            && key is not (UsageAttribution.OtherKey or UsageAttribution.UnattributedKey))
            key = UsageAttribution.OtherKey;
        if (!rows.TryGetValue(key, out var cell)) rows[key] = cell = new Cell();
        if (exit == RouteExit.Phone)
        {
            cell.PhoneUp += traffic.Up;
            cell.PhoneDown += traffic.Down;
        }
        else
        {
            cell.LanUp += traffic.Up;
            cell.LanDown += traffic.Down;
        }
        cell.Kept += kept;
        _dirty = true;
    }

    void AddRate(string key, RouteExit exit, long bytes, DateTimeOffset at)
    {
        if (!_recent.TryGetValue(key, out var queue)) _recent[key] = queue = new();
        queue.Enqueue((at, exit, bytes));
    }

    void RebuildRates(DateTimeOffset now)
    {
        var rates = new Dictionary<string, UsageRate>();
        foreach (var (key, queue) in _recent.ToList())
        {
            // Samples stamped after "now" come from a clock that has since stepped back: they are stale too.
            if (queue.Any(e => e.At > now || now - e.At >= RateWindow))
            {
                var fresh = queue.Where(e => e.At <= now && now - e.At < RateWindow).ToList();
                queue.Clear();
                foreach (var e in fresh) queue.Enqueue(e);
            }
            if (queue.Count == 0)
            {
                _recent.Remove(key);
                continue;
            }
            long phone = 0, lan = 0;
            foreach (var entry in queue)
            {
                if (entry.Exit == RouteExit.Lan) lan += entry.Bytes;
                else phone += entry.Bytes;
            }
            var perSecond = (phone + lan) / (long)RateWindow.TotalSeconds;
            if (perSecond >= ActiveBytesPerSecond) rates[key] = new UsageRate(lan > phone ? RouteExit.Lan : RouteExit.Phone, perSecond);
        }
        _rates = rates;
    }

    void Prune()
    {
        var cutoff = _today.AddDays(-(UsageSnapshot.RetentionDays - 1));
        foreach (var old in _days.Keys.Where(d => d < cutoff).ToList()) _days.Remove(old);
    }
}
