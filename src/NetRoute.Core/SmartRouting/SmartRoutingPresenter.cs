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
    public static SmartRow Row(SmartRoutingStatus s) => s.State switch
    {
        // A message while Off/Starting is a warning from the controller (e.g. sing-box could not be stopped): show it.
        SmartState.Off or SmartState.Starting when !string.IsNullOrEmpty(s.Message) => new($"⚠ {s.Message}", SmartTone.Warning),
        SmartState.Off => new("⚡ Smart routing off", SmartTone.Muted),
        SmartState.Starting => new("⚡ Smart routing starting…", SmartTone.Muted),
        SmartState.Unavailable => new($"⚡ Smart routing paused — {s.Message}", SmartTone.Muted),
        SmartState.Faulted => new($"⚠ {s.Message}", SmartTone.Warning),
        _ when s.Waiting => new("⏸ LAN-only traffic waiting (LAN offline)", SmartTone.Warning),
        _ when s.LanRulesOnPhone => new("⚠ Using phone for all traffic — LAN offline", SmartTone.Warning),
        _ => new($"⚡ Smart routing ON · {s.RuleCount} rules · {ByteFormat.Human(s.Today.Total)} kept off 4G today", SmartTone.Normal),
    };

    public static string WaitingText(IReadOnlyList<string> names) =>
        $"{string.Join(", ", names)} {(names.Count == 1 ? "is" : "are")} waiting — the LAN is offline.";
}

public sealed record PageItem(string Id, string Name, bool On, long TodayBytes);

public sealed record PageGroup(string Id, string Name, bool? On, long TodayBytes, IReadOnlyList<PageItem> Items);

public sealed record PageUserRule(UserRule Rule, long TodayBytes);

public sealed record PageModel(
    bool Enabled, SmartRow Status, bool CanUsePhone, long TotalToday,
    IReadOnlyList<PageGroup> Groups, IReadOnlyList<PageUserRule> UserRules);

/// Pure model for the management page.
public static class SmartRoutingPage
{
    public static PageModel Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, SmartRoutingStatus status)
    {
        long Bytes(string id) => status.Today.BytesByEntry.GetValueOrDefault(id);

        var groups = catalog.GroupBy(i => (i.GroupId, i.GroupName)).Select(g =>
        {
            var items = g.Select(i => new PageItem(i.Id, i.Name, settings.IsItemOn(i), Bytes(i.Id))).ToList();
            bool? on = items.All(i => i.On) ? true : items.Any(i => i.On) ? null : false;
            return new PageGroup(g.Key.GroupId, g.Key.GroupName, on, items.Sum(i => i.TodayBytes), items);
        }).ToList();
        var rules = settings.UserRules.Select(r => new PageUserRule(r, Bytes(UserRule.IdOf(r)))).ToList();

        return new PageModel(
            settings.Enabled, SmartRoutingPresenter.Row(status),
            CanUsePhone: status.State == SmartState.Running && !status.LanOnline && !status.LanRulesOnPhone,
            status.Today.Total, groups, rules);
    }

    /// What a click on a group's tick box sets: ON unless the group is fully on (a mixed group goes ON, not off).
    public static bool NextGroupState(bool? current) => current != true;

    public static SmartRoutingSettings WithGroup(
        IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string groupId, bool on) =>
        catalog.Where(i => i.GroupId == groupId).Aggregate(settings, (s, i) => s.WithItem(i.Id, on));
}
