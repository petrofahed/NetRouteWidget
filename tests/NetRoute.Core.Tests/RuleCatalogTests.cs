namespace NetRoute.Core.Tests;

public class RuleCatalogTests
{
    [Fact]
    public void Parses_groups_and_items_with_defaults()
    {
        const string json = """
            { "version": 1, "groups": [
              { "id": "video", "name": "Video & social", "items": [
                { "id": "youtube", "name": "YouTube", "domains": ["youtube.com", "googlevideo.com"] },
                { "id": "facebook", "name": "Facebook", "domains": ["facebook.com"], "defaultOn": false } ] },
              { "id": "sync", "name": "Cloud sync", "items": [
                { "id": "onedrive", "name": "OneDrive", "processes": ["OneDrive.exe"], "domains": ["onedrive.live.com"] } ] } ] }
            """;

        var items = RuleCatalog.Parse(json);

        Assert.Equal(new[] { "youtube", "facebook", "onedrive" }, items.Select(i => i.Id));
        var yt = items[0];
        Assert.Equal(("video", "Video & social", "YouTube", true), (yt.GroupId, yt.GroupName, yt.Name, yt.DefaultOn));
        Assert.Empty(yt.Processes);
        Assert.Equal(new[] { "youtube.com", "googlevideo.com" }, yt.Domains);
        Assert.False(items[1].DefaultOn);
        Assert.Equal(new[] { "OneDrive.exe" }, items[2].Processes);
    }

    [Fact]
    public void Duplicate_item_ids_are_rejected()
    {
        const string json = """{ "groups": [ { "id": "g", "name": "G", "items": [ { "id": "x", "name": "A" }, { "id": "x", "name": "B" } ] } ] }""";

        Assert.Throws<InvalidDataException>(() => RuleCatalog.Parse(json));
    }

    [Fact]
    public void Shipped_catalog_has_the_spec_items_all_on_by_default()
    {
        var items = RuleCatalog.Load(RuleCatalog.DefaultPath);

        var ids = items.Select(i => i.Id).ToHashSet();
        foreach (var id in new[] { "windows-update", "microsoft-store", "onedrive", "google-drive", "dropbox", "icloud",
                                   "youtube", "facebook", "instagram", "steam", "epic", "battlenet", "xbox" })
            Assert.Contains(id, ids);
        Assert.All(items, i => Assert.True(i.DefaultOn));
        Assert.Contains("OneDrive.Sync.Service.exe", items.Single(i => i.Id == "onedrive").Processes); // spike finding
        Assert.Contains("googlevideo.com", items.Single(i => i.Id == "youtube").Domains);
        Assert.Empty(items.Single(i => i.Id == "windows-update").Processes); // svchost: domain-only
    }
}
