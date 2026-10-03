namespace NetRoute.Core;

/// Whether a usage row is an exception of the ACTIVE profile (so a tag is shown), whether the user may add/remove it from
/// there, and where it goes while it is an exception.
public readonly record struct ExceptionState(bool InException, bool CanChange, RouteExit Destination);

/// The Usage tab's right-click menu: "Send to exception" / "Exclude from exception". It reads and writes the same switches
/// and rule lists as the Config tab. profile = the active profile's default exit (Phone = "Phone + exceptions").
public static class UsageExceptions
{
    public static ExceptionState Describe(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, RouteExit profile, string key)
    {
        var destination = profile == RouteExit.Phone ? RouteExit.Lan : RouteExit.Phone;
        if (catalog.FirstOrDefault(i => i.Id == key) is { } item)
        {
            if (Belongs(item, profile)) return new(settings.IsItemOn(item), true, item.Exit);
            // A built-in item of the other list claims this row's traffic, but the user may have a rule for the same app in
            // the active list (it really routes there): that rule is what the menu acts on.
            return ClaimedRule(item, ListFor(settings, profile)) is { } claimed
                ? new(claimed.Enabled, true, destination)
                : new(false, false, item.Exit);
        }
        var list = ListFor(settings, profile);
        if (AppExe(key) is { } exe) return new(FindApp(list, exe)?.Enabled == true, true, destination);
        if (FindWebsite(list, key) is { } site) return new(site.Enabled, true, destination);
        return new(false, false, destination);
    }

    /// The settings with the row switched to "on" (in the exceptions of the profile) or off. Unchanged when the row cannot
    /// be changed from here or is already in that state. The caller passes the state the menu DISPLAYED, so a click never
    /// flips what changed on screen in the meantime.
    public static SmartRoutingSettings Set(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, RouteExit profile, string key, bool on)
    {
        var state = Describe(catalog, settings, profile, key);
        if (!state.CanChange || state.InException == on) return settings;

        var list = ListFor(settings, profile);
        if (catalog.FirstOrDefault(i => i.Id == key) is { } item)
        {
            if (Belongs(item, profile)) return settings.WithItem(item.Id, on);
            return ClaimedRule(item, list) is { } claimed ? With(settings, profile, claimed with { Enabled = on }) : settings;
        }
        if (AppExe(key) is { } exe)
        {
            if (FindApp(list, exe) is { } rule) return With(settings, profile, rule with { Enabled = on });
            return on && UserRule.TryCreate(UserRuleType.App, exe, out var created, out _)
                ? With(settings, profile, created!)
                : settings;
        }
        return FindWebsite(list, key) is { } site ? With(settings, profile, site with { Enabled = on }) : settings;
    }

    static UserRule? ClaimedRule(RuleItem item, IReadOnlyList<UserRule> list) =>
        list.FirstOrDefault(r => r.Type == UserRuleType.App && item.Processes.Any(p => string.Equals(p, r.Value, StringComparison.OrdinalIgnoreCase)));

    /// The Phone profile's exceptions are the LAN items; the LAN profile's are the phone items plus the carve-outs.
    static bool Belongs(RuleItem item, RouteExit profile) =>
        profile == RouteExit.Phone ? item.Exit == RouteExit.Lan : item.Exit == RouteExit.Phone || item.CarveOut;

    static IReadOnlyList<UserRule> ListFor(SmartRoutingSettings s, RouteExit profile) =>
        profile == RouteExit.Phone ? s.UserRules : s.PhoneUserRules;

    static SmartRoutingSettings With(SmartRoutingSettings s, RouteExit profile, UserRule rule) =>
        profile == RouteExit.Phone ? s.WithUserRule(rule) : s.WithPhoneUserRule(rule);

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
