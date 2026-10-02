using System.Text.Json;

namespace NetRoute.Core;

/// One switchable thing that can be kept off 4G (e.g. "YouTube"), matched by process names and/or domain suffixes.
public sealed record RuleItem(
    string Id, string GroupId, string GroupName, string Name,
    IReadOnlyList<string> Processes, IReadOnlyList<string> Domains, bool DefaultOn);

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
                foreach (var item in group.GetProperty("items").EnumerateArray())
                {
                    items.Add(new RuleItem(
                        Required(item, "id"), groupId, groupName, Required(item, "name"),
                        Strings(item, "processes"), Strings(item, "domains"),
                        !item.TryGetProperty("defaultOn", out var on) || on.GetBoolean()));
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

    static string Required(JsonElement e, string name) =>
        e.GetProperty(name).GetString() ?? throw new InvalidDataException($"Rule catalog property '{name}' must be a string");

    static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) ? arr.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
}
