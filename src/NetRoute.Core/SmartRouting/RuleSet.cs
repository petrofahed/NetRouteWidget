namespace NetRoute.Core;

/// Target distinguishes logical Home from explicit physical connections. Carve-outs match before app exceptions.
public sealed record RuleEntry(
    string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains,
    RuleTarget Target = RuleTarget.Home, bool CarveOut = false);

/// Ordered active exceptions followed by inherited non-metered protection. Off rules are kept only in BuildAll for usage.
public sealed record RuleSet(IReadOnlyList<RuleEntry> Entries)
{
    /// The Phone profile ("Phone + exceptions"), which is what Smart routing was before profiles existed.
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Build(catalog, settings, ConnectionId.Phone);

    // Transitional profile adapter for the controller until it moves to ConnectionId.
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, RouteExit profileDefault) =>
        Build(catalog, settings, profileDefault switch
        {
            RouteExit.Phone => ConnectionId.Phone,
            RouteExit.Lan => ConnectionId.Lan,
            _ => throw new ArgumentOutOfRangeException(nameof(profileDefault), profileDefault, null),
        });

    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, ConnectionId profile)
    {
        _ = SmartRoutingPolicy.TargetOf(profile);
        var on = catalog.Where(settings.IsItemOn).ToList();
        var entries = on.Where(item => item.CarveOut).Select(CatalogEntry).ToList();
        var activeCatalogExit = profile == ConnectionId.Phone ? RouteExit.Lan : RouteExit.Phone;
        entries.AddRange(on.Where(item => !item.CarveOut && item.Exit == activeCatalogExit).Select(CatalogEntry));
        entries.AddRange(settings.RulesFor(profile).Where(rule => rule.Enabled).Select(rule => UserEntry(profile, rule)));
        if (profile != ConnectionId.Phone)
            entries.AddRange(on.Where(item => !item.CarveOut && item.Exit == RouteExit.Lan).Select(CatalogEntry));
        foreach (var other in Connections.CardOrder.Where(id => id != profile))
            entries.AddRange(settings.RulesFor(other).Where(rule => rule.Enabled).Select(rule => UserEntry(other, rule))
                .Where(entry => entry.Target != RuleTarget.Phone));
        return new RuleSet(entries.DistinctBy(entry => entry.Id).ToList());
    }

    /// Every built-in item and every user rule of all three lists, switched on or off. Usage uses it so that traffic of a
    /// switched-off item still lands in that item's row (it just goes through the default exit).
    public static RuleSet BuildAll(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings)
    {
        var entries = catalog.Select(CatalogEntry).ToList();
        foreach (var profile in Connections.CardOrder)
            entries.AddRange(settings.RulesFor(profile).Select(rule => UserEntry(profile, rule)));
        return new RuleSet(entries.DistinctBy(entry => entry.Id).ToList());
    }

    static RuleEntry CatalogEntry(RuleItem item) => new(item.Id, item.Name, item.Processes, item.Domains,
        item.Exit switch
        {
            RouteExit.Lan => RuleTarget.Home,
            RouteExit.Phone => RuleTarget.Phone,
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Exit, null),
        }, item.CarveOut);

    static RuleEntry UserEntry(ConnectionId profile, UserRule rule)
    {
        var target = rule.Exit is { } exit ? SmartRoutingPolicy.TargetOf(exit)
            : profile == ConnectionId.Phone ? RuleTarget.Home : RuleTarget.Phone;
        var id = UserRule.IdOf(profile, rule);
        return rule.Type == UserRuleType.App
            ? new RuleEntry(id, rule.Value, [rule.Value], [], target)
            : new RuleEntry(id, rule.Value, [], [rule.Value], target);
    }

    public string Fingerprint =>
        System.Text.Json.JsonSerializer.Serialize(Entries.Select(e => new { e.Id, e.Target, e.CarveOut, e.Processes, e.Domains }));

    public RuleEntry? FindByProcess(string? processName) =>
        processName is null ? null
            : Entries.FirstOrDefault(e => e.Processes.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)));

    public RuleEntry? FindByHost(string? host, bool carveOutOnly = false)
    {
        if (string.IsNullOrEmpty(host)) return null;
        var h = host.ToLowerInvariant();
        return Entries.FirstOrDefault(e => (!carveOutOnly || e.CarveOut) && e.Domains.Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal)));
    }
}
