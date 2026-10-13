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
    string Name, string Caption, bool Connected, IReadOnlyList<NodeView> Nodes, IReadOnlyList<LinkState> Links, InternetRowView Internet,
    string AdapterName);

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
        var leaving = diag.Where == Localization.Narrowed ? LeavingLink(diag, groups, links.Count) : -1;
        links.Add(InternetLink(internet, diag));
        // Narrowed: the fault is somewhere after the last device that answered, so the link leaving it is amber.
        if (leaving >= 0) links[leaving] = LinkState.Suspect;

        var view = new LaneView(
            name, connected ? $"{name} path · from {source}" : $"{name} path · not connected", connected,
            nodes, links, new InternetRowView($"{id}:net", name, InternetMetric(diag, internet), internet),
            snap.Adapter?.Name ?? "");
        return new Analysed(id, name, snap, diag, view, groups);
    }

    /// Index of the link leaving the last answering device: the link into the first box holding a later hop,
    /// or the link into the Internet card (index = internetLink) when no drawn box follows it.
    static int LeavingLink(LaneDiagnosis diag, List<Group> groups, int internetLink)
    {
        if (diag.LastAnsweringHop is not { } p) return -1;
        var next = groups.FindIndex(g => g.Hops.Any(h => h > p));
        return next >= 0 ? next : internetLink;
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
        // After a located break the red link is already drawn into the broken device; without a located break nobody is blamed.
        HopDisplay.Broken => diag.Where switch
        {
            Localization.Located => LinkState.Muted,
            Localization.NotEnoughData => LinkState.Neutral,
            Localization.Narrowed => LinkState.Muted,
            _ => LinkState.Broken,
        },
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
            return ["Both connections reach the internet." + string.Concat(new[] { lan, phone }
                .Where(a => a.Diagnosis.TraceStatus == TraceStatus.Unavailable).Select(a => $" {a.Name}: the {NotMeasured}"))];
        return [Line(lan, t), Line(phone, t)];
    }

    const string NotMeasured = "devices on the way could not be measured.";

    static string Line(Analysed a, PathThresholds t)
    {
        var d = a.Diagnosis;
        var name = a.Name;
        switch (d.Status)
        {
            case LaneStatus.NotConnected: return $"{name}: not connected.";
            case LaneStatus.Measuring: return $"{name}: measuring… {Math.Min(10, d.EndpointStats.Valid * 2)} of 10 s.";
            case LaneStatus.Unavailable: return $"{name}: measurement unavailable on this PC. That says nothing about the connection.";
            case LaneStatus.Healthy:
                return $"{name} reaches the internet." + (d.TraceStatus == TraceStatus.Unavailable ? $" The {NotMeasured}" : "");
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

        // Nodes and Groups are built from the same list in the same order (the trace ghost is appended after them).
        var index = lane.View.Nodes.ToList().FindIndex(n => n.Id == id);
        if (index < 0 || index >= lane.Groups.Count) return null;
        var node = lane.View.Nodes[index];
        var group = lane.Groups[index];

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
            StatsOf(hop.Stats, t), lane.Snap.HopSamples.Count > group.Hops[0] ? lane.Snap.HopSamples[group.Hops[0]] : [], NoteFor(hop, lane, group.Hops[0]));
    }

    static IReadOnlyList<(string, string)> StatsOf(SampleStats s, PathThresholds t) =>
    [
        ("Latency, average", s.AvgMs is { } a ? $"{a} ms" : "—"),
        ("Best / worst", s.BestMs is { } b && s.WorstMs is { } w ? $"{b} / {w} ms" : "—"),
        ("Packets lost", s.Valid == 0 ? "—" : $"{s.LossPercent.ToString("0", Inv)}%"),
        ("Could not be measured", s.Unavailable.ToString(Inv)),
        ("Window", $"{t.Window.TotalSeconds.ToString("0", Inv)} s"),
    ];

    static string NoteFor(HopDiagnosis hop, Analysed lane, int hopIndex)
    {
        var firstBad = lane.Diagnosis.FirstBadHop == hopIndex;
        var hasDeviceBefore = lane.Diagnosis.LastAnsweringHop is not null;
        return hop.Display switch
        {
            HopDisplay.Measuring => "Not enough checks yet to judge this device.",
            HopDisplay.Unavailable => "The checks could not run on this PC, so nothing is known about this device.",
            HopDisplay.Hidden => "This device never answered the trace. Many carriers and routers do this; it is not a failure.",
            HopDisplay.Quiet => "It answers probes badly, but there is no evidence against it. Devices often ignore probes while still passing traffic.",
            HopDisplay.Suspect when firstBad => "The first device after the last one that answered. It lost some of its probes while the internet check was failing, so it is the most likely break, though not every probe was lost.",
            HopDisplay.Suspect => "It lost some probes while the internet check was failing, but not clearly enough to call it the break.",
            HopDisplay.Broken when !hasDeviceBefore => "Its probes were lost in the same moments the internet check failed, and it is the first device on the way out.",
            HopDisplay.Broken => "Its probes were lost in the same moments the internet check failed, while the device before it kept answering.",
            HopDisplay.Silent => "It does not answer, and it is after the located break.",
            _ => hop.Hop.Ttl == 1 && lane.Id == "lan"
                ? "Your router: the first device on the way out."
                : "Answering its probes normally.",
        };
    }
}
