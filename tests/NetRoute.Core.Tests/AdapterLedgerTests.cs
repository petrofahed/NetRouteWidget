namespace NetRoute.Core.Tests;

public class AdapterLedgerTests
{
    static readonly DateOnly D1 = new(2026, 11, 21);
    static readonly DateOnly D2 = new(2026, 11, 22);

    static AdapterReading R(string name, long up, long down) => new(name, new Traffic(up, down));

    static DateTimeOffset At(DateOnly day, int h, int m, int s) =>
        new(day.Year, day.Month, day.Day, h, m, s, TimeSpan.Zero);

    static IReadOnlyDictionary<ConnectionId, AdapterReading> Map(params (ConnectionId Id, AdapterReading Reading)[] items) =>
        items.ToDictionary(i => i.Id, i => i.Reading);

    [Fact]
    public void The_first_reading_counts_nothing_and_sets_since()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 900))), At(D1, 8, 13, 0));
        Assert.Equal(default, l.Today(ConnectionId.Phone));
        Assert.Equal(new TimeOnly(8, 13, 0), l.Snapshot()[D1].Since);
    }

    [Fact]
    public void Growth_adds_up_and_down_separately()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 900))), At(D1, 8, 0, 0));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 150, 1400))), At(D1, 8, 0, 1));
        Assert.Equal(new Traffic(50, 500), l.Today(ConnectionId.Phone));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 160, 1400))), At(D1, 8, 0, 2));
        Assert.Equal(new Traffic(60, 500), l.Today(ConnectionId.Phone));
    }

    [Fact]
    public void A_lower_reading_with_the_same_name_counts_the_new_reading()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1000, 2000))), At(D1, 8, 0, 0));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 10, 30))), At(D1, 8, 0, 1));
        Assert.Equal(new Traffic(10, 30), l.Today(ConnectionId.Phone));
    }

    [Fact]
    public void A_reset_is_judged_on_the_pair_so_one_direction_going_backwards_counts_both_new_readings()
    {
        // The safe reading of a mixed case: up grew, down went back. Subtracting only the direction that grew would
        // under-count an adapter that was reset and has already moved bytes; the new pair is counted instead.
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1000, 2000))), At(D1, 8, 0, 0));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1500, 30))), At(D1, 8, 0, 1));
        Assert.Equal(new Traffic(1500, 30), l.Today(ConnectionId.Phone));
    }

    [Fact]
    public void A_changed_adapter_name_rebaselines_without_counting()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Lan, R("Ethernet", 1000, 1000))), At(D1, 8, 0, 0));
        l.Update(Map((ConnectionId.Lan, R("Ethernet 7", 5_000_000_000, 5_000_000_000))), At(D1, 8, 0, 1));
        Assert.Equal(default, l.Today(ConnectionId.Lan));
        l.Update(Map((ConnectionId.Lan, R("Ethernet 7", 5_000_000_001, 5_000_000_000))), At(D1, 8, 0, 2));
        Assert.Equal(new Traffic(1, 0), l.Today(ConnectionId.Lan));
    }

    [Fact]
    public void An_absent_slot_keeps_its_baseline()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1000, 5000))), At(D1, 8, 0, 0));
        l.Update(Map(), At(D1, 8, 0, 1));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1000, 5040))), At(D1, 8, 0, 2));
        Assert.Equal(40, l.Today(ConnectionId.Phone).Down);
        Assert.Equal(0, l.Today(ConnectionId.Phone).Up);
    }

    [Fact]
    public void Each_connection_is_counted_independently()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 100)), (ConnectionId.Lan, R("Ethernet", 5000, 5000))), At(D1, 8, 0, 0));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 110, 160)), (ConnectionId.Lan, R("Ethernet", 5300, 6000))), At(D1, 8, 0, 1));
        Assert.Equal(new Traffic(10, 60), l.Today(ConnectionId.Phone));
        Assert.Equal(new Traffic(300, 1000), l.Today(ConnectionId.Lan));
        Assert.Equal(default, l.Today(ConnectionId.Wifi));
    }

    [Fact]
    public void A_reading_after_midnight_goes_to_the_new_day_and_keeps_the_baseline()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 0, 1000))), At(D1, 23, 59, 59));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 0, 1100))), At(D2, 0, 0, 1));
        Assert.Equal(new Traffic(0, 100), l.Today(ConnectionId.Phone));
        var snapshot = l.Snapshot();
        Assert.Equal(100, snapshot[D2].Totals[ConnectionId.Phone].Down);
        Assert.Equal(default, snapshot[D1].Totals.GetValueOrDefault(ConnectionId.Phone));
    }

    [Fact]
    public void A_clock_stepped_back_never_makes_a_negative_total()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 100))), At(D2, 9, 59, 0));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 200, 300))), At(D2, 10, 0, 0));
        Assert.Equal(new Traffic(100, 200), l.Today(ConnectionId.Phone));

        var ex = Record.Exception(() => l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 250, 350))), At(D1, 9, 0, 0)));
        Assert.Null(ex);

        var snapshot = l.Snapshot();
        Assert.Equal(new Traffic(100, 200), snapshot[D2].Totals[ConnectionId.Phone]); // the later day did not shrink
        Assert.Equal(new Traffic(50, 50), snapshot[D1].Totals[ConnectionId.Phone]); // growth belongs to the earlier day
        Assert.All(snapshot.Values.SelectMany(d => d.Totals.Values), t => Assert.True(t.Up >= 0 && t.Down >= 0));
    }

    [Fact]
    public void Clear_zeroes_the_totals_and_does_not_recount_the_boot_total() // Review Focus 2
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1000, 1000))), At(D1, 8, 0, 0));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1000, 1500))), At(D1, 8, 0, 1));
        Assert.Equal(500, l.Today(ConnectionId.Phone).Down);
        l.TakeDirty();

        l.Clear();
        Assert.True(l.TakeDirty());
        Assert.Equal(default, l.Today(ConnectionId.Phone));
        Assert.Null(l.Snapshot()[D1].Since);

        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1000, 1500))), At(D1, 8, 0, 2));
        Assert.Equal(default, l.Today(ConnectionId.Phone));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1000, 1507))), At(D1, 8, 0, 3));
        Assert.Equal(7, l.Today(ConnectionId.Phone).Down);
    }

    [Fact]
    public void Snapshot_and_restore_round_trip()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 100)), (ConnectionId.Lan, R("Ethernet", 10, 10))), At(D1, 8, 13, 0));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 150, 400)), (ConnectionId.Lan, R("Ethernet", 12, 90))), At(D1, 8, 13, 1));

        var restored = new AdapterLedger(l.Snapshot(), D1);
        Assert.Equal(new Traffic(50, 300), restored.Today(ConnectionId.Phone));
        Assert.Equal(new Traffic(2, 80), restored.Today(ConnectionId.Lan));
        Assert.Equal(default, restored.Today(ConnectionId.Wifi));
        Assert.Equal(new TimeOnly(8, 13, 0), restored.Snapshot()[D1].Since);

        // The snapshot is a copy: later updates do not change it.
        var frozen = l.Snapshot();
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 160, 400))), At(D1, 8, 13, 2));
        Assert.Equal(new Traffic(50, 300), frozen[D1].Totals[ConnectionId.Phone]);
    }

    [Fact]
    public void A_restored_ledger_starts_with_no_baseline_so_the_first_reading_counts_nothing()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 100))), At(D1, 8, 0, 0));
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 200))), At(D1, 8, 0, 1));

        var restored = new AdapterLedger(l.Snapshot(), D1);
        restored.Update(Map((ConnectionId.Phone, R("Ethernet 5", 900_000, 900_000))), At(D1, 9, 0, 0));
        Assert.Equal(new Traffic(0, 100), restored.Today(ConnectionId.Phone));
    }

    [Fact]
    public void Days_past_the_retention_are_dropped_from_the_snapshot()
    {
        var oldest = D1.AddDays(-(UsageSnapshot.RetentionDays - 1));
        var tooOld = D1.AddDays(-UsageSnapshot.RetentionDays);
        var totals = new Dictionary<ConnectionId, Traffic> { [ConnectionId.Phone] = new Traffic(1, 2) };
        var restored = new Dictionary<DateOnly, LedgerDay>
        {
            [oldest] = new LedgerDay(totals, new TimeOnly(1, 0, 0)),
            [tooOld] = new LedgerDay(totals, new TimeOnly(1, 0, 0)),
        };

        var l = new AdapterLedger(restored, D1);
        Assert.Contains(oldest, l.Snapshot().Keys);
        Assert.DoesNotContain(tooOld, l.Snapshot().Keys);

        // A day change prunes the same way.
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 1, 1))), At(D1.AddDays(1), 0, 0, 1));
        Assert.DoesNotContain(oldest, l.Snapshot().Keys);
    }

    [Fact]
    public void TakeDirty_is_true_once_after_a_change()
    {
        var l = new AdapterLedger(null, D1);
        Assert.False(l.TakeDirty());

        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 100))), At(D1, 8, 0, 0));
        Assert.True(l.TakeDirty());
        Assert.False(l.TakeDirty());

        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 100))), At(D1, 8, 0, 1)); // nothing grew
        Assert.False(l.TakeDirty());

        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 101))), At(D1, 8, 0, 2));
        Assert.True(l.TakeDirty());
        Assert.False(l.TakeDirty());
    }

    // Feeds one reading per second, each perTick bytes of download above the last; returns the counter's new value.
    static long Feed(AdapterLedger l, ConnectionId id, long counter, int fromSecond, int toSecond, long perTick)
    {
        for (var s = fromSecond; s <= toSecond; s++)
        {
            counter += perTick;
            l.Update(Map((id, R("Ethernet 5", 0, counter))), At(D1, 8, 0, s));
        }
        return counter;
    }

    [Fact]
    public void Rates_are_the_bytes_over_the_last_three_seconds_per_connection()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 0, 0)), (ConnectionId.Lan, R("Ethernet", 0, 0))), At(D1, 8, 0, 0));
        // Phone +3000 down per tick; LAN +1800 down and +900 up per tick.
        for (var s = 1; s <= 4; s++)
            l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 0, s * 3000)), (ConnectionId.Lan, R("Ethernet", s * 900, s * 1800))), At(D1, 8, 0, s));

        var seconds = (long)UsageCounter.RateWindow.TotalSeconds;
        var inWindow = 3; // ticks at 2, 3, 4 s are younger than RateWindow - RateWindowMargin; the tick at 1 s has aged out
        Assert.Equal(inWindow * 3000 / seconds, l.Rates[ConnectionId.Phone]);
        Assert.Equal(inWindow * 2700 / seconds, l.Rates[ConnectionId.Lan]);
        Assert.Equal(0, l.Rates[ConnectionId.Wifi]);
    }

    [Fact]
    public void A_connection_below_the_active_threshold_reads_zero()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 0, 0))), At(D1, 8, 0, 0));
        var counter = Feed(l, ConnectionId.Phone, 0, 1, 3, UsageCounter.ActiveBytesPerSecond - 1); // x3 ticks / 3 s = just under
        Assert.Equal(0, l.Rates[ConnectionId.Phone]);

        Feed(l, ConnectionId.Phone, counter, 4, 6, UsageCounter.ActiveBytesPerSecond); // at the threshold it reads its rate
        Assert.Equal(UsageCounter.ActiveBytesPerSecond, l.Rates[ConnectionId.Phone]);
    }

    [Fact]
    public void Rates_fade_to_zero_when_ticks_stop_carrying_bytes()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 0, 0))), At(D1, 8, 0, 0));
        var counter = Feed(l, ConnectionId.Phone, 0, 1, 3, 5000);
        Assert.Equal(5000, l.Rates[ConnectionId.Phone]);

        counter = Feed(l, ConnectionId.Phone, counter, 4, 6, 0); // the counter stands still
        Assert.Equal(0, l.Rates[ConnectionId.Phone]);

        Feed(l, ConnectionId.Phone, counter, 7, 9, 5000);
        Assert.Equal(5000, l.Rates[ConnectionId.Phone]);
        l.Update(Map(), At(D1, 8, 0, 12)); // an empty tick (no adapters at all) also lets the window age out
        Assert.Equal(0, l.Rates[ConnectionId.Phone]);
    }

    [Fact]
    public void A_baseline_tick_adds_no_rate()
    {
        var l = new AdapterLedger(null, D1);
        l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 9_000_000, 90_000_000))), At(D1, 8, 0, 0)); // the since-boot counter
        Assert.Equal(0, l.Rates[ConnectionId.Phone]);

        // A renamed adapter is a new baseline too.
        l.Update(Map((ConnectionId.Phone, R("Ethernet 7", 9_000_000, 90_000_000))), At(D1, 8, 0, 1));
        Assert.Equal(0, l.Rates[ConnectionId.Phone]);
    }
}
