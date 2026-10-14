using System.Net;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public class LocalNetworkPresenterTests
{
    static IPAddress Ip(string s) => IPAddress.Parse(s);
    static readonly PathThresholds T = PathKit.T;
    static readonly IPAddress Source = Ip("192.168.86.42");

    static DeviceSnapshot Router(Func<int, ProbeOutcome> outcome, int rtt = 1) =>
        new(LocalNetworkMonitor.RouterKey, "Router", "192.168.86.1", Ip("192.168.86.1"), true, false, PathKit.Every(1, outcome, rtt));

    static DeviceSnapshot Device(string name, string address, Func<int, ProbeOutcome> outcome, int rtt = 1, string? resolved = null, bool notFound = false) =>
        new(LocalNetworkMonitor.KeyOf(address), name, address, resolved is null ? (IPAddress.TryParse(address, out var ip) ? ip : null) : Ip(resolved),
            false, notFound, notFound ? PathKit.Every(1, PathKit.AllUnavailable) : PathKit.Every(1, outcome, rtt));

    static LocalSnapshot Snap(params DeviceSnapshot[] devices) => new(PathKit.Now, TestAdapters.Lan(), Source, devices);

    static LocalNetworkView Present(LocalSnapshot snap, InternetState internet = InternetState.Unknown, string? selected = null,
                                    NicStats? nic = null, NicStats? baseline = null) =>
        LocalNetworkPresenter.Present(snap, nic, baseline, internet, selected, T, 24);

    static string Verdict(LocalNetworkView view) => string.Join(" ", view.Verdict);

    [Fact]
    public void While_the_router_has_too_few_samples_the_verdict_says_it_is_measuring()
    {
        var shortSeries = new DeviceSnapshot(LocalNetworkMonitor.RouterKey, "Router", "192.168.86.1", Ip("192.168.86.1"), true, false,
            PathKit.Every(1, PathKit.AllReply).Take(4).ToList());

        var view = Present(Snap(shortSeries));

        Assert.Equal(["Measuring… 4 of 10 s."], view.Verdict);
        Assert.False(view.VerdictWarning);
    }

    [Fact]
    public void Everything_local_healthy_and_the_internet_failing_points_beyond_the_router()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.AllReply)), InternetState.Failing);

        Assert.Equal("Your local network is healthy: Router and NAS answer normally. The fault is beyond your router (its link to the modem, the modem, or the line).", Verdict(view));
        Assert.True(view.VerdictWarning);
    }

    [Fact]
    public void Everything_local_healthy_and_the_internet_fine_or_unknown_just_says_healthy()
    {
        foreach (var internet in new[] { InternetState.Fine, InternetState.Unknown })
        {
            var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.AllReply)), internet);

            Assert.Equal("Your local network is healthy.", Verdict(view));
            Assert.False(view.VerdictWarning);
        }
    }

    [Fact]
    public void The_router_and_another_device_losing_packets_points_at_this_pcs_cable_port_and_switch()
    {
        var view = Present(Snap(Router(PathKit.LostDuring(20)), Device("NAS", "192.168.86.44", PathKit.LostDuring(20))));

        Assert.Equal("Several devices on your local network are losing packets. Check this PC's cable and its switch port, and the switch.", Verdict(view));
        Assert.True(view.VerdictWarning);
    }

    [Fact]
    public void Two_devices_losing_packets_with_a_fine_router_points_at_the_switch_they_share()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.LostDuring(20)), Device("TV", "192.168.86.50", PathKit.LostDuring(20))));

        Assert.Equal("Several devices are losing packets but the router is fine: check the switch they share and their cables.", Verdict(view));
    }

    [Fact]
    public void One_device_losing_packets_names_that_device()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.LostDuring(20)), Device("TV", "192.168.86.50", PathKit.AllReply)));

        Assert.Equal("NAS is losing packets while the router and the others are fine: check that device's cable and switch port.", Verdict(view));
    }

    [Fact]
    public void A_lossy_router_with_healthy_other_devices_is_not_a_cable_problem()
    {
        var view = Present(Snap(Router(PathKit.LostDuring(20)), Device("NAS", "192.168.86.44", PathKit.AllReply)));

        Assert.Equal("The router answers pings badly but your other devices are fine: the router may be de-prioritising pings. This does not point to a cable.", Verdict(view));
    }

    [Fact]
    public void A_lossy_router_alone_asks_for_another_device_to_compare_with()
    {
        var view = Present(Snap(Router(PathKit.LostDuring(20))));

        Assert.Equal("The router is losing packets. Add another device (like your NAS) to tell a PC-side cable problem from a router problem.", Verdict(view));
    }

    [Fact]
    public void A_device_that_never_answers_pings_is_grey_and_judged_neither_way()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("Printer", "192.168.86.60", PathKit.AllLost)), InternetState.Fine);

        var printer = view.Rows.Single(r => r.Name == "Printer");
        Assert.Equal(LocalState.NoPing, printer.State);
        Assert.Equal("Doesn't answer ping", printer.StateLabel);
        Assert.Equal("Your local network is healthy.", Verdict(view));
    }

    [Fact]
    public void With_nothing_measurable_the_verdict_asks_for_a_device()
    {
        var view = Present(Snap(Router(PathKit.AllUnavailable), Device("Printer", "192.168.86.60", PathKit.AllLost)));

        Assert.Equal("No device on your local network can be measured yet. Add your NAS or another device that answers pings.", Verdict(view));
    }

    [Fact]
    public void A_name_that_was_not_found_is_listed_and_ignored_by_the_verdict()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("Ghost", "ghost.local", PathKit.AllReply, notFound: true)), InternetState.Fine);

        Assert.Equal(LocalState.NotFound, view.Rows.Single(r => r.Name == "Ghost").State);
        Assert.Equal("Your local network is healthy.", Verdict(view));
    }

    [Fact]
    public void A_lossy_device_on_another_subnet_is_listed_but_ignored_by_the_verdict()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("Office", "10.0.0.5", PathKit.LostDuring(20))), InternetState.Fine);

        var office = view.Rows.Single(r => r.Name == "Office");
        Assert.False(office.OnSubnet);
        Assert.Equal(LocalState.Down, office.State);
        Assert.Equal("Your local network is healthy.", Verdict(view));
    }

    [Fact]
    public void Rows_show_state_latency_and_loss_with_the_router_first()
    {
        var view = Present(Snap(Router(PathKit.AllReply, rtt: 1), Device("NAS", "192.168.86.44", PathKit.LostDuring(25), rtt: 3)));

        Assert.Equal(["Router", "NAS"], view.Rows.Select(r => r.Name));
        Assert.Equal((LocalState.Answering, "Answering", "1 ms · 0%"), (view.Rows[0].State, view.Rows[0].StateLabel, view.Rows[0].Metric));
        Assert.Equal(LocalState.Down, view.Rows[1].State);
        Assert.Equal("No reply", view.Rows[1].StateLabel);
        Assert.True(view.Rows[0].IsRouter);
        Assert.True(view.Rows[0].OnSubnet);
    }

    [Fact]
    public void A_partly_lossy_device_is_losing_packets()
    {
        // 3 of every 10 pings lost, but never 3 in a row: lossy, not silent.
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", sec => sec % 10 is 2 or 5 or 8 ? ProbeOutcome.NoReply : ProbeOutcome.Reply)));

        var nas = view.Rows.Single(r => r.Name == "NAS");
        Assert.Equal(LocalState.Lossy, nas.State);
        Assert.Equal("Losing packets", nas.StateLabel);
    }

    [Fact]
    public void Without_a_lan_adapter_the_tab_says_so()
    {
        var view = Present(new LocalSnapshot(PathKit.Now, null, null, []));

        Assert.False(view.Connected);
        Assert.Equal("No LAN connection: plug in the LAN cable to test your local network.", view.Message);
        Assert.Empty(view.Rows);
        Assert.Empty(view.Verdict);
    }

    [Fact]
    public void A_router_that_is_missing_means_no_verdict_can_be_given()
    {
        var view = Present(Snap(Device("NAS", "192.168.86.44", PathKit.AllReply)));

        Assert.Equal("No router was found for the LAN connection, so the local network cannot be judged.", Verdict(view));
    }

    [Theory]
    [InlineData(1_000_000_000L, "1 Gbps", false)]
    [InlineData(2_500_000_000L, "2.5 Gbps", false)]
    [InlineData(100_000_000L, "100 Mbps", true)]
    [InlineData(10_000_000L, "10 Mbps", true)]
    public void An_ethernet_link_below_one_gigabit_is_a_warning(long bps, string text, bool warning)
    {
        var view = Present(Snap(Router(PathKit.AllReply)), nic: new NicStats("Ethernet", bps, true, 0, 0, 0, 0));

        Assert.Equal(text, view.Nic!.SpeedText);
        Assert.Equal(warning, view.Nic.SpeedWarning);
        Assert.Equal(warning, view.Nic.SpeedNote != "");
    }

    [Fact]
    public void A_wireless_or_unknown_speed_is_never_a_warning()
    {
        var wifi = Present(Snap(Router(PathKit.AllReply)), nic: new NicStats("Wi-Fi", 54_000_000, false, 0, 0, 0, 0));
        var unknown = Present(Snap(Router(PathKit.AllReply)), nic: new NicStats("Ethernet", null, true, 0, 0, 0, 0));

        Assert.False(wifi.Nic!.SpeedWarning);
        Assert.False(unknown.Nic!.SpeedWarning);
        Assert.Equal("unknown", unknown.Nic.SpeedText);
    }

    [Fact]
    public void Errors_are_the_change_since_the_tab_was_opened()
    {
        var baseline = new NicStats("Ethernet", 1_000_000_000, true, InErrors: 5, InDiscards: 2, OutErrors: 0, OutDiscards: 0);
        var calm = Present(Snap(Router(PathKit.AllReply)), nic: baseline, baseline: baseline);
        var growing = Present(Snap(Router(PathKit.AllReply)), baseline: baseline,
            nic: baseline with { InErrors = 9, InDiscards = 3, OutErrors = 1 });

        Assert.Equal("0 errors and 0 discarded packets since you opened this tab", calm.Nic!.ErrorsText);
        Assert.False(calm.Nic.ErrorsWarning);
        Assert.Equal("5 errors and 1 discarded packets since you opened this tab", growing.Nic!.ErrorsText);
        Assert.True(growing.Nic.ErrorsWarning);
    }

    [Fact]
    public void Without_nic_statistics_there_is_no_card()
    {
        Assert.Null(Present(Snap(Router(PathKit.AllReply))).Nic);
    }

    [Fact]
    public void A_selected_row_gets_a_detail_with_stats_the_series_and_a_note()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.LostDuring(25), rtt: 3)),
            selected: LocalNetworkMonitor.KeyOf("192.168.86.44"));

        var detail = view.Detail!;
        Assert.Equal("NAS", detail.Title);
        Assert.Equal("192.168.86.44", detail.Address);
        Assert.Equal(HopDisplay.Broken, detail.State);
        Assert.Equal(30, detail.Series.Count);
        Assert.Contains(detail.Stats, s => s.Label == "Packets lost");
        Assert.Contains("answered earlier", detail.Note);
    }

    [Fact]
    public void A_selection_that_no_longer_exists_has_no_detail()
    {
        Assert.Null(Present(Snap(Router(PathKit.AllReply)), selected: "dev:gone").Detail);
    }

    [Theory]
    [InlineData(HopDisplay.Answering, InternetState.Fine)]
    [InlineData(HopDisplay.Suspect, InternetState.Failing)]
    [InlineData(HopDisplay.Broken, InternetState.Failing)]
    [InlineData(HopDisplay.Silent, InternetState.Failing)]
    [InlineData(HopDisplay.Measuring, InternetState.Unknown)]
    [InlineData(HopDisplay.Unavailable, InternetState.Unknown)]
    public void The_path_maps_internet_row_becomes_an_internet_state(HopDisplay display, InternetState expected)
    {
        Assert.Equal(expected, LocalNetworkPresenter.InternetOf(display));
    }
}
