namespace NetRoute.Core.Tests;

public class RuleCatalogExitTests
{
    const string Json = """
        {"version":1,"groups":[
          {"id":"a","name":"A","items":[{"id":"a1","name":"A1","domains":["a.com"]}]},
          {"id":"p","name":"P","exit":"phone","items":[
            {"id":"p1","name":"P1","processes":["p.exe"]},
            {"id":"p2","name":"P2","exit":"lan","domains":["p2.com"]}]},
          {"id":"c","name":"C","carveOut":true,"items":[{"id":"c1","name":"C1","domains":["c.com"]}]}
        ]}
        """;

    static RuleItem Item(string id) => RuleCatalog.Parse(Json).Single(i => i.Id == id);

    [Fact]
    public void A_group_without_exit_defaults_to_lan_and_not_carve_out()
    {
        var item = Item("a1");

        Assert.Equal(RouteExit.Lan, item.Exit);
        Assert.False(item.CarveOut);
    }

    [Fact]
    public void A_group_exit_is_inherited_and_an_item_can_override_it()
    {
        Assert.Equal(RouteExit.Phone, Item("p1").Exit);
        Assert.Equal(RouteExit.Lan, Item("p2").Exit);
    }

    [Fact]
    public void A_carve_out_group_marks_its_items_and_stays_on_the_lan()
    {
        var item = Item("c1");

        Assert.True(item.CarveOut);
        Assert.Equal(RouteExit.Lan, item.Exit);
    }

    [Theory]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","exit":"wifi","items":[{"id":"x","name":"X"}]}]}""")]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","exit":"phone","carveOut":true,"items":[{"id":"x","name":"X"}]}]}""")]
    public void A_bad_exit_or_a_phone_carve_out_is_rejected(string json) =>
        Assert.Throws<InvalidDataException>(() => RuleCatalog.Parse(json));

    static string ShippedCatalogPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "NetRoute.Core", "rules", "builtin.json")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "NetRoute.Core", "rules", "builtin.json");
    }

    [Fact]
    public void The_shipped_catalog_has_the_ai_and_omantel_phone_items_and_the_lan_update_carve_outs()
    {
        var items = RuleCatalog.Load(ShippedCatalogPath()).ToDictionary(i => i.Id);

        foreach (var id in new[] { "claude", "chatgpt", "codex", "vscode", "copilot", "gemini", "cursor", "perplexity", "omantel" })
        {
            Assert.Equal(RouteExit.Phone, items[id].Exit);
            Assert.False(items[id].CarveOut);
            Assert.True(items[id].DefaultOn);
        }
        foreach (var id in new[] { "vscode-updates", "claude-updates", "codex-updates" })
        {
            Assert.Equal(RouteExit.Lan, items[id].Exit);
            Assert.True(items[id].CarveOut);
            Assert.True(items[id].DefaultOn);
            Assert.Empty(items[id].Processes); // a carve-out matches the update domain whichever process fetches it
        }
        Assert.Contains("omantel.om", items["omantel"].Domains);
        Assert.Contains("claude.exe", items["claude"].Processes);
        Assert.Contains("codex.exe", items["codex"].Processes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("code.exe", items["vscode"].Processes, StringComparer.OrdinalIgnoreCase);
        // The pre-v4 items are still LAN exceptions, not carve-outs.
        Assert.Equal(RouteExit.Lan, items["youtube"].Exit);
        Assert.False(items["youtube"].CarveOut);
    }
}
