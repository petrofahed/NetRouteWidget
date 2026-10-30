using System.Globalization;

namespace NetRoute.Core;

public static class ByteFormat
{
    /// Rounds first, then picks the unit, so a boundary value never shows as "1024 KB" or "1024 MB".
    public static string Human(long bytes)
    {
        bytes = Math.Max(bytes, 0);
        var kb = Math.Round(bytes / 1024.0, MidpointRounding.AwayFromZero);
        if (kb < 1024) return $"{kb.ToString("0", CultureInfo.InvariantCulture)} KB";
        var mb = Math.Round(bytes / (1024.0 * 1024), 1, MidpointRounding.AwayFromZero);
        if (mb < 1024) return $"{mb.ToString("0.#", CultureInfo.InvariantCulture)} MB";
        var gb = Math.Round(bytes / (1024.0 * 1024 * 1024), 1, MidpointRounding.AwayFromZero);
        return $"{gb.ToString("0.#", CultureInfo.InvariantCulture)} GB";
    }
}

public enum SmartTone { Normal, Muted, Warning }

public sealed record SmartRow(string Text, SmartTone Tone);

public static class SmartRoutingPresenter
{
    /// The card's footer line. The card's Smart button already says whether Smart routing is on, so the running texts
    /// start with the profile. With Smart routing off, the routing mode says where everything goes.
    public static SmartRow Row(SmartRoutingStatus s, RoutingMode mode = RoutingMode.Auto) => s.State switch
    {
        // A message while Off/Starting is a warning from the controller (e.g. sing-box could not be stopped): show it.
        SmartState.Off or SmartState.Starting when !string.IsNullOrEmpty(s.Message) => new($"⚠ {s.Message}", SmartTone.Warning),
        SmartState.Off when mode == RoutingMode.Phone => new("Smart routing off · everything via the phone", SmartTone.Muted),
        SmartState.Off when mode == RoutingMode.Lan => new("Smart routing off · everything via the LAN", SmartTone.Muted),
        SmartState.Off when mode == RoutingMode.Wifi => new("Smart routing off · everything via Wi-Fi", SmartTone.Muted),
        SmartState.Off => new("⚡ Smart routing off", SmartTone.Muted),
        SmartState.Starting => new("⚡ Smart routing starting…", SmartTone.Muted),
        SmartState.Unavailable when s.Message?.StartsWith("Paused ", StringComparison.Ordinal) == true =>
            new($"⚡ Smart routing {char.ToLowerInvariant(s.Message[0])}{s.Message[1..]}", SmartTone.Muted), // "paused in Auto mode"
        SmartState.Unavailable => new($"⚡ Smart routing paused — {s.Message}", SmartTone.Muted),
        SmartState.Faulted => new(string.IsNullOrEmpty(s.Detail) ? $"⚠ {s.Message}" : $"⚠ {s.Message} — {s.Detail}", SmartTone.Warning),
        _ when s.Waiting => new($"⏸ {s.Targets.OrderBy(target => target.Target).First(target => target.Names.Count > 0).Names.Count} {(s.Targets.OrderBy(target => target.Target).First(target => target.Names.Count > 0).Names.Count == 1 ? "app" : "apps")} waiting for {SmartRoutingController.TargetLabel(s.Targets.OrderBy(target => target.Target).First(target => target.Names.Count > 0).Target)}", SmartTone.Warning),
        _ when s.Targets.Any(target => target.UsingMain) => new($"⚠ Using {Connections.Label(s.DefaultExit)} for waiting exceptions", SmartTone.Warning),
        _ when s.Profile == ConnectionId.Lan =>
            new($"LAN + exceptions · {s.RuleCount} rules", SmartTone.Normal),
        _ when s.Profile == ConnectionId.Wifi => new($"Wi-Fi + exceptions · {s.RuleCount} rules", SmartTone.Normal),
        _ => new($"Phone + exceptions · {s.RuleCount} rules · {ByteFormat.Human(s.Today.Total)} kept off 4G today", SmartTone.Normal),
    };

    public static string UseMainText(WaitingTarget target) =>
        $"Use {Connections.Label(target.Main)} until {SmartRoutingController.TargetLabel(target.Target)} is back"
        + (Connections.IsMetered(target.Main) ? " (uses mobile data)" : "");

    public static string WaitingText(WaitingTarget target) =>
        $"{string.Join(", ", target.Names)} waiting for {SmartRoutingController.TargetLabel(target.Target)}.";


}

public sealed record PageItem(string Id, string Name, bool On, long TodayBytes, RouteExit Exit = RouteExit.Lan, bool CarveOut = false);

public sealed record PageGroup(string Id, string Name, bool? On, long TodayBytes, IReadOnlyList<PageItem> Items);

public sealed record PageUserRule(UserRule Rule, long TodayBytes, RuleTarget Target = RuleTarget.Home);

/// EditingProfile: which profile's list the page shows (Phone = "Phone + exceptions", Lan = "LAN + exceptions").
/// ActiveProfile: the profile Smart routing is running (the routing mode).
public sealed record PageModel(
    bool Enabled, SmartRow Status, bool CanUseMain, long TotalToday,
    IReadOnlyList<PageGroup> Groups, IReadOnlyList<PageUserRule> UserRules,
    ConnectionId EditingProfile = ConnectionId.Phone, ConnectionId ActiveProfile = ConnectionId.Phone)
{
    public IReadOnlyList<WaitingTarget> Targets { get; init; } = [];
}

/// Pure model for the management page.
public static class SmartRoutingPage
{
    public static PageModel Build(
        IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, SmartRoutingStatus status, ConnectionId editing = ConnectionId.Phone)
    {
        long Bytes(string id) => status.Today.BytesByEntry.GetValueOrDefault(id);

        // The Phone view edits the LAN exceptions; the LAN view edits the phone exceptions. Carve-outs (update items) belong to both.
        var visible = editing == ConnectionId.Phone
            ? catalog.Where(i => i.Exit == RouteExit.Lan)
            : catalog.Where(i => i.Exit == RouteExit.Phone || i.CarveOut);
        var groups = visible.GroupBy(i => (i.GroupId, i.GroupName)).Select(g =>
        {
            var items = g.Select(i => new PageItem(i.Id, i.Name, settings.IsItemOn(i), Bytes(i.Id), i.Exit, i.CarveOut)).ToList();
            bool? on = items.All(i => i.On) ? true : items.Any(i => i.On) ? null : false;
            return new PageGroup(g.Key.GroupId, g.Key.GroupName, on, items.Sum(i => i.TodayBytes), items);
        }).ToList();
        var userRules = settings.RulesFor(editing);
        var rules = userRules.Select(r => new PageUserRule(r, Bytes(UsageAttribution.RowKeyOf(r)), r.Exit is { } exit ? SmartRoutingPolicy.TargetOf(exit) : editing == ConnectionId.Phone ? RuleTarget.Home : RuleTarget.Phone)).ToList();

        return new PageModel(
            settings.Enabled, SmartRoutingPresenter.Row(status),
            CanUseMain: status.State == SmartState.Running && status.Targets.Any(target => target.CanUseMain),
            status.Today.Total, groups, rules, editing, status.Profile) { Targets = status.Targets };
    }

    /// The dimmed text at the right of an item row. The Phone view shows what the item kept off 4G today, or where it goes
    /// when switched off; the LAN view says where the item goes. A switched-off carve-out falls to its app's phone rule.
    public static string ItemCaption(PageItem item, ConnectionId editing)
    {
        if (editing == ConnectionId.Phone)
            return item.On ? ByteFormat.Human(item.TodayBytes) : "→ via phone";
        if (item.CarveOut) return item.On ? "→ Home" : "→ via phone";
        return item.On ? "→ phone" : $"→ via {Connections.Label(editing)}";
    }

    /// Apply a captured row edit to its latest rule, preserving concurrent destination/enabled changes.
    public static SmartRoutingSettings ChangeUserRule(SmartRoutingSettings settings, ConnectionId profile, UserRule identity,
        bool? enabled = null, ConnectionId? destination = null)
    {
        var current = settings.RulesFor(profile).FirstOrDefault(rule => UserRule.IdOf(rule) == UserRule.IdOf(identity));
        if (current is null || destination == profile) return settings;
        return settings.WithUserRule(profile, current with { Enabled = enabled ?? current.Enabled, Exit = destination ?? current.Exit });
    }

    /// What a click on a group's tick box sets: ON unless the group is fully on (a mixed group goes ON, not off).
    public static bool NextGroupState(bool? current) => current != true;

    public static SmartRoutingSettings WithGroup(
        IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string groupId, bool on) =>
        catalog.Where(i => i.GroupId == groupId).Aggregate(settings, (s, i) => s.WithItem(i.Id, on));
}
