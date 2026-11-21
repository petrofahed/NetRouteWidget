using System.Net.NetworkInformation;

namespace NetRoute.Core.Tests;

public class AdapterLedgerSamplerTests
{
    static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    /// Counts how often the adapter list is asked for (the mapping re-detection).
    sealed class CountingSource : IAdapterSource
    {
        public List<AdapterInfo> Adapters { get; } = new();
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }

        public IReadOnlyList<AdapterInfo> GetAdapters()
        {
            Calls++;
            return Throw is { } ex ? throw ex : Adapters.ToList();
        }
    }

    sealed class Rig
    {
        public CountingSource Source { get; } = new();
        public FakeAdapterCounters Counters { get; } = new();
        public List<string> Log { get; } = new();
        public DateTimeOffset Now { get; set; } = Start;
        public AdapterOverrides Overrides { get; set; } = AdapterOverrides.None;
        public AdapterLedgerSampler Sampler { get; }

        public Rig() => Sampler = new AdapterLedgerSampler(Source, Counters, () => Overrides, Log.Add, () => Now);
    }

    [Fact]
    public void A_slot_with_an_adapter_gets_that_adapters_counters()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5)); // named "Ethernet 5"
        rig.Counters.ByIndex[5] = new Traffic(100, 2000);
        rig.Counters.ByIndex[10] = new Traffic(1, 2); // some other adapter's row

        var readings = rig.Sampler.Sample();

        var reading = Assert.Single(readings);
        Assert.Equal(ConnectionId.Phone, reading.Key);
        Assert.Equal(new AdapterReading("Ethernet 5", new Traffic(100, 2000)), reading.Value);
    }

    [Fact]
    public void A_turned_off_connection_is_still_sampled()
    {
        // The sampler never tells Detect what the user turned off, so every connection that has an adapter is read.
        var rig = new Rig { Overrides = new AdapterOverrides(null, null, WifiMac: "AA-BB-CC-00-00-18") };
        rig.Source.Adapters.AddRange([TestAdapters.Lan(), TestAdapters.Wifi(), TestAdapters.Phone(index: 5)]);
        rig.Counters.ByIndex[10] = new Traffic(1, 2);
        rig.Counters.ByIndex[18] = new Traffic(3, 4);
        rig.Counters.ByIndex[5] = new Traffic(5, 6);

        var readings = rig.Sampler.Sample();

        Assert.Equal(3, readings.Count);
        Assert.Equal(new AdapterReading("Ethernet", new Traffic(1, 2)), readings[ConnectionId.Lan]);
        Assert.Equal(new AdapterReading("Wi-Fi", new Traffic(3, 4)), readings[ConnectionId.Wifi]);
        Assert.Equal(new AdapterReading("Ethernet 5", new Traffic(5, 6)), readings[ConnectionId.Phone]);
    }

    [Fact]
    public void Each_tick_reads_only_the_mapped_slots_interfaces()
    {
        var rig = new Rig { Overrides = new AdapterOverrides(null, null, WifiMac: "AA-BB-CC-00-00-18") };
        rig.Source.Adapters.AddRange([TestAdapters.Lan(), TestAdapters.Wifi(), TestAdapters.Phone(index: 5), TestAdapters.VmNet(1)]);
        rig.Counters.ByIndex[10] = new Traffic(1, 2);
        rig.Counters.ByIndex[18] = new Traffic(3, 4);
        rig.Counters.ByIndex[5] = new Traffic(5, 6);
        rig.Counters.ByIndex[1] = new Traffic(7, 8); // VMware: never a slot, never read

        rig.Sampler.Sample();
        Assert.Equal([10, 18, 5], rig.Counters.Reads);

        rig.Counters.Reads.Clear();
        rig.Sampler.Sample(); // a tick inside MappingRefresh: still exactly one read per mapped slot
        Assert.Equal([10, 18, 5], rig.Counters.Reads);
    }

    [Fact]
    public void A_slot_whose_read_returns_nothing_is_the_only_one_dropped()
    {
        var rig = new Rig();
        rig.Source.Adapters.AddRange([TestAdapters.Lan(), TestAdapters.Phone(index: 5)]);
        rig.Counters.ByIndex[10] = new Traffic(1, 2); // no row for index 5

        var readings = rig.Sampler.Sample();

        var reading = Assert.Single(readings);
        Assert.Equal(ConnectionId.Lan, reading.Key);
        Assert.Empty(rig.Log); // null is "no such interface", not a failure
    }

    [Fact]
    public void The_mapping_is_redetected_only_after_the_refresh_interval_or_an_invalidate()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.ByIndex[5] = new Traffic(1, 1);

        rig.Sampler.Sample();
        Assert.Equal(1, rig.Source.Calls);

        rig.Now += AdapterLedgerSampler.MappingRefresh - TimeSpan.FromMilliseconds(1);
        rig.Sampler.Sample();
        Assert.Equal(1, rig.Source.Calls);

        rig.Now += TimeSpan.FromMilliseconds(1);
        rig.Sampler.Sample();
        Assert.Equal(2, rig.Source.Calls);

        rig.Sampler.Sample();
        Assert.Equal(2, rig.Source.Calls);

        rig.Sampler.InvalidateMapping();
        rig.Sampler.Sample();
        Assert.Equal(3, rig.Source.Calls);

        rig.Sampler.Sample();
        Assert.Equal(3, rig.Source.Calls);
    }

    [Fact]
    public void A_vanished_adapter_keeps_its_last_name_and_index_until_it_is_replaced()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.ByIndex[5] = new Traffic(10, 20);
        Assert.Equal("Ethernet 5", rig.Sampler.Sample()[ConnectionId.Phone].Name);

        // The phone adapter drops out of the list (NotFound) but Windows still reports its counter row.
        rig.Source.Adapters.Clear();
        rig.Counters.ByIndex[5] = new Traffic(30, 40);
        rig.Sampler.InvalidateMapping();
        var kept = rig.Sampler.Sample();
        Assert.Equal(new AdapterReading("Ethernet 5", new Traffic(30, 40)), kept[ConnectionId.Phone]);

        // Its counter row goes too: nothing to read.
        rig.Counters.ByIndex.Remove(5);
        Assert.Empty(rig.Sampler.Sample());

        // A replacement under another name and index takes over.
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 7));
        rig.Counters.ByIndex[7] = new Traffic(1, 2);
        rig.Counters.ByIndex[5] = new Traffic(30, 40);
        rig.Sampler.InvalidateMapping();
        rig.Counters.Reads.Clear();
        var replaced = rig.Sampler.Sample();
        Assert.Equal(new AdapterReading("Ethernet 7", new Traffic(1, 2)), replaced[ConnectionId.Phone]);
        Assert.Equal([7], rig.Counters.Reads);
    }

    [Fact]
    public void A_not_used_slot_forgets_its_name()
    {
        var rig = new Rig { Overrides = new AdapterOverrides(null, null, WifiMac: "AA-BB-CC-00-00-18") };
        rig.Source.Adapters.Add(TestAdapters.Wifi());
        rig.Counters.ByIndex[18] = new Traffic(7, 8);
        Assert.Equal("Wi-Fi", rig.Sampler.Sample()[ConnectionId.Wifi].Name);

        // The user marks Wi-Fi Not used: no reading, though the adapter and its counter row are still there.
        rig.Overrides = new AdapterOverrides(null, null, WifiMac: AdapterOverrides.NotUsed);
        rig.Sampler.InvalidateMapping();
        Assert.Empty(rig.Sampler.Sample());

        // Chosen again but the adapter is gone: the old name was forgotten, so there is nothing to fall back to.
        rig.Overrides = new AdapterOverrides(null, null, WifiMac: "AA-BB-CC-00-00-18");
        rig.Source.Adapters.Clear();
        rig.Sampler.InvalidateMapping();
        Assert.Empty(rig.Sampler.Sample());
    }

    [Fact]
    public void Two_slots_that_resolve_to_one_adapter_name_are_counted_once()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.ByIndex[5] = new Traffic(1, 2);
        Assert.Equal("Ethernet 5", rig.Sampler.Sample()[ConnectionId.Phone].Name);

        // The phone vanishes (its slot falls back to the remembered "Ethernet 5") while the LAN is now an adapter
        // that Windows calls "ethernet 5" too.
        rig.Source.Adapters.Clear();
        rig.Source.Adapters.Add(TestAdapters.Lan() with { Name = "ethernet 5" });
        rig.Counters.ByIndex[10] = new Traffic(3, 4);
        rig.Sampler.InvalidateMapping();

        var readings = rig.Sampler.Sample();

        // The live LAN keeps the name; the phone's remembered one loses it.
        var reading = Assert.Single(readings);
        Assert.Equal(ConnectionId.Lan, reading.Key);
        Assert.Equal(new AdapterReading("ethernet 5", new Traffic(3, 4)), reading.Value);
    }

    [Fact]
    public void Two_slots_that_resolve_to_one_interface_index_are_counted_once()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.ByIndex[5] = new Traffic(1, 2);
        rig.Sampler.Sample();

        // The phone vanishes; the LAN is now a live adapter under another name but with the phone's old index.
        rig.Source.Adapters.Clear();
        rig.Source.Adapters.Add(TestAdapters.Lan(index: 5));
        rig.Sampler.InvalidateMapping();

        var reading = Assert.Single(rig.Sampler.Sample());
        Assert.Equal(new AdapterReading("Ethernet", new Traffic(1, 2)), reading.Value);
        Assert.Equal(ConnectionId.Lan, reading.Key);
    }

    [Fact]
    public void A_live_slot_beats_a_remembered_name_for_the_same_adapter()
    {
        // The user re-points the LAN's adapter to the Phone slot: the LAN goes NotFound and remembers "Ethernet",
        // while the Phone now detects that same adapter live. Its bytes are the Phone's, not the LAN's.
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Lan());
        rig.Counters.ByIndex[10] = new Traffic(1, 2);
        Assert.Equal(ConnectionId.Lan, Assert.Single(rig.Sampler.Sample()).Key);

        rig.Overrides = new AdapterOverrides(PhoneDescription: "Realtek Gaming 2.5GbE Family Controller", LanMac: null);
        rig.Sampler.InvalidateMapping();

        var reading = Assert.Single(rig.Sampler.Sample());
        Assert.Equal(ConnectionId.Phone, reading.Key);
        Assert.Equal(new AdapterReading("Ethernet", new Traffic(1, 2)), reading.Value);
    }

    [Fact]
    public void A_counter_failure_yields_no_readings_and_logs_once_per_streak()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.ByIndex[5] = new Traffic(1, 2);
        rig.Counters.Throws = new NetworkInformationException(5);

        Assert.Empty(rig.Sampler.Sample());
        Assert.Empty(rig.Sampler.Sample());
        Assert.Single(rig.Log);

        rig.Counters.Throws = null;
        Assert.Single(rig.Sampler.Sample());
        Assert.Single(rig.Log);

        rig.Counters.Throws = new NetworkInformationException(5);
        Assert.Empty(rig.Sampler.Sample());
        Assert.Equal(2, rig.Log.Count);
    }

    [Fact]
    public void A_detection_failure_keeps_the_previous_mapping()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.ByIndex[5] = new Traffic(1, 2);
        Assert.Single(rig.Sampler.Sample());

        rig.Source.Throw = new InvalidOperationException("adapter list unavailable");
        rig.Now += AdapterLedgerSampler.MappingRefresh;
        rig.Counters.ByIndex[5] = new Traffic(3, 4);

        var readings = rig.Sampler.Sample();

        Assert.Equal(2, rig.Source.Calls); // it did try
        var reading = Assert.Single(readings);
        Assert.Equal(new AdapterReading("Ethernet 5", new Traffic(3, 4)), reading.Value);
    }
}
