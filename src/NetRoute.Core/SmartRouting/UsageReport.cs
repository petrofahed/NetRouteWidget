using System.Globalization;

namespace NetRoute.Core;

/// Phone and Lan are the per-exit totals (up + down).
public enum UsageSortColumn { Name, Phone, Lan, NowPhone, NowLan, PhoneUp, PhoneDown, LanUp, LanDown }

public readonly record struct UsageSort(UsageSortColumn Column, bool Descending)
{
    /// Phone upload, largest first: what uploads the most over mobile data on top.
    public static UsageSort Default => new(UsageSortColumn.PhoneUp, true);

    /// What a click on a column header does: the same column flips direction; another column starts largest-first (names A to Z).
    public UsageSort Click(UsageSortColumn column) =>
        column == Column ? this with { Descending = !Descending } : new UsageSort(column, column != UsageSortColumn.Name);
}

/// PhoneBytes and LanBytes are totals (up + down); PhoneUp and LanUp are their upload part, the rest is download.
/// NowUp is the upload part of Now.
public sealed record UsageReportRow(
    string Key, string Name, ExceptionState Exception, long PhoneBytes, long LanBytes, UsageRate? Now,
    long PhoneUp = 0, long LanUp = 0, UsageUpRate? NowUp = null)
{
    public bool CanChange => Exception.CanChange;
    public long PhoneDown => PhoneBytes - PhoneUp;
    public long LanDown => LanBytes - LanUp;
    public long NowPhoneUp => Math.Min(NowUp?.PhoneBytesPerSecond ?? 0, Now?.PhoneBytesPerSecond ?? 0);
    public long NowLanUp => Math.Min(NowUp?.LanBytesPerSecond ?? 0, Now?.LanBytesPerSecond ?? 0);
}

/// RecordingSince: set only when the earliest recorded day is later than the first day of the range.
/// KeptOffIsLowerBound: a day in the range has a gap (sing-box stopped or restarted), so "kept off 4G" is "at least" that much.
/// Profile: the profile the rows' exception state was computed for (the menu acts on exactly that list).
/// Filtered: a name filter was applied; Rows and the Phone/Lan totals then cover only the matching rows (KeptOff and the adapter total stay whole-period).
/// PhoneUpTotal and LanUpTotal are the upload part of PhoneTotal and LanTotal.
public sealed record UsageReportModel(
    int RangeDays, IReadOnlyList<UsageReportRow> Rows, long PhoneTotal, long LanTotal, long KeptOff,
    bool KeptOffIsLowerBound, long PhoneAdapterTotal, DateOnly? RecordingSince, bool Filtered = false,
    ConnectionId Profile = ConnectionId.Phone, long PhoneUpTotal = 0, long LanUpTotal = 0)
{
    /// Exact bytes per connection from the adapter ledger, summed over the range's days that have a ledger (every connection
    /// is listed, 0 when it grew nothing); empty when no day in the range has one.
    public IReadOnlyDictionary<ConnectionId, Traffic> LedgerTotals { get; init; } = new Dictionary<ConnectionId, Traffic>();

    /// When the ledger started counting: the first-ever ledger day plus its Since, only when that day lies in the range
    /// and has a Since.
    public DateTime? LedgerSince { get; init; }
}

/// Pure model for the Usage tab.
public static class UsageReport
{
    public static readonly IReadOnlyList<int> Ranges = [1, 3, 7, 15, 30];
    const int DefaultRange = 7;

    public const string CountingNote =
        "Adapter totals are exact while the widget runs. Per-app rows are counted only while Smart routing runs or this tab is open. The Now columns are per app and can differ from the card's speed.";

    /// The line under the table: exact per-adapter totals from the ledger, or the older Smart-routing-only phone total
    /// when no day in the range has a ledger.
    public static string AdapterFooter(UsageReportModel model)
    {
        if (model.LedgerTotals.Count == 0)
            return "Phone adapter total in this period (exact, from Windows): "
                + (model.PhoneAdapterTotal > 0 ? ByteFormat.Human(model.PhoneAdapterTotal) : "not measured yet");
        string Part(ConnectionId id)
        {
            var bytes = model.LedgerTotals.GetValueOrDefault(id).Total;
            return $"{Connections.Label(id)} {(bytes == 0 ? "0 B" : ByteFormat.Human(bytes))}";
        }
        var text = "Exact adapter totals in this period (from Windows): " + string.Join(" · ", Connections.CardOrder.Select(Part));
        return model.LedgerSince is { } since
            ? text + $" (counted since {since.ToString("HH:mm 'on' d MMM", CultureInfo.CurrentCulture)})"
            : text;
    }

    public static int NormalizeRange(int days) => Ranges.Contains(days) ? days : DefaultRange;

    public static string RangeLabel(int days) => days == 1 ? "Today" : $"{days} days";

    /// One side's live speed: an en dash when idle, otherwise "↕ 1.4 MB/s".
    public static string NowText(long bytesPerSecond) =>
        bytesPerSecond <= 0 ? "–" : $"↕ {ByteFormat.Human(bytesPerSecond)}/s";

    /// One side's live speed per direction: an en dash when idle, otherwise "↑ 7 KB/s ↓ 2 KB/s".
    public static string NowSplitText(long bytesPerSecond, long uploadBytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "–";
        var up = Math.Clamp(uploadBytesPerSecond, 0, bytesPerSecond);
        return $"↑ {ByteFormat.Human(up)}/s ↓ {ByteFormat.Human(bytesPerSecond - up)}/s";
    }

    /// The tag shown next to a row that is an exception: where its traffic is routed.
    public static string? TagText(ExceptionState state) =>
        state.InException ? "→ " + SmartRoutingController.TargetLabel(state.Target) : null;

    public static string MenuText(ExceptionState state) =>
        !state.CanChange ? "Can't be an exception here"
            : state.InException ? "Exclude from exception"
            : $"Send to exception (→ {SmartRoutingController.TargetLabel(state.Target)})";

    public static UsageReportModel Build(
        UsageSnapshot usage, DateOnly today, int rangeDays, IReadOnlyList<RuleItem> catalog,
        SmartRoutingSettings settings, bool canAssign, UsageSort sort, string? filter = null,
        ConnectionId profile = ConnectionId.Phone)
    {
        var range = NormalizeRange(rangeDays);
        var first = today.AddDays(-(range - 1));
        var attribution = UsageAttribution.Build(catalog, settings);
        var needle = filter?.Trim() ?? "";

        bool Matches(string name, string key) =>
            needle.Length == 0
            || name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || key.Contains(needle, StringComparison.OrdinalIgnoreCase);

        var sums = new Dictionary<string, UsageRow>();
        long adapter = 0;
        var gap = false;
        var ledgerTotals = new Dictionary<ConnectionId, Traffic>();
        Traffic notCountedPhone = default, notCountedLan = default;
        foreach (var (day, data) in usage.Days)
        {
            if (day < first || day > today) continue;
            adapter += data.PhoneAdapterBytes;
            gap |= data.Gaps > 0;
            Traffic dayPhone = default, dayLan = default;
            foreach (var (key, row) in data.Rows)
            {
                sums[key] = sums.GetValueOrDefault(key, UsageRow.Empty) + row;
                dayPhone += row.Phone;
                dayLan += row.Lan;
            }
            if (data.Ledger is not { } ledger) continue;
            foreach (var id in Connections.All) ledgerTotals[id] = ledgerTotals.GetValueOrDefault(id) + ledger.Totals.GetValueOrDefault(id);
            // The adapter bytes no row of this day accounts for, per direction, never below zero. Clamped per day, so one
            // day's surplus of rows never eats into another day's remainder.
            notCountedPhone += Remainder(ledger.Totals.GetValueOrDefault(ConnectionId.Phone), dayPhone);
            notCountedLan += Remainder(ledger.Totals.GetValueOrDefault(ConnectionId.Lan) + ledger.Totals.GetValueOrDefault(ConnectionId.Wifi), dayLan);
        }
        foreach (var key in usage.Rates.Keys) sums.TryAdd(key, UsageRow.Empty);

        var rows = new List<UsageReportRow>();
        foreach (var (key, sum) in sums)
        {
            var rate = usage.Rates.GetValueOrDefault(key);
            if (sum.Total == 0 && rate is null) continue;
            var name = attribution.DisplayName(key);
            if (!Matches(name, key)) continue;
            var exception = UsageExceptions.Describe(catalog, settings, profile, key);
            if (!canAssign) exception = exception with { CanChange = false };
            rows.Add(new UsageReportRow(key, name, exception, sum.Phone.Total, sum.Lan.Total, rate,
                sum.Phone.Up, sum.Lan.Up, usage.UpRates?.GetValueOrDefault(key)));
        }

        // A derived row, never stored: the exact adapter bytes that no per-app row accounts for.
        var notCountedName = attribution.DisplayName(UsageAttribution.NotCountedKey);
        if (notCountedPhone.Total + notCountedLan.Total > 0 && Matches(notCountedName, UsageAttribution.NotCountedKey))
            rows.Add(new UsageReportRow(
                UsageAttribution.NotCountedKey, notCountedName,
                UsageExceptions.Describe(catalog, settings, profile, UsageAttribution.NotCountedKey),
                notCountedPhone.Total, notCountedLan.Total, null, notCountedPhone.Up, notCountedLan.Up));

        var recorded = usage.Days.Keys.OrderBy(d => d).ToList();
        DateOnly? since = recorded.Count > 0 && recorded[0] > first ? recorded[0] : null;
        // The first-ever ledger day, whether or not it is in the range: a later day must not stand in for it.
        DateTime? ledgerSince = null;
        var ledgerDays = usage.Days.Where(d => d.Value.Ledger is not null).Select(d => d.Key).ToList();
        if (ledgerDays.Count > 0 && ledgerDays.Min() is var firstLedgerDay && firstLedgerDay >= first && firstLedgerDay <= today
            && usage.Days[firstLedgerDay].Ledger!.Since is { } ledgerTime)
            ledgerSince = firstLedgerDay.ToDateTime(ledgerTime);
        return new UsageReportModel(
            range, Sort(rows, sort).ToList(), rows.Sum(r => r.PhoneBytes), rows.Sum(r => r.LanBytes),
            sums.Values.Sum(r => r.Kept), gap, adapter, since, needle.Length > 0, profile,
            rows.Sum(r => r.PhoneUp), rows.Sum(r => r.LanUp))
        {
            LedgerTotals = ledgerTotals,
            LedgerSince = ledgerSince,
        };
    }

    static Traffic Remainder(Traffic ledger, Traffic rows) =>
        new(Math.Max(0, ledger.Up - rows.Up), Math.Max(0, ledger.Down - rows.Down));

    static IEnumerable<UsageReportRow> Sort(List<UsageReportRow> rows, UsageSort sort)
    {
        var names = StringComparer.OrdinalIgnoreCase;
        if (sort.Column == UsageSortColumn.Name)
        {
            return (sort.Descending ? rows.OrderByDescending(r => r.Name, names) : rows.OrderBy(r => r.Name, names))
                .ThenBy(r => r.Key, StringComparer.Ordinal);
        }
        var value = Value(sort.Column);
        return (sort.Descending ? rows.OrderByDescending(value) : rows.OrderBy(value))
            .ThenBy(r => r.Name, names).ThenBy(r => r.Key, StringComparer.Ordinal);
    }

    static Func<UsageReportRow, long> Value(UsageSortColumn column) => column switch
    {
        UsageSortColumn.Phone => r => r.PhoneBytes,
        UsageSortColumn.Lan => r => r.LanBytes,
        UsageSortColumn.PhoneUp => r => r.PhoneUp,
        UsageSortColumn.PhoneDown => r => r.PhoneDown,
        UsageSortColumn.LanUp => r => r.LanUp,
        UsageSortColumn.LanDown => r => r.LanDown,
        UsageSortColumn.NowPhone => r => r.Now?.PhoneBytesPerSecond ?? 0,
        UsageSortColumn.NowLan => r => r.Now?.LanBytesPerSecond ?? 0,
        _ => r => 0,
    };
}

/// Keeps the Usage tab's rows from jumping while the user is aiming at one.
public static class UsageRowOrder
{
    /// The order the rows should appear in. Normally the sorted order. While frozen (the mouse is over the list, or a
    /// drop-down is open) rows already shown keep their place and new rows are appended, so nothing moves under the cursor.
    public static IReadOnlyList<string> Next(IReadOnlyList<string> shown, IReadOnlyList<string> sorted, bool freeze)
    {
        if (!freeze) return sorted;
        var still = new HashSet<string>(sorted);
        var result = shown.Where(still.Contains).ToList();
        var have = new HashSet<string>(result);
        result.AddRange(sorted.Where(key => !have.Contains(key)));
        return result;
    }
}
