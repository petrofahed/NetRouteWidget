using System.Globalization;

namespace NetRoute.Core;

public static class ByteFormat
{
    public static string Human(long bytes) => bytes switch
    {
        < 1024L * 1024 => $"{Math.Round(bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} MB",
        _ => $"{(bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} GB",
    };
}

public enum SmartTone { Normal, Muted, Warning }

public sealed record SmartRow(string Text, SmartTone Tone);

public static class SmartRoutingPresenter
{
    public static SmartRow Row(SmartRoutingStatus s) => s.State switch
    {
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

    public static SmartRoutingSettings WithGroup(
        IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string groupId, bool on) =>
        catalog.Where(i => i.GroupId == groupId).Aggregate(settings, (s, i) => s.WithItem(i.Id, on));
}
