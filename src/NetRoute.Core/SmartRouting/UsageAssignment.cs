namespace NetRoute.Core;

public enum GoesVia { Phone, Lan }

/// Where a usage row's traffic goes, and whether the user may change it.
public readonly record struct Assignment(GoesVia Via, bool CanChange);

/// The "Goes via" drop-down: reads and writes the same switches as the Config tab.
public static class UsageAssignment
{
    public static Assignment Describe(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string key)
    {
        if (catalog.FirstOrDefault(i => i.Id == key) is { } item) return new(Via(settings.IsItemOn(item)), true);
        if (AppExe(key) is { } exe) return new(Via(FindAppRule(settings, exe)?.Enabled == true), true);
        if (FindWebsiteRule(settings, key) is { } site) return new(Via(site.Enabled), true);
        return new(GoesVia.Phone, false);
    }

    /// Returns the settings unchanged for a row that cannot be assigned, and for "to the phone" on an application that has no rule.
    public static SmartRoutingSettings Set(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string key, bool toLan)
    {
        if (catalog.Any(i => i.Id == key)) return settings.WithItem(key, toLan);
        if (AppExe(key) is { } exe)
        {
            if (FindAppRule(settings, exe) is { } rule) return settings.WithUserRule(rule with { Enabled = toLan });
            return toLan && UserRule.TryCreate(UserRuleType.App, exe, out var created, out _)
                ? settings.WithUserRule(created!)
                : settings;
        }
        return FindWebsiteRule(settings, key) is { } site ? settings.WithUserRule(site with { Enabled = toLan }) : settings;
    }

    static GoesVia Via(bool lan) => lan ? GoesVia.Lan : GoesVia.Phone;

    /// The exe file name of an "app:" row, or null when the key is not an application row or the name is not a valid exe name.
    static string? AppExe(string key)
    {
        if (!key.StartsWith(UsageAttribution.AppPrefix, StringComparison.Ordinal)) return null;
        var exe = key[UsageAttribution.AppPrefix.Length..];
        return UserRule.TryCreate(UserRuleType.App, exe, out _, out _) ? exe : null;
    }

    static UserRule? FindAppRule(SmartRoutingSettings settings, string exe) =>
        settings.UserRules.FirstOrDefault(r => r.Type == UserRuleType.App && string.Equals(r.Value, exe, StringComparison.OrdinalIgnoreCase));

    static UserRule? FindWebsiteRule(SmartRoutingSettings settings, string key) =>
        settings.UserRules.FirstOrDefault(r => r.Type == UserRuleType.Website && UserRule.IdOf(r) == key);
}
