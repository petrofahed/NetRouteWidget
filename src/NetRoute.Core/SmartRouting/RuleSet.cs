namespace NetRoute.Core;

public sealed record RuleEntry(string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains);

/// The enabled "LAN only" rules right now: built-in items switched on plus enabled user rules.
public sealed record RuleSet(IReadOnlyList<RuleEntry> Entries)
{
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings)
    {
        var entries = catalog.Where(settings.IsItemOn)
            .Select(i => new RuleEntry(i.Id, i.Name, i.Processes, i.Domains))
            .ToList();
        foreach (var rule in settings.UserRules.Where(r => r.Enabled))
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
