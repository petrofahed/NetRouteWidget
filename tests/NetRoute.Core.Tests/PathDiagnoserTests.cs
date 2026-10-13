using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathDiagnoserTests
{
    static LaneDiagnosis Diagnose(LaneInput lane) => PathDiagnoser.Diagnose(lane, T);

    static HopDisplay[] Displays(LaneDiagnosis d) => d.Hops.Select(h => h.Display).ToArray();

    /// The internet check fails from second 14 to the end of the window.
    static List<Sample> FailingEndpoint() => Every(2, LostDuring(14));

    [Fact]
    public void Healthy_lane_shows_a_hop_that_ignores_probes_as_quiet_not_broken()
    {
        var d = Diagnose(Lane(Four, Every(2, AllReply),
            Every(1, AllReply), Every(1, AllLost), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(LaneStatus.Healthy, d.Status);
        Assert.Equal(Localization.None, d.Where);
        Assert.Equal([HopDisplay.Answering, HopDisplay.Quiet, HopDisplay.Answering, HopDisplay.Answering], Displays(d));
    }

    [Fact]
    public void One_late_failed_check_is_a_blip_not_a_fault()
    {
        // only the newest check was lost: lossy by the numbers, but one failure is not enough to call a fault
        var d = Diagnose(Lane(Four, Every(2, LostDuring(28)),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(LaneStatus.Blip, d.Status);
        Assert.Equal(Localization.None, d.Where);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
    }

    [Fact]
    public void Located_break_names_the_first_silent_device_after_the_last_one_that_answered()
    {
        var d = Diagnose(LaneM(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, LostDuring(14)), Every(1, LostDuring(14))));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.Located, d.Where);
        Assert.Equal(1, d.LastAnsweringHop);
        Assert.Equal(2, d.FirstBadHop);
        Assert.Equal([HopDisplay.Answering, HopDisplay.Answering, HopDisplay.Broken, HopDisplay.Silent], Displays(d));
    }

    [Fact]
    public void Located_break_at_the_first_device_has_no_answering_device_before_it()
    {
        var d = Diagnose(LaneM(Trace("192.168.86.1", "192.168.1.1"), FailingEndpoint(),
            Every(1, LostDuring(14)), Every(1, LostDuring(14))));

        Assert.Equal(Localization.Located, d.Where);
        Assert.Null(d.LastAnsweringHop);
        Assert.Equal(0, d.FirstBadHop);
    }

    [Fact]
    public void A_device_losing_part_of_its_probes_during_the_failure_is_suspect_not_broken()
    {
        // lost every second probe from second 14: 50% during the failure, 0% before it
        var d = Diagnose(LaneM(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, sec => sec >= 14 && sec % 2 == 0 ? ProbeOutcome.NoReply : ProbeOutcome.Reply)));

        Assert.Equal(Localization.Located, d.Where);
        Assert.Equal(HopDisplay.Suspect, d.Hops[2].Display);
    }

    [Fact]
    public void A_quiet_device_before_one_that_answers_is_not_blamed()
    {
        // hop 2 ignores probes all the time; hop 3 keeps answering while the check fails; only hop 4 goes silent with it
        var d = Diagnose(LaneM(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllLost), Every(1, AllReply), Every(1, LostDuring(14))));

        Assert.Equal(Localization.Located, d.Where);
        Assert.Equal(2, d.LastAnsweringHop);
        Assert.Equal(3, d.FirstBadHop);
        Assert.Equal(HopDisplay.Quiet, d.Hops[1].Display);
        Assert.Equal(HopDisplay.Broken, d.Hops[3].Display);
    }

    [Fact]
    public void A_device_that_went_silent_during_the_failure_is_not_blamed_when_a_later_one_answers()
    {
        // hop 2 loses probes during the failure, but hop 3 and 4 answer throughout: hop 2 is only ignoring probes
        var d = Diagnose(Lane(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, LostDuring(14)), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.Narrowed, d.Where);
        Assert.Equal(3, d.LastAnsweringHop);
        Assert.Equal([HopDisplay.Answering, HopDisplay.Quiet, HopDisplay.Answering, HopDisplay.Answering], Displays(d));
    }

    [Fact]
    public void Devices_that_answer_are_never_greyed_after_a_located_break()
    {
        // the check fails 12..20 and recovers (lossy overall, in Fault); both hops lose probes 12..16 (implicated by the
        // failing buckets) but have answered every probe since, so both are observed Answering and must not be drawn Silent
        var lostThenRecovered = (Func<int, ProbeOutcome>)(sec => sec >= 12 && sec <= 16 ? ProbeOutcome.NoReply : ProbeOutcome.Reply);
        var d = Diagnose(LaneM(Trace("192.168.86.1", "192.168.1.1"),
            Every(2, sec => sec >= 12 && sec <= 20 ? ProbeOutcome.NoReply : ProbeOutcome.Reply),
            Every(1, lostThenRecovered), Every(1, lostThenRecovered)));

        Assert.Equal(Localization.Located, d.Where);
        Assert.All(d.Hops, h => Assert.Equal(Observation.Answering, h.Observed));
        Assert.DoesNotContain(HopDisplay.Silent, Displays(d));
        Assert.Equal(HopDisplay.Answering, d.Hops[1].Display);
    }

    [Fact]
    public void Loss_that_does_not_line_up_with_the_failure_is_not_blamed()
    {
        // hop 3 is silent in good periods too: it just ignores probes, so the break cannot be pinned on it
        var d = Diagnose(Lane(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllLost)));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.Narrowed, d.Where);
        Assert.Null(d.FirstBadHop);
        Assert.Equal(1, d.LastAnsweringHop);
        Assert.Equal(HopDisplay.Quiet, d.Hops[2].Display);
    }

    [Fact]
    public void Every_visible_device_answering_while_the_check_fails_blames_none_of_them()
    {
        var d = Diagnose(Lane(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(Localization.Narrowed, d.Where);
        Assert.Equal(3, d.LastAnsweringHop);
        Assert.All(d.Hops, h => Assert.Equal(HopDisplay.Answering, h.Display));
    }

    [Fact]
    public void Too_little_evidence_refuses_to_name_a_hop()
    {
        // the failure is 3 checks old and the hops have only 4 probes since: below the 5 needed
        var d = Diagnose(Lane(Four, Every(2, LostDuring(24)),
            Every(1, AllReply, fromSecond: 26), Every(1, AllReply, fromSecond: 26),
            Every(1, AllLost, fromSecond: 26), Every(1, AllLost, fromSecond: 26)));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.NotEnoughData, d.Where);
        Assert.Null(d.FirstBadHop);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
        Assert.DoesNotContain(HopDisplay.Suspect, Displays(d));
    }

    [Fact]
    public void Evidence_comes_only_from_probes_in_the_same_buckets_as_the_failed_checks()
    {
        // hop 3 was silent only before the internet check began to fail: that loss is not evidence against it now
        var d = Diagnose(Lane(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, sec => sec < 10 ? ProbeOutcome.NoReply : ProbeOutcome.Reply)));

        Assert.NotEqual(HopDisplay.Broken, d.Hops[2].Display);
        Assert.Null(d.FirstBadHop);
        Assert.Equal(Localization.Narrowed, d.Where);
        Assert.Equal(2, d.LastAnsweringHop);
    }

    [Fact]
    public void Unavailable_probes_are_not_evidence_either_way()
    {
        // hop 3's probes could not run during the failure: it must not be implicated by them
        var d = Diagnose(Lane(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, sec => sec >= 14 ? ProbeOutcome.Unavailable : ProbeOutcome.Reply)));

        Assert.Null(d.FirstBadHop);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
    }

    [Fact]
    public void A_new_lane_is_measuring_until_the_internet_check_has_enough_samples()
    {
        var d = Diagnose(Lane(Four, Every(2, AllReply).Take(2).ToList(),
            Every(1, AllReply).Take(2).ToList(), Every(1, AllReply).Take(2).ToList(),
            Every(1, AllReply).Take(2).ToList(), Every(1, AllReply).Take(2).ToList()));

        Assert.Equal(LaneStatus.Measuring, d.Status);
        Assert.All(d.Hops, h => Assert.Equal(HopDisplay.Measuring, h.Display));
    }

    [Fact]
    public void A_trace_that_has_not_finished_yet_is_measuring_with_no_devices()
    {
        var d = Diagnose(Lane(null, []));

        Assert.Equal(LaneStatus.Measuring, d.Status);
        Assert.Empty(d.Hops);
    }

    [Fact]
    public void Local_measurement_errors_are_unavailable_never_a_fault()
    {
        var d = Diagnose(Lane(Four, Every(2, AllUnavailable),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(LaneStatus.Unavailable, d.Status);
        Assert.Equal(Localization.None, d.Where);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
    }

    static readonly PathTrace NoTrace = new(Ip("192.168.86.42"), Ip("1.1.1.1"), [], TraceStatus.Unavailable);

    [Fact]
    public void An_unavailable_trace_with_an_answering_internet_check_is_healthy()
    {
        // ICMP cannot run here (e.g. security software), but the internet check works and says the connection is fine
        var d = Diagnose(Lane(NoTrace, Every(2, AllReply)));

        Assert.Equal(LaneStatus.Healthy, d.Status);
        Assert.Equal(TraceStatus.Unavailable, d.TraceStatus);
        Assert.Empty(d.Hops);
    }

    [Fact]
    public void An_unavailable_trace_with_a_failing_internet_check_is_a_fault_with_nothing_to_examine()
    {
        var d = Diagnose(Lane(NoTrace, FailingEndpoint()));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.NotEnoughData, d.Where);
        Assert.Null(d.FirstBadHop);
        Assert.Null(d.LastAnsweringHop);
    }

    [Fact]
    public void An_unavailable_trace_with_one_failed_check_is_a_blip_and_with_too_few_checks_is_measuring()
    {
        Assert.Equal(LaneStatus.Blip, Diagnose(Lane(NoTrace, Every(2, LostDuring(28)))).Status);
        Assert.Equal(LaneStatus.Measuring, Diagnose(Lane(NoTrace, Every(2, AllReply).Take(2).ToList())).Status);
    }

    [Fact]
    public void An_unavailable_trace_with_an_unavailable_internet_check_is_unavailable_not_a_broken_connection()
    {
        var d = Diagnose(Lane(NoTrace, Every(2, AllUnavailable)));

        Assert.Equal(LaneStatus.Unavailable, d.Status);
        Assert.Equal(Localization.None, d.Where);
    }

    [Fact]
    public void An_incomplete_trace_is_not_a_fault_when_the_internet_check_answers()
    {
        var trace = new PathTrace(Ip("192.168.86.42"), Ip("1.1.1.1"),
            Four.Hops, TraceStatus.Incomplete);

        var d = Diagnose(Lane(trace, Every(2, AllReply),
            Every(1, AllReply), Every(1, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(LaneStatus.Healthy, d.Status);
        Assert.Equal(TraceStatus.Incomplete, d.TraceStatus);
    }

    [Fact]
    public void A_whole_window_outage_cannot_name_a_device_because_there_is_no_good_period_to_compare_with()
    {
        // the internet check lost every check in the window: hop 2 lost every probe too, but it could be a device that
        // ignores probes, and without a single good bucket there is nothing to tell the two apart
        var d = Diagnose(Lane(Trace("192.168.86.1", "192.168.1.1"), Every(2, AllLost),
            Every(1, AllReply), Every(1, AllLost)));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.Narrowed, d.Where);
        Assert.Equal(0, d.LastAnsweringHop);
        Assert.Null(d.FirstBadHop);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
        Assert.DoesNotContain(HopDisplay.Suspect, Displays(d));
    }

    [Fact]
    public void Hidden_hops_are_never_blamed_and_never_block_a_located_break()
    {
        var trace = Trace("192.168.42.129", null, "172.26.242.30");
        var d = Diagnose(LaneM(trace, FailingEndpoint(),
            Every(1, AllReply), [], Every(1, LostDuring(14))));

        Assert.Equal(Localization.Located, d.Where);
        Assert.Equal(HopDisplay.Hidden, d.Hops[1].Display);
        Assert.Equal(2, d.FirstBadHop);
    }

    // ----- direct ping is extra evidence; missing route probes alone name no device -----

    [Fact]
    public void Observed_outage_route_probes_to_the_first_devices_went_silent_but_the_router_still_answers_a_direct_ping()
    {
        // The real outage: TTL 1 and 2 stopped returning "time exceeded" while the router kept answering a direct ping.
        // The old rule named "between this PC and 192.168.86.1". Now the router is on the working path and nothing is blamed.
        var trace = Trace("192.168.86.1", "192.168.1.1", "172.17.18.1");
        var d = Diagnose(LaneWithDirect(trace, FailingEndpoint(),
            [Every(1, LostDuring(14)), Every(1, LostDuring(14)), Every(1, LostDuring(14))],
            [Every(1, AllReply), Every(1, AllUnavailable), Every(1, AllUnavailable)]));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.Narrowed, d.Where);
        Assert.Equal(0, d.LastAnsweringHop);
        Assert.Null(d.FirstBadHop);
        Assert.Equal(HopDisplay.Answering, d.Hops[0].Display);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
        Assert.DoesNotContain(HopDisplay.Suspect, Displays(d));
    }

    [Fact]
    public void Lost_route_probes_alone_never_name_a_device_even_when_every_probe_was_lost_in_step_with_the_failure()
    {
        // Same route-probe pattern as the first-device break, but no direct ping was ever sent: the location is uncertain, not "hop 1"
        var d = Diagnose(Lane(Trace("192.168.86.1", "192.168.1.1"), FailingEndpoint(),
            Every(1, LostDuring(14)), Every(1, LostDuring(14))));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.Uncertain, d.Where);
        Assert.Null(d.FirstBadHop);
        Assert.Null(d.LastAnsweringHop);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
        Assert.DoesNotContain(HopDisplay.Suspect, Displays(d));
    }

    [Fact]
    public void A_device_whose_direct_ping_is_lost_too_but_with_no_baseline_is_still_not_named()
    {
        // Both kinds of probe lost for the whole window: could be a device that ignores both, so no good period to compare with
        var d = Diagnose(LaneM(Trace("192.168.86.1", "192.168.1.1"), Every(2, AllLost),
            Every(1, AllLost), Every(1, AllLost)));

        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
        Assert.DoesNotContain(HopDisplay.Suspect, Displays(d));
        Assert.Null(d.FirstBadHop);
    }

    [Fact]
    public void Silent_early_hops_with_an_answering_later_hop_make_no_claim_about_the_early_hops()
    {
        // hops 1 and 2 never return route probes, hop 3 answers throughout: traffic evidently gets past both of them
        var trace = Trace("192.168.86.1", "192.168.1.1", "172.17.18.1");
        var failing = Diagnose(Lane(trace, FailingEndpoint(), Every(1, AllLost), Every(1, AllLost), Every(1, AllReply)));

        Assert.Equal(LaneStatus.Fault, failing.Status);
        Assert.Equal(Localization.Narrowed, failing.Where);
        Assert.Equal(2, failing.LastAnsweringHop);
        Assert.Equal([HopDisplay.Quiet, HopDisplay.Quiet, HopDisplay.Answering], Displays(failing));

        var healthy = Diagnose(Lane(trace, Every(2, AllReply), Every(1, AllLost), Every(1, AllLost), Every(1, AllReply)));

        Assert.Equal(LaneStatus.Healthy, healthy.Status);
        Assert.Equal(Localization.None, healthy.Where);
        Assert.Equal([HopDisplay.Quiet, HopDisplay.Quiet, HopDisplay.Answering], Displays(healthy));
    }

    [Fact]
    public void A_router_that_answers_ping_while_the_connection_beyond_it_fails_is_not_blamed()
    {
        // The router answers a direct ping and the check fails; the devices behind it never answered the trace.
        // That shows the router is reachable, not that it forwards: the fault is "at or beyond" it, never "the router".
        var trace = Trace("192.168.86.1", null, null);
        var d = Diagnose(LaneWithDirect(trace, FailingEndpoint(),
            [Every(1, LostDuring(14)), [], []],
            [Every(1, AllReply), [], []]));

        Assert.Equal(LaneStatus.Fault, d.Status);
        Assert.Equal(Localization.Narrowed, d.Where);
        Assert.Equal(0, d.LastAnsweringHop);
        Assert.Null(d.FirstBadHop);
        Assert.Equal(HopDisplay.Answering, d.Hops[0].Display);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
        Assert.DoesNotContain(HopDisplay.Suspect, Displays(d));
    }

    [Fact]
    public void A_device_that_answers_a_direct_ping_but_not_route_probes_is_drawn_answering_while_the_lane_is_healthy()
    {
        var d = Diagnose(LaneWithDirect(Trace("192.168.86.1", "192.168.1.1"), Every(2, AllReply),
            [Every(1, AllLost), Every(1, AllReply)], [Every(1, AllReply), Every(1, AllReply)]));

        Assert.Equal([HopDisplay.Answering, HopDisplay.Answering], Displays(d));
        Assert.Equal(Observation.Silent, d.Hops[0].Observed);
        Assert.Equal(Observation.Answering, d.Hops[0].DirectObserved);
    }

    [Fact]
    public void A_device_unreachable_by_both_kinds_of_probe_in_step_with_the_failure_is_still_located()
    {
        // the rule still finds real breaks: the router answers both kinds, the next device is lost to both only while the check fails
        var d = Diagnose(LaneM(Trace("192.168.86.1", "192.168.1.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, LostDuring(14))));

        Assert.Equal(Localization.Located, d.Where);
        Assert.Equal(0, d.LastAnsweringHop);
        Assert.Equal(1, d.FirstBadHop);
        Assert.Equal(HopDisplay.Answering, d.Hops[0].Display);
        Assert.Equal(HopDisplay.Broken, d.Hops[1].Display);
    }

    [Fact]
    public void A_lane_that_is_not_connected_says_so()
    {
        var d = Diagnose(new LaneInput("Phone", false, null, [], [], Now));

        Assert.Equal(LaneStatus.NotConnected, d.Status);
    }

    [Fact]
    public void A_recovered_internet_check_clears_the_fault()
    {
        // failed from 4 to 14, then replies at 16..28 (7 in a row): healthy again, and the hops are calm
        var d = Diagnose(Lane(Four, Every(2, sec => sec is >= 4 and < 16 ? ProbeOutcome.NoReply : ProbeOutcome.Reply),
            Every(1, AllReply), Every(1, AllReply), Every(1, LostDuring(4, 16)), Every(1, LostDuring(4, 16))));

        Assert.Equal(LaneStatus.Healthy, d.Status);
        Assert.DoesNotContain(HopDisplay.Broken, Displays(d));
    }

    [Fact]
    public void Fewer_sample_lists_than_hops_are_treated_as_no_samples_yet()
    {
        // a re-trace added devices after the samples were copied: the missing lists must not crash and must read as measuring
        var d = Diagnose(Lane(Four, Every(2, AllReply), Every(1, AllReply), Every(1, AllReply)));

        Assert.Equal(4, d.Hops.Count);
        Assert.Equal(HopDisplay.Measuring, d.Hops[3].Display);
    }
}
