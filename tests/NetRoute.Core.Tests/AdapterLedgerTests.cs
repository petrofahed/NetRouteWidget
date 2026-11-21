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
}
