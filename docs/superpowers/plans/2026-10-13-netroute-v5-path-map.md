# Network Path Map (v5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A window opened from the widget card that draws the route from this PC to the internet over both the LAN and the phone, with live per-device health, and that names where a connection breaks only when the evidence supports it.

**Architecture:** A pure-C# core in `NetRoute.Core.PathMap` (outcome statistics, observation rules, a tracer, a diagnoser that applies the fault rule, a presenter that builds the view model, and a monitor that runs the probe loops) sits behind two probe interfaces. Windows implementations (`IcmpSendEcho2Ex` for hops, a three-outcome TCP check for the internet) plug in at the edge. A WPF window and canvas in `NetRoute.App` draw the view model; the card gets two entry points. Probing runs only while the window is open.

**Tech Stack:** C# 13, .NET 10 (`net10.0-windows`), WPF, xUnit 2.9.3, `Microsoft.Extensions.TimeProvider.Testing`, `iphlpapi.dll` ICMP interop.

**Spec:** `docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md` (draft 2). Mockup: `docs/superpowers/mockups/2026-10-13-path-map-mockup.html`. Read the spec's "The outcome of one check", "Per-device observation" and "The fault rule" sections before Tasks 2 and 4.

**Execution note (added after the plan was run):** the plan was executed task by task with a review after each task and a whole-branch review at the end. The reviews found real defects in the code below, which were fixed in follow-up commits (fix rounds per task and one final fix wave). The code blocks in this document are therefore the starting point, not the final code: **the code in the repository is the truth**, and the spec was amended to match (hidden hops are not probed between traces, no Implicated verdict without a good-period baseline, a degraded re-trace is not adopted on first sight, samples are stamped with the probe's send time, an unavailable trace no longer hides the internet check). The `PathKit.Lane` helper moved from Task 1 to Task 4 because it needs `LaneInput`.

**Provenance of the code below:** every code block in Tasks 1–10 was compiled and its tests were run in a throwaway copy of the repo before this plan was written (135 new Core tests, all passing; the solution built; the window was rendered to PNG for six scenarios and inspected). The copy was not committed. Treat the code as reviewed and tested, but still run each task's tests as written: they are the executor's evidence.

## Global Constraints

- All new Core code lives in namespace `NetRoute.Core.PathMap` under `src/NetRoute.Core/PathMap/` (Windows-specific files under `src/NetRoute.Core/Windows/`). **Never name the namespace `Path`**: it would shadow `System.IO.Path` everywhere in `NetRoute.Core`.
- Every check ends in exactly one outcome: `Reply`, `NoReply` (counts as loss) or `Unavailable` (a local problem or a cancelled check; **never** counts as loss, in any statistic or any evidence count).
- A trace that does not reach `1.1.1.1` within 12 hops is **Incomplete**, never a fault by itself.
- A device is blamed only with evidence from the same 2-second buckets as the failed internet checks; an intermediate device that ignores probes is never called broken while the destination still answers; a device that answers is never greyed out.
- Every tunable number lives in `PathThresholds`; no literal thresholds elsewhere.
- Core has no UI references. WPF code lives only in `NetRoute.App`.
- `ILatencyProbe`, `TcpLatencyProbe` and auto-heal (`RouteController`) are **not changed**.
- IPv4 only; ICMP needs no administrator rights; probing runs only while the window is open.
- Diagnosis sentences and state labels are used exactly as the spec words them (the presenter tests pin them).
- Work on branch `feat/v5-path-map` (PR #3). One commit per task. Every commit message ends with the line `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Run tests with `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~<ClassName>"`. Known and unrelated: two existing tests in `WindowsIntegrationTests` (`Metrics_read_through_ip_helper_match_Get_NetIPInterface`, `Adapter_source_reports_the_internet_adapter_as_up_with_an_address`) fail on this machine on the unmodified repo. Run the whole suite with `--filter "FullyQualifiedName!~WindowsIntegrationTests"` to ignore them.
- Do not run the dev build of the widget while the installed widget is running (the single-instance lock) and never start it with `--replace`. UI checks use the render harness in Task 9; the real-window checks are the user's, in Task 11.

## Review Focus

Failure modes the spec implies that are most likely to bite, each pinned by a named test:

- **A path with no devices** (the target answers at TTL 1, as on a direct link): one link to the Internet card, healthy, no crash. Pinned by `Target_answering_at_the_first_ttl_gives_an_empty_complete_path` (Task 3) and `A_direct_path_with_no_devices_draws_a_single_link_to_the_internet` (Task 7).
- **Launching with no adapters detected, or an adapter whose address has not arrived yet** (DHCP pending): both lanes read "not connected", nothing is probed, nothing throws. Pinned by `Nothing_detected_yet_shows_both_connections_as_not_connected` and `An_adapter_that_has_no_address_yet_is_not_connected` (Task 7) and `An_adapter_that_has_no_address_yet_is_not_probed` (Task 8).
- **The path changes while the window is open** (a device disappears, the target answers sooner, a device's address changes): the monitor re-traces after the minimum gap and keeps history only when the addresses are unchanged. Pinned by `A_reply_from_another_address_triggers_a_retrace_after_the_minimum_gap`, `The_target_answering_at_a_device_ttl_means_the_path_got_shorter_and_is_retraced` and `An_unchanged_path_is_retraced_only_every_thirty_seconds_and_keeps_its_samples` (Task 8).
- **Closing the window while probes are in flight:** nothing is stored afterwards and nothing keeps running. Pinned by `A_round_cancelled_while_probes_are_in_flight_stores_nothing` and `A_probe_in_flight_when_the_adapter_changes_is_dropped` (Task 8); the `Closed` handler in Task 10 cancels the token.
- **A re-trace that adds devices after the samples were copied, and replies that name no address:** must not crash; they read as "measuring" and "hidden". Pinned by `Fewer_sample_lists_than_hops_are_treated_as_no_samples_yet` (Task 4) and `A_reply_that_names_no_address_is_a_hidden_hop` (Task 3).

---

### Task 1: Outcome model, thresholds, sample statistics and the test kit

**Files:**
- Create: `src/NetRoute.Core/PathMap/PathModels.cs`
- Create: `src/NetRoute.Core/PathMap/PathThresholds.cs`
- Create: `src/NetRoute.Core/PathMap/SampleStats.cs`
- Create: `src/NetRoute.Core/PathMap/PathSamples.cs`
- Create: `tests/NetRoute.Core.Tests/PathMapKit.cs` (builders and fakes shared by every later path-map test)
- Create: `tests/NetRoute.Core.Tests/PathSampleStatsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ProbeOutcome { Reply, NoReply, Unavailable }`; `Sample(DateTimeOffset At, ProbeOutcome Outcome, int? RttMs = null)`; `EchoResult(ProbeOutcome Outcome, IPAddress? From = null, int? RttMs = null, bool TargetReached = false)`; `EndpointResult(ProbeOutcome Outcome, int? RttMs = null)`; `IPathProbe.EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)`; `IEndpointProbe.CheckAsync(IPAddress source, CancellationToken ct)`; `TraceStatus { Complete, Incomplete, Unavailable }`; `PathHop(int Ttl, IPAddress? Address)` with `Hidden`; `PathTrace(IPAddress Source, IPAddress Target, IReadOnlyList<PathHop> Hops, TraceStatus Status)`; `PathThresholds` (all defaults, `PathThresholds.Default`); `SampleStats.Of(IReadOnlyList<Sample>)` with `Valid`, `LossPercent`, `UnavailablePercent`, `ConsecutiveLost/Replies/Unavailable`, `AvgMs/BestMs/WorstMs`; `PathSamples(TimeSpan window)` with `Add(Sample)` and `Snapshot(DateTimeOffset now)`. In tests: `PathKit` (`Start`, `Now`, `T`, `Ip`, `Every`, `AllReply`, `AllLost`, `AllUnavailable`, `LostDuring`, `Trace`, `Four`), `FakePathProbe`, `FakeEndpointProbe`.

- [ ] **Step 1: Write the test kit and the statistics tests**

`tests/NetRoute.Core.Tests/PathMapKit.cs`:

```cs
using System.Net;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

/// Builders for path-map tests. The window is 30 s long and ends at Now; second 0 is its start. Hop probes run every second
/// and internet checks every 2 s (on even seconds), so second 2k and 2k+1 share one 2-second bucket.
static class PathKit
{
    public static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Now = Start.AddSeconds(30);
    public static readonly PathThresholds T = PathThresholds.Default;

    public static IPAddress Ip(string s) => IPAddress.Parse(s);

    /// A complete four-device LAN path: router, the box behind it, the provider gateway and one more hop.
    public static readonly PathTrace Four = Trace("192.168.86.1", "192.168.1.1", "172.17.18.1", "10.9.8.249");

    public static List<Sample> Every(int stepSeconds, Func<int, ProbeOutcome> outcome, int rtt = 20, int fromSecond = 0)
    {
        var list = new List<Sample>();
        for (var sec = fromSecond; sec < 30; sec += stepSeconds)
        {
            var o = outcome(sec);
            list.Add(new Sample(Start.AddSeconds(sec), o, o == ProbeOutcome.Reply ? rtt : null));
        }
        return list;
    }

    public static Func<int, ProbeOutcome> AllReply => _ => ProbeOutcome.Reply;
    public static Func<int, ProbeOutcome> AllLost => _ => ProbeOutcome.NoReply;
    public static Func<int, ProbeOutcome> AllUnavailable => _ => ProbeOutcome.Unavailable;

    /// NoReply from 'from' (inclusive) to 'to' (exclusive), Reply elsewhere.
    public static Func<int, ProbeOutcome> LostDuring(int from, int to = 30) =>
        sec => sec >= from && sec < to ? ProbeOutcome.NoReply : ProbeOutcome.Reply;

    /// A complete trace from the LAN adapter; null means a hidden hop.
    public static PathTrace Trace(params string?[] hops) => new(
        Ip("192.168.86.42"), Ip("1.1.1.1"),
        hops.Select((a, i) => new PathHop(i + 1, a is null ? null : Ip(a))).ToList(), TraceStatus.Complete);

}

/// Answers each TTL from a script; unknown TTLs time out.
sealed class FakePathProbe : IPathProbe
{
    public Dictionary<int, EchoResult> ByTtl { get; } = new();
    public List<int> Calls { get; } = new();
    public EchoResult Default { get; set; } = new(ProbeOutcome.NoReply);

    /// When set, every echo waits for it: holds a probe in flight.
    public TaskCompletionSource? Gate { get; set; }
    public Exception? Throw { get; set; }

    public async Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        lock (Calls) Calls.Add(ttl);
        if (Gate is { } gate) await gate.Task;
        if (Throw is { } ex) throw ex;
        return ByTtl.TryGetValue(ttl, out var r) ? r : Default;
    }

    public static EchoResult Hop(string address, int ms = 5) => new(ProbeOutcome.Reply, IPAddress.Parse(address), ms);
    public static EchoResult Target(int ms = 20) => new(ProbeOutcome.Reply, IPAddress.Parse("1.1.1.1"), ms, TargetReached: true);
}

sealed class FakeEndpointProbe : IEndpointProbe
{
    public EndpointResult Result { get; set; } = new(ProbeOutcome.Reply, 20);
    public Exception? Throw { get; set; }

    public Task<EndpointResult> CheckAsync(IPAddress source, CancellationToken ct) =>
        Throw is { } ex ? throw ex : Task.FromResult(Result);
}
```

`tests/NetRoute.Core.Tests/PathSampleStatsTests.cs`:

```cs
using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathSampleStatsTests
{
    [Fact]
    public void Unavailable_samples_are_never_counted_as_loss()
    {
        var samples = Every(1, sec => sec % 3 == 0 ? ProbeOutcome.Unavailable : ProbeOutcome.Reply);

        var s = SampleStats.Of(samples);

        Assert.Equal(0, s.Lost);
        Assert.Equal(0, s.LossPercent);
        Assert.Equal(10, s.Unavailable);
        Assert.Equal(20, s.Valid);
        Assert.Equal(30, s.Total);
    }

    [Fact]
    public void Loss_percent_counts_only_valid_samples()
    {
        // 2 lost, 8 replies, plus 10 unavailable that must not dilute the percentage
        var samples = new List<Sample>();
        for (var i = 0; i < 10; i++) samples.Add(new(Start.AddSeconds(i), i < 2 ? ProbeOutcome.NoReply : ProbeOutcome.Reply, 10));
        for (var i = 10; i < 20; i++) samples.Add(new(Start.AddSeconds(i), ProbeOutcome.Unavailable));

        Assert.Equal(20, SampleStats.Of(samples).LossPercent);
    }

    [Fact]
    public void Consecutive_runs_skip_unavailable_samples()
    {
        var samples = new List<Sample>
        {
            new(Start, ProbeOutcome.Reply, 5),
            new(Start.AddSeconds(1), ProbeOutcome.NoReply),
            new(Start.AddSeconds(2), ProbeOutcome.Unavailable),
            new(Start.AddSeconds(3), ProbeOutcome.NoReply),
            new(Start.AddSeconds(4), ProbeOutcome.NoReply),
        };

        var s = SampleStats.Of(samples);

        Assert.Equal(3, s.ConsecutiveLost);
        Assert.Equal(0, s.ConsecutiveReplies);
        Assert.Equal(0, s.ConsecutiveUnavailable);
    }

    [Fact]
    public void Consecutive_unavailable_counts_only_the_newest_run()
    {
        var samples = new List<Sample>
        {
            new(Start, ProbeOutcome.Unavailable),
            new(Start.AddSeconds(1), ProbeOutcome.Reply, 5),
            new(Start.AddSeconds(2), ProbeOutcome.Unavailable),
            new(Start.AddSeconds(3), ProbeOutcome.Unavailable),
        };

        Assert.Equal(2, SampleStats.Of(samples).ConsecutiveUnavailable);
    }

    [Fact]
    public void Latency_statistics_come_from_replies_only()
    {
        var samples = new List<Sample>
        {
            new(Start, ProbeOutcome.Reply, 10),
            new(Start.AddSeconds(1), ProbeOutcome.NoReply),
            new(Start.AddSeconds(2), ProbeOutcome.Reply, 30),
        };

        var s = SampleStats.Of(samples);

        Assert.Equal((20, 10, 30), (s.AvgMs, s.BestMs, s.WorstMs));
    }

    [Fact]
    public void Empty_window_has_no_statistics()
    {
        var s = SampleStats.Of([]);

        Assert.Equal(0, s.Valid);
        Assert.Null(s.AvgMs);
        Assert.Equal(0, s.LossPercent);
    }

    [Fact]
    public void Window_drops_old_samples()
    {
        var samples = new PathSamples(TimeSpan.FromSeconds(30));
        samples.Add(new Sample(Start, ProbeOutcome.Reply, 5));
        samples.Add(new Sample(Start.AddSeconds(20), ProbeOutcome.Reply, 5));

        Assert.Equal(2, samples.Snapshot(Start.AddSeconds(25)).Count);
        Assert.Single(samples.Snapshot(Start.AddSeconds(40)));
        Assert.Empty(samples.Snapshot(Start.AddSeconds(60)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathSampleStatsTests"`
Expected: the build FAILS with `error CS0234: The type or namespace name 'PathMap' does not exist in the namespace 'NetRoute.Core'`.

- [ ] **Step 3: Write the implementation**

`src/NetRoute.Core/PathMap/PathModels.cs`:

```cs
using System.Net;

namespace NetRoute.Core.PathMap;

/// What one check observed. Unavailable means the measurement itself failed on this PC (or was cancelled):
/// it says nothing about the network and is never counted as packet loss.
public enum ProbeOutcome { Reply, NoReply, Unavailable }

public readonly record struct Sample(DateTimeOffset At, ProbeOutcome Outcome, int? RttMs = null);

/// One ICMP echo with a chosen TTL. From is the device that answered: the hop for "TTL expired", the target for a real reply.
public sealed record EchoResult(ProbeOutcome Outcome, IPAddress? From = null, int? RttMs = null, bool TargetReached = false);

public sealed record EndpointResult(ProbeOutcome Outcome, int? RttMs = null);

public interface IPathProbe
{
    /// Never throws for a failed measurement: that is an Unavailable result. A cancelled token gives Unavailable too.
    Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct);
}

public interface IEndpointProbe
{
    /// The "does this connection reach the internet" check, sent from the given local address.
    Task<EndpointResult> CheckAsync(IPAddress source, CancellationToken ct);
}

public enum TraceStatus { Complete, Incomplete, Unavailable }

public sealed record PathHop(int Ttl, IPAddress? Address)
{
    /// A hop that never answered the trace (many carriers and some routers do not).
    public bool Hidden => Address is null;
}

public sealed record PathTrace(IPAddress Source, IPAddress Target, IReadOnlyList<PathHop> Hops, TraceStatus Status);
```

`src/NetRoute.Core/PathMap/PathThresholds.cs`:

```cs
namespace NetRoute.Core.PathMap;

/// Every tunable number of the path map in one place. These are initial defaults, to be tuned after real use.
public sealed record PathThresholds
{
    public static PathThresholds Default { get; } = new();

    /// Samples older than this are dropped.
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(30);

    /// No judgement of a hop before this many valid (Reply or NoReply) samples.
    public int HopMinSamples { get; init; } = 10;
    public int EndpointMinSamples { get; init; } = 5;

    /// Loss at or above this percentage is "losing packets".
    public double LossyPercent { get; init; } = 5;

    /// This many consecutive lost checks is "no reply".
    public int SilentConsecutive { get; init; } = 3;

    /// Consecutive replies after the last lost check that end "losing packets".
    public int HopRecoverReplies { get; init; } = 10;
    public int EndpointRecoverReplies { get; init; } = 5;

    /// Measurement unavailable: this many latest samples Unavailable, or this share of the window.
    public int UnavailableConsecutive { get; init; } = 3;
    public double UnavailableSharePercent { get; init; } = 50;

    /// Failed internet checks in the window needed before a fault (not just a blip) is declared.
    public int MinFailedChecks { get; init; } = 3;

    /// Hop probes needed in the failing buckets before a hop can be cleared or implicated.
    public int MinEvidenceProbes { get; init; } = 5;
    public double ImplicatedLossPercent { get; init; } = 50;
    public double ImplicatedMarginPoints { get; init; } = 30;
    public double ClearedLossPercent { get; init; } = 10;

    /// Hop probes are matched to internet checks in buckets of this size.
    public TimeSpan Bucket { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxHops { get; init; } = 12;

    /// Boxes drawn between the PC and the Internet card; the rest collapse into "+N more".
    public int MaxDrawnHops { get; init; } = 4;
}
```

`src/NetRoute.Core/PathMap/SampleStats.cs`:

```cs
namespace NetRoute.Core.PathMap;

/// Statistics over the samples of one device in the window. Unavailable samples are counted on their own:
/// they never count as loss, and they are skipped when counting runs of lost checks or replies.
public readonly record struct SampleStats(
    int Total, int Replies, int Lost, int Unavailable,
    int ConsecutiveLost, int ConsecutiveReplies, int ConsecutiveUnavailable,
    int? AvgMs, int? BestMs, int? WorstMs)
{
    /// Samples that say something about the network.
    public int Valid => Replies + Lost;

    public double LossPercent => Valid == 0 ? 0 : 100.0 * Lost / Valid;

    public double UnavailablePercent => Total == 0 ? 0 : 100.0 * Unavailable / Total;

    /// samples: oldest first.
    public static SampleStats Of(IReadOnlyList<Sample> samples)
    {
        int replies = 0, lost = 0, unavailable = 0, rttCount = 0;
        long rttSum = 0;
        int? best = null, worst = null;
        foreach (var s in samples)
        {
            switch (s.Outcome)
            {
                case ProbeOutcome.Reply:
                    replies++;
                    if (s.RttMs is { } ms)
                    {
                        rttCount++;
                        rttSum += ms;
                        best = best is null ? ms : Math.Min(best.Value, ms);
                        worst = worst is null ? ms : Math.Max(worst.Value, ms);
                    }
                    break;
                case ProbeOutcome.NoReply:
                    lost++;
                    break;
                default:
                    unavailable++;
                    break;
            }
        }

        int consecutiveLost = 0, consecutiveReplies = 0, consecutiveUnavailable = 0;
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            if (samples[i].Outcome == ProbeOutcome.Unavailable) continue;
            if (samples[i].Outcome != ProbeOutcome.NoReply) break;
            consecutiveLost++;
        }
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            if (samples[i].Outcome == ProbeOutcome.Unavailable) continue;
            if (samples[i].Outcome != ProbeOutcome.Reply) break;
            consecutiveReplies++;
        }
        for (var i = samples.Count - 1; i >= 0 && samples[i].Outcome == ProbeOutcome.Unavailable; i--) consecutiveUnavailable++;

        return new SampleStats(
            samples.Count, replies, lost, unavailable, consecutiveLost, consecutiveReplies, consecutiveUnavailable,
            rttCount == 0 ? null : (int)Math.Round(rttSum / (double)rttCount), best, worst);
    }
}
```

`src/NetRoute.Core/PathMap/PathSamples.cs`:

```cs
namespace NetRoute.Core.PathMap;

/// A rolling window of the samples of one device. Thread-safe: probes add while the screen reads snapshots.
public sealed class PathSamples(TimeSpan window)
{
    readonly List<Sample> _samples = [];
    readonly Lock _gate = new();

    public void Add(Sample sample)
    {
        lock (_gate)
        {
            _samples.Add(sample);
            Prune(sample.At);
        }
    }

    /// The samples still inside the window at 'now', oldest first.
    public IReadOnlyList<Sample> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            return _samples.ToArray();
        }
    }

    void Prune(DateTimeOffset now)
    {
        var cutoff = now - window;
        _samples.RemoveAll(s => s.At < cutoff);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathSampleStatsTests"`
Expected: `Passed!  - Failed:     0, Passed:     7`

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap tests/NetRoute.Core.Tests/PathMapKit.cs tests/NetRoute.Core.Tests/PathSampleStatsTests.cs
git commit -m "feat(core): path map outcome model, thresholds and sample statistics"
```

---

### Task 2: Observation rules (measuring, recovery, unavailable)

**Files:**
- Create: `src/NetRoute.Core/PathMap/PathObserver.cs`
- Create: `tests/NetRoute.Core.Tests/PathObserverTests.cs`

**Interfaces:**
- Consumes: `SampleStats`, `PathThresholds` (Task 1).
- Produces: `enum Observation { Measuring, Answering, Lossy, Silent, Hidden, Unavailable }`; `PathObserver.Observe(SampleStats s, bool hidden, bool endpoint, PathThresholds t) : Observation`. First match wins: Hidden, Unavailable, Silent (3 consecutive lost), Measuring (under the minimum valid samples), Lossy (loss at or above the threshold and fewer consecutive replies than the recovery count), Answering.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/PathObserverTests.cs`:

```cs
using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathObserverTests
{
    static Observation Hop(IReadOnlyList<Sample> samples, bool hidden = false) =>
        PathObserver.Observe(SampleStats.Of(samples), hidden, endpoint: false, T);

    static Observation Endpoint(IReadOnlyList<Sample> samples) =>
        PathObserver.Observe(SampleStats.Of(samples), hidden: false, endpoint: true, T);

    [Fact]
    public void Hidden_hops_stay_hidden()
    {
        Assert.Equal(Observation.Hidden, Hop(Every(1, AllReply), hidden: true));
    }

    [Fact]
    public void Too_few_samples_is_measuring_not_a_verdict()
    {
        // 9 valid hop samples: one short of the minimum
        Assert.Equal(Observation.Measuring, Hop(Every(1, AllReply).Take(9).ToList()));
        Assert.Equal(Observation.Answering, Hop(Every(1, AllReply).Take(10).ToList()));
    }

    [Fact]
    public void Internet_check_needs_only_five_samples()
    {
        Assert.Equal(Observation.Measuring, Endpoint(Every(2, AllReply).Take(4).ToList()));
        Assert.Equal(Observation.Answering, Endpoint(Every(2, AllReply).Take(5).ToList()));
    }

    [Fact]
    public void Three_consecutive_lost_checks_are_silent_even_during_startup()
    {
        var samples = Every(1, AllLost).Take(3).ToList();

        Assert.Equal(Observation.Silent, Hop(samples));
    }

    [Fact]
    public void Two_lost_checks_in_a_row_are_not_silent()
    {
        var samples = Every(1, AllLost).Take(2).ToList();

        Assert.Equal(Observation.Measuring, Hop(samples));
    }

    [Fact]
    public void Loss_at_the_threshold_is_lossy_while_the_last_check_was_lost()
    {
        // 20 samples, the last one lost: 5% loss, no replies since
        var samples = Every(1, sec => sec == 19 ? ProbeOutcome.NoReply : ProbeOutcome.Reply).Take(20).ToList();

        Assert.Equal(Observation.Lossy, Hop(samples));
    }

    [Fact]
    public void Loss_below_the_threshold_is_not_lossy()
    {
        // 1 lost in 25 valid samples is 4%, below the 5% default
        var samples = Every(1, sec => sec == 3 ? ProbeOutcome.NoReply : ProbeOutcome.Reply).Take(25).ToList();

        Assert.Equal(Observation.Answering, Hop(samples));
    }

    [Fact]
    public void Ten_consecutive_replies_end_lossy_even_with_old_losses_in_the_window()
    {
        // lost at seconds 2 and 18, then replies for seconds 19..29 (11 in a row)
        var samples = Every(1, sec => sec is 2 or 18 ? ProbeOutcome.NoReply : ProbeOutcome.Reply);

        Assert.Equal(Observation.Answering, Hop(samples));
    }

    [Fact]
    public void Fewer_than_ten_replies_since_the_last_loss_stay_lossy()
    {
        // lost at seconds 2 and 24, then 5 replies
        var samples = Every(1, sec => sec is 2 or 24 ? ProbeOutcome.NoReply : ProbeOutcome.Reply);

        Assert.Equal(Observation.Lossy, Hop(samples));
    }

    [Fact]
    public void Internet_check_recovers_after_five_replies()
    {
        // checks on even seconds: lost at 8 and 10, then replies at 12..28 (9 in a row)
        var samples = Every(2, sec => sec is 8 or 10 ? ProbeOutcome.NoReply : ProbeOutcome.Reply);

        Assert.Equal(Observation.Answering, Endpoint(samples));
    }

    [Fact]
    public void Local_errors_are_unavailable_not_silent()
    {
        var samples = Every(1, AllUnavailable);

        Assert.Equal(Observation.Unavailable, Hop(samples));
    }

    [Fact]
    public void Half_the_window_unavailable_is_unavailable()
    {
        var samples = Every(1, sec => sec % 2 == 0 ? ProbeOutcome.Unavailable : ProbeOutcome.Reply);

        Assert.Equal(Observation.Unavailable, Hop(samples));
    }

    [Fact]
    public void Few_unavailable_samples_among_replies_change_nothing()
    {
        var samples = Every(1, sec => sec is 7 or 15 ? ProbeOutcome.Unavailable : ProbeOutcome.Reply);

        Assert.Equal(Observation.Answering, Hop(samples));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathObserverTests"`
Expected: the build FAILS with `error CS0103: The name 'PathObserver' does not exist` (or `Observation`).

- [ ] **Step 3: Write the implementation**

`src/NetRoute.Core/PathMap/PathObserver.cs`:

```cs
namespace NetRoute.Core.PathMap;

/// What a device's own samples say, before any conclusion about the network is drawn from them.
public enum Observation { Measuring, Answering, Lossy, Silent, Hidden, Unavailable }

public static class PathObserver
{
    /// First match wins. 'endpoint' selects the internet check's sample minimum and recovery count.
    public static Observation Observe(SampleStats s, bool hidden, bool endpoint, PathThresholds t)
    {
        if (hidden) return Observation.Hidden;

        if (s.ConsecutiveUnavailable >= t.UnavailableConsecutive
            || (s.Total >= t.UnavailableConsecutive && s.UnavailablePercent >= t.UnavailableSharePercent))
            return Observation.Unavailable;

        if (s.ConsecutiveLost >= t.SilentConsecutive) return Observation.Silent;

        var minimum = endpoint ? t.EndpointMinSamples : t.HopMinSamples;
        if (s.Valid < minimum) return Observation.Measuring;

        // Recovery: enough consecutive replies since the last lost check end "losing packets",
        // even while older losses are still inside the window.
        var recover = endpoint ? t.EndpointRecoverReplies : t.HopRecoverReplies;
        if (s.LossPercent >= t.LossyPercent && s.ConsecutiveReplies < recover) return Observation.Lossy;

        return Observation.Answering;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathObserverTests"`
Expected: `Passed!  - Failed:     0, Passed:    13`

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/PathObserver.cs tests/NetRoute.Core.Tests/PathObserverTests.cs
git commit -m "feat(core): per-device observation rules with measuring, recovery and unavailable"
```

---

### Task 3: The tracer

**Files:**
- Create: `src/NetRoute.Core/PathMap/PathTracer.cs`
- Create: `tests/NetRoute.Core.Tests/PathTracerTests.cs`

**Interfaces:**
- Consumes: `IPathProbe`, `EchoResult`, `PathTrace`, `PathHop`, `TraceStatus`, `PathThresholds.MaxHops` (Task 1); `FakePathProbe` (Task 1 kit).
- Produces: `new PathTracer(IPathProbe probe, PathThresholds thresholds)`; `PathTracer.DefaultTarget` (`1.1.1.1`), `PathTracer.DefaultTimeout` (1 s); `Task<PathTrace> TraceAsync(IPAddress source, IPAddress target, TimeSpan timeout, CancellationToken ct)`. Throws `OperationCanceledException` when `ct` is cancelled.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/PathTracerTests.cs`:

```cs
using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.FakePathProbe;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathTracerTests
{
    static Task<PathTrace> Trace(FakePathProbe probe, CancellationToken ct = default) =>
        new PathTracer(probe, T).TraceAsync(Ip("192.168.86.42"), Ip("1.1.1.1"), TimeSpan.FromSeconds(1), ct);

    [Fact]
    public async Task Complete_trace_lists_the_devices_before_the_target()
    {
        var probe = new FakePathProbe { Default = Target() };
        probe.ByTtl[1] = Hop("192.168.86.1");
        probe.ByTtl[2] = Hop("192.168.1.1");
        probe.ByTtl[3] = Hop("172.17.18.1");

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Complete, trace.Status);
        Assert.Equal(["192.168.86.1", "192.168.1.1", "172.17.18.1"], trace.Hops.Select(h => h.Address!.ToString()));
        Assert.Equal([1, 2, 3], trace.Hops.Select(h => h.Ttl));
    }

    [Fact]
    public async Task A_silent_ttl_before_the_target_is_a_hidden_hop()
    {
        var probe = new FakePathProbe { Default = Target() };
        probe.ByTtl[1] = Hop("192.168.42.129");
        probe.ByTtl[2] = new EchoResult(ProbeOutcome.NoReply);
        probe.ByTtl[3] = Hop("172.26.242.30");

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Complete, trace.Status);
        Assert.Equal([false, true, false], trace.Hops.Select(h => h.Hidden));
    }

    [Fact]
    public async Task Reaching_the_hop_limit_is_an_incomplete_trace_not_a_failure()
    {
        // every TTL answers as a router; the target never does
        var probe = new FakePathProbe();
        for (var ttl = 1; ttl <= 12; ttl++) probe.ByTtl[ttl] = Hop($"10.0.0.{ttl}");

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Incomplete, trace.Status);
        Assert.Equal(12, trace.Hops.Count);
    }

    [Fact]
    public async Task Incomplete_trace_trims_trailing_hidden_hops()
    {
        var probe = new FakePathProbe();
        probe.ByTtl[1] = Hop("192.168.86.1");
        probe.ByTtl[2] = Hop("192.168.1.1");

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Incomplete, trace.Status);
        Assert.Equal(2, trace.Hops.Count);
    }

    [Fact]
    public async Task Local_errors_make_the_trace_unavailable_not_incomplete()
    {
        var probe = new FakePathProbe { Default = new EchoResult(ProbeOutcome.Unavailable) };

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Unavailable, trace.Status);
        Assert.Empty(trace.Hops);
    }

    [Fact]
    public async Task Target_answering_at_the_first_ttl_gives_an_empty_complete_path()
    {
        var probe = new FakePathProbe { Default = Target() };

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Complete, trace.Status);
        Assert.Empty(trace.Hops);
    }

    [Fact]
    public async Task Sends_every_ttl_up_to_the_hop_limit()
    {
        var probe = new FakePathProbe();

        await Trace(probe);

        Assert.Equal(Enumerable.Range(1, 12), probe.Calls.OrderBy(x => x));
    }

    [Fact]
    public async Task A_cancelled_trace_throws_instead_of_reporting_a_path()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Trace(new FakePathProbe(), cts.Token));
    }

    [Fact]
    public async Task A_reply_that_names_no_address_is_a_hidden_hop()
    {
        var probe = new FakePathProbe { Default = Target() };
        probe.ByTtl[1] = Hop("192.168.86.1");
        probe.ByTtl[2] = new EchoResult(ProbeOutcome.Reply, From: null);

        var trace = await Trace(probe);

        Assert.Equal([false, true], trace.Hops.Select(h => h.Hidden));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathTracerTests"`
Expected: the build FAILS with `error CS0246: The type or namespace name 'PathTracer' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/NetRoute.Core/PathMap/PathTracer.cs`:

```cs
using System.Net;

namespace NetRoute.Core.PathMap;

/// Finds the devices between one adapter and the target by sending TTL-limited echoes from the adapter's own address.
public sealed class PathTracer(IPathProbe probe, PathThresholds thresholds)
{
    public static readonly IPAddress DefaultTarget = IPAddress.Parse("1.1.1.1");
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    /// All TTLs are sent in parallel. Complete: the target answered at some TTL. Incomplete: it did not within the hop
    /// limit (a long path, or ICMP to the target is filtered): never a fault by itself. Unavailable: the checks could not run.
    public async Task<PathTrace> TraceAsync(IPAddress source, IPAddress target, TimeSpan timeout, CancellationToken ct)
    {
        var results = await Task.WhenAll(
            Enumerable.Range(1, thresholds.MaxHops).Select(ttl => probe.EchoAsync(source, target, ttl, timeout, ct)));
        ct.ThrowIfCancellationRequested();

        var unavailable = results.Count(r => r.Outcome == ProbeOutcome.Unavailable);
        if (results[0].Outcome == ProbeOutcome.Unavailable || unavailable * 2 >= results.Length)
            return new PathTrace(source, target, [], TraceStatus.Unavailable);

        var reached = Array.FindIndex(results, r => r.TargetReached);
        if (reached >= 0)
            return new PathTrace(source, target, HopsOf(results, reached), TraceStatus.Complete);

        var hops = HopsOf(results, results.Length);
        while (hops.Count > 0 && hops[^1].Hidden) hops.RemoveAt(hops.Count - 1);
        return new PathTrace(source, target, hops, TraceStatus.Incomplete);
    }

    static List<PathHop> HopsOf(EchoResult[] results, int count) =>
        results.Take(count)
            .Select((r, i) => new PathHop(i + 1, r.Outcome == ProbeOutcome.Reply && !r.TargetReached ? r.From : null))
            .ToList();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathTracerTests"`
Expected: `Passed!  - Failed:     0, Passed:     9`

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/PathTracer.cs tests/NetRoute.Core.Tests/PathTracerTests.cs
git commit -m "feat(core): path tracer with complete, incomplete and unavailable results"
```

---

### Task 4: The diagnoser (the fault rule)

**Files:**
- Create: `src/NetRoute.Core/PathMap/PathDiagnoser.cs`
- Modify: `tests/NetRoute.Core.Tests/PathMapKit.cs` (add the `Lane` helper; it needs `LaneInput`, which this task defines)
- Create: `tests/NetRoute.Core.Tests/PathDiagnoserTests.cs`

**Interfaces:**
- Consumes: `Sample`, `SampleStats`, `PathTrace`, `PathHop`, `TraceStatus`, `PathThresholds` (Task 1); `Observation`, `PathObserver` (Task 2).
- Produces: `enum HopDisplay { Measuring, Answering, Quiet, Hidden, Unavailable, Suspect, Broken, Silent }`; `enum LaneStatus { NotConnected, Measuring, Unavailable, Healthy, Blip, Fault }`; `enum Localization { None, Located, Narrowed, NotEnoughData }`; `LaneInput(string Name, bool Connected, PathTrace? Trace, IReadOnlyList<IReadOnlyList<Sample>> HopSamples, IReadOnlyList<Sample> EndpointSamples, DateTimeOffset Now)`; `HopDiagnosis(PathHop Hop, SampleStats Stats, Observation Observed, HopDisplay Display, double? LossDuringFailuresPercent)`; `LaneDiagnosis(string Name, LaneStatus Status, Localization Where, IReadOnlyList<HopDiagnosis> Hops, Observation Endpoint, SampleStats EndpointStats, int? LastAnsweringHop, int? FirstBadHop, TraceStatus? TraceStatus)`; `PathDiagnoser.Diagnose(LaneInput lane, PathThresholds t) : LaneDiagnosis`. `LastAnsweringHop` and `FirstBadHop` are indices into `Hops`; `LastAnsweringHop == null` with `Where == Located` means "this PC".

This is the heart of the feature. The tests are written around the review rules: a quiet hop is never broken; a device before one that answers is not blamed; evidence comes only from the same 2-second buckets as the failed checks; too little evidence names nothing.

- [ ] **Step 1: Write the failing tests**

First add the `Lane` helper to the kit: in `tests/NetRoute.Core.Tests/PathMapKit.cs`, inside `static class PathKit`, directly after the `Trace(...)` method, add:

```csharp

    public static LaneInput Lane(PathTrace? trace, List<Sample> endpoint, params List<Sample>[] hops) =>
        new("LAN", true, trace, hops.Select(h => (IReadOnlyList<Sample>)h).ToList(), endpoint, Now);
```

`tests/NetRoute.Core.Tests/PathDiagnoserTests.cs`:

```cs
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
        var d = Diagnose(Lane(Four, FailingEndpoint(),
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
        var d = Diagnose(Lane(Trace("192.168.86.1", "192.168.1.1"), FailingEndpoint(),
            Every(1, LostDuring(14)), Every(1, LostDuring(14))));

        Assert.Equal(Localization.Located, d.Where);
        Assert.Null(d.LastAnsweringHop);
        Assert.Equal(0, d.FirstBadHop);
    }

    [Fact]
    public void A_device_losing_part_of_its_probes_during_the_failure_is_suspect_not_broken()
    {
        // lost every second probe from second 14: 50% during the failure, 0% before it
        var d = Diagnose(Lane(Trace("192.168.86.1", "192.168.1.1", "172.17.18.1"), FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, sec => sec >= 14 && sec % 2 == 0 ? ProbeOutcome.NoReply : ProbeOutcome.Reply)));

        Assert.Equal(Localization.Located, d.Where);
        Assert.Equal(HopDisplay.Suspect, d.Hops[2].Display);
    }

    [Fact]
    public void A_quiet_device_before_one_that_answers_is_not_blamed()
    {
        // hop 2 ignores probes all the time; hop 3 keeps answering while the check fails; only hop 4 goes silent with it
        var d = Diagnose(Lane(Four, FailingEndpoint(),
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
        var d = Diagnose(Lane(Four, FailingEndpoint(),
            Every(1, AllReply), Every(1, AllReply), Every(1, LostDuring(14)), Every(1, LostDuring(14))));

        foreach (var hop in d.Hops.Where(h => h.Observed == Observation.Answering))
            Assert.Equal(HopDisplay.Answering, hop.Display);
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

    [Fact]
    public void An_unavailable_trace_is_unavailable_not_a_broken_connection()
    {
        var trace = new PathTrace(Ip("192.168.86.42"), Ip("1.1.1.1"), [], TraceStatus.Unavailable);

        var d = Diagnose(Lane(trace, Every(2, AllReply)));

        Assert.Equal(LaneStatus.Unavailable, d.Status);
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
    public void Hidden_hops_are_never_blamed_and_never_block_a_located_break()
    {
        var trace = Trace("192.168.42.129", null, "172.26.242.30");
        var d = Diagnose(Lane(trace, FailingEndpoint(),
            Every(1, AllReply), [], Every(1, LostDuring(14))));

        Assert.Equal(Localization.Located, d.Where);
        Assert.Equal(HopDisplay.Hidden, d.Hops[1].Display);
        Assert.Equal(2, d.FirstBadHop);
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathDiagnoserTests"`
Expected: the build FAILS with `error CS0103: The name 'PathDiagnoser' does not exist`.

- [ ] **Step 3: Write the implementation**

`src/NetRoute.Core/PathMap/PathDiagnoser.cs`:

```cs
namespace NetRoute.Core.PathMap;

/// How a device is drawn. Quiet: not answering probes well, but nothing points at it. Silent: no answer past a located break.
public enum HopDisplay { Measuring, Answering, Quiet, Hidden, Unavailable, Suspect, Broken, Silent }

public enum LaneStatus { NotConnected, Measuring, Unavailable, Healthy, Blip, Fault }

/// How far the evidence goes in a Fault. Located: a break between two devices. Narrowed: the fault is after a device that kept
/// answering, but no device is named. NotEnoughData: no claim. None: the lane is not in a Fault.
public enum Localization { None, Located, Narrowed, NotEnoughData }

public sealed record LaneInput(
    string Name,
    bool Connected,
    PathTrace? Trace,
    // One list per trace hop, same order, oldest first, already limited to the window. A hidden hop's list is empty.
    IReadOnlyList<IReadOnlyList<Sample>> HopSamples,
    IReadOnlyList<Sample> EndpointSamples,
    DateTimeOffset Now);

public sealed record HopDiagnosis(
    PathHop Hop, SampleStats Stats, Observation Observed, HopDisplay Display, double? LossDuringFailuresPercent);

public sealed record LaneDiagnosis(
    string Name,
    LaneStatus Status,
    Localization Where,
    IReadOnlyList<HopDiagnosis> Hops,
    Observation Endpoint,
    SampleStats EndpointStats,
    // Index into Hops of the furthest device that answered while the internet check was failing; null = this PC.
    int? LastAnsweringHop,
    // Index into Hops of the first device after the break; set only when Where is Located.
    int? FirstBadHop,
    TraceStatus? TraceStatus);

/// Turns raw observations into a conclusion. The internet check decides whether a lane is failing at all; a device is
/// blamed only when its probes were lost in the same 2-second buckets as the failed internet checks.
public static class PathDiagnoser
{
    public static LaneDiagnosis Diagnose(LaneInput lane, PathThresholds t)
    {
        var endpointStats = SampleStats.Of(lane.EndpointSamples);
        var endpoint = PathObserver.Observe(endpointStats, hidden: false, endpoint: true, t);

        if (!lane.Connected)
            return new LaneDiagnosis(lane.Name, LaneStatus.NotConnected, Localization.None, [], endpoint, endpointStats, null, null, null);
        if (lane.Trace is not { } trace)
            return new LaneDiagnosis(lane.Name, LaneStatus.Measuring, Localization.None, [], endpoint, endpointStats, null, null, null);

        var stats = trace.Hops.Select((_, i) => SampleStats.Of(i < lane.HopSamples.Count ? lane.HopSamples[i] : [])).ToArray();
        var observed = trace.Hops.Select((h, i) => PathObserver.Observe(stats[i], h.Hidden, endpoint: false, t)).ToArray();

        LaneDiagnosis Build(LaneStatus status, Localization where, Func<int, HopDisplay> display,
            double?[]? failureLoss = null, int? lastAnswering = null, int? firstBad = null) =>
            new(lane.Name, status, where,
                trace.Hops.Select((h, i) => new HopDiagnosis(h, stats[i], observed[i], display(i), failureLoss?[i])).ToList(),
                endpoint, endpointStats, lastAnswering, firstBad, trace.Status);

        HopDisplay Calm(int i) => observed[i] switch
        {
            Observation.Hidden => HopDisplay.Hidden,
            Observation.Unavailable => HopDisplay.Unavailable,
            Observation.Measuring => HopDisplay.Measuring,
            Observation.Answering => HopDisplay.Answering,
            _ => HopDisplay.Quiet, // silent or lossy, with nothing pointing at it
        };

        if (trace.Status == TraceStatus.Unavailable || endpoint == Observation.Unavailable)
            return Build(LaneStatus.Unavailable, Localization.None, Calm);
        if (endpoint == Observation.Measuring)
            return Build(LaneStatus.Measuring, Localization.None, i => observed[i] is Observation.Hidden or Observation.Unavailable ? Calm(i) : HopDisplay.Measuring);
        if (endpoint == Observation.Answering)
            return Build(LaneStatus.Healthy, Localization.None, Calm);
        if (endpointStats.Lost < t.MinFailedChecks)
            return Build(LaneStatus.Blip, Localization.None, Calm);

        return Localize(lane, trace, observed, t, Build, Calm);
    }

    static LaneDiagnosis Localize(
        LaneInput lane, PathTrace trace, Observation[] observed, PathThresholds t,
        Func<LaneStatus, Localization, Func<int, HopDisplay>, double?[]?, int?, int?, LaneDiagnosis> build,
        Func<int, HopDisplay> calm)
    {
        var failing = FailingBuckets(lane.EndpointSamples, t.Bucket);
        var n = trace.Hops.Count;
        var cleared = new bool[n];
        var implicated = new bool[n];
        var failureLoss = new double?[n];

        for (var i = 0; i < n; i++)
        {
            if (trace.Hops[i].Hidden || i >= lane.HopSamples.Count) continue;
            int nFailing = 0, lostFailing = 0, nGood = 0, lostGood = 0;
            foreach (var s in lane.HopSamples[i])
            {
                if (s.Outcome == ProbeOutcome.Unavailable || !failing.TryGetValue(BucketOf(s.At, t.Bucket), out var isFailing)) continue;
                var lost = s.Outcome == ProbeOutcome.NoReply ? 1 : 0;
                if (isFailing) { nFailing++; lostFailing += lost; }
                else { nGood++; lostGood += lost; }
            }
            if (nFailing == 0) continue;

            var lossFailing = 100.0 * lostFailing / nFailing;
            // With too little data from good periods, the trace itself (the hop answered it) counts as a good reply.
            var lossGood = nGood < t.MinEvidenceProbes ? 0.0 : 100.0 * lostGood / nGood;
            failureLoss[i] = lossFailing;
            if (nFailing < t.MinEvidenceProbes) continue;
            cleared[i] = lossFailing <= t.ClearedLossPercent;
            implicated[i] = lossFailing >= t.ImplicatedLossPercent && lossFailing - lossGood >= t.ImplicatedMarginPoints;
        }

        int? furthestCleared = null;
        for (var i = 0; i < n; i++) if (cleared[i]) furthestCleared = i;

        var candidates = Enumerable.Range((furthestCleared ?? -1) + 1, n - ((furthestCleared ?? -1) + 1))
            .Where(i => !trace.Hops[i].Hidden).ToList();

        if (candidates.Count > 0 && candidates.All(i => implicated[i]))
        {
            var first = candidates[0];
            var after = candidates.ToHashSet();
            return build(LaneStatus.Fault, Localization.Located,
                i => i == first ? (observed[i] == Observation.Silent ? HopDisplay.Broken : HopDisplay.Suspect)
                    : after.Contains(i) ? HopDisplay.Silent
                    : cleared[i] ? HopDisplay.Answering : calm(i),
                failureLoss, furthestCleared, first);
        }

        if (furthestCleared is { } lastAnswering)
            // Only devices after the furthest one that answered can be suspect: traffic demonstrably gets past the earlier ones.
            return build(LaneStatus.Fault, Localization.Narrowed,
                i => cleared[i] ? HopDisplay.Answering : implicated[i] && i > lastAnswering ? HopDisplay.Suspect : calm(i),
                failureLoss, lastAnswering, null);

        return build(LaneStatus.Fault, Localization.NotEnoughData, calm, failureLoss, null, null);
    }

    static long BucketOf(DateTimeOffset at, TimeSpan bucket) => at.ToUnixTimeMilliseconds() / (long)bucket.TotalMilliseconds;

    /// bucket → true when the internet check lost in it, false when it replied. Buckets with only Unavailable checks are left out.
    static Dictionary<long, bool> FailingBuckets(IReadOnlyList<Sample> endpointSamples, TimeSpan bucket)
    {
        var result = new Dictionary<long, bool>();
        foreach (var s in endpointSamples)
        {
            if (s.Outcome == ProbeOutcome.Unavailable) continue;
            var key = BucketOf(s.At, bucket);
            result[key] = s.Outcome == ProbeOutcome.NoReply || (result.TryGetValue(key, out var was) && was);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathDiagnoserTests"`
Expected: `Passed!  - Failed:     0, Passed:    22`

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/PathDiagnoser.cs tests/NetRoute.Core.Tests/PathDiagnoserTests.cs
git commit -m "feat(core): path diagnoser applying the evidence-based fault rule"
```

---

### Task 5: ICMP interop and the hop probe

**Files:**
- Create: `src/NetRoute.Core/Windows/IcmpNative.cs`
- Create: `src/NetRoute.Core/Windows/IcmpPathProbe.cs`
- Create: `tests/NetRoute.Core.Tests/IcmpPathProbeTests.cs`

**Interfaces:**
- Consumes: `IPathProbe`, `EchoResult`, `ProbeOutcome` (Task 1).
- Produces: `public sealed class IcmpPathProbe : IPathProbe` (parameterless constructor). Internals for tests: `IcmpNative` (`ICMP_ECHO_REPLY`, `IP_OPTION_INFORMATION`, status constants) and `IcmpPathProbe.Interpret(uint replies, uint status, uint address, uint roundTripMs, int lastError, IPAddress target) : EchoResult`. `InternalsVisibleTo NetRoute.Core.Tests` already exists in `NetRoute.Core.csproj`.

The spike on 2026-10-13 proved source-bound, TTL-limited ICMP works on this machine for both adapters. This task makes the outcome classification a pure, table-tested function so a local failure can never be reported as loss.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/IcmpPathProbeTests.cs`:

```cs
using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.PathMap;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class IcmpPathProbeTests
{
    static readonly IPAddress Target = IPAddress.Parse("1.1.1.1");
    static uint Addr(string ip) => BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);

    [Fact]
    public void Echo_reply_struct_matches_the_native_layout_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        Assert.Equal(40, Marshal.SizeOf<IcmpNative.ICMP_ECHO_REPLY>());
        Assert.Equal(16, Marshal.SizeOf<IcmpNative.IP_OPTION_INFORMATION>());
        Assert.Equal(16, (int)Marshal.OffsetOf<IcmpNative.ICMP_ECHO_REPLY>(nameof(IcmpNative.ICMP_ECHO_REPLY.Data)));
        Assert.Equal(24, (int)Marshal.OffsetOf<IcmpNative.ICMP_ECHO_REPLY>(nameof(IcmpNative.ICMP_ECHO_REPLY.Options)));
    }

    [Fact]
    public void A_timeout_is_a_lost_check()
    {
        var r = IcmpPathProbe.Interpret(0, 0, 0, 0, lastError: 11010, Target);

        Assert.Equal(ProbeOutcome.NoReply, r.Outcome);
    }

    [Theory]
    [InlineData(87)]    // ERROR_INVALID_PARAMETER: for example the source address no longer exists
    [InlineData(1231)]  // ERROR_NETWORK_UNREACHABLE
    [InlineData(5)]     // access denied
    [InlineData(11050)] // IP_GENERAL_FAILURE
    [InlineData(0)]
    public void Any_other_failure_to_send_did_not_measure_the_network(int lastError)
    {
        Assert.Equal(ProbeOutcome.Unavailable, IcmpPathProbe.Interpret(0, 0, 0, 0, lastError, Target).Outcome);
    }

    [Fact]
    public void An_echo_reply_from_the_target_reaches_it()
    {
        var r = IcmpPathProbe.Interpret(1, 0, Addr("1.1.1.1"), 52, 0, Target);

        Assert.Equal(ProbeOutcome.Reply, r.Outcome);
        Assert.True(r.TargetReached);
        Assert.Equal(52, r.RttMs);
        Assert.Equal(Target, r.From);
    }

    [Fact]
    public void Ttl_expired_reveals_the_device_and_does_not_reach_the_target()
    {
        var r = IcmpPathProbe.Interpret(1, 11013, Addr("192.168.1.1"), 0, 0, Target);

        Assert.Equal(ProbeOutcome.Reply, r.Outcome);
        Assert.False(r.TargetReached);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), r.From);
        Assert.Equal(1, r.RttMs); // under a millisecond is shown as 1
    }

    [Theory]
    [InlineData(11010u)] // timed out
    [InlineData(11002u)] // destination net unreachable
    [InlineData(11003u)] // destination host unreachable
    [InlineData(11004u)]
    [InlineData(11005u)]
    [InlineData(11018u)] // bad destination
    public void The_network_saying_no_is_a_lost_check(uint status)
    {
        Assert.Equal(ProbeOutcome.NoReply, IcmpPathProbe.Interpret(1, status, Addr("192.168.1.1"), 3, 0, Target).Outcome);
    }

    [Theory]
    [InlineData(11001u)] // buffer too small
    [InlineData(11006u)] // no resources
    [InlineData(11007u)] // bad option
    [InlineData(11050u)] // general failure
    [InlineData(99999u)] // unknown
    public void Unexpected_statuses_did_not_measure_the_network(uint status)
    {
        Assert.Equal(ProbeOutcome.Unavailable, IcmpPathProbe.Interpret(1, status, Addr("192.168.1.1"), 3, 0, Target).Outcome);
    }

    [Fact]
    public async Task A_cancelled_check_is_unavailable_and_sends_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = await new IcmpPathProbe().EchoAsync(IPAddress.Loopback, IPAddress.Loopback, 64, TimeSpan.FromSeconds(1), cts.Token);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    public async Task IPv6_is_not_supported_and_is_unavailable()
    {
        var r = await new IcmpPathProbe().EchoAsync(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, 64, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Loopback_echo_gets_a_real_reply()
    {
        var r = await new IcmpPathProbe().EchoAsync(IPAddress.Loopback, IPAddress.Loopback, 64, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Reply, r.Outcome);
        Assert.True(r.TargetReached);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_source_address_this_pc_does_not_have_is_unavailable_not_lost()
    {
        var r = await new IcmpPathProbe().EchoAsync(IPAddress.Parse("203.0.113.7"), IPAddress.Loopback, 64, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~IcmpPathProbeTests"`
Expected: the build FAILS with `error CS0103: The name 'IcmpNative' does not exist` (or `IcmpPathProbe`).

- [ ] **Step 3: Write the implementation**

`src/NetRoute.Core/Windows/IcmpNative.cs`:

```cs
using System.Runtime.InteropServices;

namespace NetRoute.Core.Windows;

/// iphlpapi.dll ICMP interop (icmpapi.h / ipexport.h). The reply struct is 40 bytes on x64; IcmpLayoutTests guards it.
internal static class IcmpNative
{
    public const uint IP_SUCCESS = 0;
    public const uint IP_DEST_NET_UNREACHABLE = 11002;
    public const uint IP_DEST_HOST_UNREACHABLE = 11003;
    public const uint IP_DEST_PROT_UNREACHABLE = 11004;
    public const uint IP_DEST_PORT_UNREACHABLE = 11005;
    public const uint IP_REQ_TIMED_OUT = 11010;
    public const uint IP_TTL_EXPIRED_TRANSIT = 11013;
    public const uint IP_BAD_DESTINATION = 11018;

    [StructLayout(LayoutKind.Sequential)]
    public struct IP_OPTION_INFORMATION
    {
        public byte Ttl;
        public byte Tos;
        public byte Flags;
        public byte OptionsSize;
        public IntPtr OptionsData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICMP_ECHO_REPLY
    {
        public uint Address;
        public uint Status;
        public uint RoundTripTime;
        public ushort DataSize;
        public ushort Reserved;
        public IntPtr Data;
        public IP_OPTION_INFORMATION Options;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern bool IcmpCloseHandle(IntPtr handle);

    /// Synchronous when eventHandle and apcRoutine are null. Returns the number of replies, or 0 (see GetLastError).
    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint IcmpSendEcho2Ex(
        IntPtr icmpHandle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destinationAddress,
        byte[] requestData, ushort requestSize, ref IP_OPTION_INFORMATION requestOptions,
        IntPtr replyBuffer, uint replySize, uint timeout);
}
```

`src/NetRoute.Core/Windows/IcmpPathProbe.cs`:

```cs
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

/// ICMP echo with a chosen TTL, sent from a chosen local address (so each connection is probed through its own adapter).
/// Needs no administrator rights. A check that could not run is Unavailable, never a lost packet.
public sealed class IcmpPathProbe : IPathProbe
{
    const int PayloadSize = 32;
    const int ReplyBufferSize = 256; // ICMP_ECHO_REPLY (40) + payload + 8 for an ICMP error + IO_STATUS_BLOCK, with room to spare

    public Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(new EchoResult(ProbeOutcome.Unavailable));
        // IcmpSendEcho2Ex blocks for up to the timeout; keep it off the caller's thread.
        return Task.Run(() => Send(source, target, ttl, timeout), CancellationToken.None);
    }

    static EchoResult Send(IPAddress source, IPAddress target, int ttl, TimeSpan timeout)
    {
        if (source.AddressFamily != AddressFamily.InterNetwork || target.AddressFamily != AddressFamily.InterNetwork)
            return new EchoResult(ProbeOutcome.Unavailable);

        var handle = IcmpNative.IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return new EchoResult(ProbeOutcome.Unavailable);
        var reply = Marshal.AllocHGlobal(ReplyBufferSize);
        try
        {
            var options = new IcmpNative.IP_OPTION_INFORMATION { Ttl = (byte)Math.Clamp(ttl, 1, 255) };
            var replies = IcmpNative.IcmpSendEcho2Ex(
                handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                Address(source), Address(target),
                new byte[PayloadSize], PayloadSize, ref options,
                reply, ReplyBufferSize, (uint)Math.Max(1, timeout.TotalMilliseconds));
            var lastError = Marshal.GetLastWin32Error();
            if (replies == 0) return Interpret(0, 0, 0, 0, lastError, target);
            var r = Marshal.PtrToStructure<IcmpNative.ICMP_ECHO_REPLY>(reply);
            return Interpret(replies, r.Status, r.Address, r.RoundTripTime, 0, target);
        }
        finally
        {
            Marshal.FreeHGlobal(reply);
            IcmpNative.IcmpCloseHandle(handle);
        }
    }

    static uint Address(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

    /// Maps the API's result to an outcome. Pure, so a table test pins it.
    /// Reply: a real echo reply, or "TTL expired" from a device. NoReply: a timeout, or the network answering "unreachable".
    /// Everything else (resource and general failures, parameter errors, any other Win32 error) did not measure the network.
    internal static EchoResult Interpret(uint replies, uint status, uint address, uint roundTripMs, int lastError, IPAddress target)
    {
        if (replies == 0)
            return new EchoResult(lastError == (int)IcmpNative.IP_REQ_TIMED_OUT ? ProbeOutcome.NoReply : ProbeOutcome.Unavailable);

        var from = new IPAddress(BitConverter.GetBytes(address));
        var rtt = (int)Math.Max(1, roundTripMs);
        switch (status)
        {
            case IcmpNative.IP_SUCCESS:
                return new EchoResult(ProbeOutcome.Reply, from, rtt, TargetReached: from.Equals(target));
            case IcmpNative.IP_TTL_EXPIRED_TRANSIT:
                return new EchoResult(ProbeOutcome.Reply, from, rtt);
            case IcmpNative.IP_REQ_TIMED_OUT:
            case IcmpNative.IP_DEST_NET_UNREACHABLE:
            case IcmpNative.IP_DEST_HOST_UNREACHABLE:
            case IcmpNative.IP_DEST_PROT_UNREACHABLE:
            case IcmpNative.IP_DEST_PORT_UNREACHABLE:
            case IcmpNative.IP_BAD_DESTINATION:
                return new EchoResult(ProbeOutcome.NoReply, from);
            default:
                return new EchoResult(ProbeOutcome.Unavailable);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~IcmpPathProbeTests"`
Expected: `Passed!  - Failed:     0, Passed:    24` (this count includes the theory cases and two `Integration`-category tests that use loopback and a made-up source address).

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/Windows/IcmpNative.cs src/NetRoute.Core/Windows/IcmpPathProbe.cs tests/NetRoute.Core.Tests/IcmpPathProbeTests.cs
git commit -m "feat(core): ICMP hop probe with a pure outcome classifier"
```

---

### Task 6: The internet check with a three-way outcome

**Files:**
- Create: `src/NetRoute.Core/Windows/TcpEndpointProbe.cs`
- Create: `tests/NetRoute.Core.Tests/TcpEndpointProbeTests.cs`

**Interfaces:**
- Consumes: `IEndpointProbe`, `EndpointResult`, `ProbeOutcome` (Task 1).
- Produces: `public sealed class TcpEndpointProbe(IReadOnlyList<IPEndPoint> targets, TimeSpan timeout) : IEndpointProbe`; `TcpEndpointProbe.Default()` (`1.1.1.1:443` then `8.8.8.8:443`, 2 s each); `TcpEndpointProbe.Classify(SocketError) : ProbeOutcome` (public static). The existing `TcpLatencyProbe` is left untouched.

This is where the earlier port-exhaustion confusion is fixed for the new feature: exhausted ports, a vanished source address and a cancelled check are **Unavailable**, not lost checks.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/TcpEndpointProbeTests.cs`:

```cs
using System.Net;
using System.Net.Sockets;
using NetRoute.Core.PathMap;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class TcpEndpointProbeTests
{
    [Theory]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.ConnectionAborted)]
    [InlineData(SocketError.HostDown)]
    public void The_network_not_answering_is_a_lost_check(SocketError error)
    {
        Assert.Equal(ProbeOutcome.NoReply, TcpEndpointProbe.Classify(error));
    }

    [Theory]
    [InlineData(SocketError.AddressAlreadyInUse)]
    [InlineData(SocketError.NoBufferSpaceAvailable)]
    [InlineData(SocketError.TooManyOpenSockets)]
    [InlineData(SocketError.AddressNotAvailable)]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.SocketError)]
    public void A_problem_on_this_pc_is_unavailable_not_loss(SocketError error)
    {
        Assert.Equal(ProbeOutcome.Unavailable, TcpEndpointProbe.Classify(error));
    }

    [Fact]
    public async Task Connecting_to_a_listener_is_a_reply_with_a_latency()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var probe = new TcpEndpointProbe([(IPEndPoint)listener.LocalEndpoint], TimeSpan.FromSeconds(2));

            var r = await probe.CheckAsync(IPAddress.Loopback, CancellationToken.None);

            Assert.Equal(ProbeOutcome.Reply, r.Outcome);
            Assert.InRange(r.RttMs!.Value, 1, 2000);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task A_refused_connection_is_a_lost_check()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop(); // nothing listens there any more

        var r = await new TcpEndpointProbe([endpoint], TimeSpan.FromSeconds(2)).CheckAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Equal(ProbeOutcome.NoReply, r.Outcome);
    }

    [Fact]
    public async Task A_source_address_this_pc_does_not_have_is_unavailable()
    {
        var probe = new TcpEndpointProbe([new IPEndPoint(IPAddress.Loopback, 9)], TimeSpan.FromSeconds(2));

        var r = await probe.CheckAsync(IPAddress.Parse("203.0.113.7"), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    public async Task A_cancelled_check_is_unavailable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var probe = new TcpEndpointProbe([new IPEndPoint(IPAddress.Loopback, 9)], TimeSpan.FromSeconds(2));

        var r = await probe.CheckAsync(IPAddress.Loopback, cts.Token);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    public async Task One_unavailable_attempt_without_a_reply_keeps_the_whole_check_unavailable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var refused = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        // the first target is refused (a real non-answer); the check still cannot be called lost if a later attempt could not run,
        // so use a target on a family the source address cannot reach
        var probe = new TcpEndpointProbe([refused, new IPEndPoint(IPAddress.IPv6Loopback, 9)], TimeSpan.FromSeconds(2));

        var r = await probe.CheckAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~TcpEndpointProbeTests"`
Expected: the build FAILS with `error CS0246: The type or namespace name 'TcpEndpointProbe' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/NetRoute.Core/Windows/TcpEndpointProbe.cs`:

```cs
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

/// The path map's internet check: a TCP connection from one adapter's address, with a three-way outcome. Unlike the
/// auto-heal probe (which treats every socket error as "no internet" and is left untouched), a local problem such as
/// exhausted ports or a vanished adapter address is Unavailable, not a lost check.
public sealed class TcpEndpointProbe(IReadOnlyList<IPEndPoint> targets, TimeSpan timeout) : IEndpointProbe
{
    public static TcpEndpointProbe Default() => new(
        [new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443)],
        TimeSpan.FromSeconds(2));

    /// Any target answering is a Reply. Otherwise, if any attempt could not be measured the check is Unavailable
    /// (we cannot claim the network said no); only when every attempt really got no answer is it NoReply.
    public async Task<EndpointResult> CheckAsync(IPAddress source, CancellationToken ct)
    {
        var sawUnavailable = false;
        foreach (var target in targets)
        {
            var result = await ConnectAsync(source, target, ct);
            if (result.Outcome == ProbeOutcome.Reply) return result;
            if (result.Outcome == ProbeOutcome.Unavailable) sawUnavailable = true;
        }
        return new EndpointResult(sawUnavailable ? ProbeOutcome.Unavailable : ProbeOutcome.NoReply);
    }

    async Task<EndpointResult> ConnectAsync(IPAddress source, IPEndPoint target, CancellationToken ct)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            socket.Bind(new IPEndPoint(source, 0));
            var stopwatch = Stopwatch.StartNew();
            await socket.ConnectAsync(target, timeoutCts.Token);
            return new EndpointResult(ProbeOutcome.Reply, (int)Math.Max(1, stopwatch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled: the check did not finish, which says nothing. Our own timeout firing is a real non-answer.
            return new EndpointResult(ct.IsCancellationRequested ? ProbeOutcome.Unavailable : ProbeOutcome.NoReply);
        }
        catch (SocketException e)
        {
            return new EndpointResult(Classify(e.SocketErrorCode));
        }
    }

    /// The network (or the stack's routing table) said no, or nothing came back: NoReply. Anything that points at this PC
    /// (ports or buffers exhausted, the source address gone, access denied) or is unknown: Unavailable.
    public static ProbeOutcome Classify(SocketError error) => error switch
    {
        SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
            or SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted
            or SocketError.HostDown => ProbeOutcome.NoReply,
        _ => ProbeOutcome.Unavailable,
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~TcpEndpointProbeTests"`
Expected: `Passed!  - Failed:     0, Passed:    19` (about 4 s: Windows retries a refused loopback connect for roughly 2 s, in two tests).

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/Windows/TcpEndpointProbe.cs tests/NetRoute.Core.Tests/TcpEndpointProbeTests.cs
git commit -m "feat(core): internet check that tells a lost check from a local problem"
```

---

### Task 7: The presenter (view model and diagnosis text)

**Files:**
- Create: `src/NetRoute.Core/PathMap/PathMapPresenter.cs`
- Create: `tests/NetRoute.Core.Tests/PathMapPresenterTests.cs`

**Interfaces:**
- Consumes: `PathDiagnoser`, `LaneInput`, `LaneDiagnosis`, `HopDisplay`, `LaneStatus`, `Localization` (Task 4); `Sample`, `PathTrace`, `PathThresholds`, `TraceStatus` (Task 1); the existing `StatusPresenter.Present(NetworkStatus)`, `NetworkStatus`, `AdapterInfo`; test helpers `TestAdapters` (existing) and `PathKit` (Task 1).
- Produces: `LaneSnapshot(string Name, AdapterInfo? Adapter, PathTrace? Trace, IReadOnlyList<IReadOnlyList<Sample>> HopSamples, IReadOnlyList<Sample> EndpointSamples)`; `PathSnapshot(DateTimeOffset At, LaneSnapshot Lan, LaneSnapshot Phone, DateTimeOffset? LastSampleAt)`; `NodeKind { Device, HiddenRun, Collapsed, TraceIncomplete }`; `LinkState { Healthy, Suspect, Broken, Neutral, Measuring, Unavailable, Muted }`; `NodeView(string Id, NodeKind Kind, string Title, string Address, string Metric, HopDisplay State, string StateLabel)`; `InternetRowView(string Id, string Label, string Metric, HopDisplay State)`; `LaneView(string Name, string Caption, bool Connected, IReadOnlyList<NodeView> Nodes, IReadOnlyList<LinkState> Links, InternetRowView Internet)` (`Links.Count == Nodes.Count + 1`); `DetailView(...)`; `PathMapView(string Headline, bool HeadlineWarning, IReadOnlyList<string> Diagnosis, LaneView Lan, LaneView Phone, DetailView? Detail, string FooterText)`; `PathMapPresenter.Present(NetworkStatus status, PathSnapshot snapshot, string? selectedId, PathThresholds t) : PathMapView`. Node ids: `lan:3`, `lan:2-4` (a hidden run or the collapsed box), `lan:incomplete`, `lan:net` (and the same with `phone:`).

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/PathMapPresenterTests.cs`:

```cs
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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathMapPresenterTests"`
Expected: the build FAILS with `error CS0103: The name 'PathMapPresenter' does not exist` (or `LaneSnapshot`).

- [ ] **Step 3: Write the implementation**

`src/NetRoute.Core/PathMap/PathMapPresenter.cs`:

```cs
using System.Globalization;

namespace NetRoute.Core.PathMap;

public sealed record LaneSnapshot(
    string Name, AdapterInfo? Adapter, PathTrace? Trace,
    IReadOnlyList<IReadOnlyList<Sample>> HopSamples, IReadOnlyList<Sample> EndpointSamples);

public sealed record PathSnapshot(DateTimeOffset At, LaneSnapshot Lan, LaneSnapshot Phone, DateTimeOffset? LastSampleAt);

public enum NodeKind { Device, HiddenRun, Collapsed, TraceIncomplete }

public enum LinkState { Healthy, Suspect, Broken, Neutral, Measuring, Unavailable, Muted }

public sealed record NodeView(string Id, NodeKind Kind, string Title, string Address, string Metric, HopDisplay State, string StateLabel);

public sealed record InternetRowView(string Id, string Label, string Metric, HopDisplay State);

/// Links has one entry per node plus one: the link into each node, then the link into the Internet card.
public sealed record LaneView(
    string Name, string Caption, bool Connected, IReadOnlyList<NodeView> Nodes, IReadOnlyList<LinkState> Links, InternetRowView Internet);

public sealed record DetailView(
    string Id, string Title, string Address, string Chip, HopDisplay State,
    IReadOnlyList<(string Label, string Value)> Stats, IReadOnlyList<Sample> Series, string Note);

public sealed record PathMapView(
    string Headline, bool HeadlineWarning, IReadOnlyList<string> Diagnosis, LaneView Lan, LaneView Phone,
    DetailView? Detail, string FooterText);

/// Pure mapping from a monitor snapshot to everything the path-map window shows.
public static class PathMapPresenter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    sealed record Analysed(string Id, string Name, LaneSnapshot Snap, LaneDiagnosis Diagnosis, LaneView View, List<Group> Groups);

    /// A drawn box: the hops it stands for (indices into the diagnosis' hops) and how it is drawn.
    sealed record Group(NodeKind Kind, IReadOnlyList<int> Hops);

    public static PathMapView Present(NetworkStatus status, PathSnapshot snapshot, string? selectedId, PathThresholds t)
    {
        var card = StatusPresenter.Present(status);
        var lan = Analyse("lan", "LAN", "Router", snapshot.Lan, snapshot.At, t);
        var phone = Analyse("phone", "Phone", "Phone", snapshot.Phone, snapshot.At, t);
        return new PathMapView(
            card.Header, card.HeaderWarning,
            Describe(lan, phone, t),
            lan.View, phone.View,
            selectedId is null ? null : DetailFor(selectedId, lan, phone, t),
            Footer(snapshot));
    }

    static string Footer(PathSnapshot s) =>
        s.LastSampleAt is not { } last ? "Starting…"
        : s.At - last > TimeSpan.FromSeconds(5) ? "No recent checks"
        : "Devices checked every second, internet every 2 s";

    static Analysed Analyse(string id, string name, string firstHopTitle, LaneSnapshot snap, DateTimeOffset now, PathThresholds t)
    {
        var source = snap.Adapter?.IPv4;
        var connected = source is not null;
        var diag = PathDiagnoser.Diagnose(new LaneInput(name, connected, snap.Trace, snap.HopSamples, snap.EndpointSamples, now), t);
        var groups = GroupHops(diag, t);
        var nodes = groups.Select(g => NodeOf(id, g, diag, firstHopTitle)).ToList();
        if (diag.TraceStatus == TraceStatus.Incomplete)
            nodes.Add(new NodeView($"{id}:incomplete", NodeKind.TraceIncomplete, "Not traced", "", "", HopDisplay.Hidden, "Trace incomplete"));

        var internet = InternetState(diag, connected);
        var links = nodes.Select(n => LinkInto(n, diag)).ToList();
        links.Add(InternetLink(internet, diag));

        var view = new LaneView(
            name, connected ? $"{name} path · from {source}" : $"{name} path · not connected", connected,
            nodes, links, new InternetRowView($"{id}:net", name, InternetMetric(diag, internet), internet));
        return new Analysed(id, name, snap, diag, view, groups);
    }

    /// Consecutive hidden hops share one box; anything beyond the drawn limit collapses into "+N more".
    static List<Group> GroupHops(LaneDiagnosis diag, PathThresholds t)
    {
        var groups = new List<(NodeKind Kind, List<int> Hops)>();
        for (var i = 0; i < diag.Hops.Count; i++)
        {
            var hidden = diag.Hops[i].Hop.Hidden;
            if (hidden && groups.Count > 0 && groups[^1].Kind == NodeKind.HiddenRun) groups[^1].Hops.Add(i);
            else groups.Add((hidden ? NodeKind.HiddenRun : NodeKind.Device, [i]));
        }
        if (groups.Count > t.MaxDrawnHops)
        {
            var rest = groups.Skip(t.MaxDrawnHops - 1).SelectMany(g => g.Hops).ToList();
            groups = groups.Take(t.MaxDrawnHops - 1).Append((NodeKind.Collapsed, rest)).ToList();
        }
        return groups.Select(g => new Group(g.Kind, g.Hops)).ToList();
    }

    static NodeView NodeOf(string laneId, Group g, LaneDiagnosis diag, string firstHopTitle)
    {
        var hops = g.Hops.Select(i => diag.Hops[i]).ToList();
        var first = hops[0].Hop.Ttl;
        var last = hops[^1].Hop.Ttl;
        var id = g.Hops.Count == 1 ? $"{laneId}:{first}" : $"{laneId}:{first}-{last}";
        switch (g.Kind)
        {
            case NodeKind.HiddenRun:
                return new NodeView(id, g.Kind, g.Hops.Count == 1 ? $"Hop {first}" : $"Hops {first}–{last}",
                    "no answer", "", HopDisplay.Hidden, Label(HopDisplay.Hidden));
            case NodeKind.Collapsed:
                var worst = Worst(hops.Select(h => h.Display));
                return new NodeView(id, g.Kind, $"+{hops.Count} more", $"hops {first}–{last}", "", worst, Label(worst));
            default:
                var h = hops[0];
                return new NodeView(id, g.Kind, first == 1 ? firstHopTitle : $"Hop {first}", h.Hop.Address!.ToString(),
                    MetricOf(h), h.Display, Label(h.Display));
        }
    }

    // Severity order for a box that stands for several hops; Hidden and Answering are the least interesting.
    static readonly HopDisplay[] Severity =
        [HopDisplay.Broken, HopDisplay.Suspect, HopDisplay.Silent, HopDisplay.Unavailable, HopDisplay.Measuring, HopDisplay.Quiet, HopDisplay.Answering, HopDisplay.Hidden];

    static HopDisplay Worst(IEnumerable<HopDisplay> displays)
    {
        var set = displays.ToHashSet();
        return Severity.First(set.Contains);
    }

    static string Label(HopDisplay d) => d switch
    {
        HopDisplay.Measuring => "Measuring…",
        HopDisplay.Answering => "Answering",
        HopDisplay.Quiet => "Ignores probes",
        HopDisplay.Hidden => "Hidden hop",
        HopDisplay.Unavailable => "Measurement unavailable",
        HopDisplay.Suspect => "Losing probes",
        HopDisplay.Broken => "No answer",
        _ => "After the break",
    };

    static string MetricOf(HopDiagnosis h) => h.Display switch
    {
        HopDisplay.Measuring => "Measuring…",
        HopDisplay.Unavailable => "unavailable",
        HopDisplay.Hidden => "",
        _ when h.Stats.Valid == 0 => "",
        _ when h.Stats.Lost == h.Stats.Valid => "no reply",
        _ => $"{h.Stats.AvgMs ?? 0} ms · {h.Stats.LossPercent.ToString("0", Inv)}%",
    };

    static LinkState LinkInto(NodeView n, LaneDiagnosis diag)
    {
        if (n.Kind == NodeKind.TraceIncomplete) return LinkState.Neutral;
        return n.State switch
        {
            HopDisplay.Answering => LinkState.Healthy,
            // A quiet device is only drawn as passing traffic when the destination demonstrably answers.
            HopDisplay.Quiet => diag.Status is LaneStatus.Healthy or LaneStatus.Blip ? LinkState.Healthy : LinkState.Neutral,
            HopDisplay.Measuring => LinkState.Measuring,
            HopDisplay.Unavailable => LinkState.Unavailable,
            HopDisplay.Suspect => LinkState.Suspect,
            HopDisplay.Broken => LinkState.Broken,
            HopDisplay.Silent => LinkState.Muted,
            _ => LinkState.Neutral,
        };
    }

    static HopDisplay InternetState(LaneDiagnosis diag, bool connected)
    {
        if (!connected) return HopDisplay.Hidden;
        if (diag.Status == LaneStatus.Unavailable) return HopDisplay.Unavailable;
        return diag.Endpoint switch
        {
            Observation.Answering => HopDisplay.Answering,
            Observation.Measuring => HopDisplay.Measuring,
            Observation.Unavailable => HopDisplay.Unavailable,
            Observation.Lossy => HopDisplay.Suspect,
            Observation.Silent => HopDisplay.Broken,
            _ => HopDisplay.Hidden,
        };
    }

    static LinkState InternetLink(HopDisplay internet, LaneDiagnosis diag) => internet switch
    {
        HopDisplay.Answering => LinkState.Healthy,
        HopDisplay.Measuring => LinkState.Measuring,
        HopDisplay.Unavailable => LinkState.Unavailable,
        HopDisplay.Suspect => LinkState.Suspect,
        // After a located break the red link is already drawn into the broken device.
        HopDisplay.Broken => diag.Where == Localization.Located ? LinkState.Muted : LinkState.Broken,
        _ => LinkState.Neutral,
    };

    static string InternetLabel(HopDisplay state) => state switch
    {
        HopDisplay.Answering => "Answering",
        HopDisplay.Measuring => "Measuring…",
        HopDisplay.Unavailable => "Measurement unavailable",
        HopDisplay.Suspect => "Losing packets",
        HopDisplay.Broken => "No reply",
        _ => "—",
    };

    static string InternetMetric(LaneDiagnosis diag, HopDisplay state) => state switch
    {
        HopDisplay.Answering => $"{diag.EndpointStats.AvgMs ?? 0} ms",
        HopDisplay.Measuring => "Measuring…",
        HopDisplay.Unavailable => "unavailable",
        HopDisplay.Suspect => $"losing {diag.EndpointStats.LossPercent.ToString("0", Inv)}%",
        HopDisplay.Broken => "no reply",
        _ => "—",
    };

    // ----- diagnosis text -----

    static IReadOnlyList<string> Describe(Analysed lan, Analysed phone, PathThresholds t)
    {
        if (lan.Diagnosis.Status == LaneStatus.Healthy && phone.Diagnosis.Status == LaneStatus.Healthy)
            return ["Both connections reach the internet."];
        return [Line(lan, t), Line(phone, t)];
    }

    static string Line(Analysed a, PathThresholds t)
    {
        var d = a.Diagnosis;
        var name = a.Name;
        switch (d.Status)
        {
            case LaneStatus.NotConnected: return $"{name}: not connected.";
            case LaneStatus.Measuring: return $"{name}: measuring… {Math.Min(10, d.EndpointStats.Valid * 2)} of 10 s.";
            case LaneStatus.Unavailable: return $"{name}: measurement unavailable on this PC. That says nothing about the connection.";
            case LaneStatus.Healthy: return $"{name} reaches the internet.";
            case LaneStatus.Blip:
                var n = d.EndpointStats.Lost;
                return $"{name}: {n} internet check{(n == 1 ? "" : "s")} failed in the last {t.Window.TotalSeconds.ToString("0", Inv)} s. That is not enough to call a fault.";
        }

        string Address(int index) => d.Hops[index].Hop.Address!.ToString();
        switch (d.Where)
        {
            case Localization.Located:
                var first = d.Hops[d.FirstBadHop!.Value];
                var loss = first.LossDuringFailuresPercent ?? 100;
                var verb = loss >= 99.5 ? "did not answer" : $"lost {loss.ToString("0", Inv)}% of its probes";
                var bad = Address(d.FirstBadHop.Value);
                return d.LastAnsweringHop is { } p
                    ? $"{name}: the break is between {Address(p)} and {bad}. While the internet check failed, {Address(p)} answered and {bad} {verb}."
                    : $"{name}: the break is between this PC and {bad}. While the internet check failed, {bad} {verb}.";
            case Localization.Narrowed:
                var last = d.LastAnsweringHop!.Value;
                var tail = d.Hops.Skip(last + 1).All(h => h.Hop.Hidden)
                    ? "so the fault is beyond it or on the internet side."
                    : "so the fault is after it, but the evidence does not say exactly where.";
                return $"{name}: the internet check is failing. The last device that answered during the failures is {Address(last)}, {tail}";
            default:
                return $"{name}: the internet check is failing, but there is not enough data yet to say where.";
        }
    }

    // ----- detail panel -----

    static DetailView? DetailFor(string id, Analysed lan, Analysed phone, PathThresholds t)
    {
        var lane = id.StartsWith("lan:", StringComparison.Ordinal) ? lan : id.StartsWith("phone:", StringComparison.Ordinal) ? phone : null;
        if (lane is null) return null;
        var rest = id[(lane.Id.Length + 1)..];

        if (rest == "net")
        {
            var state = lane.View.Internet.State;
            return new DetailView(id, $"Internet via {lane.Name}", "1.1.1.1 · 8.8.8.8", InternetLabel(state), state,
                StatsOf(lane.Diagnosis.EndpointStats, t), lane.Snap.EndpointSamples,
                "A TCP connection to 1.1.1.1 (then 8.8.8.8) from this connection's own adapter. This is what decides whether the connection counts as working.");
        }

        if (rest == "incomplete")
            return new DetailView(id, "Trace incomplete", "", "Trace incomplete", HopDisplay.Hidden, [], [],
                $"The trace did not reach 1.1.1.1 within {t.MaxHops} hops, so devices further on are not listed. That says nothing about the connection: the internet check decides that.");

        var node = lane.View.Nodes.FirstOrDefault(n => n.Id == id);
        var group = lane.Groups.FirstOrDefault(g => NodeIdOf(lane, g) == id);
        if (node is null || group is null) return null;

        if (group.Hops.Count > 1 || group.Kind == NodeKind.Collapsed)
        {
            var members = string.Join("; ", group.Hops.Select(i =>
            {
                var h = lane.Diagnosis.Hops[i];
                return $"hop {h.Hop.Ttl} {(h.Hop.Hidden ? "hidden" : h.Hop.Address)} ({Label(h.Display).ToLowerInvariant()})";
            }));
            return new DetailView(id, node.Title, node.Address, node.StateLabel, node.State, [], [], members + ".");
        }

        var hop = lane.Diagnosis.Hops[group.Hops[0]];
        return new DetailView(id, node.Title, node.Address, node.StateLabel, node.State,
            StatsOf(hop.Stats, t), lane.Snap.HopSamples.Count > group.Hops[0] ? lane.Snap.HopSamples[group.Hops[0]] : [], NoteFor(hop, lane));
    }

    static string NodeIdOf(Analysed lane, Group g)
    {
        var first = lane.Diagnosis.Hops[g.Hops[0]].Hop.Ttl;
        var last = lane.Diagnosis.Hops[g.Hops[^1]].Hop.Ttl;
        return g.Hops.Count == 1 ? $"{lane.Id}:{first}" : $"{lane.Id}:{first}-{last}";
    }

    static IReadOnlyList<(string, string)> StatsOf(SampleStats s, PathThresholds t) =>
    [
        ("Latency, average", s.AvgMs is { } a ? $"{a} ms" : "—"),
        ("Best / worst", s.BestMs is { } b && s.WorstMs is { } w ? $"{b} / {w} ms" : "—"),
        ("Packets lost", s.Valid == 0 ? "—" : $"{s.LossPercent.ToString("0", Inv)}%"),
        ("Could not be measured", s.Unavailable.ToString(Inv)),
        ("Window", $"{t.Window.TotalSeconds.ToString("0", Inv)} s"),
    ];

    static string NoteFor(HopDiagnosis hop, Analysed lane) => hop.Display switch
    {
        HopDisplay.Measuring => "Not enough checks yet to judge this device.",
        HopDisplay.Unavailable => "The checks could not run on this PC, so nothing is known about this device.",
        HopDisplay.Hidden => "This device never answered the trace. Many carriers and routers do this; it is not a failure.",
        HopDisplay.Quiet => "It answers probes badly, but there is no evidence against it. Devices often ignore probes while still passing traffic.",
        HopDisplay.Suspect => "It lost some probes while the internet check was failing, but not clearly enough to call it the break.",
        HopDisplay.Broken => "Its probes were lost in the same moments the internet check failed, while the device before it kept answering.",
        HopDisplay.Silent => "It does not answer, and it is after the located break.",
        _ => hop.Hop.Ttl == 1 && lane.Id == "lan"
            ? "Your router: the first device on the way out."
            : "Answering its probes normally.",
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathMapPresenterTests"`
Expected: `Passed!  - Failed:     0, Passed:    26`

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/PathMapPresenter.cs tests/NetRoute.Core.Tests/PathMapPresenterTests.cs
git commit -m "feat(core): path map presenter with the view model and diagnosis sentences"
```

---

### Task 8: The monitor (probe loops, retracing, snapshots)

**Files:**
- Create: `src/NetRoute.Core/PathMap/PathMonitor.cs`
- Create: `tests/NetRoute.Core.Tests/PathMonitorTests.cs`

**Interfaces:**
- Consumes: `IPathProbe`, `IEndpointProbe`, `PathTracer`, `PathSamples`, `PathThresholds`, `PathTrace`, `Sample` (Tasks 1 and 3); `LaneSnapshot`, `PathSnapshot` (Task 7); the existing `AdapterInfo`; `FakePathProbe`, `FakeEndpointProbe`, `FakeTimeProvider`.
- Produces: `new PathMonitor(IPathProbe hopProbe, IEndpointProbe endpointProbe, TimeProvider time, PathThresholds thresholds, Action<string> log)`; `SetAdapters(AdapterInfo? lan, AdapterInfo? phone)`; `Task RetraceAsync(CancellationToken ct, bool force = false)`; `Task HopRoundAsync(CancellationToken ct)`; `Task EndpointRoundAsync(CancellationToken ct)`; `PathSnapshot Snapshot()`; `Task RunAsync(CancellationToken ct)` (the two loops: hops every 1 s with retracing, internet every 2 s; ends quietly when cancelled); constants `HopPeriod`, `EndpointPeriod`, `RetraceEvery` (30 s), `RetraceMinGap` (10 s).

Rules the tests pin: a sample is stored only if its lane was not reset or re-traced meanwhile; a cancelled round stores nothing; a probe that throws becomes an Unavailable sample and is logged once per distinct message; an unchanged path keeps its history across a re-trace.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/PathMonitorTests.cs`:

```cs
using Microsoft.Extensions.Time.Testing;
using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.FakePathProbe;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathMonitorTests
{
    sealed class Rig
    {
        public FakePathProbe Hops { get; } = new();
        public FakeEndpointProbe Endpoint { get; } = new();
        public FakeTimeProvider Time { get; } = new(Start);
        public List<string> Log { get; } = new();
        public PathMonitor Monitor { get; }

        public Rig()
        {
            // two devices, then the target
            Hops.ByTtl[1] = Hop("192.168.86.1");
            Hops.ByTtl[2] = Hop("192.168.1.1");
            Hops.Default = Target();
            Monitor = new PathMonitor(Hops, Endpoint, Time, T, Log.Add);
            Monitor.SetAdapters(TestAdapters.Lan(), null);
        }

        public async Task Ready()
        {
            await Monitor.RetraceAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_round_stores_one_sample_per_device_and_one_internet_sample()
    {
        var rig = new Rig();
        await rig.Ready();

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);
        var lan = rig.Monitor.Snapshot().Lan;

        Assert.Equal(TraceStatus.Complete, lan.Trace!.Status);
        Assert.Equal(2, lan.Trace.Hops.Count);
        Assert.All(lan.HopSamples, s => Assert.Equal(ProbeOutcome.Reply, Assert.Single(s).Outcome));
        Assert.Equal(ProbeOutcome.Reply, Assert.Single(lan.EndpointSamples).Outcome);
        Assert.NotNull(rig.Monitor.Snapshot().LastSampleAt);
    }

    [Fact]
    public async Task A_lane_without_an_adapter_is_not_probed()
    {
        var rig = new Rig();
        await rig.Ready();

        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);

        Assert.Null(rig.Monitor.Snapshot().Phone.Trace);
        Assert.Empty(rig.Monitor.Snapshot().Phone.EndpointSamples);
    }

    [Fact]
    public async Task A_probe_that_could_not_run_is_stored_as_unavailable_not_as_loss()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.ByTtl[1] = new EchoResult(ProbeOutcome.Unavailable);
        rig.Endpoint.Result = new EndpointResult(ProbeOutcome.Unavailable);

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);
        var lan = rig.Monitor.Snapshot().Lan;

        Assert.Equal(ProbeOutcome.Unavailable, lan.HopSamples[0][0].Outcome);
        Assert.Equal(ProbeOutcome.Unavailable, lan.EndpointSamples[0].Outcome);
        Assert.Equal(0, SampleStats.Of(lan.EndpointSamples).Lost);
    }

    [Fact]
    public async Task A_probe_that_throws_is_unavailable_and_logged_once()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Endpoint.Throw = new InvalidOperationException("boom");

        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);

        Assert.All(rig.Monitor.Snapshot().Lan.EndpointSamples, s => Assert.Equal(ProbeOutcome.Unavailable, s.Outcome));
        Assert.Single(rig.Log);
        Assert.Contains("boom", rig.Log[0]);
    }

    [Fact]
    public async Task A_cancelled_round_stores_nothing()
    {
        var rig = new Rig();
        await rig.Ready();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await rig.Monitor.HopRoundAsync(cts.Token);
        await rig.Monitor.EndpointRoundAsync(cts.Token);
        var lan = rig.Monitor.Snapshot().Lan;

        Assert.All(lan.HopSamples, s => Assert.Empty(s));
        Assert.Empty(lan.EndpointSamples);
    }

    [Fact]
    public async Task A_probe_in_flight_when_the_adapter_changes_is_dropped()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.Gate = new TaskCompletionSource();

        var round = rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Monitor.SetAdapters(TestAdapters.Lan(index: 11), null); // a different adapter: the lane is reset
        rig.Hops.Gate.SetResult();
        await round;

        var lan = rig.Monitor.Snapshot().Lan;
        Assert.Null(lan.Trace);
        Assert.Empty(lan.HopSamples);
    }

    [Fact]
    public async Task The_same_adapter_keeps_its_samples()
    {
        var rig = new Rig();
        await rig.Ready();
        await rig.Monitor.HopRoundAsync(CancellationToken.None);

        rig.Monitor.SetAdapters(TestAdapters.Lan(), null);

        Assert.Single(rig.Monitor.Snapshot().Lan.HopSamples[0]);
    }

    [Fact]
    public async Task Samples_expire_with_the_window()
    {
        var rig = new Rig();
        await rig.Ready();
        await rig.Monitor.HopRoundAsync(CancellationToken.None);

        rig.Time.Advance(TimeSpan.FromSeconds(31));

        Assert.All(rig.Monitor.Snapshot().Lan.HopSamples, s => Assert.Empty(s));
    }

    [Fact]
    public async Task A_reply_from_another_address_triggers_a_retrace_after_the_minimum_gap()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.ByTtl[2] = Hop("10.1.1.1"); // the second device changed

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.RetraceAsync(CancellationToken.None); // too soon: nothing happens
        Assert.Equal("192.168.1.1", rig.Monitor.Snapshot().Lan.Trace!.Hops[1].Address!.ToString());

        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.Equal("10.1.1.1", rig.Monitor.Snapshot().Lan.Trace!.Hops[1].Address!.ToString());
    }

    [Fact]
    public async Task An_unchanged_path_is_retraced_only_every_thirty_seconds_and_keeps_its_samples()
    {
        var rig = new Rig();
        await rig.Ready();

        rig.Time.Advance(TimeSpan.FromSeconds(20));
        await rig.Monitor.HopRoundAsync(CancellationToken.None); // a sample at 20 s, still inside the window at 31 s
        var callsBefore = rig.Hops.Calls.Count;
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.Equal(callsBefore, rig.Hops.Calls.Count); // 20 s since the trace: not due

        rig.Time.Advance(TimeSpan.FromSeconds(11));
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.True(rig.Hops.Calls.Count > callsBefore); // 31 s: due
        Assert.Single(rig.Monitor.Snapshot().Lan.HopSamples[0]); // same addresses: the history stays
    }

    [Fact]
    public async Task An_unavailable_trace_is_retried_after_the_minimum_gap()
    {
        var rig = new Rig();
        rig.Hops.Default = new EchoResult(ProbeOutcome.Unavailable);
        rig.Hops.ByTtl.Clear();
        await rig.Ready();
        Assert.Equal(TraceStatus.Unavailable, rig.Monitor.Snapshot().Lan.Trace!.Status);

        rig.Hops.Default = Target();
        rig.Hops.ByTtl[1] = Hop("192.168.86.1");
        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None);

        Assert.Equal(TraceStatus.Complete, rig.Monitor.Snapshot().Lan.Trace!.Status);
    }

    [Fact]
    public async Task Run_stops_when_cancelled()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        var run = rig.Monitor.RunAsync(cts.Token);
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task An_adapter_that_has_no_address_yet_is_not_probed()
    {
        var rig = new Rig();
        rig.Monitor.SetAdapters(TestAdapters.Lan() with { IPv4 = null }, null);

        await rig.Monitor.RetraceAsync(CancellationToken.None);
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);

        Assert.Empty(rig.Hops.Calls);
        Assert.Null(rig.Monitor.Snapshot().Lan.Trace);
    }

    [Fact]
    public async Task The_target_answering_at_a_device_ttl_means_the_path_got_shorter_and_is_retraced()
    {
        var rig = new Rig();
        await rig.Ready();
        Assert.Equal(2, rig.Monitor.Snapshot().Lan.Trace!.Hops.Count);
        rig.Hops.ByTtl[2] = Target(); // the second device is gone: the target now answers at TTL 2

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None);

        Assert.Single(rig.Monitor.Snapshot().Lan.Trace!.Hops);
    }

    [Fact]
    public async Task A_round_cancelled_while_probes_are_in_flight_stores_nothing()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.Gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var round = rig.Monitor.HopRoundAsync(cts.Token);
        cts.Cancel(); // the window closed while the probes were out
        rig.Hops.Gate.SetResult();
        await round;

        Assert.All(rig.Monitor.Snapshot().Lan.HopSamples, s => Assert.Empty(s));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathMonitorTests"`
Expected: the build FAILS with `error CS0246: The type or namespace name 'PathMonitor' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/NetRoute.Core/PathMap/PathMonitor.cs`:

```cs
using System.Net;

namespace NetRoute.Core.PathMap;

/// Probes both connections while it runs: a trace now and then, one echo per device per second, one internet check per lane
/// every two seconds. Holds only the last 30 s of samples and hands out immutable snapshots; it never touches the UI.
/// The rounds are public so tests can drive them without timers.
public sealed class PathMonitor
{
    public static readonly TimeSpan HopPeriod = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EndpointPeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RetraceEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RetraceMinGap = TimeSpan.FromSeconds(10);

    readonly IPathProbe _hopProbe;
    readonly IEndpointProbe _endpointProbe;
    readonly PathTracer _tracer;
    readonly TimeProvider _time;
    readonly PathThresholds _t;
    readonly Action<string> _log;
    readonly Lock _gate = new();
    readonly Lane _lan, _phone;
    DateTimeOffset? _lastSampleAt;
    string? _lastError;

    public PathMonitor(IPathProbe hopProbe, IEndpointProbe endpointProbe, TimeProvider time, PathThresholds thresholds, Action<string> log)
    {
        _hopProbe = hopProbe;
        _endpointProbe = endpointProbe;
        _tracer = new PathTracer(hopProbe, thresholds);
        _time = time;
        _t = thresholds;
        _log = log;
        _lan = new Lane("LAN", thresholds);
        _phone = new Lane("Phone", thresholds);
    }

    sealed class Lane(string name, PathThresholds t)
    {
        public string Name { get; } = name;
        public AdapterInfo? Adapter;
        public IPAddress? Source;
        public PathTrace? Trace;
        public List<PathSamples> Hops = [];
        public PathSamples Endpoint = new(t.Window);
        /// Bumped whenever the lane is reset, so a probe that was in flight for the old state can be recognised and dropped.
        public int Generation;
        public bool NeedsRetrace = true;
        public DateTimeOffset LastTrace = DateTimeOffset.MinValue;
    }

    /// The adapters to watch. A different adapter or address clears that lane and schedules a new trace.
    public void SetAdapters(AdapterInfo? lan, AdapterInfo? phone)
    {
        lock (_gate)
        {
            Apply(_lan, lan);
            Apply(_phone, phone);
        }
    }

    void Apply(Lane lane, AdapterInfo? adapter)
    {
        var source = adapter?.IPv4 is { } text && IPAddress.TryParse(text, out var ip) ? ip : null;
        if (adapter?.Index == lane.Adapter?.Index && Equals(source, lane.Source)) return;
        lane.Adapter = adapter;
        lane.Source = source;
        lane.Generation++;
        lane.Trace = null;
        lane.Hops = [];
        lane.Endpoint = new PathSamples(_t.Window);
        lane.NeedsRetrace = true;
        lane.LastTrace = DateTimeOffset.MinValue;
    }

    /// Traces every lane that has an address and is due. Pass force to trace regardless.
    public async Task RetraceAsync(CancellationToken ct, bool force = false)
    {
        var due = new List<(Lane Lane, int Generation, IPAddress Source)>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var lane in new[] { _lan, _phone })
                if (lane.Source is { } source && (force || RetraceDue(lane, now))) due.Add((lane, lane.Generation, source));
        }

        await Task.WhenAll(due.Select(async d =>
        {
            var trace = await Guard<PathTrace?>(async () => await _tracer.TraceAsync(d.Source, PathTracer.DefaultTarget, PathTracer.DefaultTimeout, ct), null);
            if (trace is null || ct.IsCancellationRequested) return;
            lock (_gate)
            {
                if (d.Generation != d.Lane.Generation) return;
                var sameHops = d.Lane.Trace is { } old && old.Hops.Select(h => h.Address?.ToString()).SequenceEqual(trace.Hops.Select(h => h.Address?.ToString()));
                if (!sameHops) d.Lane.Hops = trace.Hops.Select(_ => new PathSamples(_t.Window)).ToList();
                d.Lane.Trace = trace;
                d.Lane.NeedsRetrace = trace.Status == TraceStatus.Unavailable;
                d.Lane.LastTrace = _time.GetUtcNow();
            }
        }));
    }

    bool RetraceDue(Lane lane, DateTimeOffset now) =>
        lane.Trace is null || now - lane.LastTrace >= (lane.NeedsRetrace ? RetraceMinGap : RetraceEvery);

    /// One echo per non-hidden device of each traced lane, in parallel.
    public async Task HopRoundAsync(CancellationToken ct)
    {
        var work = new List<Task>();
        lock (_gate)
        {
            foreach (var lane in new[] { _lan, _phone })
            {
                if (lane.Trace is not { } trace || lane.Source is not { } source) continue;
                var generation = lane.Generation;
                var samples = lane.Hops;
                for (var i = 0; i < trace.Hops.Count && i < samples.Count; i++)
                {
                    var hop = trace.Hops[i];
                    if (hop.Hidden) continue;
                    var store = samples[i];
                    work.Add(ProbeHop(lane, generation, trace, source, hop, store, ct));
                }
            }
        }
        await Task.WhenAll(work);
    }

    async Task ProbeHop(Lane lane, int generation, PathTrace trace, IPAddress source, PathHop hop, PathSamples store, CancellationToken ct)
    {
        var result = await Guard(() => _hopProbe.EchoAsync(source, trace.Target, hop.Ttl, PathTracer.DefaultTimeout, ct),
            new EchoResult(ProbeOutcome.Unavailable));
        if (ct.IsCancellationRequested) return; // the window is closing: a cancelled check is discarded, not stored
        lock (_gate)
        {
            if (generation != lane.Generation || !ReferenceEquals(lane.Trace, trace)) return; // the lane was reset or re-traced meanwhile
            var now = _time.GetUtcNow();
            store.Add(new Sample(now, result.Outcome, result.RttMs));
            _lastSampleAt = now;
            if (result.Outcome == ProbeOutcome.Reply && PathChanged(trace, hop, result)) lane.NeedsRetrace = true;
        }
    }

    /// A reply from another address, from the target at a TTL that used to be a device, or from a hop that used to be silent.
    static bool PathChanged(PathTrace trace, PathHop hop, EchoResult result) =>
        result.TargetReached || hop.Address is null || !Equals(result.From, hop.Address);

    /// One internet check per connected lane.
    public async Task EndpointRoundAsync(CancellationToken ct)
    {
        var work = new List<Task>();
        lock (_gate)
        {
            foreach (var lane in new[] { _lan, _phone })
            {
                if (lane.Source is not { } source) continue;
                var generation = lane.Generation;
                var store = lane.Endpoint;
                work.Add(CheckEndpoint(lane, generation, source, store, ct));
            }
        }
        await Task.WhenAll(work);
    }

    async Task CheckEndpoint(Lane lane, int generation, IPAddress source, PathSamples store, CancellationToken ct)
    {
        var result = await Guard(() => _endpointProbe.CheckAsync(source, ct), new EndpointResult(ProbeOutcome.Unavailable));
        if (ct.IsCancellationRequested) return;
        lock (_gate)
        {
            if (generation != lane.Generation) return;
            var now = _time.GetUtcNow();
            store.Add(new Sample(now, result.Outcome, result.RttMs));
            _lastSampleAt = now;
        }
    }

    /// A probe must not throw for a failed measurement; if one does, the sample is Unavailable (never loss) and the problem is logged once.
    async Task<T> Guard<T>(Func<Task<T>> probe, T onError)
    {
        try { return await probe(); }
        catch (OperationCanceledException) { return onError; }
        catch (Exception ex)
        {
            if (_lastError != ex.Message)
            {
                _lastError = ex.Message;
                _log($"Path map: a check failed to run: {ex.Message}");
            }
            return onError;
        }
    }

    public PathSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return new PathSnapshot(now, Of(_lan, now), Of(_phone, now), _lastSampleAt);
        }
    }

    LaneSnapshot Of(Lane lane, DateTimeOffset now)
    {
        var hopSamples = lane.Trace is { } trace
            ? trace.Hops.Select((_, i) => i < lane.Hops.Count ? lane.Hops[i].Snapshot(now) : []).ToList()
            : [];
        return new LaneSnapshot(lane.Name, lane.Adapter, lane.Trace, hopSamples, lane.Endpoint.Snapshot(now));
    }

    /// Runs both loops until cancelled. The hop loop also retraces when due; the internet check has its own loop because it can take longer than a second.
    public async Task RunAsync(CancellationToken ct)
    {
        try { await Task.WhenAll(HopLoop(ct), EndpointLoop(ct)); }
        catch (OperationCanceledException) { }
    }

    async Task HopLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RetraceAsync(ct);
            await HopRoundAsync(ct);
            await Task.Delay(HopPeriod, _time, ct);
        }
    }

    async Task EndpointLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await EndpointRoundAsync(ct);
            await Task.Delay(EndpointPeriod, _time, ct);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~PathMonitorTests"`
Expected: `Passed!  - Failed:     0, Passed:    15`

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/PathMonitor.cs tests/NetRoute.Core.Tests/PathMonitorTests.cs
git commit -m "feat(core): path monitor with probe loops, retracing and immutable snapshots"
```

---

### Task 9: The window and the canvas (drawing)

**Files:**
- Modify: `src/NetRoute.App/Theme.cs` (new palette keys)
- Create: `src/NetRoute.App/PathMapCanvas.cs`
- Create: `src/NetRoute.App/PathMapWindow.xaml`
- Create: `src/NetRoute.App/PathMapWindow.xaml.cs`

**Interfaces:**
- Consumes: `PathMapView`, `LaneView`, `NodeView`, `InternetRowView`, `DetailView`, `LinkState`, `NodeKind`, `HopDisplay`, `Sample`, `ProbeOutcome` (Tasks 1, 4, 7); the existing `Theme.Track(Window)`.
- Produces: `public partial class PathMapWindow : Window` with constructor `PathMapWindow(Func<string?, PathMapView> present)` (the argument is called every 500 ms and on selection with the selected id); internal `PathMapCanvas.Render(PathMapView, string? selectedId)`, `Paused`, `NodeClicked`.

There is no automated test for drawing. The verification is a build, then rendering the real window to PNG for scenarios and checking them by eye (Step 4).

- [ ] **Step 1: Add the palette keys**

In `src/NetRoute.App/Theme.cs`, inside the `Palette` dictionary, directly after the `["DotGray"] = ...` line, add:

```csharp
        ["DotRed"] = ("#FFCF222E", "#FFF85149"),
        ["DotMeasuring"] = ("#FF6E8CB4", "#FF7D9CC8"),
        ["NodeBackground"] = ("#FFFFFFFF", "#FF272727"),
        ["NodeBorder"] = ("#38000000", "#3DFFFFFF"),
        ["Track"] = ("#14000000", "#12FFFFFF"),
```

- [ ] **Step 2: Create the canvas**

`src/NetRoute.App/PathMapCanvas.cs`:

```cs
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// Draws the path map from a view model on a fixed 980 x 380 design canvas (the window scales it with a Viewbox).
/// The shapes are rebuilt on every Render (a few dozen, twice a second). The flowing dashes and pulses are driven by the
/// render clock, not by Render, so they stay smooth across rebuilds and keep running while data is unchanged.
sealed class PathMapCanvas : Canvas
{
    public const double CanvasWidth = 980, CanvasHeight = 380;
    const double NodeW = 108, NodeH = 66, PcW = 132, PcH = 84, NetW = 132, NetH = 96;
    const double LanY = 108, PhoneY = 292, MidY = 190, MinGap = 22;
    const double DashPeriodUnits = 4.667; // one dash + gap in stroke-thickness units, for the flow speed

    readonly List<(Shape Shape, double Speed)> _flows = [];
    readonly List<Shape> _halos = [];
    readonly List<Ellipse> _rings = [];
    double _phase;
    TimeSpan _lastFrame;

    public PathMapCanvas()
    {
        Width = CanvasWidth;
        Height = CanvasHeight;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    /// Freezes the animations (the user's Pause button).
    public bool Paused { get; set; }

    public event Action<string>? NodeClicked;

    public void Render(PathMapView view, string? selectedId)
    {
        Children.Clear();
        _flows.Clear();
        _halos.Clear();
        _rings.Clear();

        var pc = new Rect(20, MidY - PcH / 2, PcW, PcH);
        var net = new Rect(CanvasWidth - 12 - NetW, MidY - NetH / 2, NetW, NetH);
        DrawLane(view.Lan, LanY, pc, net, selectedId);
        DrawLane(view.Phone, PhoneY, pc, net, selectedId);
        DrawPc(pc, view);
        DrawInternet(net, view, selectedId);
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (Paused || !SystemParameters.ClientAreaAnimation) return;
        _phase += Math.Clamp(dt, 0, 0.25);

        foreach (var (shape, speed) in _flows) shape.StrokeDashOffset = -_phase * DashPeriodUnits * speed;
        var pulse = 0.5 + 0.5 * Math.Sin(_phase * 3.5);
        foreach (var halo in _halos) halo.Opacity = 0.12 + 0.4 * pulse;
        var t = (_phase * 0.6) % 1.0;
        foreach (var ring in _rings)
        {
            ring.Opacity = 0.8 * (1 - t);
            ring.RenderTransform = new ScaleTransform(1 + 1.4 * t, 1 + 1.4 * t, 9, 9);
        }
    }

    Brush B(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    // ----- lanes -----

    void DrawLane(LaneView lane, double y, Rect pc, Rect net, string? selectedId)
    {
        Text(lane.Caption.ToUpperInvariant(), 10.5, FontWeights.SemiBold, "TextSecondary", pc.Right + 50, y - NodeH / 2 - 22);
        if (!lane.Connected)
        {
            Text("Not connected", 12, FontWeights.Normal, "TextSecondary", pc.Right + 60, y - 8);
            return;
        }

        var n = lane.Nodes.Count;
        var available = net.Left - pc.Right;
        var width = Math.Min(NodeW, (available - (n + 1) * MinGap) / Math.Max(n, 1));
        var gap = (available - n * width) / (n + 1);
        var rects = Enumerable.Range(0, n)
            .Select(i => new Rect(pc.Right + gap * (i + 1) + width * i, y - NodeH / 2, width, NodeH)).ToList();

        var from = new Point(pc.Right, MidY);
        for (var i = 0; i < n; i++)
        {
            Link(from, new Point(rects[i].Left, y), lane.Links[i]);
            from = new Point(rects[i].Right, y);
        }
        Link(from, new Point(net.Left, MidY), lane.Links[n]);

        var pulseId = lane.Nodes.FirstOrDefault(x => x.State is HopDisplay.Broken or HopDisplay.Suspect)?.Id;
        for (var i = 0; i < n; i++) DrawNode(lane.Nodes[i], rects[i], selectedId, pulse: lane.Nodes[i].Id == pulseId);
    }

    void Link(Point a, Point b, LinkState state)
    {
        var figure = new PathFigure { StartPoint = a };
        if (Math.Abs(a.Y - b.Y) < 0.5) figure.Segments.Add(new LineSegment(b, true));
        else
        {
            var dx = (b.X - a.X) / 2;
            figure.Segments.Add(new BezierSegment(new Point(a.X + dx, a.Y), new Point(b.X - dx, b.Y), b, true));
        }
        var geometry = new PathGeometry([figure]);

        Children.Add(new Path { Data = geometry, Stroke = B("Track"), StrokeThickness = 9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

        var (brush, dashes, opacity, flow) = state switch
        {
            LinkState.Healthy => ("DotGreen", new[] { 2.0, 2.667 }, 1.0, 1.0),
            LinkState.Suspect => ("DotAmber", new[] { 2.0, 2.667 }, 1.0, 0.5),
            LinkState.Broken => ("DotRed", new[] { 0.7, 2.3 }, 1.0, 0.0),
            LinkState.Measuring => ("DotMeasuring", new[] { 2.0, 2.667 }, 0.9, 0.35),
            LinkState.Unavailable => ("DotGray", new[] { 0.4, 1.6 }, 0.55, 0.0),
            LinkState.Muted => ("DotGray", new[] { 0.7, 2.7 }, 0.45, 0.0),
            _ => ("DotGray", new[] { 1.0, 3.0 }, 0.6, 0.0),
        };
        var line = new Path { Data = geometry, Stroke = B(brush), StrokeThickness = 3, StrokeDashArray = new DoubleCollection(dashes), Opacity = opacity };
        Children.Add(line);
        if (flow > 0) _flows.Add((line, flow));

        if (state == LinkState.Broken) DrawCut(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2));
    }

    /// The red ✕ on a broken link, with a ring that pulses outwards.
    void DrawCut(Point centre)
    {
        var ring = new Ellipse { Width = 18, Height = 18, Stroke = B("DotRed"), StrokeThickness = 2, IsHitTestVisible = false };
        SetLeft(ring, centre.X - 9);
        SetTop(ring, centre.Y - 9);
        Children.Add(ring);
        _rings.Add(ring);

        var disc = new Ellipse { Width = 18, Height = 18, Fill = B("NodeBackground"), Stroke = B("DotRed"), StrokeThickness = 2 };
        SetLeft(disc, centre.X - 9);
        SetTop(disc, centre.Y - 9);
        Children.Add(disc);

        var cross = new Path
        {
            Data = Geometry.Parse($"M{centre.X - 3.5},{centre.Y - 3.5} L{centre.X + 3.5},{centre.Y + 3.5} M{centre.X + 3.5},{centre.Y - 3.5} L{centre.X - 3.5},{centre.Y + 3.5}"),
            Stroke = B("DotRed"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        Children.Add(cross);
    }

    // ----- boxes -----

    void DrawNode(NodeView node, Rect r, string? selectedId, bool pulse)
    {
        var selected = node.Id == selectedId;
        var dashed = node.State is HopDisplay.Quiet or HopDisplay.Hidden or HopDisplay.Silent or HopDisplay.Unavailable
                     || node.Kind == NodeKind.TraceIncomplete;

        if (pulse)
        {
            var halo = new Rectangle
            {
                Width = r.Width + 6, Height = r.Height + 6, RadiusX = 13, RadiusY = 13, Opacity = 0.3,
                Stroke = B(StateBrush(node.State)), StrokeThickness = 5, IsHitTestVisible = false,
            };
            SetLeft(halo, r.Left - 3);
            SetTop(halo, r.Top - 3);
            Children.Add(halo);
            _halos.Add(halo);
        }

        var box = new Grid { Width = r.Width, Height = r.Height, Cursor = Cursors.Hand, Focusable = true, Background = Brushes.Transparent };
        box.Children.Add(new Rectangle
        {
            RadiusX = 10, RadiusY = 10, Fill = B("NodeBackground"),
            Stroke = selected ? B("Accent") : node.State is HopDisplay.Answering ? B("NodeBorder") : B(StateBrush(node.State)),
            StrokeThickness = selected ? 2 : 1,
            StrokeDashArray = dashed ? new DoubleCollection([4.0, 3.0]) : null,
        });

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(StateBrush(node.State)), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = node.Title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });

        var showMetric = node.State is HopDisplay.Answering or HopDisplay.Suspect or HopDisplay.Broken && node.Metric.Length > 0;
        var panel = new StackPanel { Margin = new Thickness(11, 9, 8, 6) };
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = node.Address, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10.5, Foreground = B("TextSecondary"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock
        {
            Text = showMetric ? node.Metric : node.StateLabel, FontSize = 10.5, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 3, 0, 0),
            Foreground = B(node.State switch { HopDisplay.Suspect => "DotAmber", HopDisplay.Broken => "DotRed", _ => "TextSecondary" }),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        box.Children.Add(panel);

        Clickable(box, node.Id, $"{node.Title}, {node.Address}, {node.StateLabel}");
        box.ToolTip = node.StateLabel;
        SetLeft(box, r.Left);
        SetTop(box, r.Top);
        Children.Add(box);
    }

    void DrawPc(Rect r, PathMapView view)
    {
        var box = new Grid { Width = r.Width, Height = r.Height };
        box.Children.Add(new Rectangle { RadiusX = 12, RadiusY = 12, Fill = B("NodeBackground"), Stroke = B("NodeBorder"), StrokeThickness = 1 });
        var panel = new StackPanel { Margin = new Thickness(12, 10, 8, 6) };
        panel.Children.Add(new TextBlock { Text = "This PC", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary") });
        panel.Children.Add(PcRow("LAN · Ethernet", view.Lan.Connected ? view.Lan.Internet.State : HopDisplay.Hidden, 8));
        panel.Children.Add(PcRow("Phone · Ethernet 5", view.Phone.Connected ? view.Phone.Internet.State : HopDisplay.Hidden, 5));
        box.Children.Add(panel);
        SetLeft(box, r.Left);
        SetTop(box, r.Top);
        Children.Add(box);
    }

    StackPanel PcRow(string text, HopDisplay state, double top)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) };
        row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(StateBrush(state)), Margin = new Thickness(0, 0, 7, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 10.5, Foreground = B("TextSecondary") });
        return row;
    }

    void DrawInternet(Rect r, PathMapView view, string? selectedId)
    {
        var box = new Grid { Width = r.Width, Height = r.Height };
        box.Children.Add(new Rectangle { RadiusX = 12, RadiusY = 12, Fill = B("NodeBackground"), Stroke = B("NodeBorder"), StrokeThickness = 1 });
        var panel = new StackPanel { Margin = new Thickness(12, 10, 6, 6) };
        panel.Children.Add(new TextBlock { Text = "Internet", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary") });
        panel.Children.Add(new TextBlock { Text = "1.1.1.1 · 8.8.8.8", FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10.5, Foreground = B("TextSecondary"), Margin = new Thickness(0, 2, 0, 4) });
        foreach (var row in new[] { view.Lan.Internet, view.Phone.Internet }) panel.Children.Add(InternetRow(row, selectedId));
        box.Children.Add(panel);
        SetLeft(box, r.Left);
        SetTop(box, r.Top);
        Children.Add(box);
    }

    Grid InternetRow(InternetRowView row, string? selectedId)
    {
        var grid = new Grid { Height = 22, Cursor = Cursors.Hand, Focusable = true, Background = row.Id == selectedId ? B("Track") : Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Ellipse { Width = 8, Height = 8, Fill = B(StateBrush(row.State)), HorizontalAlignment = HorizontalAlignment.Left };
        var name = new TextBlock { Text = row.Label, FontSize = 11.5, Foreground = B("TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
        var metric = new TextBlock
        {
            Text = row.Metric, FontSize = 10.5, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = B(row.State switch { HopDisplay.Suspect => "DotAmber", HopDisplay.Broken => "DotRed", _ => "TextSecondary" }),
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(metric, 2);
        grid.Children.Add(dot);
        grid.Children.Add(name);
        grid.Children.Add(metric);
        Clickable(grid, row.Id, $"Internet via {row.Label}, {row.Metric}");
        return grid;
    }

    // ----- helpers -----

    void Clickable(FrameworkElement element, string id, string name)
    {
        AutomationProperties.SetName(element, name);
        element.MouseLeftButtonUp += (_, _) => NodeClicked?.Invoke(id);
        element.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { e.Handled = true; NodeClicked?.Invoke(id); }
        };
    }

    void Text(string text, double size, FontWeight weight, string brush, double x, double y)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = B(brush), IsHitTestVisible = false };
        SetLeft(block, x);
        SetTop(block, y);
        Children.Add(block);
    }

    static string StateBrush(HopDisplay state) => state switch
    {
        HopDisplay.Answering => "DotGreen",
        HopDisplay.Suspect => "DotAmber",
        HopDisplay.Broken => "DotRed",
        HopDisplay.Measuring => "DotMeasuring",
        _ => "DotGray",
    };
}
```

- [ ] **Step 3: Create the window**

`src/NetRoute.App/PathMapWindow.xaml`:

```xaml
<Window x:Class="NetRoute.App.PathMapWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:NetRoute.App"
        Icon="Assets/NetRouteWidget.ico"
        Title="NetRoute — Network path" Width="1020" Height="780" MinWidth="720" MinHeight="600"
        WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" FontSize="13" ThemeMode="System"
        Background="{DynamicResource WindowBackground}" Foreground="{DynamicResource TextPrimary}">
    <DockPanel Margin="16">
        <StackPanel DockPanel.Dock="Top">
            <TextBlock x:Name="Headline" FontSize="16" FontWeight="SemiBold" TextWrapping="Wrap" />
            <StackPanel x:Name="Diagnosis" Margin="0,6,0,0" />
        </StackPanel>

        <DockPanel DockPanel.Dock="Bottom" Margin="0,12,0,0">
            <Button x:Name="PauseButton" DockPanel.Dock="Right" Content="Pause" Padding="12,3" Click="OnPause" />
            <TextBlock DockPanel.Dock="Right" x:Name="Footer" Margin="0,0,16,0" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" />
            <TextBlock VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap">
                <Run Foreground="{DynamicResource DotGreen}" Text="●" /> Answering
                <Run Foreground="{DynamicResource DotAmber}" Text="●" /> Losing packets
                <Run Foreground="{DynamicResource DotRed}" Text="●" /> No reply
                <Run Foreground="{DynamicResource DotGray}" Text="●" /> Ignores probes, hidden or unavailable
                <Run Foreground="{DynamicResource DotMeasuring}" Text="●" /> Measuring
            </TextBlock>
        </DockPanel>

        <Border DockPanel.Dock="Bottom" Margin="0,12,0,0" Padding="14,10" CornerRadius="8" BorderThickness="1"
                Background="{DynamicResource CardBackground}" BorderBrush="{DynamicResource CardBorder}">
            <StackPanel>
                <TextBlock x:Name="DetailHint" Text="Click a device on the map to see its details." Foreground="{DynamicResource TextSecondary}" />
                <StackPanel x:Name="DetailBody" Visibility="Collapsed">
                    <DockPanel>
                        <Border x:Name="DetailChipBox" DockPanel.Dock="Right" BorderThickness="1" CornerRadius="10" Padding="9,1" VerticalAlignment="Top">
                            <TextBlock x:Name="DetailChip" FontSize="11.5" FontWeight="SemiBold" />
                        </Border>
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="DetailTitle" FontSize="14" FontWeight="SemiBold" />
                            <TextBlock x:Name="DetailAddress" Margin="10,2,0,0" FontFamily="Cascadia Mono, Consolas" FontSize="12" Foreground="{DynamicResource TextSecondary}" />
                        </StackPanel>
                    </DockPanel>
                    <WrapPanel x:Name="DetailStats" Margin="0,8,0,0" />
                    <Canvas x:Name="Spark" Width="320" Height="56" HorizontalAlignment="Left" Margin="0,8,0,0" />
                    <TextBlock x:Name="DetailNote" Margin="0,6,0,0" TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}" />
                </StackPanel>
            </StackPanel>
        </Border>

        <Border Margin="0,12,0,0" CornerRadius="8" BorderThickness="1" Background="{DynamicResource CardBackground}" BorderBrush="{DynamicResource CardBorder}">
            <Viewbox Stretch="Uniform" Margin="6">
                <local:PathMapCanvas x:Name="Map" />
            </Viewbox>
        </Border>
    </DockPanel>
</Window>
```

`src/NetRoute.App/PathMapWindow.xaml.cs`:

```cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// The network path window. It owns only the screen: a timer asks for the latest view every 500 ms (the probes run on their own
/// schedule inside the PathMonitor), and clicking a device selects it for the detail panel.
public partial class PathMapWindow : Window
{
    readonly Func<string?, PathMapView> _present;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    string? _selected;
    bool _paused;
    string _diagnosisKey = "";

    public PathMapWindow(Func<string?, PathMapView> present)
    {
        InitializeComponent();
        Theme.Track(this);
        _present = present;
        Map.NodeClicked += id =>
        {
            _selected = id;
            Refresh();
        };
        _timer.Tick += (_, _) =>
        {
            if (!_paused) Refresh();
        };
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    void Refresh()
    {
        var view = _present(_selected);
        // With nothing selected yet, show the first device that looks like trouble.
        if (_selected is null && FirstProblem(view) is { } id)
        {
            _selected = id;
            view = _present(id);
        }

        Headline.Text = "🌐 " + view.Headline;
        Headline.SetResourceReference(TextBlock.ForegroundProperty, view.HeadlineWarning ? "Warning" : "TextPrimary");
        Footer.Text = view.FooterText;
        RenderDiagnosis(view.Diagnosis);
        Map.Render(view, _selected);
        RenderDetail(view.Detail);
    }

    static string? FirstProblem(PathMapView view) =>
        view.Lan.Nodes.Concat(view.Phone.Nodes)
            .FirstOrDefault(n => n.State is HopDisplay.Broken or HopDisplay.Suspect)?.Id;

    void RenderDiagnosis(IReadOnlyList<string> lines)
    {
        var key = string.Join("\n", lines);
        if (key == _diagnosisKey) return;
        _diagnosisKey = key;
        Diagnosis.Children.Clear();
        foreach (var line in lines)
            Diagnosis.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
    }

    void RenderDetail(DetailView? detail)
    {
        DetailHint.Visibility = detail is null ? Visibility.Visible : Visibility.Collapsed;
        DetailBody.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        if (detail is null) return;

        DetailTitle.Text = detail.Title;
        DetailAddress.Text = detail.Address;
        DetailChip.Text = detail.Chip;
        var brush = detail.State switch
        {
            HopDisplay.Answering => "DotGreen",
            HopDisplay.Suspect => "DotAmber",
            HopDisplay.Broken => "DotRed",
            HopDisplay.Measuring => "DotMeasuring",
            _ => "DotGray",
        };
        DetailChip.SetResourceReference(TextBlock.ForegroundProperty, brush);
        DetailChipBox.SetResourceReference(Border.BorderBrushProperty, brush);

        DetailStats.Children.Clear();
        foreach (var (label, value) in detail.Stats)
        {
            var stat = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            var caption = new TextBlock { Text = label, FontSize = 11.5 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stat.Children.Add(caption);
            stat.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold });
            DetailStats.Children.Add(stat);
        }

        DrawSpark(detail.Series);
        Spark.Visibility = detail.Series.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailNote.Text = detail.Note;
    }

    /// Latency over the window. A lost check is a red tick on the baseline; a check that could not be measured is a grey one.
    void DrawSpark(IReadOnlyList<Sample> series)
    {
        Spark.Children.Clear();
        if (series.Count == 0) return;
        Brush Resource(string key) => (Brush)(TryFindResource(key) ?? Brushes.Gray);

        var values = series.Where(s => s.Outcome == ProbeOutcome.Reply && s.RttMs is not null).Select(s => s.RttMs!.Value).ToList();
        var max = Math.Max(1, values.Count == 0 ? 1 : values.Max()) * 1.15;
        var step = series.Count > 1 ? (Spark.Width - 10) / (series.Count - 1) : 0;
        Spark.Children.Add(new Line { X1 = 0, X2 = Spark.Width, Y1 = 44, Y2 = 44, Stroke = Resource("CardBorder"), StrokeThickness = 1 });

        var points = new PointCollection();
        void Flush()
        {
            if (points.Count > 1) Spark.Children.Add(new Polyline { Points = points, Stroke = Resource("Accent"), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
            else if (points.Count == 1) Spark.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = Resource("Accent"), Margin = new Thickness(points[0].X - 1.5, points[0].Y - 1.5, 0, 0) });
            points = new PointCollection();
        }

        for (var i = 0; i < series.Count; i++)
        {
            var x = 5 + i * step;
            switch (series[i].Outcome)
            {
                case ProbeOutcome.Reply:
                    points.Add(new Point(x, 44 - (series[i].RttMs ?? 0) / max * 38));
                    break;
                default:
                    Flush();
                    Spark.Children.Add(new Line
                    {
                        X1 = x, X2 = x, Y1 = 46, Y2 = 54, StrokeThickness = 2,
                        Stroke = Resource(series[i].Outcome == ProbeOutcome.NoReply ? "DotRed" : "DotGray"),
                    });
                    break;
            }
        }
        Flush();
    }

    void OnPause(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        Map.Paused = _paused;
        PauseButton.Content = _paused ? "Resume" : "Pause";
        if (!_paused) Refresh();
    }
}
```

Run: `dotnet build src/NetRoute.App`
Expected: `Build succeeded.` with 0 warnings and 0 errors.

- [ ] **Step 4: Render the window and check it by eye (throwaway harness, not committed)**

Create a folder **outside the repository** (for example `shot` inside the session scratchpad) with these two files.

`Shot.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <NoWarn>$(NoWarn);WPF0001</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$(Repo)\src\NetRoute.App\NetRoute.App.csproj" />
  </ItemGroup>
</Project>
```

`Program.cs` (it builds six synthetic situations, renders the real window for each and saves a PNG):

```cs
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetRoute.App;
using NetRoute.Core;
using NetRoute.Core.PathMap;

static class Program
{
    static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = Start.AddSeconds(30);
    static readonly PathThresholds T = PathThresholds.Default;
    static IPAddress Ip(string s) => IPAddress.Parse(s);

    static List<Sample> Every(int step, Func<int, ProbeOutcome> f, int rtt = 20, int from = 0)
    {
        var l = new List<Sample>();
        for (var s = from; s < 30; s += step) { var o = f(s); l.Add(new Sample(Start.AddSeconds(s), o, o == ProbeOutcome.Reply ? rtt + s % 3 : null)); }
        return l;
    }
    static ProbeOutcome R(int _) => ProbeOutcome.Reply;
    static ProbeOutcome L(int _) => ProbeOutcome.NoReply;
    static Func<int, ProbeOutcome> LostFrom(int a) => s => s >= a ? ProbeOutcome.NoReply : ProbeOutcome.Reply;

    static AdapterInfo LanA => new(10, "Ethernet", "Realtek", AdapterKind.Ethernet, true, true, "192.168.86.42", "AA");
    static AdapterInfo PhoneA => new(31, "Ethernet 5", "SAMSUNG Mobile USB Remote NDIS Network Device", AdapterKind.Ethernet, true, true, "192.168.42.11", "BB");

    static PathTrace Trace(TraceStatus st, params string?[] h) => new(Ip("192.168.86.42"), Ip("1.1.1.1"),
        h.Select((a, i) => new PathHop(i + 1, a is null ? null : Ip(a))).ToList(), st);

    static LaneSnapshot Lane(string name, AdapterInfo? a, PathTrace? t, List<Sample> ep, params List<Sample>[] hops) =>
        new(name, a, t, hops.Select(x => (IReadOnlyList<Sample>)x).ToList(), ep);

    static LaneSnapshot HealthyPhone() => Lane("Phone", PhoneA, Trace(TraceStatus.Complete, "192.168.42.129", "172.26.220.1", "172.26.242.30"),
        Every(2, R, 26), Every(1, R, 2), Every(1, R, 26), Every(1, R, 14));

    static LaneSnapshot HealthyLan() => Lane("LAN", LanA, Trace(TraceStatus.Complete, "192.168.86.1", "192.168.1.1", "172.17.18.1", "10.9.8.249"),
        Every(2, R, 52), Every(1, R, 1), Every(1, R, 3), Every(1, R, 12), Every(1, R, 50));

    static LaneSnapshot LocatedLan() => Lane("LAN", LanA, Trace(TraceStatus.Complete, "192.168.86.1", "192.168.1.1", "172.17.18.1", "10.9.8.249"),
        Every(2, LostFrom(14), 52), Every(1, R, 1), Every(1, R, 3), Every(1, LostFrom(14), 12), Every(1, LostFrom(14), 50));

    static NetworkStatus Status(bool fallback) => new(RoutingMode.Phone,
        new DetectionResult(PhoneA, DetectionIssue.None, LanA, DetectionIssue.None),
        InternetPath.Phone, InternetPath.None, 38, fallback ? null : 12, true, null);

    [STAThread]
    static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var outDir = args[0];
        var scenarios = new (string Name, LaneSnapshot Lan, LaneSnapshot Phone, string? Sel, bool Fallback)[]
        {
            ("healthy", HealthyLan(), HealthyPhone(), "lan:2", false),
            ("located", LocatedLan(), HealthyPhone(), null, true),
            ("narrowed_quiet", Lane("LAN", LanA, Trace(TraceStatus.Complete, "192.168.86.1", "192.168.1.1", "172.17.18.1", "10.9.8.249"),
                Every(2, LostFrom(14), 52), Every(1, R, 1), Every(1, L, 3), Every(1, R, 12), Every(1, LostFrom(14), 50)), HealthyPhone(), null, true),
            ("measuring", Lane("LAN", LanA, Trace(TraceStatus.Complete, "192.168.86.1", "192.168.1.1", "172.17.18.1"),
                Every(2, R).Take(2).ToList(), Every(1, R).Take(3).ToList(), Every(1, R).Take(3).ToList(), Every(1, R).Take(3).ToList()),
                Lane("Phone", null, null, []), null, false),
            ("incomplete_hidden_collapsed", Lane("LAN", LanA, Trace(TraceStatus.Incomplete, "192.168.86.1", null, null, "172.17.18.1", "10.9.8.249", "10.9.8.250", "10.9.8.251"),
                Every(2, R, 52), Every(1, R, 1), [], [], Every(1, R, 12), Every(1, R, 50), Every(1, R, 60), Every(1, R, 70)), HealthyPhone(), "lan:incomplete", false),
            ("unavailable", Lane("LAN", LanA, Trace(TraceStatus.Complete, "192.168.86.1", "192.168.1.1"),
                Every(2, _ => ProbeOutcome.Unavailable), Every(1, R, 1), Every(1, R, 3)), HealthyPhone(), "lan:net", false),
        };

        foreach (var s in scenarios)
        {
            var status = Status(s.Fallback);
            var window = new PathMapWindow(sel => PathMapPresenter.Present(status, new PathSnapshot(Now, s.Lan, s.Phone, Now.AddSeconds(-1)), sel, T))
            { Left = 0, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            Pump(900);
            var rtb = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(window);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.Combine(outDir, s.Name + ".png"))) enc.Save(fs);
            window.Close();
        }
        return 0;
    }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
```

Run (from that folder, with an existing output folder): `dotnet run -p:Repo="F:\source\petrofahed\NetRouteWidget" -- "<output folder>"`
Expected: six PNGs. Open each and check:
- `healthy.png`: all links green, "Both connections reach the internet.", the detail panel shows `Hop 2` with a sparkline.
- `located.png`: a red ✕ on the link into `Hop 3`, `Hop 3` red with a pulsing ring around it, `Hop 4` grey dashed "After the break", the LAN row of the Internet card says "no reply", the Phone lane green, and the first diagnosis line reads "LAN: the break is between 192.168.1.1 and 172.17.18.1. While the internet check failed, 192.168.1.1 answered and 172.17.18.1 did not answer."
- `narrowed_quiet.png`: `Hop 2` dashed grey "Ignores probes" with its link **not** red, the break placed after `Hop 3`.
- `measuring.png`: blue-grey boxes and links, "LAN: measuring… 4 of 10 s.", the phone lane drawn as "Not connected".
- `incomplete_hidden_collapsed.png`: a dashed "Hops 2–3 / Hidden hop" box, "+3 more", and a dashed "Not traced / Trace incomplete" box; both connections still reported as reaching the internet.
- `unavailable.png`: "LAN: measurement unavailable on this PC. That says nothing about the connection.", the LAN row of the Internet card reads "unavailable" in grey.

If a label is clipped or a box overlaps, fix `PathMapCanvas.cs` sizes (`NodeW`, `PcW`, `NetW`) and re-render. Do not commit the harness.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.App/Theme.cs src/NetRoute.App/PathMapCanvas.cs src/NetRoute.App/PathMapWindow.xaml src/NetRoute.App/PathMapWindow.xaml.cs
git commit -m "feat(app): network path window and canvas"
```

---

### Task 10: Card entry points and wiring

**Files:**
- Modify: `src/NetRoute.App/CardWindow.xaml`
- Modify: `src/NetRoute.App/CardWindow.xaml.cs`
- Modify: `src/NetRoute.App/App.xaml.cs`

**Interfaces:**
- Consumes: `PathMapWindow(Func<string?, PathMapView>)` (Task 9); `PathMonitor`, `PathThresholds`, `PathMapPresenter`, `IcmpPathProbe`, `TcpEndpointProbe` (Tasks 1, 5–8); the existing `RouteController.Status` (`NetworkStatus.Adapters.Lan/Phone`) and `FileLog.Info`.
- Produces: `CardWindow.PathMapRequested` event; `App.OpenPathMap()`.

The card's header text opens the window (a press and release without moving the card counts as a click, because the header is also the card's drag handle), and a new drawn network icon next to ⏱ and ⚙ does the same.

- [ ] **Step 1: Make the header a link and add the footer icon (`CardWindow.xaml`)**

Replace the header line

```xml
                <TextBlock x:Name="Header" FontSize="13" FontWeight="SemiBold" Style="{StaticResource Primary}" TextTrimming="CharacterEllipsis" />
```

with

```xml
                <TextBlock x:Name="Header" FontSize="13" FontWeight="SemiBold" Style="{StaticResource Primary}" TextTrimming="CharacterEllipsis"
                           Cursor="Hand" ToolTip="Open the network path map: see where the connection breaks">
                    <TextBlock.TextDecorations>
                        <!-- A dotted underline marks the status line as a link. A constant brush: dynamic resources do not work inside a Pen here. -->
                        <TextDecoration Location="Underline" PenOffset="3">
                            <TextDecoration.Pen>
                                <Pen Brush="#80808080" Thickness="1"><Pen.DashStyle><DashStyle Dashes="1,2" /></Pen.DashStyle></Pen>
                            </TextDecoration.Pen>
                        </TextDecoration>
                    </TextBlock.TextDecorations>
                </TextBlock>
```

Then, directly after the `⚙` button

```xml
                <Button DockPanel.Dock="Right" Content="⚙" Style="{StaticResource IconButton}" Click="OnSmartSettings"
                        ToolTip="Smart routing: choose what stays off 4G" />
```

add the new icon button (right-docked buttons stack leftwards, so this lands left of ⚙):

```xml
                <Button DockPanel.Dock="Right" Style="{StaticResource IconButton}" Click="OnPathMap"
                        ToolTip="Network path: see where the connection breaks">
                    <Viewbox Width="14" Height="14">
                        <Path Width="16" Height="16" Fill="Transparent" StrokeThickness="1.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                              Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
                              Data="M4.8,8 A1.8,1.8 0 1 1 1.2,8 A1.8,1.8 0 1 1 4.8,8 M9.8,3.5 A1.8,1.8 0 1 1 6.2,3.5 A1.8,1.8 0 1 1 9.8,3.5 M9.8,12.5 A1.8,1.8 0 1 1 6.2,12.5 A1.8,1.8 0 1 1 9.8,12.5 M14.8,8 A1.8,1.8 0 1 1 11.2,8 A1.8,1.8 0 1 1 14.8,8 M4.6,7.1 L6.5,4.7 M4.6,8.9 L6.5,11.3 M9.5,4.7 L11.5,7.1 M9.5,11.3 L11.5,8.9" />
                    </Viewbox>
                </Button>
```

- [ ] **Step 2: Raise the event (`CardWindow.xaml.cs`)**

After `public event Action? SpeedTestRequested;` add:

```csharp
    public event Action? PathMapRequested;
```

Replace `OnDrag` with:

```csharp
    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        var (left, top) = (Left, Top);
        DragMove();
        Moved?.Invoke(Left, Top);
        // Pressing and releasing on the status line without moving the card is a click on it: open the path map.
        if (ReferenceEquals(e.OriginalSource, Header) && Left == left && Top == top) PathMapRequested?.Invoke();
    }
```

After the line `void OnSpeedTest(object sender, RoutedEventArgs e) => SpeedTestRequested?.Invoke();` add:

```csharp

    void OnPathMap(object sender, RoutedEventArgs e) => PathMapRequested?.Invoke();
```

- [ ] **Step 3: Wire it up (`App.xaml.cs`)**

Add the using (keep the others):

```csharp
using NetRoute.Core.PathMap;
```

Directly after `SmartRoutingWindow? _smartWindow;` add the field:

```csharp
    PathMapWindow? _pathWindow;
```

Directly after `_card.SmartSettingsRequested += OpenSmartRouting;` add:

```csharp
            _card.PathMapRequested += OpenPathMap;
```

Directly before `void OpenSmartRouting()` add:

```csharp
    /// Opens the network path window (one at a time). Probing runs only while it is open: the monitor starts here and
    /// is cancelled when the window closes.
    void OpenPathMap()
    {
        if (_controller is not { } controller) return;
        if (_pathWindow is not null)
        {
            if (_pathWindow.WindowState == WindowState.Minimized) _pathWindow.WindowState = WindowState.Normal;
            _pathWindow.Activate();
            return;
        }

        var monitor = new PathMonitor(new IcmpPathProbe(), TcpEndpointProbe.Default(), TimeProvider.System, PathThresholds.Default, message => _log?.Info(message));
        var cts = new CancellationTokenSource();
        var window = _pathWindow = new PathMapWindow(selected =>
        {
            // Keep the monitor on the adapters the card shows; a no-op when nothing changed.
            var adapters = controller.Status.Adapters;
            monitor.SetAdapters(adapters.Lan, adapters.Phone);
            return PathMapPresenter.Present(controller.Status, monitor.Snapshot(), selected, PathThresholds.Default);
        });
        window.Closed += (_, _) =>
        {
            cts.Cancel();
            cts.Dispose();
            _pathWindow = null;
        };
        var initial = controller.Status.Adapters;
        monitor.SetAdapters(initial.Lan, initial.Phone);
        _ = monitor.RunAsync(cts.Token);
        window.Show();
    }

```

(`Shutdown()` in `Quit()` closes every window, so the `Closed` handler also stops the monitor on exit.)

- [ ] **Step 4: Build everything and run the Core tests**

Run: `dotnet build NetRouteWidget.slnx`
Expected: `Build succeeded.` with 0 errors.

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName!~WindowsIntegrationTests"`
Expected: `Passed!  - Failed:     0, Passed:   707`

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.App/CardWindow.xaml src/NetRoute.App/CardWindow.xaml.cs src/NetRoute.App/App.xaml.cs
git commit -m "feat(app): open the network path map from the card's status line or a footer icon"
```

---

### Task 11: Documentation, spec status and the on-machine checklist

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md` (status line)

- [ ] **Step 1: README: the feature row, a section and the version entry**

In the "Features at a glance" table, after the `**Profiles** (v4)` row, add:

```markdown
| **Network path** (v5) | Click the card's status line, or the network icon next to ⏱ and ⚙, for a live map of the route from this PC to the internet over the LAN and over the phone: your router, the devices behind it, the internet. It shows where a connection stops answering, and says so only when the evidence supports it. |
```

Before the `## Editing the lists` heading add:

```markdown
## Network path (v5)

Click the card's status line (it has a dotted underline) or the network icon next to ⏱ and ⚙ to open a map of the route from this PC to the internet, over the **LAN** and over the **phone**, side by side.

- **What it shows:** each device on the way (your router, the box behind it, your provider's gateway, and so on) with its latency and packet loss, and the internet at the end. Healthy links flow green; a link with loss turns amber; a located break is red with a ✕.
- **What it will and will not claim:** the connection's own internet check decides whether it is failing. A device is blamed only if its probes were lost in the same moments as the failed internet checks. A device that ignores probes while traffic still gets through is shown as "Ignores probes", never as broken. When the evidence is partial the window says the fault is "after" a device without naming one, and when there is too little data it says so.
- **Calm states:** "Measuring…" for the first ten seconds, "Measurement unavailable" when this PC could not run a check (this is never counted as packet loss), and "Trace incomplete" when the route is longer than 12 hops or ICMP to the target is filtered (this is never a fault by itself).
- **How it measures:** ICMP echo with a chosen TTL, sent from each connection's own address, once a second per device; a TCP connection to `1.1.1.1` (then `8.8.8.8`) every two seconds as the internet check. It runs only while the window is open and needs no administrator rights. IPv4 only.
- **Details:** click any device for its latency, loss, a 30-second sparkline and a plain-language note. See [the v5 spec](docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md).
```

At the end of the Versions list, after the v4 bullet, add:

```markdown
- **v5:** A network path map: a live drawing of the route from this PC to the internet over the LAN and the phone, opened from the card, with per-device health and an evidence-based diagnosis of where a connection stops answering. See [the v5 spec](docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md). ✅
```

- [ ] **Step 2: Spec status line**

In the spec, replace the first `**Status:**` line with:

```markdown
**Status:** Implemented on branch feat/v5-path-map (PR #3); waiting for the user's on-machine verification.
```

- [ ] **Step 3: Run the whole suite and a clean build**

Run: `dotnet build NetRouteWidget.slnx`
Expected: `Build succeeded.`

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName!~WindowsIntegrationTests"`
Expected: `Passed!  - Failed:     0, Passed:   707`

Run: `dotnet test tests/NetRoute.Core.Tests`
Expected: `Failed:     2` and they are exactly the two known `WindowsIntegrationTests` named in the Global Constraints (unchanged from the base branch).

- [ ] **Step 4: Commit, push and update the PR**

```bash
git add README.md docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md
git commit -m "docs(v5): README section for the network path map; spec marked implemented"
git push
gh pr edit 3 --title "feat(v5): network path map" --body "Adds the network path map: a window opened from the card (status line or footer icon) that draws the route from this PC to the internet over the LAN and the phone, with live per-device health and an evidence-based diagnosis. Design: docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md. Plan: docs/superpowers/plans/2026-10-13-netroute-v5-path-map.md. Pending the user's on-machine verification (checklist in the plan, Task 11)."
```

- [ ] **Step 5: Hand the on-machine checklist to the user (run by the user, with the installed widget closed first)**

1. Quit the installed widget from the tray, then start the dev build. Click the card's status line: the path window opens. Click the new icon: the same window comes forward (no second window).
2. Drag the card by its status line: it moves and does **not** open the window.
3. Within about 10 seconds the map shows both lanes with real addresses. The LAN lane matches `192.168.86.1 → 192.168.1.1 → 172.17.18.1 → …` and the phone lane matches the earlier check; a silent hop appears as a dashed "Hidden hop".
4. Unplug the LAN cable: the LAN lane reads "Not connected" within a few seconds and the phone lane is unaffected. Plug it back: the lane re-traces and shows "Measuring…" first, then healthy.
5. While the LAN is dropping (as on 2026-10-13), the diagnosis names a break only when it says so with the "While the internet check failed…" sentence; when it cannot, it says the fault is after a device, or that there is not enough data. Compare it with `%APPDATA%\NetRouteWidget\logs\lan-monitor.log`.
6. Pause freezes the picture and the animation; Resume catches up. Switch Windows between light and dark: the window follows.
7. Close the window and watch CPU and the network in Task Manager: nothing keeps probing. Reopen it: probing restarts.
8. With Smart routing on, open the window: it still traces both lanes (the earlier feasibility check ran with it on).

Report anything that looks wrong with a screenshot; thresholds are all in `PathThresholds` and can be tuned without touching the rest.
