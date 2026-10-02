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

    public static IReadOnlyList<RuleItem> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<RuleItem>();
        foreach (var group in doc.RootElement.GetProperty("groups").EnumerateArray())
        {
            var groupId = group.GetProperty("id").GetString()!;
            var groupName = group.GetProperty("name").GetString()!;
            foreach (var item in group.GetProperty("items").EnumerateArray())
            {
                items.Add(new RuleItem(
                    item.GetProperty("id").GetString()!, groupId, groupName, item.GetProperty("name").GetString()!,
                    Strings(item, "processes"), Strings(item, "domains"),
                    !item.TryGetProperty("defaultOn", out var on) || on.GetBoolean()));
            }
        }

        var duplicate = items.GroupBy(i => i.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate rule item id '{duplicate.Key}'");
        return items;
    }

    static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) ? arr.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
}
