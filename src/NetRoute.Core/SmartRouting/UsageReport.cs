namespace NetRoute.Core;

public enum UsageSortColumn { Name, Phone, Lan, Now }

public readonly record struct UsageSort(UsageSortColumn Column, bool Descending)
{
    /// Phone, largest first: the biggest consumers of mobile data on top.
    public static UsageSort Default => new(UsageSortColumn.Phone, true);

    /// What a click on a column header does: the same column flips direction; another column starts largest-first (names A to Z).
    public UsageSort Click(UsageSortColumn column) =>
        column == Column ? this with { Descending = !Descending } : new UsageSort(column, column != UsageSortColumn.Name);
}

public sealed record UsageReportRow(
    string Key, string Name, GoesVia Via, bool CanChange, long PhoneBytes, long LanBytes, UsageRate? Now);

/// RecordingSince: set only when the earliest recorded day is later than the first day of the range.
/// KeptOffIsLowerBound: a day in the range has a gap (sing-box stopped or restarted), so "kept off 4G" is "at least" that much.
/// Filtered: a name filter was applied; Rows and the Phone/Lan totals then cover only the matching rows (KeptOff and the adapter total stay whole-period).
public sealed record UsageReportModel(
    int RangeDays, IReadOnlyList<UsageReportRow> Rows, long PhoneTotal, long LanTotal, long KeptOff,
    bool KeptOffIsLowerBound, long PhoneAdapterTotal, DateOnly? RecordingSince, bool Filtered = false);

/// Pure model for the Usage tab.
public static class UsageReport
{
    public static readonly IReadOnlyList<int> Ranges = [1, 3, 7, 15, 30];
    const int DefaultRange = 7;

    public static int NormalizeRange(int days) => Ranges.Contains(days) ? days : DefaultRange;

    public static string RangeLabel(int days) => days == 1 ? "Today" : $"{days} days";

    public static string NowText(UsageRate? rate) =>
        rate is null ? "idle" : $"↕ {ByteFormat.Human(rate.BytesPerSecond)}/s · {(rate.Exit == RouteExit.Lan ? "LAN" : "phone")}";

    public static UsageReportModel Build(
        UsageSnapshot usage, DateOnly today, int rangeDays, IReadOnlyList<RuleItem> catalog,
        SmartRoutingSettings settings, bool canAssign, UsageSort sort, string? filter = null)
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
            var assignment = UsageAssignment.Describe(catalog, settings, key);
            rows.Add(new UsageReportRow(
                key, name, assignment.Via, canAssign && assignment.CanChange,
                sum.Phone.Total, sum.Lan.Total, rate));
        }

        var recorded = usage.Days.Keys.OrderBy(d => d).ToList();
        DateOnly? since = recorded.Count > 0 && recorded[0] > first ? recorded[0] : null;
        return new UsageReportModel(
            range, Sort(rows, sort).ToList(), rows.Sum(r => r.PhoneBytes), rows.Sum(r => r.LanBytes),
            sums.Values.Sum(r => r.Kept), gap, adapter, since, needle.Length > 0);
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
        _ => r => r.Now?.BytesPerSecond ?? 0,
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
