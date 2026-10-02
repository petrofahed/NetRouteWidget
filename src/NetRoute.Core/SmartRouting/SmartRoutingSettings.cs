using System.Text.RegularExpressions;

namespace NetRoute.Core;

public enum UserRuleType { App, Website }

/// A user-added "keep this off 4G" rule: an app by .exe file name, or a website by domain (subdomains included).
public sealed partial record UserRule(UserRuleType Type, string Value, bool Enabled = true)
{
    public static string IdOf(UserRule rule) =>
        $"user:{rule.Type.ToString().ToLowerInvariant()}:{rule.Value.ToLowerInvariant()}";

    public static bool TryCreate(UserRuleType type, string raw, out UserRule? rule, out string? error)
    {
        rule = null;
        var value = type == UserRuleType.App ? NormaliseApp(raw) : NormaliseWebsite(raw);
        error = value is null
            ? type == UserRuleType.App ? "Enter an app file name ending in .exe, e.g. qbittorrent.exe"
                                       : "Enter a website like youtube.com"
            : null;
        if (value is not null) rule = new UserRule(type, value);
        return value is not null;
    }

    static string? NormaliseApp(string raw)
    {
        var name = Path.GetFileName(raw.Trim().Trim('"').Trim());
        return name.Length > 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? name
            : null;
    }

    static string? NormaliseWebsite(string raw)
    {
        var host = Scheme().Replace(raw.Trim().ToLowerInvariant(), "");
        host = host.Split('/', '?', '#')[0].Trim();
        host = Port().Replace(host, "").TrimStart('*').TrimStart('.');
        if (host.EndsWith('.')) host = host[..^1];
        if (host.StartsWith("www.")) host = host[4..];
        return Hostname().IsMatch(host) ? host : null;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://")] private static partial Regex Scheme();
    [GeneratedRegex(@":\d+$")] private static partial Regex Port();
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}\z")] private static partial Regex Hostname();
}

public sealed record SmartRoutingSettings
{
    public bool Enabled { get; init; }

    /// The LAN adapter name last seen by v1. Smart routing binds its "lan" outbound to it when it starts while the LAN
    /// is absent (cable out at boot, router rebooting), so LAN-only traffic waits instead of falling onto 4G.
    /// Not part of the rule fingerprint: changing it never restarts sing-box by itself.
    public string? LastLanInterface { get; init; }

    public IReadOnlyDictionary<string, bool> Items { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyList<UserRule> UserRules { get; init; } = [];

    /// Built-in items not mentioned in Items fall back to their catalog default (ON).
    public bool IsItemOn(RuleItem item) => Items.TryGetValue(item.Id, out var on) ? on : item.DefaultOn;

    public SmartRoutingSettings WithItem(string id, bool on) =>
        this with { Items = new Dictionary<string, bool>(Items) { [id] = on } };

    public SmartRoutingSettings WithUserRule(UserRule rule) =>
        this with { UserRules = [.. UserRules.Where(r => !SameRule(r, rule)), rule] };

    public SmartRoutingSettings WithoutUserRule(UserRule rule) =>
        this with { UserRules = [.. UserRules.Where(r => !SameRule(r, rule))] };

    /// True (with the updated settings) only when a LAN is present and its name differs from the saved one, so callers
    /// write the settings file on a change rather than on every status update.
    public bool TryRememberLan(string? seen, out SmartRoutingSettings updated)
    {
        updated = this;
        if (string.IsNullOrWhiteSpace(seen) || seen == LastLanInterface) return false;
        updated = this with { LastLanInterface = seen };
        return true;
    }

    /// False for hand-edited or corrupt content that would crash later (null collections or rules, blank values, unknown rule types).
    public bool IsValid() =>
        Items is not null && UserRules is not null
        && UserRules.All(r => r is not null && !string.IsNullOrWhiteSpace(r.Value) && Enum.IsDefined(r.Type));

    static bool SameRule(UserRule a, UserRule b) =>
        a.Type == b.Type && string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);

    public bool Equals(SmartRoutingSettings? other) =>
        other is not null && Enabled == other.Enabled && LastLanInterface == other.LastLanInterface
        && Items.Count == other.Items.Count
        && Items.All(kv => other.Items.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && UserRules.SequenceEqual(other.UserRules);

    public override int GetHashCode() => HashCode.Combine(Enabled, Items.Count, UserRules.Count);
}
