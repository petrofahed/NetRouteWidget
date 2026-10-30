namespace NetRoute.Core;

/// Whether a usage row is an exception of the ACTIVE profile (so a tag is shown), whether the user may add/remove it from
/// there, and where it goes while it is an exception.
public readonly record struct ExceptionState(bool InException, bool CanChange, ConnectionId Destination, bool Home = false)
{
    public bool FixedTarget { get; init; }
    public RuleTarget Target => Home ? RuleTarget.Home : SmartRoutingPolicy.TargetOf(Destination);
}

/// Immutable menu intent; key, profile and destination belong to the rendered menu, never live UI state.
public sealed record UsageExceptionAction(string Key, ConnectionId Profile, bool On, ConnectionId? Destination);

/// The Usage tab's right-click menu: "Send to exception" / "Exclude from exception". It reads and writes the same switches
/// and rule lists as the Config tab. profile = the active profile's default exit (Phone = "Phone + exceptions").
public static class UsageExceptions
{
    public static ExceptionState Describe(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, ConnectionId profile, string key)
    {
        var destination = UserRule.ImpliedExit(profile);
        ExceptionState UserState(UserRule? rule) => new(rule?.Enabled == true, true, rule?.Exit ?? destination, rule?.Exit is null && profile == ConnectionId.Phone);
        if (catalog.FirstOrDefault(i => i.Id == key) is { } item)
        {
            if (Belongs(item, profile)) return new(settings.IsItemOn(item), true, item.Exit == RouteExit.Phone ? ConnectionId.Phone : ConnectionId.Lan, item.Exit == RouteExit.Lan) { FixedTarget = true };
            // A built-in item of the other list claims this row's traffic, but the user may have a rule for the same app in
            // the active list (it really routes there): that rule is what the menu acts on.
            return ClaimedRule(item, ListFor(settings, profile)) is { } claimed
                ? UserState(claimed)
                : new(false, false, item.Exit == RouteExit.Phone ? ConnectionId.Phone : ConnectionId.Lan, item.Exit == RouteExit.Lan) { FixedTarget = true };
        }
        var list = ListFor(settings, profile);
        if (AppExe(key) is { } exe) return UserState(FindApp(list, exe));
        if (FindWebsite(list, key) is { } site) return UserState(site);
        return new(false, false, destination);
    }

    /// The settings with the row switched to "on" (in the exceptions of the profile) or off. Unchanged when the row cannot
    /// be changed from here or is already in that state. The caller passes the state the menu DISPLAYED, so a click never
    /// flips what changed on screen in the meantime.
    public static SmartRoutingSettings Set(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, ConnectionId profile, string key, bool on, ConnectionId? destination = null)
    {
        var state = Describe(catalog, settings, profile, key);
        if (!state.CanChange || (state.InException == on && destination is null)) return settings;
        if (destination == profile || (destination is { } exit && !Connections.All.Contains(exit))) return settings;
        UserRule Change(UserRule rule) => rule with { Enabled = on, Exit = destination ?? rule.Exit };

        var list = ListFor(settings, profile);
        if (catalog.FirstOrDefault(i => i.Id == key) is { } item)
        {
            if (Belongs(item, profile)) return settings.WithItem(item.Id, on);
            return ClaimedRule(item, list) is { } claimed ? With(settings, profile, Change(claimed)) : settings;
        }
        if (AppExe(key) is { } exe)
        {
            if (FindApp(list, exe) is { } rule) return With(settings, profile, Change(rule));
            return on && UserRule.TryCreate(UserRuleType.App, exe, out var created, out _)
                ? With(settings, profile, Change(created!))
                : settings;
        }
        return FindWebsite(list, key) is { } site ? With(settings, profile, Change(site)) : settings;
    }

    public static IReadOnlyList<UsageExceptionAction> Actions(string key, ConnectionId profile, ExceptionState state)
    {
        if (!state.CanChange) return [];
        if (state.FixedTarget) return [new(key, profile, !state.InException, null)];
        var actions = Connections.All.Where(id => id != profile)
            .Select(id => new UsageExceptionAction(key, profile, true, id)).ToList();
        if (state.InException) actions.Add(new(key, profile, false, null));
        return actions;
    }

    static UserRule? ClaimedRule(RuleItem item, IReadOnlyList<UserRule> list) =>
        list.FirstOrDefault(r => r.Type == UserRuleType.App && item.Processes.Any(p => string.Equals(p, r.Value, StringComparison.OrdinalIgnoreCase)));

    /// The Phone profile's exceptions are the LAN items; the LAN profile's are the phone items plus the carve-outs.
    static bool Belongs(RuleItem item, ConnectionId profile) =>
        profile == ConnectionId.Phone ? item.Exit == RouteExit.Lan : item.Exit == RouteExit.Phone || item.CarveOut;

    static IReadOnlyList<UserRule> ListFor(SmartRoutingSettings s, ConnectionId profile) =>
        s.RulesFor(profile);

    static SmartRoutingSettings With(SmartRoutingSettings s, ConnectionId profile, UserRule rule) =>
        s.WithUserRule(profile, rule);

    /// The exe file name of an "app:" row, or null when the key is not an application row or the name is not a valid exe name.
    static string? AppExe(string key)
    {
        if (!key.StartsWith(UsageAttribution.AppPrefix, StringComparison.Ordinal)) return null;
        var exe = key[UsageAttribution.AppPrefix.Length..];
        return UserRule.TryCreate(UserRuleType.App, exe, out _, out _) ? exe : null;
    }

    static UserRule? FindApp(IReadOnlyList<UserRule> list, string exe) =>
        list.FirstOrDefault(r => r.Type == UserRuleType.App && string.Equals(r.Value, exe, StringComparison.OrdinalIgnoreCase));

    static UserRule? FindWebsite(IReadOnlyList<UserRule> list, string key) =>
        list.FirstOrDefault(r => r.Type == UserRuleType.Website && UserRule.IdOf(r) == key);
}
