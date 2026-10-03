namespace NetRoute.Core.Tests;

public class RuleCatalogMergeTests
{
    static readonly IReadOnlyList<RuleItem> Builtin =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),
        new("steam", "games", "Games", "Steam", ["steam.exe"], [], true),
        new("claude", "ai", "AI", "Claude", ["claude.exe"], [], true, RouteExit.Phone),
    ];

    const string UserJson = """
        {"version":1,"groups":[
          {"id":"ai","name":"AI","exit":"phone","items":[
            {"id":"claude","name":"Claude (mine)","processes":["claude.exe","claude-code.exe"],"domains":["claude.ai"]},
            {"id":"mistral","name":"Mistral","domains":["mistral.ai"]}]},
          {"id":"mine","name":"My apps","items":[{"id":"nas","name":"NAS sync","processes":["nassync.exe"],"defaultOn":false}]},
          {"id":"games","name":"Games","items":[{"id":"steam","remove":true},{"id":"not-there","remove":true}]}
        ]}
        """;

    static IReadOnlyList<RuleItem> Merged()
    {
        var user = RuleCatalog.ParseUser(UserJson);
        return RuleCatalog.Merge(Builtin, user.Items, user.Removed);
    }

    [Fact]
    public void A_user_item_with_a_built_in_id_replaces_it_in_place()
    {
        var merged = Merged();

        var claude = merged.Single(i => i.Id == "claude");
        Assert.Equal("Claude (mine)", claude.Name);
        Assert.Equal(new[] { "claude.exe", "claude-code.exe" }, claude.Processes);
        Assert.Equal(RouteExit.Phone, claude.Exit); // the user's group says phone
        Assert.Equal(1, merged.ToList().FindIndex(i => i.Id == "claude")); // youtube, claude, ... (steam was removed)
    }

    [Fact]
    public void New_ids_are_added_and_a_new_group_comes_after_the_built_in_ones()
    {
        var merged = Merged();

        Assert.Equal(new[] { "youtube", "claude", "mistral", "nas" }, merged.Select(i => i.Id));
        var nas = merged.Single(i => i.Id == "nas");
        Assert.Equal(("mine", "My apps", RouteExit.Lan, false), (nas.GroupId, nas.GroupName, nas.Exit, nas.DefaultOn));
        Assert.Equal(RouteExit.Phone, merged.Single(i => i.Id == "mistral").Exit);
    }

    [Fact]
    public void Remove_deletes_a_built_in_item_and_an_unknown_id_is_ignored()
    {
        var merged = Merged();

        Assert.DoesNotContain(merged, i => i.Id == "steam");
        Assert.DoesNotContain(merged, i => i.Id == "not-there");
    }

    [Fact]
    public void An_id_that_is_both_removed_and_replaced_is_removed()
    {
        var user = RuleCatalog.ParseUser("""
            {"version":1,"groups":[{"id":"video","name":"Video","items":[
              {"id":"youtube","name":"YouTube (mine)","domains":["youtube.com"]},
              {"id":"youtube","remove":true}]}]}
            """);

        var merged = RuleCatalog.Merge(Builtin, user.Items, user.Removed);

        Assert.DoesNotContain(merged, i => i.Id == "youtube");
    }

    [Theory]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","items":[{"id":"x","name":"X","processes":[null]}]}]}""", "processes")]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","items":[{"id":"x","name":"X","domains":["  "]}]}]}""", "domains")]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","items":[{"id":"x","name":"X","domains":[5]}]}]}""", "domains")]
    public void A_null_blank_or_non_string_list_element_is_rejected_naming_the_property(string json, string property)
    {
        var ex = Assert.Throws<InvalidDataException>(() => RuleCatalog.Parse(json));
        Assert.Contains(property, ex.Message);
        Assert.Throws<InvalidDataException>(() => RuleCatalog.ParseUser(json));
    }

    [Theory]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","items":[{"id":"x","name":"X","processes":[null]}]}]}""")]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","items":[{"id":"x","name":"X","domains":["  "]}]}]}""")]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","items":[{"id":"x","name":"X","domains":[5]}]}]}""")]
    public void A_user_file_with_such_an_element_is_ignored_and_the_built_in_catalog_is_used(string userJson)
    {
        var dir = Directory.CreateTempSubdirectory("rules-merge-").FullName;
        try
        {
            var builtinPath = Path.Combine(dir, "builtin.json");
            var userPath = Path.Combine(dir, "rules.user.json");
            File.WriteAllText(builtinPath, """{"version":1,"groups":[{"id":"v","name":"V","items":[{"id":"youtube","name":"YouTube","domains":["youtube.com"]}]}]}""");
            File.WriteAllText(userPath, userJson);
            var logs = new List<string>();

            var items = RuleCatalog.LoadMerged(builtinPath, userPath, logs.Add);

            Assert.Equal(new[] { "youtube" }, items.Select(i => i.Id));
            Assert.Contains(logs, l => l.Contains("rules.user.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_empty_user_catalog_changes_nothing()
    {
        var user = RuleCatalog.ParseUser("""{"version":1,"groups":[]}""");

        Assert.Equal(Builtin, RuleCatalog.Merge(Builtin, user.Items, user.Removed));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","exit":"wifi","items":[{"id":"x","name":"X"}]}]}""")]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","items":[{"id":"x","name":"X"},{"id":"x","name":"Y"}]}]}""")]
    [InlineData("""{"version":1,"groups":[{"id":"g","name":"G","exit":"phone","carveOut":true,"items":[{"id":"x","name":"X"}]}]}""")]
    public void A_bad_user_file_is_reported_and_ignored_as_a_whole(string userJson)
    {
        var dir = Directory.CreateTempSubdirectory("rules-merge-").FullName;
        try
        {
            var builtinPath = Path.Combine(dir, "builtin.json");
            var userPath = Path.Combine(dir, "rules.user.json");
            File.WriteAllText(builtinPath, """{"version":1,"groups":[{"id":"v","name":"V","items":[{"id":"youtube","name":"YouTube","domains":["youtube.com"]}]}]}""");
            File.WriteAllText(userPath, userJson);
            var logs = new List<string>();

            var items = RuleCatalog.LoadMerged(builtinPath, userPath, logs.Add);

            Assert.Equal(new[] { "youtube" }, items.Select(i => i.Id));
            Assert.Contains(logs, l => l.Contains("rules.user.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_user_file_is_fine_and_a_good_one_is_merged()
    {
        var dir = Directory.CreateTempSubdirectory("rules-merge-").FullName;
        try
        {
            var builtinPath = Path.Combine(dir, "builtin.json");
            var userPath = Path.Combine(dir, "rules.user.json");
            File.WriteAllText(builtinPath, """{"version":1,"groups":[{"id":"v","name":"V","items":[{"id":"youtube","name":"YouTube","domains":["youtube.com"]}]}]}""");
            var logs = new List<string>();

            Assert.Single(RuleCatalog.LoadMerged(builtinPath, userPath, logs.Add));
            Assert.Empty(logs);

            File.WriteAllText(userPath, """{"version":1,"groups":[{"id":"m","name":"Mine","items":[{"id":"nas","name":"NAS","domains":["nas.local"]}]}]}""");
            Assert.Equal(new[] { "youtube", "nas" }, RuleCatalog.LoadMerged(builtinPath, userPath, logs.Add).Select(i => i.Id));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
