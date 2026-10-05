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
    RouteExit Profile = RouteExit.Phone, long PhoneUpTotal = 0, long LanUpTotal = 0);

/// Pure model for the Usage tab.
public static class UsageReport
{
    public static readonly IReadOnlyList<int> Ranges = [1, 3, 7, 15, 30];
    const int DefaultRange = 7;

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
        state.InException ? Destination(state.Destination) : null;

    public static string MenuText(ExceptionState state) =>
        !state.CanChange ? "Can't be an exception here"
            : state.InException ? "Exclude from exception"
            : $"Send to exception ({Destination(state.Destination)})";

    static string Destination(RouteExit exit) => exit == RouteExit.Lan ? "→ LAN" : "→ phone";

    public static UsageReportModel Build(
        UsageSnapshot usage, DateOnly today, int rangeDays, IReadOnlyList<RuleItem> catalog,
        SmartRoutingSettings settings, bool canAssign, UsageSort sort, string? filter = null,
        RouteExit profile = RouteExit.Phone)
    {
        var range = NormalizeRange(rangeDays);
        var first = today.AddDays(-(range - 1));
        var attribution = UsageAttribution.Build(catalog, settings);
        var needle = filter?.Trim() ?? "";

        var sums = new Dictionary<string, UsageRow>();
        long adapter = 0;
        var gap = false;
        foreach (var (day, data) in usage.Days)
        {
            if (day < first || day > today) continue;
            adapter += data.PhoneAdapterBytes;
            gap |= data.Gaps > 0;
            foreach (var (key, row) in data.Rows) sums[key] = sums.GetValueOrDefault(key, UsageRow.Empty) + row;
        }
        foreach (var key in usage.Rates.Keys) sums.TryAdd(key, UsageRow.Empty);

        var rows = new List<UsageReportRow>();
        foreach (var (key, sum) in sums)
        {
            var rate = usage.Rates.GetValueOrDefault(key);
            if (sum.Total == 0 && rate is null) continue;
            var name = attribution.DisplayName(key);
            if (needle.Length > 0
                && !name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !key.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            var exception = UsageExceptions.Describe(catalog, settings, profile, key);
            if (!canAssign) exception = exception with { CanChange = false };
            rows.Add(new UsageReportRow(key, name, exception, sum.Phone.Total, sum.Lan.Total, rate,
                sum.Phone.Up, sum.Lan.Up, usage.UpRates?.GetValueOrDefault(key)));
        }

        var recorded = usage.Days.Keys.OrderBy(d => d).ToList();
        DateOnly? since = recorded.Count > 0 && recorded[0] > first ? recorded[0] : null;
        return new UsageReportModel(
            range, Sort(rows, sort).ToList(), rows.Sum(r => r.PhoneBytes), rows.Sum(r => r.LanBytes),
            sums.Values.Sum(r => r.Kept), gap, adapter, since, needle.Length > 0, profile,
            rows.Sum(r => r.PhoneUp), rows.Sum(r => r.LanUp));
    }

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
