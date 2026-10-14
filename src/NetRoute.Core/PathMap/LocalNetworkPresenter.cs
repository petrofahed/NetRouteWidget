using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.PathMap;

/// What a device in the local list is doing. NoPing: it never answered in the window (off, unplugged, or it blocks pings): grey, and
/// judged neither way. Down: it answered earlier in the window but not now.
public enum LocalState { Measuring, Answering, Lossy, Down, NoPing, NotFound, Unavailable }

public enum InternetState { Unknown, Fine, Failing }

public sealed record LocalDeviceRow(string Key, string Name, string Address, LocalState State, string StateLabel, string Metric, bool IsRouter, bool OnSubnet);

public sealed record NicView(string Name, string SpeedText, bool SpeedWarning, string SpeedNote, string ErrorsText, bool ErrorsWarning);

public sealed record LocalNetworkView(
    bool Connected, string Message, IReadOnlyList<string> Verdict, bool VerdictWarning, NicView? Nic,
    IReadOnlyList<LocalDeviceRow> Rows, DetailView? Detail);

/// Pure mapping from a monitor snapshot (plus the card's statistics and the path map's internet state) to everything the tab shows.
public static class LocalNetworkPresenter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    const long Gigabit = 1_000_000_000;

    public static LocalNetworkView Present(
        LocalSnapshot snap, NicStats? nic, NicStats? nicBaseline, InternetState internet, string? selectedKey, PathThresholds t, int? prefixLength)
    {
        if (snap.Lan is null || snap.Source is null)
            return new LocalNetworkView(false, "No LAN connection: plug in the LAN cable to test your local network.", [], false, null, [], null);

        var devices = snap.Devices.Select(d => (Device: d, Stats: SampleStats.Of(d.Samples), State: StateOf(d, t))).ToList();
        var rows = devices.Select(x => new LocalDeviceRow(
            x.Device.Key, x.Device.Name, x.Device.Address, x.State, LabelOf(x.State), MetricOf(x.Stats, x.State),
            x.Device.IsRouter, x.Device.IsRouter || OnSubnet(snap.Source, x.Device.Resolved, prefixLength))).ToList();

        // The same machine listed twice (the router added by hand after a scan, "nas" and "nas.local") is judged once: the router
        // wins, then the first device with that address. The duplicates stay in the list as rows.
        var counted = new HashSet<IPAddress>();
        if (devices.Where(x => x.Device.IsRouter).Select(x => x.Device.Resolved).FirstOrDefault() is { } routerAddress) counted.Add(routerAddress);
        var entries = new List<LocalVerdict.Entry>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsRouter && devices[i].Device.Resolved is { } address && !counted.Add(address)) continue;
            entries.Add(new LocalVerdict.Entry(rows[i].Name, rows[i].State, rows[i].IsRouter, rows[i].OnSubnet, devices[i].Stats.Valid));
        }
        var verdict = LocalVerdict.Of(entries, internet, t);

        var detail = selectedKey is null ? null : devices.Where(x => x.Device.Key == selectedKey).Select(x => DetailOf(x.Device, x.Stats, x.State, t)).FirstOrDefault();
        return new LocalNetworkView(true, "", verdict.Lines, verdict.Warning, nic is null ? null : NicOf(nic, nicBaseline), rows, detail);
    }

    /// The path map's internet row for the LAN lane, as the verdict needs it.
    public static InternetState InternetOf(HopDisplay state) => state switch
    {
        HopDisplay.Answering => InternetState.Fine,
        HopDisplay.Suspect or HopDisplay.Broken or HopDisplay.Silent => InternetState.Failing,
        _ => InternetState.Unknown,
    };

    /// The colour family of a state in the path map (the tab draws its dots and chip with the same brushes).
    public static HopDisplay AsHop(LocalState state) => state switch
    {
        LocalState.Measuring => HopDisplay.Measuring,
        LocalState.Answering => HopDisplay.Answering,
        LocalState.Lossy => HopDisplay.Suspect,
        LocalState.Down => HopDisplay.Broken,
        LocalState.NoPing => HopDisplay.Quiet,
        _ => HopDisplay.Unavailable,
    };

    static LocalState StateOf(DeviceSnapshot d, PathThresholds t)
    {
        if (d.NameNotFound) return LocalState.NotFound;
        var s = SampleStats.Of(d.Samples);
        return PathObserver.Observe(s, hidden: false, endpoint: false, t) switch
        {
            Observation.Answering => LocalState.Answering,
            Observation.Lossy => LocalState.Lossy,
            Observation.Silent => s.Replies == 0 ? LocalState.NoPing : LocalState.Down,
            Observation.Unavailable => LocalState.Unavailable,
            _ => LocalState.Measuring,
        };
    }

    static string LabelOf(LocalState state) => state switch
    {
        LocalState.Measuring => "Measuring",
        LocalState.Answering => "Answering",
        LocalState.Lossy => "Losing packets",
        LocalState.Down => "No reply",
        LocalState.NoPing => "Doesn't answer ping",
        LocalState.NotFound => "Name not found",
        _ => "Unavailable",
    };

    static string MetricOf(SampleStats s, LocalState state) => state switch
    {
        LocalState.NotFound or LocalState.Unavailable => "",
        _ when s.Valid == 0 => "",
        _ when s.Lost == s.Valid => "no reply",
        _ => $"{s.AvgMs ?? 0} ms · {s.LossPercent.ToString("0", Inv)}%",
    };

    /// A device whose address is unknown (a name still to resolve) is never held against the verdict, so it counts as on the subnet.
    static bool OnSubnet(IPAddress source, IPAddress? device, int? prefixLength)
    {
        if (device is null) return true;
        if (source.AddressFamily != AddressFamily.InterNetwork || device.AddressFamily != AddressFamily.InterNetwork) return false;
        var prefix = Math.Clamp(prefixLength ?? 24, 0, 32);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (ToUInt(source) & mask) == (ToUInt(device) & mask);
    }

    static uint ToUInt(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray(), 0);

    static DetailView DetailOf(DeviceSnapshot d, SampleStats stats, LocalState state, PathThresholds t) =>
        new(d.Key, d.Name, d.Address, LabelOf(state), AsHop(state), PathMapPresenter.StatsOf(stats, t), d.Samples, NoteOf(state, d.IsRouter));

    static string NoteOf(LocalState state, bool router) => state switch
    {
        LocalState.Measuring => "Not enough checks yet to judge this device.",
        LocalState.Answering => router ? "Your router, answering its pings normally." : "Answering its pings normally.",
        LocalState.Lossy => "It loses some of its pings. If other devices are fine, check this device's cable and switch port.",
        LocalState.Down => "It answered earlier in the last 30 s but not now.",
        LocalState.NoPing => "It did not answer any ping in the last 30 s. A device that is off or unplugged looks like this, and so does one that ignores pings.",
        LocalState.NotFound => "This name could not be found on your network.",
        _ => "The checks could not run on this PC.",
    };

    static NicView NicOf(NicStats nic, NicStats? baseline)
    {
        var speedWarning = nic.IsEthernet && nic.SpeedBps is > 0 and < Gigabit;
        var errors = Math.Max(0, nic.InErrors - (baseline?.InErrors ?? nic.InErrors)) + Math.Max(0, nic.OutErrors - (baseline?.OutErrors ?? nic.OutErrors));
        var discards = Math.Max(0, nic.InDiscards - (baseline?.InDiscards ?? nic.InDiscards)) + Math.Max(0, nic.OutDiscards - (baseline?.OutDiscards ?? nic.OutDiscards));
        return new NicView(
            nic.Name, SpeedText(nic.SpeedBps), speedWarning,
            speedWarning ? "Gigabit is normal on modern cables; 100 Mbps often means a damaged cable or an old device in the path." : "",
            $"{errors.ToString(Inv)} errors and {discards.ToString(Inv)} discarded packets since you opened this tab", errors + discards > 0);
    }

    static string SpeedText(long? bps) => bps switch
    {
        null or <= 0 => "unknown",
        >= Gigabit => $"{(bps.Value / (double)Gigabit).ToString("0.##", Inv)} Gbps",
        _ => $"{(bps.Value / 1_000_000).ToString(Inv)} Mbps",
    };
}

/// The plain-words conclusion of the Local network tab: first matching rule wins (see the v5.2 spec).
static class LocalVerdict
{
    internal sealed record Entry(string Name, LocalState State, bool IsRouter, bool OnSubnet, int Valid);

    internal sealed record Result(IReadOnlyList<string> Lines, bool Warning);

    internal static Result Of(IReadOnlyList<Entry> entries, InternetState internet, PathThresholds t)
    {
        static Result Say(bool warning, string line) => new([line], warning);

        var onLan = entries.Where(e => e.OnSubnet).ToList();
        var router = onLan.FirstOrDefault(e => e.IsRouter);
        if (router is null) return Say(false, "No router was found for the LAN connection, so the local network cannot be judged.");
        if (router.State == LocalState.Measuring) return Say(false, $"Measuring… {Math.Min(t.HopMinSamples, router.Valid)} of {t.HopMinSamples} s.");

        static bool Bad(Entry e) => e.State is LocalState.Lossy or LocalState.Down;
        var others = onLan.Where(e => !e.IsRouter).ToList();
        var badOthers = others.Where(Bad).ToList();

        if (Bad(router) && badOthers.Count > 0)
            return Say(true, "Several devices on your local network are losing packets. Check this PC's cable and its switch port, and the switch.");
        if (!Bad(router) && badOthers.Count >= 2)
            return Say(true, "Several devices are losing packets but the router is fine: check the switch they share and their cables.");
        if (!Bad(router) && badOthers.Count == 1)
            return Say(true, $"{badOthers[0].Name} is losing packets while the router and the others are fine: check that device's cable and switch port.");

        var healthy = onLan.Where(e => e.State == LocalState.Answering).ToList();
        if (Bad(router))
            return healthy.Count > 0
                ? Say(true, "The router answers pings badly but your other devices are fine: the router may be de-prioritising pings. This does not point to a cable.")
                : Say(true, "The router is losing packets. Add another device (like your NAS) to tell a PC-side cable problem from a router problem.");

        if (healthy.Count == 0)
            return Say(false, "No device on your local network can be measured yet. Add your NAS or another device that answers pings.");
        return internet == InternetState.Failing
            ? Say(true, $"Your local network is healthy: {Join(healthy.Select(e => e.Name).ToList())} {(healthy.Count == 1 ? "answers" : "answer")} normally. The fault is beyond your router (its link to the modem, the modem, or the line).")
            : Say(false, "Your local network is healthy.");
    }

    static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
