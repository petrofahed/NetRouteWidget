namespace NetRoute.Core;

public sealed record RuleEntry(string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains);

/// The enabled "LAN only" rules right now: built-in items switched on plus enabled user rules.
public sealed record RuleSet(IReadOnlyList<RuleEntry> Entries)
{
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Create(catalog.Where(settings.IsItemOn), settings.UserRules.Where(r => r.Enabled));

    /// Every built-in item and every user rule, switched on or off. Usage uses it so that traffic of a switched-off
    /// item still lands in that item's row (it just goes through the phone).
    public static RuleSet BuildAll(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Create(catalog, settings.UserRules);

    static RuleSet Create(IEnumerable<RuleItem> items, IEnumerable<UserRule> userRules)
    {
        var entries = items.Select(i => new RuleEntry(i.Id, i.Name, i.Processes, i.Domains)).ToList();
        foreach (var rule in userRules)
        {
            entries.Add(rule.Type == UserRuleType.App
                ? new RuleEntry(UserRule.IdOf(rule), rule.Value, [rule.Value], [])
                : new RuleEntry(UserRule.IdOf(rule), rule.Value, [], [rule.Value]));
        }
        return new RuleSet(entries);
    }

    public string Fingerprint =>
        string.Join("|", Entries.Select(e => $"{e.Id}:{string.Join(",", e.Processes)}:{string.Join(",", e.Domains)}"));

    public RuleEntry? FindByProcess(string? processName) =>
        processName is null ? null
            : Entries.FirstOrDefault(e => e.Processes.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)));

    public RuleEntry? FindByHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        var h = host.ToLowerInvariant();
        return Entries.FirstOrDefault(e => e.Domains.Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal)));
    }
}
