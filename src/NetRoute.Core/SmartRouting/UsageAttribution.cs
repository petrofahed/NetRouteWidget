namespace NetRoute.Core;

/// Which usage row a connection counts under, and what each row is called. A connection counts under exactly one row,
/// so the totals never double-count:
///   1. a rule that lists the connection's application (a built-in item such as OneDrive, or a user App rule of either list);
///   2. otherwise a rule whose domains match the connection's host (YouTube, Facebook, a user Website rule);
///   3. otherwise the application itself ("app:chrome.exe");
///   4. otherwise "other".
/// Rules switched off still count, so a row keeps its history when the user moves it to the phone and back. The one
/// exception is a disabled user App rule: it does not claim its application (so Chrome's YouTube traffic still reaches
/// the YouTube row); its traffic falls through to the domain rules and then to the same "app:x.exe" row.
public sealed class UsageAttribution
{
    public const string OtherKey = "other";
    public const string UnattributedKey = "unattributed";
    public const string AppPrefix = "app:";
    const string PhonePrefix = "phone-";
    const string UserAppPrefix = "user:app:";
    const string UserWebsitePrefix = "user:website:";

    readonly RuleSet _all;
    readonly Dictionary<string, string> _names;

    UsageAttribution(RuleSet all)
    {
        _all = all;
        _names = new Dictionary<string, string>();
        // User App rules are named after their file (see DisplayName), not with the ".exe". TryAdd: a hand-edited settings
        // file may list a rule twice.
        foreach (var entry in all.Entries.Where(e => !BareId(e).StartsWith(UserAppPrefix, StringComparison.Ordinal)))
            _names.TryAdd(KeyOf(entry), entry.Name);
    }

    public static UsageAttribution Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        new(RuleSet.BuildAll(catalog, settings with
        {
            // A disabled App rule routes nothing, so it must not claim its process ahead of the site rules; its traffic
            // falls through to them and then to the same "app:x.exe" row, so no history is lost.
            UserRules = [.. settings.UserRules.Where(r => r.Enabled || r.Type != UserRuleType.App)],
            PhoneUserRules = [.. settings.PhoneUserRules.Where(r => r.Enabled || r.Type != UserRuleType.App)],
        }));

    public string Resolve(string? processName, string? host)
    {
        var exe = string.IsNullOrWhiteSpace(processName) ? null : processName.Trim();
        if (_all.FindByProcess(exe) is { } byProcess) return KeyOf(byProcess);
        if (_all.FindByHost(host) is { } byHost) return KeyOf(byHost);
        return exe is null ? OtherKey : AppPrefix + exe.ToLowerInvariant();
    }

    public string DisplayName(string key)
    {
        if (key == OtherKey) return "Other";
        if (key == UnattributedKey) return "Unattributed (short connections)";
        if (_names.TryGetValue(key, out var name)) return name;
        if (key.StartsWith(AppPrefix, StringComparison.Ordinal))
        {
            var exe = key[AppPrefix.Length..];
            return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        }
        return key.StartsWith(UserWebsitePrefix, StringComparison.Ordinal) ? key[UserWebsitePrefix.Length..] : key;
    }

    /// The usage row key of a user rule, as Resolve counts it: an App rule is the application row ("app:x.exe"), a Website
    /// rule keeps its own id.
    public static string RowKeyOf(UserRule rule) =>
        rule.Type == UserRuleType.App ? AppPrefix + rule.Value.ToLowerInvariant() : UserRule.IdOf(rule);

    /// The entry id without the phone-list prefix: the same app or site counts under one row whichever list holds its rule.
    static string BareId(RuleEntry entry) =>
        entry.Id.StartsWith(PhonePrefix, StringComparison.Ordinal) ? entry.Id[PhonePrefix.Length..] : entry.Id;

    /// A user App rule (either list) shares the application row's key, so assigning or unassigning it never splits the history.
    static string KeyOf(RuleEntry entry)
    {
        var id = BareId(entry);
        return id.StartsWith(UserAppPrefix, StringComparison.Ordinal) ? AppPrefix + id[UserAppPrefix.Length..] : id;
    }
}
