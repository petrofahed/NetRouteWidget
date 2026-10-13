using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathMapPresenterTests
{
    static NetworkStatus Status() => new(
        RoutingMode.Phone,
        new DetectionResult(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None),
        InternetPath.Phone, InternetPath.None, 38, 12, CanModify: true, Error: null);

    static LaneSnapshot Lan(PathTrace? trace, List<Sample> endpoint, params List<Sample>[] hops) =>
        new("LAN", TestAdapters.Lan(), trace, hops.Select(h => (IReadOnlyList<Sample>)h).ToList(), endpoint);

    static LaneSnapshot HealthyPhone() => new("Phone", TestAdapters.Phone(),
        new PathTrace(Ip("192.168.42.11"), Ip("1.1.1.1"),
            [new PathHop(1, Ip("192.168.42.129")), new PathHop(2, Ip("172.26.220.1"))], TraceStatus.Complete),
        [Every(1, AllReply), Every(1, AllReply)], Every(2, AllReply));

    static LaneSnapshot HealthyLan() => Lan(Four, Every(2, AllReply),
        Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply));

    static PathMapView Present(LaneSnapshot lan, LaneSnapshot? phone = null, string? selected = null, DateTimeOffset? lastSample = null) =>
        PathMapPresenter.Present(Status(), new PathSnapshot(Now, lan, phone ?? HealthyPhone(), lastSample ?? Now.AddSeconds(-1)), selected, T);

    static List<Sample> FailingEndpoint() => Every(2, LostDuring(14));

    [Fact]
    public void Healthy_lanes_say_so_and_draw_green_links_under_the_cards_own_headline()
    {
        var view = Present(HealthyLan());

        Assert.Equal("Internet via PHONE", view.Headline);
        Assert.False(view.HeadlineWarning);
        Assert.Equal(["Both connections reach the internet."], view.Diagnosis);
        Assert.All(view.Lan.Links, l => Assert.Equal(LinkState.Healthy, l));
        Assert.Equal(5, view.Lan.Links.Count);
        Assert.Equal(["Router", "Hop 2", "Hop 3", "Hop 4"], view.Lan.Nodes.Select(n => n.Title));
        Assert.Equal("LAN path · from 192.168.86.42", view.Lan.Caption);
        Assert.Equal(HopDisplay.Answering, view.Lan.Internet.State);
        Assert.Equal("20 ms", view.Lan.Internet.Metric);
    }

    [Fact]
    public void Each_lane_carries_the_name_of_its_adapter_for_the_pc_card()
    {
        var view = Present(HealthyLan());

        Assert.Equal(TestAdapters.Lan().Name, view.Lan.AdapterName);
        Assert.Equal("Ethernet", view.Lan.AdapterName);
        Assert.Equal(TestAdapters.Phone().Name, view.Phone.AdapterName);
        Assert.Equal("Ethernet 31", view.Phone.AdapterName);
    }

    [Fact]
    public void Located_break_is_named_with_the_devices_on_both_sides()
    {
        var view = Present(Lan(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, LostDuring(14)), Every(1, LostDuring(14))));

        Assert.Equal(
            "LAN: the break is between 192.168.1.1 and 172.17.18.1. While the internet check failed, 192.168.1.1 answered and 172.17.18.1 did not answer.",
            view.Diagnosis[0]);
        Assert.Equal("Phone reaches the internet.", view.Diagnosis[1]);
        // into router, box, then the broken link, then silent devices; the Internet link is muted because the red link is already drawn
        Assert.Equal([LinkState.Healthy, LinkState.Healthy, LinkState.Broken, LinkState.Muted, LinkState.Muted], view.Lan.Links);
        Assert.Equal(HopDisplay.Broken, view.Lan.Nodes[2].State);
        Assert.Equal(HopDisplay.Broken, view.Lan.Internet.State);
        Assert.Equal("no reply", view.Lan.Internet.Metric);
    }

    [Fact]
    public void A_partial_loss_is_reported_as_a_percentage_and_drawn_suspect()
    {
        var view = Present(Lan(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply),
            Every(1, sec => sec >= 14 && sec % 2 == 0 ? ProbeOutcome.NoReply : ProbeOutcome.Reply)));

        Assert.Equal(
            "LAN: the break is between 192.168.1.1 and 172.17.18.1. While the internet check failed, 192.168.1.1 answered and 172.17.18.1 lost 50% of its probes.",
            view.Diagnosis[0]);
        Assert.Equal(HopDisplay.Suspect, view.Lan.Nodes[2].State);
        Assert.Equal(LinkState.Suspect, view.Lan.Links[2]);
    }

    [Fact]
    public void A_break_at_the_first_device_names_this_pc()
    {
        var view = Present(Lan(Trace("192.168.86.1", "192.168.1.1"), FailingEndpoint(),
            Every(1, LostDuring(14)), Every(1, LostDuring(14))));

        Assert.Equal(
            "LAN: the break is between this PC and 192.168.86.1. While the internet check failed, 192.168.86.1 did not answer.",
            view.Diagnosis[0]);
    }

    [Fact]
    public void Narrowed_when_every_visible_device_answers_says_beyond_it_or_the_internet_side()
    {
        var view = Present(Lan(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(
            "LAN: the internet check is failing. The last device that answered during the failures is 10.9.8.249, so the fault is beyond it or on the internet side.",
            view.Diagnosis[0]);
    }

    [Fact]
    public void Narrowed_with_a_device_after_it_does_not_name_that_device()
    {
        var view = Present(Lan(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllLost)));

        Assert.Equal(
            "LAN: the internet check is failing. The last device that answered during the failures is 192.168.1.1, so the fault is after it, but the evidence does not say exactly where.",
            view.Diagnosis[0]);
        Assert.DoesNotContain(view.Lan.Nodes, n => n.State == HopDisplay.Broken);
    }

    [Fact]
    public void Too_little_evidence_says_so_and_names_nothing()
    {
        var view = Present(Lan(Four, Every(2, LostDuring(24)),
            Every(1, AllReply, fromSecond: 26), Every(1, AllReply, fromSecond: 26),
            Every(1, AllLost, fromSecond: 26), Every(1, AllLost, fromSecond: 26)));

        Assert.Equal("LAN: the internet check is failing, but there is not enough data yet to say where.", view.Diagnosis[0]);
    }

    [Fact]
    public void One_failed_check_is_a_blip_in_plain_words()
    {
        var view = Present(Lan(Four, Every(2, LostDuring(28)),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal("LAN: 1 internet check failed in the last 30 s. That is not enough to call a fault.", view.Diagnosis[0]);
    }

    [Fact]
    public void Local_measurement_trouble_is_unavailable_and_never_loss()
    {
        var view = Present(Lan(Four, Every(2, AllUnavailable),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal("LAN: measurement unavailable on this PC. That says nothing about the connection.", view.Diagnosis[0]);
        Assert.Equal(HopDisplay.Unavailable, view.Lan.Internet.State);
        Assert.Equal("unavailable", view.Lan.Internet.Metric);
        Assert.Equal(LinkState.Unavailable, view.Lan.Links[^1]);
        Assert.DoesNotContain(view.Lan.Nodes, n => n.State is HopDisplay.Broken or HopDisplay.Suspect);
    }

    [Fact]
    public void A_new_lane_reports_measuring_progress()
    {
        var view = Present(Lan(Four, Every(2, AllReply).Take(2).ToList(),
            Every(1, AllReply).Take(2).ToList(), Every(1, AllReply).Take(2).ToList(),
            Every(1, AllReply).Take(2).ToList(), Every(1, AllReply).Take(2).ToList()));

        Assert.Equal("LAN: measuring… 4 of 10 s.", view.Diagnosis[0]);
        Assert.All(view.Lan.Nodes, n => Assert.Equal(HopDisplay.Measuring, n.State));
        Assert.Equal(LinkState.Measuring, view.Lan.Links[0]);
    }

    [Fact]
    public void A_missing_adapter_is_drawn_as_not_connected()
    {
        var view = Present(new LaneSnapshot("LAN", null, null, [], []));

        Assert.Equal("LAN: not connected.", view.Diagnosis[0]);
        Assert.False(view.Lan.Connected);
        Assert.Equal("LAN path · not connected", view.Lan.Caption);
        Assert.Equal("", view.Lan.AdapterName);
        Assert.Empty(view.Lan.Nodes);
    }

    [Fact]
    public void A_long_path_collapses_into_a_plus_n_more_box()
    {
        var trace = Trace("10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6");
        var view = Present(Lan(trace, Every(2, AllReply), Enumerable.Range(0, 6).Select(_ => Every(1, AllReply)).ToArray()));

        Assert.Equal(4, view.Lan.Nodes.Count);
        Assert.Equal(5, view.Lan.Links.Count);
        Assert.Equal(NodeKind.Collapsed, view.Lan.Nodes[3].Kind);
        Assert.Equal("+3 more", view.Lan.Nodes[3].Title);
        Assert.Equal("lan:4-6", view.Lan.Nodes[3].Id);
    }

    [Fact]
    public void A_collapsed_box_takes_the_worst_state_of_the_hops_it_hides()
    {
        var trace = Trace("10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6");
        var view = Present(Lan(trace, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply),
            Every(1, AllReply), Every(1, LostDuring(14)), Every(1, LostDuring(14))));

        Assert.Equal(HopDisplay.Broken, view.Lan.Nodes[3].State);
    }

    [Fact]
    public void Consecutive_hidden_hops_share_one_box()
    {
        var trace = Trace("192.168.42.129", null, null, "172.26.242.30");
        var view = Present(HealthyLan(), new LaneSnapshot("Phone", TestAdapters.Phone(), trace,
            [Every(1, AllReply), [], [], Every(1, AllReply)], Every(2, AllReply)));

        Assert.Equal(["Phone", "Hops 2–3", "Hop 4"], view.Phone.Nodes.Select(n => n.Title));
        Assert.Equal(NodeKind.HiddenRun, view.Phone.Nodes[1].Kind);
        Assert.Equal(LinkState.Neutral, view.Phone.Links[1]);
    }

    [Fact]
    public void An_incomplete_trace_adds_a_ghost_box_with_a_neutral_link_and_is_not_a_fault()
    {
        var trace = new PathTrace(Ip("192.168.86.42"), Ip("1.1.1.1"), Four.Hops, TraceStatus.Incomplete);
        var view = Present(Lan(trace, Every(2, AllReply),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(["Both connections reach the internet."], view.Diagnosis);
        Assert.Equal(NodeKind.TraceIncomplete, view.Lan.Nodes[^1].Kind);
        Assert.Equal("Trace incomplete", view.Lan.Nodes[^1].StateLabel);
        Assert.Equal(LinkState.Neutral, view.Lan.Links[4]);
        Assert.Equal(LinkState.Healthy, view.Lan.Links[5]);
    }

    [Fact]
    public void A_quiet_device_keeps_a_green_link_only_while_the_destination_answers()
    {
        var healthy = Present(Lan(Four, Every(2, AllReply),
            Every(1, AllReply), Every(1, AllLost), Every(1, AllReply), Every(1, AllReply)));
        Assert.Equal(HopDisplay.Quiet, healthy.Lan.Nodes[1].State);
        Assert.Equal(LinkState.Healthy, healthy.Lan.Links[1]);

        var failing = Present(Lan(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllLost), Every(1, AllReply), Every(1, LostDuring(14))));
        Assert.Equal(HopDisplay.Quiet, failing.Lan.Nodes[1].State);
        Assert.Equal(LinkState.Neutral, failing.Lan.Links[1]);
    }

    [Fact]
    public void Footer_distinguishes_starting_live_and_stale()
    {
        Assert.Equal("Starting…", PathMapPresenter.Present(Status(), new PathSnapshot(Now, HealthyLan(), HealthyPhone(), null), null, T).FooterText);
        Assert.Equal("Devices checked every second, internet every 2 s", Present(HealthyLan(), lastSample: Now.AddSeconds(-2)).FooterText);
        Assert.Equal("No recent checks", Present(HealthyLan(), lastSample: Now.AddSeconds(-6)).FooterText);
    }

    [Fact]
    public void Detail_for_a_device_has_statistics_a_series_and_a_note()
    {
        var view = Present(HealthyLan(), selected: "lan:1");

        Assert.NotNull(view.Detail);
        Assert.Equal("Router", view.Detail.Title);
        Assert.Equal("192.168.86.1", view.Detail.Address);
        Assert.Equal("Answering", view.Detail.Chip);
        Assert.Equal(("Latency, average", "20 ms"), view.Detail.Stats[0]);
        Assert.Equal(("Packets lost", "0%"), view.Detail.Stats[2]);
        Assert.Equal(30, view.Detail.Series.Count);
        Assert.Contains("router", view.Detail.Note);
    }

    [Fact]
    public void Detail_for_the_internet_row_describes_the_check()
    {
        var detail = Present(HealthyLan(), selected: "lan:net").Detail;

        Assert.NotNull(detail);
        Assert.Equal("Internet via LAN", detail.Title);
        Assert.Equal(15, detail.Series.Count);
    }

    [Fact]
    public void Detail_for_the_trace_marker_says_it_is_not_a_fault()
    {
        var trace = new PathTrace(Ip("192.168.86.42"), Ip("1.1.1.1"), Four.Hops, TraceStatus.Incomplete);
        var detail = Present(Lan(trace, Every(2, AllReply),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)), selected: "lan:incomplete").Detail;

        Assert.NotNull(detail);
        Assert.Contains("12 hops", detail.Note);
        Assert.Contains("says nothing about the connection", detail.Note);
    }

    [Fact]
    public void Detail_for_a_collapsed_box_lists_its_members()
    {
        var trace = Trace("10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6");
        var detail = Present(Lan(trace, Every(2, AllReply), Enumerable.Range(0, 6).Select(_ => Every(1, AllReply)).ToArray()),
            selected: "lan:4-6").Detail;

        Assert.NotNull(detail);
        Assert.Contains("hop 5 10.0.0.5", detail.Note);
    }

    [Fact]
    public void Unknown_selection_has_no_detail()
    {
        Assert.Null(Present(HealthyLan(), selected: "lan:99").Detail);
        Assert.Null(Present(HealthyLan(), selected: "nonsense").Detail);
    }

    [Fact]
    public void A_direct_path_with_no_devices_draws_a_single_link_to_the_internet()
    {
        var direct = new PathTrace(Ip("192.168.86.42"), Ip("1.1.1.1"), [], TraceStatus.Complete);

        var view = Present(Lan(direct, Every(2, AllReply)));

        Assert.Empty(view.Lan.Nodes);
        Assert.Equal([LinkState.Healthy], view.Lan.Links);
        Assert.Equal(["Both connections reach the internet."], view.Diagnosis);
    }

    [Fact]
    public void Nothing_detected_yet_shows_both_connections_as_not_connected()
    {
        var view = Present(new LaneSnapshot("LAN", null, null, [], []), new LaneSnapshot("Phone", null, null, [], []));

        Assert.Equal(["LAN: not connected.", "Phone: not connected."], view.Diagnosis);
        Assert.False(view.Lan.Connected);
        Assert.False(view.Phone.Connected);
    }

    [Fact]
    public void An_adapter_that_has_no_address_yet_is_not_connected()
    {
        var noAddress = TestAdapters.Lan() with { IPv4 = null };

        var view = Present(new LaneSnapshot("LAN", noAddress, null, [], []));

        Assert.Equal("LAN: not connected.", view.Diagnosis[0]);
    }

    [Fact]
    public void Devices_that_answer_from_the_same_address_still_get_distinct_ids()
    {
        var view = Present(Lan(Trace("10.0.0.1", "10.0.0.1"), Every(2, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(["lan:1", "lan:2"], view.Lan.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void Narrowed_with_every_visible_device_answering_draws_the_link_into_the_internet_amber()
    {
        var view = Present(Lan(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(LinkState.Suspect, view.Lan.Links[^1]);
        Assert.DoesNotContain(LinkState.Broken, view.Lan.Links);
    }

    [Fact]
    public void Narrowed_with_a_device_after_the_last_answering_one_draws_the_link_leaving_it_amber()
    {
        var view = Present(Lan(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllLost)));

        Assert.Equal(LinkState.Suspect, view.Lan.Links[2]);
        Assert.Equal(LinkState.Muted, view.Lan.Links[^1]);
        Assert.DoesNotContain(LinkState.Broken, view.Lan.Links);
    }

    [Fact]
    public void Too_little_evidence_draws_a_neutral_internet_link_and_blames_nobody()
    {
        var view = Present(Lan(Four, Every(2, LostDuring(24)),
            Every(1, AllReply, fromSecond: 26), Every(1, AllReply, fromSecond: 26),
            Every(1, AllLost, fromSecond: 26), Every(1, AllLost, fromSecond: 26)));

        Assert.Equal(LinkState.Neutral, view.Lan.Links[^1]);
        Assert.DoesNotContain(LinkState.Broken, view.Lan.Links);
    }

    [Fact]
    public void The_note_for_the_located_first_bad_device_agrees_with_the_diagnosis()
    {
        var detail = Present(Lan(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply),
            Every(1, sec => sec >= 14 && sec % 2 == 0 ? ProbeOutcome.NoReply : ProbeOutcome.Reply)),
            selected: "lan:3").Detail;

        Assert.NotNull(detail);
        Assert.StartsWith("The first device after the last one that answered", detail.Note);
    }

    [Fact]
    public void The_note_for_a_break_at_the_first_device_does_not_claim_a_device_before_it()
    {
        var detail = Present(Lan(Trace("192.168.86.1", "192.168.1.1"), FailingEndpoint(),
            Every(1, LostDuring(14)), Every(1, LostDuring(14))),
            selected: "lan:1").Detail;

        Assert.NotNull(detail);
        Assert.Contains("first device on the way out", detail.Note);
        Assert.DoesNotContain("device before it", detail.Note);
    }
}
