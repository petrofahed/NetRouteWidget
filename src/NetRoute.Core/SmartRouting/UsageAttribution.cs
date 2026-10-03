namespace NetRoute.Core;

/// Which usage row a connection counts under, and what each row is called. A connection counts under exactly one row,
/// so the totals never double-count:
///   1. a rule that lists the connection's application (a built-in item such as OneDrive, or a user App rule);
///   2. otherwise a rule whose domains match the connection's host (YouTube, Facebook, a user Website rule);
///   3. otherwise the application itself ("app:chrome.exe");
///   4. otherwise "other".
/// Rules switched off still count, so a row keeps its history when the user moves it to the phone and back.
public sealed class UsageAttribution
{
    public const string OtherKey = "other";
    public const string UnattributedKey = "unattributed";
    public const string AppPrefix = "app:";
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
        foreach (var entry in all.Entries.Where(e => !e.Id.StartsWith(UserAppPrefix, StringComparison.Ordinal)))
            _names.TryAdd(KeyOf(entry), entry.Name);
    }

    public static UsageAttribution Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        new(RuleSet.BuildAll(catalog, settings));

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

    /// A user App rule shares the application row's key, so assigning or unassigning it never splits the history.
    static string KeyOf(RuleEntry entry) =>
        entry.Id.StartsWith(UserAppPrefix, StringComparison.Ordinal) ? AppPrefix + entry.Id[UserAppPrefix.Length..] : entry.Id;
}
