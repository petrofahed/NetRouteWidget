namespace NetRoute.Core;

/// Bytes in each direction.
public readonly record struct Traffic(long Up, long Down)
{
    public long Total => Up + Down;
    public static Traffic operator +(Traffic a, Traffic b) => new(a.Up + b.Up, a.Down + b.Down);
}

/// One row's bytes for one day: through the phone, through the LAN, and the part of the LAN bytes that LAN-only rules
/// kept off 4G (LAN bytes that arrived while the phone was the default exit).
public sealed record UsageRow(Traffic Phone, Traffic Lan, long Kept)
{
    public static readonly UsageRow Empty = new(default, default, 0);
    public long Total => Phone.Total + Lan.Total;
    public static UsageRow operator +(UsageRow a, UsageRow b) => new(a.Phone + b.Phone, a.Lan + b.Lan, a.Kept + b.Kept);
}

/// One local day: its rows by row key, the bytes Windows counted on the phone adapter while Smart routing was recording,
/// and how many times sing-box stopped or restarted (the bytes just before each stop were not recorded).
public sealed record UsageDay(IReadOnlyDictionary<string, UsageRow> Rows, long PhoneAdapterBytes, int Gaps = 0);

/// Bytes through each exit for one day, all rows together (the Usage tab's "Today" Total row). A class, not a struct, so the
/// controller can hold it in a volatile field and hand it to the UI thread without a lock.
public sealed record ExitTotals(long PhoneBytes, long LanBytes)
{
    public static readonly ExitTotals Zero = new(0, 0);
}

/// A row's live speed over the last few seconds, per exit. A side that is idle (below the active threshold) is 0.
public sealed record UsageRate(long PhoneBytesPerSecond, long LanBytesPerSecond)
{
    public long Total => PhoneBytesPerSecond + LanBytesPerSecond;
}

/// An immutable copy of everything the counter knows. Safe to hand to the UI thread.
public sealed record UsageSnapshot(IReadOnlyDictionary<DateOnly, UsageDay> Days, IReadOnlyDictionary<string, UsageRate> Rates)
{
    /// Today plus the 34 days before it.
    public const int RetentionDays = 35;

    public static UsageSnapshot Empty { get; } =
        new(new Dictionary<DateOnly, UsageDay>(), new Dictionary<string, UsageRate>());
}

/// Kept-off-4G bytes for one local day, per row key (the card's "kept off 4G today" and the per-item numbers on the Config tab).
public sealed record DailyStats(DateOnly Day, IReadOnlyDictionary<string, long> BytesByEntry)
{
    public long Total => BytesByEntry.Values.Sum();
}
