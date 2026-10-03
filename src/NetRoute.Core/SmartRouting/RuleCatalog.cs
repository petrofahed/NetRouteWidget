using System.Text.Json;

namespace NetRoute.Core;

/// One switchable thing with an exit: a LAN exception ("keep off 4G", the default) or a phone exception (used by the
/// LAN + exceptions profile), matched by process names and/or domain suffixes. A carve-out is a LAN item that stays
/// active in both profiles and is matched before the phone rules (e.g. an app's update download).
public sealed record RuleItem(
    string Id, string GroupId, string GroupName, string Name,
    IReadOnlyList<string> Processes, IReadOnlyList<string> Domains, bool DefaultOn,
    RouteExit Exit = RouteExit.Lan, bool CarveOut = false);

/// What a user rules file contributes: items to add or replace, and built-in ids to remove.
public sealed record UserCatalog(IReadOnlyList<RuleItem> Items, IReadOnlySet<string> Removed);

/// Built-in items shipped as rules/builtin.json next to the app, so lists can be updated without code changes.
public static class RuleCatalog
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "rules", "builtin.json");

    public static IReadOnlyList<RuleItem> Load(string path) => Parse(File.ReadAllText(path));

    /// Throws <see cref="InvalidDataException"/> for any malformed catalog (bad JSON, missing or wrongly typed fields, duplicate ids).
    public static IReadOnlyList<RuleItem> Parse(string json) => ReadCatalog(json, allowRemove: false).Items;

    /// Reads a user rules file: the same schema as the built-in catalog, plus <c>{ "id": "...", "remove": true }</c> items
    /// that name a built-in item to drop. Throws <see cref="InvalidDataException"/> exactly like <see cref="Parse"/>.
    public static UserCatalog ParseUser(string json) => ReadCatalog(json, allowRemove: true);

    /// Built-in items first, in their order; a user item with the same id replaces the built-in one in place; new ids follow
    /// (a new group after the built-in ones); removed ids are dropped.
    public static IReadOnlyList<RuleItem> Merge(IReadOnlyList<RuleItem> builtin, IReadOnlyList<RuleItem> user, IReadOnlySet<string> removed)
    {
        var replacements = user.ToDictionary(i => i.Id);
        var result = new List<RuleItem>();
        foreach (var item in builtin)
        {
            if (removed.Contains(item.Id)) continue;
            result.Add(replacements.TryGetValue(item.Id, out var mine) ? mine : item);
        }
        var known = builtin.Select(i => i.Id).ToHashSet();
        result.AddRange(user.Where(i => !known.Contains(i.Id) && !removed.Contains(i.Id)));
        return result;
    }

    /// The built-in catalog merged with the user's file. A missing user file changes nothing; a bad one is reported through
    /// log and ignored as a whole, so the user's own file can never stop Smart routing from starting.
    public static IReadOnlyList<RuleItem> LoadMerged(string builtinPath, string userPath, Action<string> log)
    {
        var builtin = Load(builtinPath);
        if (!File.Exists(userPath)) return builtin;
        try
        {
            var user = ParseUser(File.ReadAllText(userPath));
            return Merge(builtin, user.Items, user.Removed);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            log($"rules.user.json ignored ({ex.Message}); using the built-in rules only");
            return builtin;
        }
    }

    static UserCatalog ReadCatalog(string json, bool allowRemove)
    {
        var items = new List<RuleItem>();
        var removed = new HashSet<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var group in doc.RootElement.GetProperty("groups").EnumerateArray())
            {
                var groupId = Required(group, "id");
                var groupName = Required(group, "name");
                var groupExit = ExitOf(group, RouteExit.Lan);
                var groupCarveOut = Flag(group, "carveOut", false);
                foreach (var item in group.GetProperty("items").EnumerateArray())
                {
                    if (allowRemove && Flag(item, "remove", false))
                    {
                        removed.Add(Required(item, "id"));
                        continue;
                    }
                    var exit = ExitOf(item, groupExit);
                    var carveOut = Flag(item, "carveOut", groupCarveOut);
                    var id = Required(item, "id");
                    if (carveOut && exit != RouteExit.Lan)
                        throw new InvalidDataException($"Rule item '{id}' is a carve-out, which must route to the LAN");
                    items.Add(new RuleItem(
                        id, groupId, groupName, Required(item, "name"),
                        Strings(item, "processes"), Strings(item, "domains"),
                        !item.TryGetProperty("defaultOn", out var on) || on.GetBoolean(),
                        exit, carveOut));
                }
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            throw new InvalidDataException($"Invalid rule catalog: {ex.Message}", ex);
        }

        var duplicate = items.GroupBy(i => i.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate rule item id '{duplicate.Key}'");
        return new UserCatalog(items, removed);
    }

    static RouteExit ExitOf(JsonElement e, RouteExit fallback)
    {
        if (!e.TryGetProperty("exit", out var value)) return fallback;
        return value.GetString()?.ToLowerInvariant() switch
        {
            "lan" => RouteExit.Lan,
            "phone" => RouteExit.Phone,
            var other => throw new InvalidDataException($"Rule catalog 'exit' must be \"lan\" or \"phone\", not '{other}'"),
        };
    }

    static bool Flag(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;

    static string Required(JsonElement e, string name) =>
        e.GetProperty(name).GetString() ?? throw new InvalidDataException($"Rule catalog property '{name}' must be a string");

    /// A missing list is empty; a null, blank or non-string element is rejected (it would otherwise reach the sing-box config as a null string).
    static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr)) return [];
        var result = new List<string>();
        foreach (var element in arr.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
                throw new InvalidDataException($"Rule catalog property '{name}' must contain only non-blank strings");
            result.Add(element.GetString()!);
        }
        return result;
    }
}
