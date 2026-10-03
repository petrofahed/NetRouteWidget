using System.Text.Json;

namespace NetRoute.Core;

/// One switchable thing with an exit: a LAN exception ("keep off 4G", the default) or a phone exception (used by the
/// LAN + exceptions profile), matched by process names and/or domain suffixes. A carve-out is a LAN item that stays
/// active in both profiles and is matched before the phone rules (e.g. an app's update download).
public sealed record RuleItem(
    string Id, string GroupId, string GroupName, string Name,
    IReadOnlyList<string> Processes, IReadOnlyList<string> Domains, bool DefaultOn,
    RouteExit Exit = RouteExit.Lan, bool CarveOut = false);

/// Built-in items shipped as rules/builtin.json next to the app, so lists can be updated without code changes.
public static class RuleCatalog
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "rules", "builtin.json");

    public static IReadOnlyList<RuleItem> Load(string path) => Parse(File.ReadAllText(path));

    /// Throws <see cref="InvalidDataException"/> for any malformed catalog (bad JSON, missing or wrongly typed fields, duplicate ids).
    public static IReadOnlyList<RuleItem> Parse(string json)
    {
        var items = new List<RuleItem>();
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
        return items;
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

    static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) ? arr.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
}
