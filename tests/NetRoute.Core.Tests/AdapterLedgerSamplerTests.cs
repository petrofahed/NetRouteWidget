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
        rig.Counters.All["Ethernet 5"] = new Traffic(100, 2000);
        rig.Counters.All["Ethernet"] = new Traffic(1, 2); // some other adapter's row

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
        rig.Counters.All["Ethernet"] = new Traffic(1, 2);
        rig.Counters.All["Wi-Fi"] = new Traffic(3, 4);
        rig.Counters.All["Ethernet 5"] = new Traffic(5, 6);

        var readings = rig.Sampler.Sample();

        Assert.Equal(3, readings.Count);
        Assert.Equal(new Traffic(1, 2), readings[ConnectionId.Lan].Bytes);
        Assert.Equal(new Traffic(3, 4), readings[ConnectionId.Wifi].Bytes);
        Assert.Equal(new Traffic(5, 6), readings[ConnectionId.Phone].Bytes);
    }

    [Fact]
    public void The_mapping_is_redetected_only_after_the_refresh_interval_or_an_invalidate()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.All["Ethernet 5"] = new Traffic(1, 1);

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
    public void A_vanished_adapter_keeps_its_last_name_until_it_is_replaced()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.All["Ethernet 5"] = new Traffic(10, 20);
        Assert.Equal("Ethernet 5", rig.Sampler.Sample()[ConnectionId.Phone].Name);

        // The phone adapter drops out of the list (NotFound) but Windows still reports its counter row.
        rig.Source.Adapters.Clear();
        rig.Counters.All["Ethernet 5"] = new Traffic(30, 40);
        rig.Sampler.InvalidateMapping();
        var kept = rig.Sampler.Sample();
        Assert.Equal(new AdapterReading("Ethernet 5", new Traffic(30, 40)), kept[ConnectionId.Phone]);

        // Its counter row goes too: nothing to read.
        rig.Counters.All.Remove("Ethernet 5");
        Assert.Empty(rig.Sampler.Sample());

        // A replacement under another name takes over.
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 7));
        rig.Counters.All["Ethernet 7"] = new Traffic(1, 2);
        rig.Counters.All["Ethernet 5"] = new Traffic(30, 40);
        rig.Sampler.InvalidateMapping();
        var replaced = rig.Sampler.Sample();
        Assert.Equal(new AdapterReading("Ethernet 7", new Traffic(1, 2)), replaced[ConnectionId.Phone]);
    }

    [Fact]
    public void A_not_used_slot_forgets_its_name()
    {
        var rig = new Rig { Overrides = new AdapterOverrides(null, null, WifiMac: "AA-BB-CC-00-00-18") };
        rig.Source.Adapters.Add(TestAdapters.Wifi());
        rig.Counters.All["Wi-Fi"] = new Traffic(7, 8);
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
        rig.Counters.All["Ethernet 5"] = new Traffic(1, 2);
        Assert.Equal("Ethernet 5", rig.Sampler.Sample()[ConnectionId.Phone].Name);

        // The phone vanishes (its slot falls back to the remembered "Ethernet 5") while the LAN is now an adapter
        // that Windows calls "ethernet 5" too.
        rig.Source.Adapters.Clear();
        rig.Source.Adapters.Add(TestAdapters.Lan() with { Name = "ethernet 5" });
        rig.Sampler.InvalidateMapping();

        var readings = rig.Sampler.Sample();

        // Lan comes before Phone in Connections.All, so it keeps the name.
        var reading = Assert.Single(readings);
        Assert.Equal(ConnectionId.Lan, reading.Key);
        Assert.Equal(new Traffic(1, 2), reading.Value.Bytes);
    }

    [Fact]
    public void A_counter_failure_yields_no_readings_and_logs_once_per_streak()
    {
        var rig = new Rig();
        rig.Source.Adapters.Add(TestAdapters.Phone(index: 5));
        rig.Counters.All["Ethernet 5"] = new Traffic(1, 2);
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
        rig.Counters.All["Ethernet 5"] = new Traffic(1, 2);
        Assert.Single(rig.Sampler.Sample());

        rig.Source.Throw = new InvalidOperationException("adapter list unavailable");
        rig.Now += AdapterLedgerSampler.MappingRefresh;
        rig.Counters.All["Ethernet 5"] = new Traffic(3, 4);

        var readings = rig.Sampler.Sample();

        Assert.Equal(2, rig.Source.Calls); // it did try
        var reading = Assert.Single(readings);
        Assert.Equal(new AdapterReading("Ethernet 5", new Traffic(3, 4)), reading.Value);
    }
}
