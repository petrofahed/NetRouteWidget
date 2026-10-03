namespace NetRoute.Core;

/// Exit: where a connection that matches this entry goes. CarveOut: a LAN entry that stays active in the LAN profile and
/// is matched before the phone entries (an app's update download).
public sealed record RuleEntry(
    string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains,
    RouteExit Exit = RouteExit.Lan, bool CarveOut = false);

/// The rules active right now. In the Phone profile: the LAN items switched on plus the enabled LAN user rules (traffic
/// that must stay off 4G). In the LAN profile: the carve-outs (update items, LAN), then the phone items switched on and
/// the enabled phone user rules (traffic that must use the phone).
public sealed record RuleSet(IReadOnlyList<RuleEntry> Entries)
{
    /// The Phone profile ("Phone + exceptions"), which is what Smart routing was before profiles existed.
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Build(catalog, settings, RouteExit.Phone);

    /// profileDefault is the profile's default exit: Phone = "Phone + exceptions", Lan = "LAN + exceptions".
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, RouteExit profileDefault)
    {
        var on = catalog.Where(settings.IsItemOn).ToList();
        if (profileDefault == RouteExit.Phone)
            return Create(on.Where(i => i.Exit == RouteExit.Lan), settings.UserRules.Where(r => r.Enabled), []);

        var items = on.Where(i => i.CarveOut).Concat(on.Where(i => i.Exit == RouteExit.Phone && !i.CarveOut));
        return Create(items, [], settings.PhoneUserRules.Where(r => r.Enabled));
    }

    /// Every built-in item and every user rule of both lists, switched on or off. Usage uses it so that traffic of a
    /// switched-off item still lands in that item's row (it just goes through the default exit).
    public static RuleSet BuildAll(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Create(catalog, settings.UserRules, settings.PhoneUserRules);

    static RuleSet Create(IEnumerable<RuleItem> items, IEnumerable<UserRule> lanRules, IEnumerable<UserRule> phoneRules)
    {
        var entries = items.Select(i => new RuleEntry(i.Id, i.Name, i.Processes, i.Domains, i.Exit, i.CarveOut)).ToList();
        foreach (var rule in lanRules) entries.Add(UserEntry(UserRule.IdOf(rule), rule, RouteExit.Lan));
        foreach (var rule in phoneRules) entries.Add(UserEntry(UserRule.PhoneIdOf(rule), rule, RouteExit.Phone));
        return new RuleSet(entries);
    }

    static RuleEntry UserEntry(string id, UserRule rule, RouteExit exit) =>
        rule.Type == UserRuleType.App
            ? new RuleEntry(id, rule.Value, [rule.Value], [], exit)
            : new RuleEntry(id, rule.Value, [], [rule.Value], exit);

    public string Fingerprint =>
        string.Join("|", Entries.Select(e =>
            $"{e.Id}:{e.Exit}:{e.CarveOut}:{string.Join(",", e.Processes)}:{string.Join(",", e.Domains)}"));

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
