# NetRoute Widget v3 — Usage & Routing Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One management window with two tabs, **Usage** (first) and **Config**. The Usage tab shows, per application or site, the bytes that went through the **phone** and through the **LAN** for a chosen date range, plus a live "Now" column and a "Goes via" drop-down that assigns the row to the phone or the LAN. The numbers are accurate: they are reconciled against sing-box's exact totals and Windows' own phone-adapter counters.

**Architecture:**
- All counting, attribution, reconciling, storage and report logic is pure and unit-tested in `NetRoute.Core`:
  - `UsageAttribution` (which row a connection belongs to), `UsageAssignment` ("Goes via" read/write on the settings);
  - `UsageReconciler` (unseen traffic split by exit), `UsageCounter` (per-day, per-row, per-exit totals and live rates), `UsageStore` (`usage.json`);
  - `UsageReport` (rows, sorting, totals, banners) and `UsageRowOrder` (rows do not jump under the cursor).
- `SmartRoutingController` owns the `UsageCounter` (replacing `DataSavedCounter`), polls it once a second through the Clash API and reads the phone adapter's byte counter through `IAdapterCounters`.
- The WPF app turns `SmartRoutingWindow` into a `TabControl`: a new `UsageTab` plus today's page as the Config tab (with "Clear usage history"). `App` runs the 1 s poll and the 30 s usage-file save.

**Tech Stack:** .NET 10 (C#), WPF (`ThemeMode="System"`), xUnit with `FakeTimeProvider`, sing-box **1.14.2** Clash API (`/connections`, with `uploadTotal` / `downloadTotal`).

**Spec:** `docs/superpowers/specs/2026-10-02-netroute-v3-usage-and-routing-design.md` (read it first; it is the authority). The v2 plan `docs/superpowers/plans/2026-10-02-netroute-v2-smart-routing.md` describes the code this builds on.

## Global Constraints

- **Branch:** `feat/v2-smart-routing` at `F:\source\petrofahed\NetRouteWidget` (v3 builds on the unmerged v2 work). Work only there; never push. All existing tests keep passing except the ones this plan deletes or edits (Task 6).
- **SDK and platform:** SDK pinned in `global.json` (10.0.401), projects target `net10.0-windows`. **No new NuGet packages.**
- **Never** launch, stop or touch the installed widget (`C:\Program Files\NetRouteWidget`). The user runs it. **Never** run sing-box with a TUN. **Never** run `tools\Restore-Network.cmd` without `/check`. Nothing in this plan may change the machine's network configuration.
- **App builds go only to the scratch folder** (never `src\NetRoute.App\bin\Debug`): `dotnet build src/NetRoute.App -c Release -o "<scratch>\nrw-v3-build"` where `<scratch>` is `F:\Temp\claude\C--Users-petrofahed\f7547ae8-854b-4cab-929e-e8437d2356f3\scratchpad`.
- **Unit tests:** `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`. Expected after every task: all green, **0 warnings**.
- **Culture:** every number or date written to a file or key uses `CultureInfo.InvariantCulture` (day keys are `yyyy-MM-dd`). Pinned by a test under `ar-SA` (Task 5).
- **Threading:** `UsageCounter` is not thread-safe. It is only touched under the `SmartRoutingController`'s gate (as `DataSavedCounter` was). Nothing in Core uses `.Result` or `.Wait()`; every `await` inside the controller keeps `ConfigureAwait(false)`.
- **Theme:** every colour in new WPF code comes from the theme palette (`SetResourceReference` / `DynamicResource` with keys from `Theme.cs`). No literal colours, no `Brushes.Gray`.
- **Privacy:** `usage.json` holds only row keys (application file names, item ids), day keys and byte totals. Never URLs or host lists.
- **Retention and caps:** 35 days (today plus 34 earlier days); at most 500 application/item rows per day, plus the two special rows `other` and `unattributed`.
- **Ranges:** the date-range choices are exactly 1 (shown as "Today"), 3, 7, 15 and 30 days. The default is 7. Range = today and the previous `n-1` local days.
- **Commit trailer** (verbatim on every commit):
  ```
  Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3
  ```
  PowerShell: `git commit -m "<subject>" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"`

## Review Focus

Inputs and conditions the spec implies but no happy-path test covers, most likely first. Each has a pinning test in the task that owns the code.

1. **sing-box restarts mid-day** (rule change, crash, phone replug): its totals go back to zero and every connection id is new. Expected: no negative numbers, no double counting, the day's history is kept. Pinned by Task 4 `Totals_going_backwards_after_a_restart_count_from_zero` and `Reset_baselines_counts_open_connections_again_without_losing_history`.
2. **Midnight while connections are open:** a long download continues across 00:00. Expected: its bytes after midnight land in the new day, nothing is lost or counted twice, and day 35+ is pruned. Pinned by Task 4 `A_download_crossing_midnight_splits_between_the_two_days` and `Days_older_than_the_retention_are_pruned_when_restored_and_on_rollover`.
3. **`usage.json` damaged or locked:** truncated JSON, a negative or absurd number, a wrong version, a file held open by another program. Expected: the widget keeps working; corrupt content is set aside as `usage.json.bad`; a locked file is left untouched and not overwritten this session. Pinned by Task 5 `Corrupt_file_is_set_aside_and_starts_empty`, `Negative_values_are_clamped_to_zero`, `Locked_file_is_reported_unreadable_and_left_untouched`.
4. **The Windows clock goes backwards** (DST, manual change, time sync). Expected: no exception; bytes go to the (earlier) day that the clock now says. Pinned by Task 4 `A_clock_that_goes_backwards_writes_to_the_earlier_day`.
5. **Thousands of open connections, a reused connection id, and a counter that moves backwards.** Expected: bounded work, counts stay correct. Pinned by Task 4 `Five_thousand_connections_are_counted_correctly` and `A_reused_id_with_smaller_counters_counts_as_a_new_connection`.

(Not unit-testable, so checked by hand in Task 8: a "Goes via" drop-down that is open while the page refreshes once a second must stay open and must not move.)

---

## File Structure

```
src/NetRoute.Core/
  SmartRouting/SingBoxConnection.cs        + ConnectionsSnapshot; ISingBoxApi.GetConnectionsAsync returns it        (Task 1)
  Windows/SingBoxApi.cs                    parses uploadTotal / downloadTotal                                       (Task 1)
  SmartRouting/RuleSet.cs                  + RuleSet.BuildAll (all rules, switched on or off)                       (Task 2)
  SmartRouting/SmartRoutingSettings.cs     + UsageRangeDays                                                         (Task 2)
  SmartRouting/UsageAttribution.cs         row keys + display names                                                 (Task 2)
  SmartRouting/UsageAssignment.cs          GoesVia, Describe, Set                                                   (Task 2)
  SmartRouting/UsageReconciler.cs          unseen traffic split by exit                                             (Task 3)
  SmartRouting/IAdapterCounters.cs         Windows byte counters for an adapter                                    (Task 3)
  Windows/WindowsAdapterCounters.cs        NetworkInterface byte counters                                           (Task 3)
  SmartRouting/UsageData.cs                Traffic, UsageRow, UsageDay, UsageRate, UsageSnapshot, DailyStats        (Tasks 4, 6)
  SmartRouting/UsageCounter.cs             UsagePoll, UsageCounter                                                  (Task 4)
  SmartRouting/UsageStore.cs               usage.json load/save                                                     (Task 5)
  SmartRouting/SmartRoutingController.cs   owns the UsageCounter; GetUsageAsync / ClearUsageAsync / TakeUsageIfChangedAsync (Task 6)
  SmartRouting/DataSavedCounter.cs         DELETED (replaced by UsageCounter)                                       (Task 6)
  SmartRouting/UsageReport.cs              UsageSort, UsageReportModel, UsageReport, UsageRowOrder                  (Task 7)
src/NetRoute.App/
  UsageTab.xaml(.cs)                       the Usage tab                                                            (Task 8)
  SmartRoutingWindow.xaml(.cs)             TabControl: Usage + Config; Clear usage history                          (Task 8)
  AppPaths.cs, App.xaml.cs                 usage.json path, 1 s poll, 30 s save, wiring                             (Task 9)
tools/Check-UsageAccuracy.ps1              read-only accuracy check on the real machine                             (Task 10)
tests/NetRoute.Core.Tests/
  SingBoxApiParseTests.cs  SmartFakes.cs  RuleSetTests.cs  SmartRoutingSettingsTests.cs
  UsageAttributionTests.cs  UsageAssignmentTests.cs  UsageReconcilerTests.cs  WindowsAdapterCountersTests.cs
  UsageCounterTests.cs  UsageStoreTests.cs  UsageReportTests.cs  SmartRoutingControllerTests.cs
  DataSavedCounterTests.cs                 DELETED (Task 6)
```

**Row keys** (used everywhere, defined once in `UsageAttribution`):
- a built-in item row: the item id, e.g. `youtube`, `onedrive`;
- a user website rule row: its rule id, e.g. `user:website:dropbox.com`;
- an application row: `app:<exe file name, lower case>`, e.g. `app:chrome.exe`. A **user App rule** for the same file keeps this key, so history stays continuous when the user assigns or unassigns it;
- `other` (no application and no matching host) and `unattributed` (bytes of connections the poll missed).

---

### Task 1: Clash API totals

sing-box's `/connections` lists only open connections, but its `uploadTotal` / `downloadTotal` include closed ones. Everything later needs those totals.

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/SingBoxConnection.cs`, `src/NetRoute.Core/Windows/SingBoxApi.cs`, `src/NetRoute.Core/SmartRouting/SmartRoutingController.cs` (one call site)
- Modify: `tests/NetRoute.Core.Tests/SmartFakes.cs`
- Test: `tests/NetRoute.Core.Tests/SingBoxApiParseTests.cs`

**Interfaces:**
- Produces: `public sealed record ConnectionsSnapshot(IReadOnlyList<SingBoxConnection> Connections, long? UploadTotal, long? DownloadTotal);` and `ISingBoxApi.GetConnectionsAsync(CancellationToken ct = default)` now returns `Task<ConnectionsSnapshot?>` (null = API unreachable). `SingBoxApi.ParseSnapshot(string json)` (`internal static`). The fake gets `ClosedUpload` / `ClosedDownload` (bytes of already-closed connections, added to the totals).

- [ ] **Step 1: Write the failing tests** — append to `tests/NetRoute.Core.Tests/SingBoxApiParseTests.cs`, inside the class:

```csharp
    [Fact]
    public void Parses_the_running_totals_that_include_closed_connections()
    {
        const string json = """{"connections":[],"downloadTotal":5390000,"uploadTotal":42000}""";

        var snapshot = SingBoxApi.ParseSnapshot(json);

        Assert.Empty(snapshot.Connections);
        Assert.Equal(5_390_000, snapshot.DownloadTotal);
        Assert.Equal(42_000, snapshot.UploadTotal);
    }

    [Theory]
    [InlineData("""{"connections":[]}""")]
    [InlineData("""{"connections":[],"downloadTotal":"x","uploadTotal":1}""")]
    [InlineData("""{"connections":[],"downloadTotal":1.5,"uploadTotal":1}""")]
    public void Missing_or_malformed_totals_are_null(string json) =>
        Assert.Null(SingBoxApi.ParseSnapshot(json).DownloadTotal);

    [Fact]
    public void A_snapshot_still_lists_the_open_connections()
    {
        const string json = """
            {"connections":[{"chains":["lan","lan-only"],"download":5,"id":"b","metadata":{"host":"x.com","processPath":"C:\\a\\Chrome.exe"},"upload":1}],
             "downloadTotal":50,"uploadTotal":10}
            """;

        var snapshot = SingBoxApi.ParseSnapshot(json);

        var c = Assert.Single(snapshot.Connections);
        Assert.Equal("Chrome.exe", c.ProcessName);
        Assert.Equal(50, snapshot.DownloadTotal);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~SingBoxApiParseTests"`
Expected: build FAIL — `'SingBoxApi' does not contain a definition for 'ParseSnapshot'`.

- [ ] **Step 3: Implement.**

`src/NetRoute.Core/SmartRouting/SingBoxConnection.cs`: add the record above `ISingBoxHost` and change the interface method:

```csharp
/// What one /connections poll returns. The totals are sing-box's running counters since it started and, unlike the list
/// of open connections, include connections that already closed. Null when the API did not report them.
public sealed record ConnectionsSnapshot(IReadOnlyList<SingBoxConnection> Connections, long? UploadTotal, long? DownloadTotal);
```

```csharp
    /// Null when the API cannot be reached.
    Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default);
```

`src/NetRoute.Core/Windows/SingBoxApi.cs`: replace `GetConnectionsAsync` and `ParseConnections` with:

```csharp
    public async Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default)
    {
        try
        {
            return ParseSnapshot(await _http.GetStringAsync("connections", ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    internal static IReadOnlyList<SingBoxConnection> ParseConnections(string json) => ParseSnapshot(json).Connections;

    /// Entries that are not shaped as expected are skipped rather than failing the whole poll.
    internal static ConnectionsSnapshot ParseSnapshot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new ConnectionsSnapshot([], null, null);
        long? Total(string name) => TryGetLong(root, name, out var value) ? value : null;
        return new ConnectionsSnapshot(ReadConnections(root), Total("uploadTotal"), Total("downloadTotal"));
    }

    static IReadOnlyList<SingBoxConnection> ReadConnections(JsonElement root)
    {
        if (!root.TryGetProperty("connections", out var list) || list.ValueKind != JsonValueKind.Array) return [];

        var result = new List<SingBoxConnection>();
        foreach (var c in list.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object
                || !c.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object
                || !c.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || !c.TryGetProperty("chains", out var chainsElement) || chainsElement.ValueKind != JsonValueKind.Array
                || !TryGetLong(c, "upload", out var upload) || !TryGetLong(c, "download", out var download))
                continue;

            var chains = new List<string>();
            foreach (var x in chainsElement.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) chains.Add(x.GetString()!);

            var host = GetString(meta, "host");
            var path = GetString(meta, "processPath");
            result.Add(new SingBoxConnection(idElement.GetString()!, host, path is null ? null : Path.GetFileName(path), chains, upload, download));
        }
        return result;
    }
```

(Keep `GetString` and `TryGetLong` as they are.)

`tests/NetRoute.Core.Tests/SmartFakes.cs`: in `FakeSingBoxApi`, replace the `GetConnectionsAsync` method with:

```csharp
    /// Bytes of connections that already closed: they are in the totals but no longer in Connections.
    public long ClosedUpload { get; set; }
    public long ClosedDownload { get; set; }

    public Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default)
    {
        if (ConnectionsUnreachable) return Task.FromResult<ConnectionsSnapshot?>(null);
        var open = Connections.ToList();
        return Task.FromResult<ConnectionsSnapshot?>(new ConnectionsSnapshot(
            open, open.Sum(c => c.Upload) + ClosedUpload, open.Sum(c => c.Download) + ClosedDownload));
    }
```

`src/NetRoute.Core/SmartRouting/SmartRoutingController.cs`, in `PollStatsAsync`: rename the pattern variable and the call:

```csharp
                if (await _api.GetConnectionsAsync(ct).ConfigureAwait(false) is not { } snapshot)
```
```csharp
                _counter.Update(snapshot.Connections, _rules, _appliedExit == RouteExit.Phone, Today());
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings (the 303 earlier tests plus the 5 new ones).

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): read the Clash API's running byte totals (they include closed connections)"
```
(add the trailer from Global Constraints)

---

### Task 2: Attribution, assignment and the remembered range

Pure logic: which row a connection counts under, what a row is called, and how "Goes via" reads and writes the settings.

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/RuleSet.cs`, `src/NetRoute.Core/SmartRouting/SmartRoutingSettings.cs`
- Create: `src/NetRoute.Core/SmartRouting/UsageAttribution.cs`, `src/NetRoute.Core/SmartRouting/UsageAssignment.cs`
- Test: `tests/NetRoute.Core.Tests/RuleSetTests.cs`, `tests/NetRoute.Core.Tests/SmartRoutingSettingsTests.cs`, `tests/NetRoute.Core.Tests/UsageAttributionTests.cs`, `tests/NetRoute.Core.Tests/UsageAssignmentTests.cs`

**Interfaces:**
- Consumes: `RuleItem`, `RuleEntry`, `RuleSet.FindByProcess/FindByHost`, `SmartRoutingSettings` (`IsItemOn`, `WithItem`, `WithUserRule`, `UserRules`), `UserRule` (`TryCreate`, `IdOf`).
- Produces:
  - `RuleSet.BuildAll(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings)`: every catalog item and every user rule, switched on or off.
  - `SmartRoutingSettings.UsageRangeDays` (`int`, default 7, included in `Equals`).
  - `UsageAttribution.Build(catalog, settings)`; `string Resolve(string? processName, string? host)`; `string DisplayName(string key)`; constants `OtherKey = "other"`, `UnattributedKey = "unattributed"`, `AppPrefix = "app:"`.
  - `enum GoesVia { Phone, Lan }`, `readonly record struct Assignment(GoesVia Via, bool CanChange)`, `UsageAssignment.Describe(catalog, settings, key)`, `UsageAssignment.Set(catalog, settings, key, bool toLan)`.

- [ ] **Step 1: Write the failing tests.**

Append to `tests/NetRoute.Core.Tests/RuleSetTests.cs` (inside the class; keep its existing usings and helpers; if the class has no catalog helper, add the local one shown):

```csharp
    [Fact]
    public void BuildAll_includes_items_and_rules_that_are_switched_off()
    {
        IReadOnlyList<RuleItem> catalog = [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];
        var settings = new SmartRoutingSettings
        {
            Items = new Dictionary<string, bool> { ["youtube"] = false },
            UserRules = [new UserRule(UserRuleType.App, "qbittorrent.exe", Enabled: false)],
        };

        var all = RuleSet.BuildAll(catalog, settings);
        var enabled = RuleSet.Build(catalog, settings);

        Assert.Equal(new[] { "youtube", "user:app:qbittorrent.exe" }, all.Entries.Select(e => e.Id));
        Assert.Empty(enabled.Entries);
    }
```

Append to `tests/NetRoute.Core.Tests/SmartRoutingSettingsTests.cs` (inside the class):

```csharp
    [Fact]
    public void Usage_range_defaults_to_seven_days_and_takes_part_in_equality()
    {
        Assert.Equal(7, new SmartRoutingSettings().UsageRangeDays);
        Assert.NotEqual(new SmartRoutingSettings(), new SmartRoutingSettings { UsageRangeDays = 30 });
    }

    [Fact]
    public void A_settings_file_without_the_usage_range_loads_with_the_default()
    {
        var loaded = System.Text.Json.JsonSerializer.Deserialize<SmartRoutingSettings>("""{"Enabled":true}""");

        Assert.Equal(7, loaded!.UsageRangeDays);
    }
```

`tests/NetRoute.Core.Tests/UsageAttributionTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class UsageAttributionTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com", "googlevideo.com"], true),
        new("onedrive", "sync", "Cloud sync", "OneDrive", ["OneDrive.exe"], ["onedrive.com"], true),
    ];

    static UsageAttribution Build(SmartRoutingSettings? settings = null) =>
        UsageAttribution.Build(Catalog, settings ?? new SmartRoutingSettings());

    [Fact]
    public void A_rule_that_lists_the_application_wins_over_a_domain_rule()
    {
        Assert.Equal("onedrive", Build().Resolve("OneDrive.exe", "www.youtube.com"));
    }

    [Theory]
    [InlineData("chrome.exe", "www.youtube.com")]
    [InlineData(null, "r1---sn.googlevideo.com")]
    public void A_domain_rule_claims_the_connection_when_no_process_rule_does(string? process, string host)
    {
        Assert.Equal("youtube", Build().Resolve(process, host));
    }

    [Fact]
    public void Other_traffic_of_an_application_counts_under_the_application_lower_cased()
    {
        Assert.Equal("app:chrome.exe", Build().Resolve("Chrome.EXE", "example.com"));
        Assert.Equal("app:chrome.exe", Build().Resolve("chrome.exe", null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "example.com")]
    [InlineData("", "")]
    [InlineData("  ", null)]
    public void Without_an_application_or_a_matching_host_the_row_is_Other(string? process, string? host)
    {
        Assert.Equal(UsageAttribution.OtherKey, Build().Resolve(process, host));
    }

    [Fact]
    public void A_user_app_rule_keeps_the_application_key_whether_it_is_on_or_off()
    {
        foreach (var enabled in new[] { true, false })
        {
            var settings = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.App, "qbittorrent.exe", enabled)] };

            Assert.Equal("app:qbittorrent.exe", Build(settings).Resolve("qBittorrent.exe", "tracker.example"));
        }
    }

    [Fact]
    public void A_switched_off_item_still_gets_its_row_so_the_phone_usage_is_visible()
    {
        var settings = new SmartRoutingSettings().WithItem("youtube", false);

        Assert.Equal("youtube", Build(settings).Resolve("chrome.exe", "www.youtube.com"));
    }

    [Fact]
    public void A_user_website_rule_is_a_row_of_its_own()
    {
        var settings = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.Website, "dropbox.com")] };

        Assert.Equal("user:website:dropbox.com", Build(settings).Resolve("chrome.exe", "dl.dropbox.com"));
    }

    [Fact]
    public void Display_names()
    {
        var settings = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.Website, "dropbox.com")] };
        var a = Build(settings);

        Assert.Equal("YouTube", a.DisplayName("youtube"));
        Assert.Equal("OneDrive", a.DisplayName("onedrive"));
        Assert.Equal("chrome", a.DisplayName("app:chrome.exe"));
        Assert.Equal("dropbox.com", a.DisplayName("user:website:dropbox.com"));
        Assert.Equal("Other", a.DisplayName(UsageAttribution.OtherKey));
        Assert.Equal("Unattributed (short connections)", a.DisplayName(UsageAttribution.UnattributedKey));
    }

    [Fact]
    public void A_row_from_history_whose_rule_was_deleted_still_has_a_readable_name()
    {
        var a = Build();

        Assert.Equal("gone.com", a.DisplayName("user:website:gone.com"));
        Assert.Equal("removed-item", a.DisplayName("removed-item"));
    }
}
```

`tests/NetRoute.Core.Tests/UsageAssignmentTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class UsageAssignmentTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
        [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];

    [Fact]
    public void A_built_in_item_row_follows_the_item_switch()
    {
        var on = new SmartRoutingSettings();
        var off = on.WithItem("youtube", false);

        Assert.Equal(new Assignment(GoesVia.Lan, true), UsageAssignment.Describe(Catalog, on, "youtube"));
        Assert.Equal(new Assignment(GoesVia.Phone, true), UsageAssignment.Describe(Catalog, off, "youtube"));
        Assert.False(UsageAssignment.Set(Catalog, on, "youtube", toLan: false).IsItemOn(Catalog[0]));
        Assert.True(UsageAssignment.Set(Catalog, off, "youtube", toLan: true).IsItemOn(Catalog[0]));
    }

    [Fact]
    public void An_application_row_without_a_rule_creates_one_when_sent_to_the_lan()
    {
        var none = new SmartRoutingSettings();

        Assert.Equal(new Assignment(GoesVia.Phone, true), UsageAssignment.Describe(Catalog, none, "app:chrome.exe"));
        var after = UsageAssignment.Set(Catalog, none, "app:chrome.exe", toLan: true);

        var rule = Assert.Single(after.UserRules);
        Assert.Equal(new UserRule(UserRuleType.App, "chrome.exe", true), rule);
        Assert.Equal(new Assignment(GoesVia.Lan, true), UsageAssignment.Describe(Catalog, after, "app:chrome.exe"));
    }

    [Fact]
    public void Sending_an_application_without_a_rule_to_the_phone_changes_nothing()
    {
        var none = new SmartRoutingSettings();

        Assert.Equal(none, UsageAssignment.Set(Catalog, none, "app:chrome.exe", toLan: false));
    }

    [Fact]
    public void An_application_row_with_a_rule_enables_and_disables_it_and_keeps_it_listed()
    {
        var on = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.App, "chrome.exe")] };

        var off = UsageAssignment.Set(Catalog, on, "app:chrome.exe", toLan: false);
        var rule = Assert.Single(off.UserRules);
        Assert.False(rule.Enabled);
        Assert.Equal(new Assignment(GoesVia.Phone, true), UsageAssignment.Describe(Catalog, off, "app:chrome.exe"));

        var back = UsageAssignment.Set(Catalog, off, "app:chrome.exe", toLan: true);
        Assert.True(Assert.Single(back.UserRules).Enabled);
    }

    [Fact]
    public void A_user_website_row_enables_and_disables_its_rule()
    {
        var on = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.Website, "dropbox.com")] };

        var off = UsageAssignment.Set(Catalog, on, "user:website:dropbox.com", toLan: false);

        Assert.False(Assert.Single(off.UserRules).Enabled);
        Assert.Equal(new Assignment(GoesVia.Phone, true), UsageAssignment.Describe(Catalog, off, "user:website:dropbox.com"));
    }

    [Theory]
    [InlineData("other")]
    [InlineData("unattributed")]
    [InlineData("user:website:not-a-rule.com")]
    [InlineData("app:")]
    [InlineData("app:notanexe")]
    public void Rows_that_cannot_be_assigned_are_locked_and_never_change_the_settings(string key)
    {
        var settings = new SmartRoutingSettings();

        Assert.False(UsageAssignment.Describe(Catalog, settings, key).CanChange);
        Assert.Equal(settings, UsageAssignment.Set(Catalog, settings, key, toLan: true));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageAttribution|FullyQualifiedName~UsageAssignment|FullyQualifiedName~RuleSetTests|FullyQualifiedName~SmartRoutingSettingsTests"`
Expected: build FAIL — `RuleSet` has no `BuildAll`; `UsageAttribution`, `UsageAssignment`, `Assignment`, `GoesVia` and `UsageRangeDays` do not exist.

- [ ] **Step 3: Implement.**

`src/NetRoute.Core/SmartRouting/RuleSet.cs`: replace the `Build` method with these three:

```csharp
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Create(catalog.Where(settings.IsItemOn), settings.UserRules.Where(r => r.Enabled));

    /// Every built-in item and every user rule, switched on or off. Usage uses it so that traffic of a switched-off
    /// item still lands in that item's row (it just goes through the phone).
    public static RuleSet BuildAll(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Create(catalog, settings.UserRules);

    static RuleSet Create(IEnumerable<RuleItem> items, IEnumerable<UserRule> userRules)
    {
        var entries = items.Select(i => new RuleEntry(i.Id, i.Name, i.Processes, i.Domains)).ToList();
        foreach (var rule in userRules)
        {
            entries.Add(rule.Type == UserRuleType.App
                ? new RuleEntry(UserRule.IdOf(rule), rule.Value, [rule.Value], [])
                : new RuleEntry(UserRule.IdOf(rule), rule.Value, [], [rule.Value]));
        }
        return new RuleSet(entries);
    }
```

`src/NetRoute.Core/SmartRouting/SmartRoutingSettings.cs`: in `SmartRoutingSettings` add the property after `LastLanInterface` and extend `Equals` / `GetHashCode`:

```csharp
    /// The Usage tab's date range in days (1 = Today, 3, 7, 15 or 30), remembered between runs.
    /// Not part of the rule fingerprint: changing it never restarts sing-box.
    public int UsageRangeDays { get; init; } = 7;
```
```csharp
        other is not null && Enabled == other.Enabled && LastLanInterface == other.LastLanInterface && UsageRangeDays == other.UsageRangeDays
```
(i.e. add `&& UsageRangeDays == other.UsageRangeDays` to the existing `Equals` expression; leave `GetHashCode` as is.)

`src/NetRoute.Core/SmartRouting/UsageAttribution.cs`:

```csharp
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
```

`src/NetRoute.Core/SmartRouting/UsageAssignment.cs`:

```csharp
namespace NetRoute.Core;

public enum GoesVia { Phone, Lan }

/// Where a usage row's traffic goes, and whether the user may change it.
public readonly record struct Assignment(GoesVia Via, bool CanChange);

/// The "Goes via" drop-down: reads and writes the same switches as the Config tab.
public static class UsageAssignment
{
    public static Assignment Describe(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string key)
    {
        if (catalog.FirstOrDefault(i => i.Id == key) is { } item) return new(Via(settings.IsItemOn(item)), true);
        if (AppExe(key) is { } exe) return new(Via(FindAppRule(settings, exe)?.Enabled == true), true);
        if (FindWebsiteRule(settings, key) is { } site) return new(Via(site.Enabled), true);
        return new(GoesVia.Phone, false);
    }

    /// Returns the settings unchanged for a row that cannot be assigned, and for "to the phone" on an application that has no rule.
    public static SmartRoutingSettings Set(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string key, bool toLan)
    {
        if (catalog.Any(i => i.Id == key)) return settings.WithItem(key, toLan);
        if (AppExe(key) is { } exe)
        {
            if (FindAppRule(settings, exe) is { } rule) return settings.WithUserRule(rule with { Enabled = toLan });
            return toLan && UserRule.TryCreate(UserRuleType.App, exe, out var created, out _)
                ? settings.WithUserRule(created!)
                : settings;
        }
        return FindWebsiteRule(settings, key) is { } site ? settings.WithUserRule(site with { Enabled = toLan }) : settings;
    }

    static GoesVia Via(bool lan) => lan ? GoesVia.Lan : GoesVia.Phone;

    /// The exe file name of an "app:" row, or null when the key is not an application row or the name is not a valid exe name.
    static string? AppExe(string key)
    {
        if (!key.StartsWith(UsageAttribution.AppPrefix, StringComparison.Ordinal)) return null;
        var exe = key[UsageAttribution.AppPrefix.Length..];
        return UserRule.TryCreate(UserRuleType.App, exe, out _, out _) ? exe : null;
    }

    static UserRule? FindAppRule(SmartRoutingSettings settings, string exe) =>
        settings.UserRules.FirstOrDefault(r => r.Type == UserRuleType.App && string.Equals(r.Value, exe, StringComparison.OrdinalIgnoreCase));

    static UserRule? FindWebsiteRule(SmartRoutingSettings settings, string key) =>
        settings.UserRules.FirstOrDefault(r => r.Type == UserRuleType.Website && UserRule.IdOf(r) == key);
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): usage attribution, Goes-via assignment and the remembered range"
```
(add the trailer)


### Task 3: Reconciling the bytes the poll missed

sing-box only shows connections that are open at the moment of a poll. The Clash totals know every byte; Windows' own counter for the phone adapter knows exactly what went through the phone. This task turns those into "unseen bytes per exit". It is pure arithmetic plus one small Windows adapter.

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/UsageReconciler.cs`, `src/NetRoute.Core/SmartRouting/IAdapterCounters.cs`, `src/NetRoute.Core/Windows/WindowsAdapterCounters.cs`
- Test: `tests/NetRoute.Core.Tests/UsageReconcilerTests.cs`, `tests/NetRoute.Core.Tests/WindowsAdapterCountersTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct UnseenSplit(long Phone, long Lan);`
  - `UsageReconciler.Split(long totalDelta, long seenDelta, long seenPhoneDelta, long? phoneAdapterDelta, bool phoneIsDefault) → UnseenSplit`:
    - `unseen = max(0, totalDelta - seenDelta)`;
    - with an adapter delta: `phone = max(0, adapterDelta - seenPhoneDelta)` (framing overhead lands here on purpose: it is real 4G usage), `lan = max(0, unseen - phone)`;
    - without one: everything unseen goes to the default exit.
  - `public interface IAdapterCounters { long? TotalBytes(string adapterName); }` (sent + received since the adapter came up; null when the adapter is not found), and `WindowsAdapterCounters : IAdapterCounters` in `NetRoute.Core.Windows`.

- [ ] **Step 1: Write the failing tests.**

`tests/NetRoute.Core.Tests/UsageReconcilerTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class UsageReconcilerTests
{
    [Fact]
    public void Nothing_missed_and_no_overhead_is_all_zero()
    {
        Assert.Equal(new UnseenSplit(0, 0), UsageReconciler.Split(1000, 1000, 1000, 1000, phoneIsDefault: true));
    }

    [Fact]
    public void Framing_overhead_on_the_adapter_lands_in_the_phone_share()
    {
        // 1000 payload bytes seen on the phone; Windows counted 1040 (headers, handshakes). Nothing was missed.
        Assert.Equal(new UnseenSplit(40, 0), UsageReconciler.Split(1000, 1000, 1000, 1040, phoneIsDefault: true));
    }

    [Fact]
    public void Everything_missed_on_the_phone_goes_to_the_phone()
    {
        Assert.Equal(new UnseenSplit(2000, 0), UsageReconciler.Split(2000, 0, 0, 2000, phoneIsDefault: true));
    }

    [Fact]
    public void The_rest_of_the_missed_bytes_is_the_lan_share()
    {
        // 1500 missed bytes; the phone adapter grew by 1000 beyond what the open connections explain.
        Assert.Equal(new UnseenSplit(1000, 500), UsageReconciler.Split(1900, 400, 300, 1300, phoneIsDefault: true));
    }

    [Fact]
    public void An_adapter_delta_smaller_than_the_attributed_phone_bytes_means_no_missed_phone_bytes()
    {
        Assert.Equal(new UnseenSplit(0, 600), UsageReconciler.Split(1000, 400, 300, 100, phoneIsDefault: true));
    }

    [Theory]
    [InlineData(true, 3000, 0)]
    [InlineData(false, 0, 3000)]
    public void Without_the_adapter_counter_the_missed_bytes_follow_the_default_exit(bool phoneIsDefault, long phone, long lan)
    {
        Assert.Equal(new UnseenSplit(phone, lan), UsageReconciler.Split(4000, 1000, 1000, null, phoneIsDefault));
    }

    [Fact]
    public void Totals_that_are_smaller_than_what_was_seen_never_go_negative()
    {
        Assert.Equal(new UnseenSplit(0, 0), UsageReconciler.Split(100, 500, 500, null, phoneIsDefault: true));
        Assert.Equal(new UnseenSplit(0, 0), UsageReconciler.Split(-5, 0, 0, null, phoneIsDefault: false));
    }
}
```

`tests/NetRoute.Core.Tests/WindowsAdapterCountersTests.cs`:

```csharp
using System.Net.NetworkInformation;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class WindowsAdapterCountersTests
{
    [Fact]
    public void An_unknown_adapter_has_no_counters()
    {
        Assert.Null(new WindowsAdapterCounters().TotalBytes("No such adapter " + Guid.NewGuid()));
    }

    [Fact]
    public void A_real_adapter_reports_a_non_negative_total()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        if (nic is null) return; // a machine with no live adapter at all: nothing to read

        var total = new WindowsAdapterCounters().TotalBytes(nic.Name);

        Assert.NotNull(total);
        Assert.True(total >= 0);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageReconciler|FullyQualifiedName~WindowsAdapterCounters"`
Expected: build FAIL — `UsageReconciler`, `UnseenSplit`, `WindowsAdapterCounters` do not exist.

- [ ] **Step 3: Implement.**

`src/NetRoute.Core/SmartRouting/UsageReconciler.cs`:

```csharp
namespace NetRoute.Core;

/// Bytes the connection poll could not attribute to any connection, split by exit.
public readonly record struct UnseenSplit(long Phone, long Lan);

/// Turns "what sing-box says passed in total" and "what Windows says the phone adapter carried" into the bytes that
/// the per-connection numbers of one poll missed (connections that opened and closed between two polls).
public static class UsageReconciler
{
    public static UnseenSplit Split(long totalDelta, long seenDelta, long seenPhoneDelta, long? phoneAdapterDelta, bool phoneIsDefault)
    {
        var unseen = Math.Max(0, totalDelta - seenDelta);
        if (phoneAdapterDelta is not { } adapter)
        {
            var phone = phoneIsDefault ? unseen : 0;
            return new UnseenSplit(phone, unseen - phone);
        }
        // Protocol framing makes the adapter's count a few percent larger than sing-box's payload count. That difference
        // is real mobile data, so it stays in the phone share rather than being trimmed away.
        var unseenPhone = Math.Max(0, adapter - seenPhoneDelta);
        return new UnseenSplit(unseenPhone, Math.Max(0, unseen - unseenPhone));
    }
}
```

`src/NetRoute.Core/SmartRouting/IAdapterCounters.cs`:

```csharp
namespace NetRoute.Core;

/// Windows' own byte counters for an adapter: exact, per adapter, independent of sing-box.
public interface IAdapterCounters
{
    /// Bytes sent plus received since the adapter came up; null when no adapter has that name.
    long? TotalBytes(string adapterName);
}
```

`src/NetRoute.Core/Windows/WindowsAdapterCounters.cs`:

```csharp
using System.Net.NetworkInformation;

namespace NetRoute.Core.Windows;

public sealed class WindowsAdapterCounters : IAdapterCounters
{
    public long? TotalBytes(string adapterName)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(nic.Name, adapterName, StringComparison.OrdinalIgnoreCase)) continue;
                var stats = nic.GetIPStatistics();
                return stats.BytesReceived + stats.BytesSent;
            }
            return null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): reconcile missed bytes against the Clash totals and the phone adapter's Windows counter"
```
(add the trailer)

---

### Task 4: The usage counter

Per local day, per row, per exit, byte totals from the 1 s polls, with the unseen bytes added as an "Unattributed" row, the live rates for "Now", and the kept-off-4G number.

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/UsageData.cs`, `src/NetRoute.Core/SmartRouting/UsageCounter.cs`
- Test: `tests/NetRoute.Core.Tests/UsageCounterTests.cs`

**Interfaces:**
- Consumes: `SingBoxConnection` (`Id`, `Host`, `ProcessName`, `Chains`, `Upload`, `Download`, `IsLanOnly`), `UsageAttribution.Resolve`, `UsageAttribution.OtherKey/UnattributedKey`, `UsageReconciler.Split`, `RouteExit`, `DailyStats` (still defined in `DataSavedCounter.cs` until Task 6).
- Produces (`UsageData.cs`):
  - `readonly record struct Traffic(long Up, long Down)` with `Total` and `operator +`;
  - `sealed record UsageRow(Traffic Phone, Traffic Lan, long Kept)` with `Total`, `operator +` and `static readonly UsageRow Empty`;
  - `sealed record UsageDay(IReadOnlyDictionary<string, UsageRow> Rows, long PhoneAdapterBytes, int Gaps = 0)` (`Gaps` = how many times sing-box stopped or restarted that day: the bytes in the moments before each stop were not recorded, so the kept-off-4G figure is then "at least");
  - `sealed record UsageRate(RouteExit Exit, long BytesPerSecond)`;
  - `sealed record UsageSnapshot(IReadOnlyDictionary<DateOnly, UsageDay> Days, IReadOnlyDictionary<string, UsageRate> Rates)` with `const int RetentionDays = 35` and `static UsageSnapshot Empty`.
- Produces (`UsageCounter.cs`):
  - `sealed record UsagePoll(IReadOnlyList<SingBoxConnection> Connections, long? UploadTotal, long? DownloadTotal, long? PhoneAdapterBytes, bool PhoneIsDefault, DateTimeOffset Now)`; `Now` is the **local** time (its date is the day the bytes belong to);
  - `sealed class UsageCounter(UsageSnapshot? restored, DateOnly today)` with `const int MaxRowsPerDay = 500`, `static readonly TimeSpan RateWindow` (3 s), `const long ActiveBytesPerSecond = 1024`, `void Update(UsagePoll, UsageAttribution)`, `UsageSnapshot Snapshot()`, `DailyStats TodayKept(DateOnly today)`, `bool TakeDirty()`, `void Clear()` (wipes the history only; open connections are not recounted), `void ResetBaselines()` (forgets per-connection and total baselines, the adapter reading and the rates; history stays; called when sing-box restarts), `void NoteGap(DateOnly day)` (adds one to that day's `Gaps`; called when a running sing-box goes away).

- [ ] **Step 1: Write the failing tests** — `tests/NetRoute.Core.Tests/UsageCounterTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class UsageCounterTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
        [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];
    static readonly UsageAttribution Attr = UsageAttribution.Build(Catalog, new SmartRoutingSettings());
    static readonly DateOnly D0 = new(2026, 10, 2);
    static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    static SingBoxConnection Conn(string id, long up, long down, string exit = "phone", string? host = null,
                                  string? process = null, bool lanOnly = false) =>
        new(id, host, process, lanOnly ? ["lan", "lan-only"] : [exit, "default"], up, down);

    /// Totals default to the sum of the open connections, i.e. no closed connections.
    static UsagePoll Poll(DateTimeOffset now, SingBoxConnection[] conns, long? up = null, long? down = null,
                          long? adapter = null, bool phoneIsDefault = true) =>
        new(conns, up ?? conns.Sum(c => c.Upload), down ?? conns.Sum(c => c.Download), adapter, phoneIsDefault, now);

    static UsageRow Row(UsageCounter counter, DateOnly day, string key) => counter.Snapshot().Days[day].Rows[key];

    static UsageSnapshot SnapshotWith(DateOnly day, string key, long bytes) =>
        new(new Dictionary<DateOnly, UsageDay>
        {
            [day] = new(new Dictionary<string, UsageRow> { [key] = new(new Traffic(0, bytes), default, 0) }, 0),
        }, new Dictionary<string, UsageRate>());

    [Fact]
    public void A_first_sighting_counts_the_current_totals_and_later_polls_only_the_growth()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 10, 90, host: "youtube.com", process: "chrome.exe")]), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 15, 190, host: "youtube.com", process: "chrome.exe")]), Attr);

        Assert.Equal(new UsageRow(new Traffic(15, 190), default, 0), Row(counter, D0, "youtube"));
    }

    [Fact]
    public void The_exit_is_the_first_entry_of_the_chain()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 100, exit: "lan", process: "steam.exe"), Conn("2", 0, 7, exit: "phone", process: "steam.exe")]), Attr);

        var row = Row(counter, D0, "app:steam.exe");
        Assert.Equal(new Traffic(0, 100), row.Lan);
        Assert.Equal(new Traffic(0, 7), row.Phone);
    }

    [Fact]
    public void Lan_only_bytes_are_kept_off_4G_only_while_the_phone_is_the_default_exit()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, host: "youtube.com", lanOnly: true)], phoneIsDefault: true), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 1500, host: "youtube.com", lanOnly: true)], phoneIsDefault: false), Attr);

        var row = Row(counter, D0, "youtube");

        Assert.Equal(1500, row.Lan.Total);
        Assert.Equal(1000, row.Kept);
    }

    [Fact]
    public void A_reused_id_with_smaller_counters_counts_as_a_new_connection()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 1000, 0, process: "a.exe")]), Attr);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 5, 0, process: "a.exe")]), Attr);

        Assert.Equal(1005, Row(counter, D0, "app:a.exe").Phone.Up);
    }

    [Fact]
    public void A_connection_that_closes_and_a_new_one_with_the_same_id_later_is_counted_in_full()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 100, process: "a.exe")]), Attr);
        counter.Update(Poll(T0.AddSeconds(1), []), Attr);

        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 40, process: "a.exe")]), Attr);

        Assert.Equal(140, Row(counter, D0, "app:a.exe").Phone.Down);
    }

    [Fact]
    public void Connections_that_moved_no_data_create_no_row()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")]), Attr);

        Assert.Empty(counter.Snapshot().Days);
        Assert.False(counter.TakeDirty());
    }

    [Fact]
    public void Bytes_of_closed_connections_become_an_unattributed_row_taken_from_the_phone_adapter()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")], adapter: 50_000), Attr);

        // Nothing grew on the open connection, but the totals grew by 2000: connections that closed between polls.
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 1000, process: "a.exe")], down: 3000, adapter: 52_000), Attr);

        var unattributed = Row(counter, D0, UsageAttribution.UnattributedKey);
        Assert.Equal(new Traffic(0, 2000), unattributed.Phone);
        Assert.Equal(default, unattributed.Lan);
        Assert.Equal(2000, counter.Snapshot().Days[D0].PhoneAdapterBytes);
    }

    [Fact]
    public void Missed_bytes_the_phone_adapter_does_not_explain_are_lan_and_count_as_kept_while_the_phone_is_the_default()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 100, process: "a.exe")], adapter: 10_000), Attr);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 100, process: "a.exe")], down: 1600, adapter: 11_000), Attr);

        var unattributed = Row(counter, D0, UsageAttribution.UnattributedKey);
        Assert.Equal(new Traffic(0, 1000), unattributed.Phone);
        Assert.Equal(new Traffic(0, 500), unattributed.Lan);
        Assert.Equal(500, unattributed.Kept);
    }

    [Theory]
    [InlineData(true, 3000, 0)]
    [InlineData(false, 0, 3000)]
    public void Without_a_phone_adapter_reading_missed_bytes_follow_the_default_exit(bool phoneIsDefault, long phone, long lan)
    {
        var counter = new UsageCounter(null, D0);

        // First poll after sing-box started: 4000 bytes went through in total, one open connection explains 1000.
        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")], down: 4000, adapter: null, phoneIsDefault: phoneIsDefault), Attr);

        var unattributed = Row(counter, D0, UsageAttribution.UnattributedKey);
        Assert.Equal(phone, unattributed.Phone.Total);
        Assert.Equal(lan, unattributed.Lan.Total);
    }

    [Fact]
    public void An_adapter_counter_that_moves_backwards_is_a_new_baseline_not_negative_traffic()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 100, process: "a.exe")], adapter: 9_000_000), Attr);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 100, process: "a.exe")], adapter: 500), Attr);
        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 100, process: "a.exe")], adapter: 900), Attr);

        Assert.Equal(400, counter.Snapshot().Days[D0].PhoneAdapterBytes);
    }

    [Fact]
    public void Totals_going_backwards_after_a_restart_count_from_zero()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")]), Attr);

        // sing-box restarted without the caller resetting: every total, and every id, starts over.
        counter.Update(Poll(T0.AddSeconds(1), [Conn("9", 0, 300, process: "a.exe")]), Attr);

        var snapshot = counter.Snapshot();
        Assert.Equal(1300, snapshot.Days[D0].Rows.Values.Sum(r => r.Phone.Total));
        Assert.DoesNotContain(snapshot.Days[D0].Rows.Values, r => r.Phone.Up < 0 || r.Phone.Down < 0);
    }

    [Fact]
    public void Reset_baselines_counts_open_connections_again_without_losing_history()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.NotEmpty(counter.Snapshot().Rates);

        counter.ResetBaselines();
        Assert.Empty(counter.Snapshot().Rates);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("2", 0, 400, process: "a.exe")]), Attr);

        Assert.Equal(3_000_400, Row(counter, D0, "app:a.exe").Phone.Down);
    }

    [Fact]
    public void Clear_wipes_the_history_but_does_not_recount_connections_that_are_still_open()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 5_000_000, process: "a.exe")]), Attr);

        counter.Clear();
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 5_000_000, process: "a.exe")]), Attr);

        Assert.Empty(counter.Snapshot().Days);
        Assert.True(counter.TakeDirty());
    }

    [Fact]
    public void TakeDirty_is_true_once_after_a_change()
    {
        var counter = new UsageCounter(null, D0);
        Assert.False(counter.TakeDirty());

        counter.Update(Poll(T0, [Conn("1", 0, 10, process: "a.exe")]), Attr);

        Assert.True(counter.TakeDirty());
        Assert.False(counter.TakeDirty());
    }

    // ---- rates ("Now") ----

    [Fact]
    public void A_busy_row_has_a_rate_over_the_last_three_seconds_that_fades_when_it_stops()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, host: "youtube.com", lanOnly: true)]), Attr);

        Assert.Equal(new UsageRate(RouteExit.Lan, 1_000_000), counter.Snapshot().Rates["youtube"]);

        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 3_000_000, host: "youtube.com", lanOnly: true)]), Attr);
        Assert.True(counter.Snapshot().Rates.ContainsKey("youtube")); // still inside the window

        counter.Update(Poll(T0.AddSeconds(3), [Conn("1", 0, 3_000_000, host: "youtube.com", lanOnly: true)]), Attr);
        Assert.False(counter.Snapshot().Rates.ContainsKey("youtube"));
    }

    [Fact]
    public void A_trickle_below_one_kilobyte_per_second_is_idle()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")]), Attr); // 333 B/s over the window

        Assert.Empty(counter.Snapshot().Rates);
    }

    [Fact]
    public void Unattributed_bytes_never_show_as_a_live_rate()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [], down: 9_000_000, adapter: null), Attr);

        Assert.Empty(counter.Snapshot().Rates);
    }

    // ---- days ----

    [Fact]
    public void A_download_crossing_midnight_splits_between_the_two_days()
    {
        var counter = new UsageCounter(null, D0);
        var before = new DateTimeOffset(2026, 10, 2, 23, 59, 59, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 10, 3, 0, 0, 1, TimeSpan.Zero);

        counter.Update(Poll(before, [Conn("1", 0, 100, process: "a.exe")]), Attr);
        counter.Update(Poll(after, [Conn("1", 0, 150, process: "a.exe")]), Attr);

        Assert.Equal(100, Row(counter, D0, "app:a.exe").Phone.Down);
        Assert.Equal(50, Row(counter, D0.AddDays(1), "app:a.exe").Phone.Down);
    }

    [Fact]
    public void Days_older_than_the_retention_are_pruned_when_restored_and_on_rollover()
    {
        var oneRow = SnapshotWith(D0, "x", 1).Days[D0];
        var days = new Dictionary<DateOnly, UsageDay>();
        foreach (var offset in new[] { 40, 35, 34, 0 }) days[D0.AddDays(-offset)] = oneRow;
        var counter = new UsageCounter(new UsageSnapshot(days, new Dictionary<string, UsageRate>()), D0);

        Assert.Equal(new[] { D0.AddDays(-34), D0 }, counter.Snapshot().Days.Keys.Order());

        counter.Update(Poll(T0.AddDays(1), [Conn("1", 0, 10, process: "a.exe")]), Attr);

        Assert.DoesNotContain(D0.AddDays(-34), counter.Snapshot().Days.Keys);
        Assert.Contains(D0, counter.Snapshot().Days.Keys);
        Assert.Contains(D0.AddDays(1), counter.Snapshot().Days.Keys);
    }

    [Fact]
    public void A_clock_that_goes_backwards_writes_to_the_earlier_day()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0.AddDays(1), [Conn("1", 0, 100, process: "a.exe")]), Attr);

        counter.Update(Poll(T0, [Conn("1", 0, 160, process: "a.exe")]), Attr);

        Assert.Equal(100, Row(counter, D0.AddDays(1), "app:a.exe").Phone.Down);
        Assert.Equal(60, Row(counter, D0, "app:a.exe").Phone.Down);
    }

    [Fact]
    public void A_gap_is_recorded_on_the_day_and_survives_a_snapshot_round_trip()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 10, process: "a.exe")]), Attr);

        counter.NoteGap(D0);
        counter.NoteGap(D0);

        Assert.Equal(2, counter.Snapshot().Days[D0].Gaps);
        Assert.Equal(2, new UsageCounter(counter.Snapshot(), D0).Snapshot().Days[D0].Gaps);
        Assert.True(counter.TakeDirty());
    }

    // ---- scale ----

    [Fact]
    public void Five_thousand_connections_are_counted_correctly()
    {
        var counter = new UsageCounter(null, D0);
        var conns = Enumerable.Range(0, 5000).Select(i => Conn("c" + i, 1, 1)).ToArray();

        counter.Update(Poll(T0, conns), Attr);
        counter.Update(Poll(T0.AddSeconds(1), conns.Select(c => c with { Upload = 2, Download = 3 }).ToArray()), Attr);

        Assert.Equal(new Traffic(10_000, 15_000), Row(counter, D0, UsageAttribution.OtherKey).Phone);
    }

    [Fact]
    public void Rows_beyond_the_cap_fold_into_Other_and_no_bytes_are_lost()
    {
        var counter = new UsageCounter(null, D0);
        var conns = Enumerable.Range(0, UsageCounter.MaxRowsPerDay + 100).Select(i => Conn("c" + i, 0, 10, process: $"app{i}.exe")).ToArray();

        counter.Update(Poll(T0, conns), Attr);

        var rows = counter.Snapshot().Days[D0].Rows;
        Assert.True(rows.Count <= UsageCounter.MaxRowsPerDay + 2);
        Assert.Equal(10L * conns.Length, rows.Values.Sum(r => r.Phone.Total));
        Assert.True(rows[UsageAttribution.OtherKey].Phone.Total >= 1000);
    }

    // ---- today's kept-off-4G figure and restoring ----

    [Fact]
    public void TodayKept_lists_the_kept_bytes_per_row_including_the_unattributed_share()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, host: "youtube.com", lanOnly: true)], adapter: 10_000), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 1000, host: "youtube.com", lanOnly: true)], down: 1500, adapter: 10_000), Attr);

        var kept = counter.TodayKept(D0);

        Assert.Equal(D0, kept.Day);
        Assert.Equal(1000, kept.BytesByEntry["youtube"]);
        Assert.Equal(500, kept.BytesByEntry[UsageAttribution.UnattributedKey]);
        Assert.Equal(1500, kept.Total);
    }

    [Fact]
    public void TodayKept_of_a_day_with_no_data_is_empty()
    {
        Assert.Equal(0, new UsageCounter(null, D0).TodayKept(D0).Total);
    }

    [Fact]
    public void A_restored_snapshot_continues_where_it_left_off()
    {
        var counter = new UsageCounter(SnapshotWith(D0, "youtube", 700), D0);

        counter.Update(Poll(T0, [Conn("1", 0, 300, host: "youtube.com")]), Attr);

        Assert.Equal(1000, Row(counter, D0, "youtube").Phone.Down);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageCounterTests"`
Expected: build FAIL — `UsageCounter`, `UsagePoll`, `Traffic`, `UsageRow` … do not exist.

- [ ] **Step 3: Implement.**

`src/NetRoute.Core/SmartRouting/UsageData.cs`:

```csharp
namespace NetRoute.Core;

/// Bytes in each direction.
public readonly record struct Traffic(long Up, long Down)
{
    public long Total => Up + Down;
    public static Traffic operator +(Traffic a, Traffic b) => new(a.Up + b.Up, a.Down + b.Down);
}

/// One row's bytes for one day: through the phone, through the LAN, and the part of the LAN bytes that LAN-only rules
/// kept off 4G (LAN bytes that arrived while the phone was the default exit).
public sealed record UsageRow(Traffic Phone, Traffic Lan, long Kept)
{
    public static readonly UsageRow Empty = new(default, default, 0);
    public long Total => Phone.Total + Lan.Total;
    public static UsageRow operator +(UsageRow a, UsageRow b) => new(a.Phone + b.Phone, a.Lan + b.Lan, a.Kept + b.Kept);
}

/// One local day: its rows by row key, the bytes Windows counted on the phone adapter while Smart routing was recording,
/// and how many times sing-box stopped or restarted (the bytes just before each stop were not recorded).
public sealed record UsageDay(IReadOnlyDictionary<string, UsageRow> Rows, long PhoneAdapterBytes, int Gaps = 0);

/// A row's live rate over the last few seconds and the exit most of it used.
public sealed record UsageRate(RouteExit Exit, long BytesPerSecond);

/// An immutable copy of everything the counter knows. Safe to hand to the UI thread.
public sealed record UsageSnapshot(IReadOnlyDictionary<DateOnly, UsageDay> Days, IReadOnlyDictionary<string, UsageRate> Rates)
{
    /// Today plus the 34 days before it.
    public const int RetentionDays = 35;

    public static UsageSnapshot Empty { get; } =
        new(new Dictionary<DateOnly, UsageDay>(), new Dictionary<string, UsageRate>());
}
```

`src/NetRoute.Core/SmartRouting/UsageCounter.cs`:

```csharp
namespace NetRoute.Core;

/// What one poll of the Clash API saw. Now is the LOCAL time: its date is the day the bytes belong to.
public sealed record UsagePoll(
    IReadOnlyList<SingBoxConnection> Connections, long? UploadTotal, long? DownloadTotal,
    long? PhoneAdapterBytes, bool PhoneIsDefault, DateTimeOffset Now);

/// Counts bytes per local day, per row, per exit.
///   - Per-connection growth since the previous poll goes to the connection's row and exit.
///   - Bytes the poll missed (connections that opened and closed between two polls) are found by comparing the Clash
///     totals with what the open connections explain, and go to the "unattributed" row, split by exit with the help of
///     the phone adapter's Windows counter (see UsageReconciler).
///   - Live rates ("Now") are the bytes seen over the last RateWindow.
/// Not thread-safe: callers must serialize all calls (the SmartRoutingController does this under its gate).
public sealed class UsageCounter
{
    public const int MaxRowsPerDay = 500;
    public const long ActiveBytesPerSecond = 1024;
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(3);

    sealed class Cell
    {
        public long PhoneUp, PhoneDown, LanUp, LanDown, Kept;
    }

    sealed class DayData
    {
        public readonly Dictionary<string, Cell> Rows = new();
        public long PhoneAdapterBytes;
        public int Gaps;
    }

    readonly SortedDictionary<DateOnly, DayData> _days = new();
    readonly Dictionary<string, (long Up, long Down)> _lastSeen = new();
    readonly Dictionary<string, Queue<(DateTimeOffset At, RouteExit Exit, long Bytes)>> _recent = new();
    Dictionary<string, UsageRate> _rates = new();
    long? _lastUp, _lastDown, _lastAdapter;
    DateOnly _today;
    bool _dirty;

    public UsageCounter(UsageSnapshot? restored, DateOnly today)
    {
        _today = today;
        if (restored is not null)
        {
            foreach (var (day, data) in restored.Days)
            {
                var d = new DayData { PhoneAdapterBytes = data.PhoneAdapterBytes, Gaps = data.Gaps };
                foreach (var (key, row) in data.Rows)
                {
                    d.Rows[key] = new Cell
                    {
                        PhoneUp = row.Phone.Up, PhoneDown = row.Phone.Down, LanUp = row.Lan.Up, LanDown = row.Lan.Down, Kept = row.Kept,
                    };
                }
                _days[day] = d;
            }
        }
        Prune();
    }

    public void Update(UsagePoll poll, UsageAttribution attribution)
    {
        var day = DateOnly.FromDateTime(poll.Now.DateTime);
        if (day != _today)
        {
            _today = day;
            Prune();
        }
        var dayData = DayFor(day);
        var rows = dayData.Rows;

        long seenUp = 0, seenDown = 0, seenPhone = 0;
        var alive = new HashSet<string>();
        foreach (var c in poll.Connections)
        {
            alive.Add(c.Id);
            var (lastUp, lastDown) = _lastSeen.GetValueOrDefault(c.Id);
            var up = Grow(c.Upload, lastUp);
            var down = Grow(c.Download, lastDown);
            _lastSeen[c.Id] = (c.Upload, c.Download);
            if (up + down <= 0) continue;

            var exit = c.Chains.Count > 0 && c.Chains[0] == SingBoxConfigBuilder.LanTag ? RouteExit.Lan : RouteExit.Phone;
            var key = attribution.Resolve(c.ProcessName, c.Host);
            var kept = exit == RouteExit.Lan && c.IsLanOnly && poll.PhoneIsDefault ? up + down : 0;
            Add(rows, key, exit, new Traffic(up, down), kept);
            AddRate(key, exit, up + down, poll.Now);
            seenUp += up;
            seenDown += down;
            if (exit == RouteExit.Phone) seenPhone += up + down;
        }
        foreach (var gone in _lastSeen.Keys.Where(id => !alive.Contains(id)).ToList()) _lastSeen.Remove(gone);

        long? adapterDelta = null;
        if (poll.PhoneAdapterBytes is { } adapterNow && _lastAdapter is { } adapterBefore && adapterNow >= adapterBefore)
            adapterDelta = adapterNow - adapterBefore;
        _lastAdapter = poll.PhoneAdapterBytes; // a counter that went backwards (adapter reset) is just a new baseline
        if (adapterDelta is { } counted)
        {
            dayData.PhoneAdapterBytes += counted;
            _dirty = true;
        }

        if (poll.UploadTotal is { } totalUp && poll.DownloadTotal is { } totalDown)
        {
            // The first poll after (re)start has no earlier total: everything since sing-box started counts.
            var upDelta = Grow(totalUp, _lastUp ?? 0);
            var downDelta = Grow(totalDown, _lastDown ?? 0);
            _lastUp = totalUp;
            _lastDown = totalDown;

            var split = UsageReconciler.Split(upDelta + downDelta, seenUp + seenDown, seenPhone, adapterDelta, poll.PhoneIsDefault);
            var unseenUp = Math.Max(0, upDelta - seenUp);
            var unseenDown = Math.Max(0, downDelta - seenDown);
            Add(rows, UsageAttribution.UnattributedKey, RouteExit.Phone, Spread(split.Phone, unseenUp, unseenDown), kept: 0);
            Add(rows, UsageAttribution.UnattributedKey, RouteExit.Lan, Spread(split.Lan, unseenUp, unseenDown),
                kept: poll.PhoneIsDefault ? split.Lan : 0);
        }
        else
        {
            _lastUp = _lastDown = null;
        }

        RebuildRates(poll.Now);
    }

    /// An immutable copy of everything.
    public UsageSnapshot Snapshot()
    {
        var days = new Dictionary<DateOnly, UsageDay>();
        foreach (var (day, data) in _days)
        {
            if (data.Rows.Count == 0 && data.PhoneAdapterBytes == 0) continue;
            var rows = data.Rows.ToDictionary(
                r => r.Key,
                r => new UsageRow(new Traffic(r.Value.PhoneUp, r.Value.PhoneDown), new Traffic(r.Value.LanUp, r.Value.LanDown), r.Value.Kept));
            days[day] = new UsageDay(rows, data.PhoneAdapterBytes, data.Gaps);
        }
        return new UsageSnapshot(days, new Dictionary<string, UsageRate>(_rates));
    }

    /// Today's kept-off-4G bytes per row (the card's "kept off 4G today" figure).
    public DailyStats TodayKept(DateOnly today)
    {
        var kept = new Dictionary<string, long>();
        if (_days.TryGetValue(today, out var data))
            foreach (var (key, cell) in data.Rows)
                if (cell.Kept > 0) kept[key] = cell.Kept;
        return new DailyStats(today, kept);
    }

    /// True once after any change since the last call: the signal to save the file.
    public bool TakeDirty()
    {
        var dirty = _dirty;
        _dirty = false;
        return dirty;
    }

    /// Wipes the history. Connections that are still open are not recounted from zero (their baselines stay).
    public void Clear()
    {
        _days.Clear();
        _dirty = true;
    }

    /// A running sing-box went away (stopped, crashed, restarted): the bytes between the last poll and that moment were
    /// not recorded. The day is marked so the kept-off-4G figure can say "at least".
    public void NoteGap(DateOnly day)
    {
        DayFor(day).Gaps++;
        _dirty = true;
    }

    /// Forgets what the next poll would be compared with (per-connection bytes, totals, the adapter reading, rates).
    /// Called when sing-box restarts or cannot be reached. The history stays.
    public void ResetBaselines()
    {
        _lastSeen.Clear();
        _recent.Clear();
        _rates = new Dictionary<string, UsageRate>();
        _lastUp = _lastDown = _lastAdapter = null;
    }

    /// Growth of a counter. A first sighting counts the whole value; a counter that went backwards (a reused id, a
    /// restarted sing-box) starts over from its new value.
    static long Grow(long now, long last) => now < 0 ? 0 : now >= last ? now - last : now;

    /// Splits bytes over up and down in the proportion of the missed up/down bytes; all of it goes to down when unknown.
    static Traffic Spread(long bytes, long up, long down)
    {
        if (bytes <= 0) return default;
        var sum = up + down;
        if (sum <= 0) return new Traffic(0, bytes);
        var upShare = (long)Math.Round((double)bytes * up / sum);
        return new Traffic(upShare, bytes - upShare);
    }

    DayData DayFor(DateOnly day)
    {
        if (!_days.TryGetValue(day, out var data)) _days[day] = data = new DayData();
        return data;
    }

    void Add(Dictionary<string, Cell> rows, string key, RouteExit exit, Traffic traffic, long kept)
    {
        if (traffic.Total <= 0 && kept <= 0) return;
        // A new row beyond the cap folds into "Other"; the two special rows are always allowed.
        if (!rows.ContainsKey(key) && rows.Count >= MaxRowsPerDay
            && key is not (UsageAttribution.OtherKey or UsageAttribution.UnattributedKey))
            key = UsageAttribution.OtherKey;
        if (!rows.TryGetValue(key, out var cell)) rows[key] = cell = new Cell();
        if (exit == RouteExit.Phone)
        {
            cell.PhoneUp += traffic.Up;
            cell.PhoneDown += traffic.Down;
        }
        else
        {
            cell.LanUp += traffic.Up;
            cell.LanDown += traffic.Down;
        }
        cell.Kept += kept;
        _dirty = true;
    }

    void AddRate(string key, RouteExit exit, long bytes, DateTimeOffset at)
    {
        if (!_recent.TryGetValue(key, out var queue)) _recent[key] = queue = new();
        queue.Enqueue((at, exit, bytes));
    }

    void RebuildRates(DateTimeOffset now)
    {
        var rates = new Dictionary<string, UsageRate>();
        foreach (var (key, queue) in _recent.ToList())
        {
            while (queue.Count > 0 && now - queue.Peek().At >= RateWindow) queue.Dequeue();
            if (queue.Count == 0)
            {
                _recent.Remove(key);
                continue;
            }
            long phone = 0, lan = 0;
            foreach (var entry in queue)
            {
                if (entry.Exit == RouteExit.Lan) lan += entry.Bytes;
                else phone += entry.Bytes;
            }
            var perSecond = (phone + lan) / (long)RateWindow.TotalSeconds;
            if (perSecond >= ActiveBytesPerSecond) rates[key] = new UsageRate(lan > phone ? RouteExit.Lan : RouteExit.Phone, perSecond);
        }
        _rates = rates;
    }

    void Prune()
    {
        var cutoff = _today.AddDays(-(UsageSnapshot.RetentionDays - 1));
        foreach (var old in _days.Keys.Where(d => d < cutoff).ToList()) _days.Remove(old);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): usage counter with exit attribution, unattributed row, live rates and retention"
```
(add the trailer)

---

### Task 5: The usage file

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/UsageStore.cs`
- Test: `tests/NetRoute.Core.Tests/UsageStoreTests.cs`

**Interfaces:**
- Consumes: `UsageSnapshot`, `UsageDay`, `UsageRow`, `Traffic`, `UsageCounter.MaxRowsPerDay`, `UsageAttribution.OtherKey/UnattributedKey`.
- Produces: `sealed record UsageLoadResult(UsageSnapshot Usage, bool Unreadable)`; `UsageStore.Load(string path, DateOnly today) → UsageLoadResult`; `UsageStore.Save(string path, UsageSnapshot usage)` (atomic).

Rules (spec "Storage"): `{ "version": 1, "days": { "yyyy-MM-dd": { "rows": { "<key>": { "phone": {"up","down"}, "lan": {"up","down"}, "kept": n } }, "phoneAdapter": n, "gaps": n } } }`, camelCase. Load: missing file → empty. Locked or access denied → `Unreadable = true`, file untouched. Corrupt (bad JSON, wrong version, bad day key) → renamed to `usage.json.bad` (overwriting an older `.bad`), empty result. Negative numbers are clamped to 0. Days older than the retention are dropped; a day with more than 500 normal rows keeps the 500 largest and folds the rest into `other`.

(This refines the spec's structure by nesting each day's rows under `rows` and adding the day's `phoneAdapter` bytes and each row's `kept`; Task 10 updates the spec text.)

- [ ] **Step 1: Write the failing tests** — `tests/NetRoute.Core.Tests/UsageStoreTests.cs`:

```csharp
using System.Globalization;

namespace NetRoute.Core.Tests;

public sealed class UsageStoreTests : IDisposable
{
    static readonly DateOnly D0 = new(2026, 10, 2);
    readonly string _dir = Directory.CreateTempSubdirectory("usage-store-").FullName;
    string FilePath => Path.Combine(_dir, "usage.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static UsageSnapshot Sample() => new(
        new Dictionary<DateOnly, UsageDay>
        {
            [D0] = new(new Dictionary<string, UsageRow>
            {
                ["youtube"] = new(new Traffic(1, 2), new Traffic(3, 4), 5),
                ["app:chrome.exe"] = new(new Traffic(10, 20), default, 0),
            }, 777, 2),
        },
        new Dictionary<string, UsageRate>());

    [Fact]
    public void A_saved_snapshot_loads_back_the_same()
    {
        UsageStore.Save(FilePath, Sample());

        var result = UsageStore.Load(FilePath, D0);

        Assert.False(result.Unreadable);
        var day = Assert.Single(result.Usage.Days);
        Assert.Equal(D0, day.Key);
        Assert.Equal(777, day.Value.PhoneAdapterBytes);
        Assert.Equal(2, day.Value.Gaps);
        Assert.Equal(new UsageRow(new Traffic(1, 2), new Traffic(3, 4), 5), day.Value.Rows["youtube"]);
        Assert.Equal(new UsageRow(new Traffic(10, 20), default, 0), day.Value.Rows["app:chrome.exe"]);
    }

    [Fact]
    public void The_file_has_the_documented_shape_and_leaves_no_temp_file()
    {
        UsageStore.Save(FilePath, Sample());

        var text = File.ReadAllText(FilePath);

        Assert.Contains("\"version\":1", text);
        Assert.Contains("\"2026-10-02\"", text);
        Assert.Contains("\"phoneAdapter\":777", text);
        Assert.Contains("\"gaps\":2", text);
        Assert.Contains("\"kept\":5", text);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Day_keys_and_numbers_are_culture_invariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA"); // Hijri calendar, Arabic digits
            UsageStore.Save(FilePath, Sample());
            var text = File.ReadAllText(FilePath);
            var loaded = UsageStore.Load(FilePath, D0);

            Assert.Contains("\"2026-10-02\"", text);
            Assert.Equal(D0, Assert.Single(loaded.Usage.Days).Key);
            Assert.Equal(777, loaded.Usage.Days[D0].PhoneAdapterBytes);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void A_missing_file_is_an_empty_history()
    {
        var result = UsageStore.Load(FilePath, D0);

        Assert.Empty(result.Usage.Days);
        Assert.False(result.Unreadable);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"version":2,"days":{}}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"days":{"yesterday":{"rows":{},"phoneAdapter":0}}}""")]
    public void Corrupt_file_is_set_aside_and_starts_empty(string content)
    {
        File.WriteAllText(FilePath, content);

        var result = UsageStore.Load(FilePath, D0);

        Assert.Empty(result.Usage.Days);
        Assert.False(result.Unreadable);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(content, File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void A_second_corrupt_file_replaces_the_first_bad_copy()
    {
        File.WriteAllText(FilePath + ".bad", "old");
        File.WriteAllText(FilePath, "{ broken");

        UsageStore.Load(FilePath, D0);

        Assert.Equal("{ broken", File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void Negative_values_are_clamped_to_zero()
    {
        File.WriteAllText(FilePath,
            """{"version":1,"days":{"2026-10-02":{"rows":{"x":{"phone":{"up":-5,"down":7},"lan":{"up":0,"down":0},"kept":-1}},"phoneAdapter":-9}}}""");

        var day = UsageStore.Load(FilePath, D0).Usage.Days[D0];

        Assert.Equal(new UsageRow(new Traffic(0, 7), default, 0), day.Rows["x"]);
        Assert.Equal(0, day.PhoneAdapterBytes);
    }

    [Fact]
    public void A_row_with_missing_parts_loads_as_zeros()
    {
        File.WriteAllText(FilePath, """{"version":1,"days":{"2026-10-02":{"rows":{"x":{"kept":3}},"phoneAdapter":1}}}""");

        Assert.Equal(new UsageRow(default, default, 3), UsageStore.Load(FilePath, D0).Usage.Days[D0].Rows["x"]);
    }

    [Fact]
    public void Locked_file_is_reported_unreadable_and_left_untouched()
    {
        UsageStore.Save(FilePath, Sample());
        var before = File.ReadAllText(FilePath);

        UsageLoadResult result;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            result = UsageStore.Load(FilePath, D0);

        Assert.True(result.Unreadable);
        Assert.Empty(result.Usage.Days);
        Assert.Equal(before, File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Days_older_than_the_retention_are_dropped_on_load()
    {
        var row = Sample().Days[D0];
        var days = new Dictionary<DateOnly, UsageDay>();
        foreach (var offset in new[] { 40, 35, 34, 0 }) days[D0.AddDays(-offset)] = row;
        UsageStore.Save(FilePath, new UsageSnapshot(days, new Dictionary<string, UsageRate>()));

        var loaded = UsageStore.Load(FilePath, D0).Usage;

        Assert.Equal(new[] { D0.AddDays(-34), D0 }, loaded.Days.Keys.Order());
    }

    [Fact]
    public void A_day_with_too_many_rows_keeps_the_largest_and_folds_the_rest_into_Other()
    {
        var rows = new Dictionary<string, UsageRow>();
        for (var i = 0; i < UsageCounter.MaxRowsPerDay + 20; i++)
            rows["app:a" + i + ".exe"] = new UsageRow(new Traffic(0, i + 1), default, 0);
        rows[UsageAttribution.UnattributedKey] = new UsageRow(new Traffic(0, 99_999), default, 0);
        var total = rows.Values.Sum(r => r.Total);
        UsageStore.Save(FilePath, new UsageSnapshot(
            new Dictionary<DateOnly, UsageDay> { [D0] = new(rows, 0) }, new Dictionary<string, UsageRate>()));

        var loaded = UsageStore.Load(FilePath, D0).Usage.Days[D0].Rows;

        Assert.Equal(UsageCounter.MaxRowsPerDay + 2, loaded.Count); // the cap, plus "other" and "unattributed"
        Assert.Equal(total, loaded.Values.Sum(r => r.Total));
        Assert.False(loaded.ContainsKey("app:a0.exe")); // the smallest rows folded away
        Assert.Equal(Enumerable.Range(1, 20).Sum(), loaded[UsageAttribution.OtherKey].Total);
    }

    [Fact]
    public void Empty_days_are_not_written()
    {
        UsageStore.Save(FilePath, new UsageSnapshot(
            new Dictionary<DateOnly, UsageDay> { [D0] = new(new Dictionary<string, UsageRow>(), 0) }, new Dictionary<string, UsageRate>()));

        Assert.Empty(UsageStore.Load(FilePath, D0).Usage.Days);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageStoreTests"`
Expected: build FAIL — `UsageStore`, `UsageLoadResult` do not exist.

- [ ] **Step 3: Implement** — `src/NetRoute.Core/SmartRouting/UsageStore.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace NetRoute.Core;

/// Unreadable: the file exists but could not be read (locked, access denied). It was left untouched, so the caller must
/// not save over it this session (the same rule as settings.json).
public sealed record UsageLoadResult(UsageSnapshot Usage, bool Unreadable);

/// Reads and writes %AppData%\NetRouteWidget\usage.json. Only row keys, day keys and byte totals are stored.
public static class UsageStore
{
    const int Version = 1;
    const string DayFormat = "yyyy-MM-dd";

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal sealed record FileDto(int Version, Dictionary<string, DayDto>? Days);
    internal sealed record DayDto(Dictionary<string, RowDto>? Rows, long PhoneAdapter, int Gaps);
    internal sealed record RowDto(TrafficDto? Phone, TrafficDto? Lan, long Kept);
    internal sealed record TrafficDto(long Up, long Down);

    public static UsageLoadResult Load(string path, DateOnly today)
    {
        if (!File.Exists(path)) return new(UsageSnapshot.Empty, false);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(UsageSnapshot.Empty, true);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<FileDto>(text, Options);
            if (dto is not { Version: Version, Days: { } days }) throw new JsonException("Unsupported usage file");
            return new(Prune(Read(days), today), false);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            SetAside(path);
            return new(UsageSnapshot.Empty, false);
        }
    }

    /// Writes to a temp file first so a crash never leaves a half-written file.
    public static void Save(string path, UsageSnapshot usage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var days = new Dictionary<string, DayDto>();
        foreach (var (day, data) in usage.Days)
        {
            if (data.Rows.Count == 0 && data.PhoneAdapterBytes == 0) continue;
            days[day.ToString(DayFormat, CultureInfo.InvariantCulture)] = new DayDto(
                data.Rows.ToDictionary(
                    r => r.Key,
                    r => new RowDto(
                        new TrafficDto(r.Value.Phone.Up, r.Value.Phone.Down),
                        new TrafficDto(r.Value.Lan.Up, r.Value.Lan.Down),
                        r.Value.Kept)),
                data.PhoneAdapterBytes, data.Gaps);
        }
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileDto(Version, days), Options));
        File.Move(temp, path, overwrite: true);
    }

    static UsageSnapshot Read(Dictionary<string, DayDto> days)
    {
        var result = new Dictionary<DateOnly, UsageDay>();
        foreach (var (dayText, dto) in days)
        {
            if (!DateOnly.TryParseExact(dayText, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new FormatException($"Bad day key '{dayText}'");
            var rows = new Dictionary<string, UsageRow>();
            foreach (var (key, row) in dto?.Rows ?? new())
            {
                if (row is null) continue;
                rows[key] = new UsageRow(Clamp(row.Phone), Clamp(row.Lan), Math.Max(0, row.Kept));
            }
            result[day] = new UsageDay(rows, Math.Max(0, dto?.PhoneAdapter ?? 0), Math.Max(0, dto?.Gaps ?? 0));
        }
        return new UsageSnapshot(result, new Dictionary<string, UsageRate>());
    }

    static Traffic Clamp(TrafficDto? t) => t is null ? default : new Traffic(Math.Max(0, t.Up), Math.Max(0, t.Down));

    /// Drops days past the retention and, for a hand-edited or runaway day, folds the smallest rows into "other".
    static UsageSnapshot Prune(UsageSnapshot snapshot, DateOnly today)
    {
        var cutoff = today.AddDays(-(UsageSnapshot.RetentionDays - 1));
        var days = new Dictionary<DateOnly, UsageDay>();
        foreach (var (day, data) in snapshot.Days)
            if (day >= cutoff) days[day] = CapRows(data);
        return new UsageSnapshot(days, snapshot.Rates);
    }

    static UsageDay CapRows(UsageDay day)
    {
        var normal = day.Rows
            .Where(r => r.Key is not (UsageAttribution.OtherKey or UsageAttribution.UnattributedKey))
            .OrderByDescending(r => r.Value.Total).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        if (normal.Count <= UsageCounter.MaxRowsPerDay) return day;

        var rows = day.Rows
            .Where(r => r.Key is UsageAttribution.OtherKey or UsageAttribution.UnattributedKey)
            .ToDictionary(r => r.Key, r => r.Value);
        foreach (var (key, row) in normal.Take(UsageCounter.MaxRowsPerDay)) rows[key] = row;
        var overflow = normal.Skip(UsageCounter.MaxRowsPerDay).Aggregate(UsageRow.Empty, (sum, r) => sum + r.Value);
        rows[UsageAttribution.OtherKey] = rows.GetValueOrDefault(UsageAttribution.OtherKey, UsageRow.Empty) + overflow;
        return new UsageDay(rows, day.PhoneAdapterBytes, day.Gaps);
    }

    static void SetAside(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not move it away: the next save overwrites the corrupt file, which is fine.
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): usage.json store (atomic, culture-invariant, corrupt/locked safe, 35-day retention, 500-row cap)"
```
(add the trailer)


### Task 6: The controller records usage (replaces `DataSavedCounter`)

`SmartRoutingController` owns a `UsageCounter`, feeds it from the Clash API and the phone adapter's Windows counter, derives the card's "kept off 4G today" from it, and exposes the history to the UI. `DataSavedCounter` and `stats.json` go away (the old file is simply ignored; its few MB of history are not migrated). The App is updated just enough to keep building and saving; Task 9 does the 1 s cadence and the window wiring.

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/SmartRoutingController.cs`, `src/NetRoute.Core/SmartRouting/UsageData.cs` (gains `DailyStats`), `src/NetRoute.Core/SmartRouting/UsageCounter.cs` (gains `ClearRates`)
- Delete: `src/NetRoute.Core/SmartRouting/DataSavedCounter.cs`, `tests/NetRoute.Core.Tests/DataSavedCounterTests.cs`
- Modify: `src/NetRoute.App/App.xaml.cs`, `src/NetRoute.App/AppPaths.cs`
- Modify: `tests/NetRoute.Core.Tests/SmartFakes.cs`, `tests/NetRoute.Core.Tests/SmartRoutingControllerTests.cs`, `tests/NetRoute.Core.Tests/UsageCounterTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces:
  - constructor `SmartRoutingController(IReadOnlyList<RuleItem> catalog, ISingBoxHost host, Func<int, string, ISingBoxApi> createApi, Func<int> freePort, UsageSnapshot? restoredUsage, Action<string> log, TimeProvider? time = null, IAdapterCounters? counters = null)` (the 5th parameter changed from `DailyStats?`; `counters` is new and last);
  - `Task<UsageSnapshot> GetUsageAsync()`, `Task<UsageSnapshot?> TakeUsageIfChangedAsync()`, `Task ClearUsageAsync()`;
  - `UsageCounter.ClearRates()`;
  - the controller calls `UsageCounter.NoteGap(today)` whenever a running sing-box goes away;
  - `SmartRoutingController.ApiFailureLimit` becomes **10** (it was 3 polls at 5-10 s; the poll now runs once a second, so 10 keeps the same tolerance of about ten seconds before a silent sing-box is restarted);
  - `DailyStats` (unchanged shape) now lives in `UsageData.cs`;
  - `AppPaths.UsageFile` replaces `AppPaths.StatsFile`.

- [ ] **Step 1: Write the failing tests.**

`tests/NetRoute.Core.Tests/SmartFakes.cs`: append

```csharp
sealed class FakeAdapterCounters : IAdapterCounters
{
    public long? Bytes { get; set; }
    public Exception? Throws { get; set; }
    public List<string> Asked { get; } = new();

    public long? TotalBytes(string adapterName)
    {
        Asked.Add(adapterName);
        if (Throws is { } ex) throw ex;
        return Bytes;
    }
}
```

`tests/NetRoute.Core.Tests/SmartRoutingControllerTests.cs`:

(a) Replace the `Create` helper with:

```csharp
    SmartRoutingController Create(Func<int>? freePort = null, IAdapterCounters? counters = null, UsageSnapshot? restored = null)
    {
        var c = new SmartRoutingController(Catalog, _host, (_, _) => _api, freePort ?? (() => 40000), restored, _logs.Add, _time, counters);
        c.WaitingDetected += _popups.Add;
        c.Notify += _notes.Add;
        return c;
    }
```

(b) The API-failure tests counted three polls; they now count `ApiFailureLimit`. Replace these four tests' bodies:

```csharp
    [Fact]
    public async Task Failed_polls_after_grace_restart_sing_box_when_the_limit_is_reached()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _time.Advance(SmartRoutingController.ApiStartGrace);
        _api.ConnectionsUnreachable = true;

        for (var i = 0; i < SmartRoutingController.ApiFailureLimit - 1; i++) await c.PollStatsAsync();
        Assert.Single(_host.Starts);
        await c.PollStatsAsync();

        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(1, _host.Stops);
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task A_successful_poll_resets_the_failure_count()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _time.Advance(SmartRoutingController.ApiStartGrace);

        _api.ConnectionsUnreachable = true;
        for (var i = 0; i < SmartRoutingController.ApiFailureLimit - 1; i++) await c.PollStatsAsync();
        _api.ConnectionsUnreachable = false;
        await c.PollStatsAsync();
        _api.ConnectionsUnreachable = true;
        for (var i = 0; i < SmartRoutingController.ApiFailureLimit - 1; i++) await c.PollStatsAsync();

        Assert.Single(_host.Starts);
    }

    [Fact]
    public async Task Failed_polls_count_towards_the_crash_limit()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.ConnectionsUnreachable = true;

        for (var i = 0; i < SmartRoutingController.CrashLimit * SmartRoutingController.ApiFailureLimit; i++)
        {
            _time.Advance(SmartRoutingController.ApiStartGrace); // each restart starts a new grace period
            await c.PollStatsAsync();
        }

        Assert.Equal(3, _host.Starts.Count);
        Assert.Equal(SmartState.Faulted, c.Status.State);
        Assert.False(_host.IsRunning);
    }

    [Fact]
    public async Task A_restart_gets_a_new_grace_period()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.ConnectionsUnreachable = true;
        _time.Advance(SmartRoutingController.ApiStartGrace);
        for (var i = 0; i < SmartRoutingController.ApiFailureLimit; i++) await c.PollStatsAsync(); // restarts
        Assert.Equal(2, _host.Starts.Count);

        for (var i = 0; i < SmartRoutingController.ApiFailureLimit + 2; i++) await c.PollStatsAsync(); // inside the new grace

        Assert.Equal(2, _host.Starts.Count);
    }
```

(Delete the old versions named `Three_failed_polls_after_grace_restart_sing_box`, `A_successful_poll_resets_the_failure_count`, `Failed_polls_count_towards_the_crash_limit` and `A_restart_gets_a_new_grace_period`. `Api_failures_during_startup_grace_do_not_count` stays as it is: its five polls are inside the grace.)

(c) Add these tests (before the closing brace of the class):

```csharp
    // ---- usage (v3) ----

    [Fact]
    public async Task A_poll_records_phone_and_lan_bytes_per_row()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.Connections.Add(new SingBoxConnection("1", "youtube.com", "chrome.exe", ["lan", "lan-only"], 0, 1000));
        _api.Connections.Add(new SingBoxConnection("2", "example.com", "chrome.exe", ["phone", "default"], 0, 200));

        await c.PollStatsAsync();

        var rows = (await c.GetUsageAsync()).Days.Values.Single().Rows;
        Assert.Equal(1000, rows["youtube"].Lan.Total);
        Assert.Equal(200, rows["app:chrome.exe"].Phone.Total);
    }

    [Fact]
    public async Task Bytes_of_closed_connections_are_unattributed_and_split_with_the_phone_adapter_counter()
    {
        var counters = new FakeAdapterCounters { Bytes = 5000 };
        var c = Create(counters: counters);
        await c.ApplyAsync(Net(), On());
        _api.Connections.Add(new SingBoxConnection("1", "example.com", "chrome.exe", ["phone", "default"], 0, 100));
        await c.PollStatsAsync();

        _api.ClosedDownload = 500; // connections that opened and closed between the two polls
        counters.Bytes = 5600;
        await c.PollStatsAsync();

        var day = (await c.GetUsageAsync()).Days.Values.Single();
        Assert.Equal(600, day.Rows[UsageAttribution.UnattributedKey].Phone.Total);
        Assert.Equal(600, day.PhoneAdapterBytes);
        Assert.Contains(counters.Asked, name => name == TestAdapters.Phone(31).Name);
    }

    [Fact]
    public async Task A_failing_adapter_counter_does_not_stop_recording_and_is_logged_once()
    {
        var counters = new FakeAdapterCounters { Throws = new InvalidOperationException("no stats") };
        var c = Create(counters: counters);
        await c.ApplyAsync(Net(), On());
        _api.Connections.Add(new SingBoxConnection("1", "example.com", "chrome.exe", ["phone", "default"], 0, 100));

        await c.PollStatsAsync();
        _api.Connections[0] = _api.Connections[0] with { Download = 150 };
        await c.PollStatsAsync();

        Assert.Equal(150, (await c.GetUsageAsync()).Days.Values.Single().Rows["app:chrome.exe"].Phone.Total);
        Assert.Equal(1, _logs.Count(l => l.Contains("byte counter failed")));
    }

    [Fact]
    public async Task A_restart_resets_the_baselines_so_the_new_process_totals_count_in_full()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.Connections.Add(new SingBoxConnection("1", "example.com", "chrome.exe", ["phone", "default"], 0, 100));
        await c.PollStatsAsync();

        await c.ApplyAsync(Net(phoneIndex: 36), On()); // the phone was replugged: sing-box restarts, its totals start from zero
        _api.Connections.Clear();
        _api.Connections.Add(new SingBoxConnection("2", "example.com", "chrome.exe", ["phone", "default"], 0, 300));
        _api.ClosedDownload = 2000;
        await c.PollStatsAsync();

        var day = (await c.GetUsageAsync()).Days.Values.Single();
        Assert.Equal(400, day.Rows["app:chrome.exe"].Phone.Total);
        Assert.Equal(2000, day.Rows[UsageAttribution.UnattributedKey].Phone.Total);
        Assert.Equal(1, day.Gaps); // the restart is marked, so the kept-off-4G figure says "at least"
    }

    [Fact]
    public async Task An_unreachable_api_clears_the_live_rates_but_keeps_the_history()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.Connections.Add(new SingBoxConnection("1", "example.com", "chrome.exe", ["phone", "default"], 0, 9_000_000));
        await c.PollStatsAsync();
        Assert.NotEmpty((await c.GetUsageAsync()).Rates);

        _api.ConnectionsUnreachable = true;
        await c.PollStatsAsync();

        var usage = await c.GetUsageAsync();
        Assert.Empty(usage.Rates);
        Assert.Equal(9_000_000, usage.Days.Values.Single().Rows["app:chrome.exe"].Phone.Total);
    }

    [Fact]
    public async Task TakeUsageIfChanged_returns_a_snapshot_once_per_change()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        Assert.Null(await c.TakeUsageIfChangedAsync());

        _api.Connections.Add(new SingBoxConnection("1", "example.com", "chrome.exe", ["phone", "default"], 0, 100));
        await c.PollStatsAsync();

        Assert.NotNull(await c.TakeUsageIfChangedAsync());
        Assert.Null(await c.TakeUsageIfChangedAsync());
    }

    [Fact]
    public async Task ClearUsage_wipes_the_history_and_todays_kept_figure()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.Connections.Add(new SingBoxConnection("1", "youtube.com", "chrome.exe", ["lan", "lan-only"], 0, 1000));
        await c.PollStatsAsync();
        Assert.Equal(1000, c.Status.Today.Total);

        await c.ClearUsageAsync();

        Assert.Empty((await c.GetUsageAsync()).Days);
        Assert.Equal(0, c.Status.Today.Total);
    }

    [Fact]
    public async Task Restored_usage_continues_and_feeds_todays_kept_figure()
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var restored = new UsageSnapshot(
            new Dictionary<DateOnly, UsageDay>
            {
                [today] = new(new Dictionary<string, UsageRow> { ["youtube"] = new(default, new Traffic(0, 700), 700) }, 0),
            },
            new Dictionary<string, UsageRate>());

        var c = Create(restored: restored);

        Assert.Equal(700, c.Status.Today.Total);
        Assert.Equal(700, c.Status.Today.BytesByEntry["youtube"]);
    }
```

`tests/NetRoute.Core.Tests/UsageCounterTests.cs`: add

```csharp
    [Fact]
    public void ClearRates_empties_the_rates_but_keeps_the_baselines()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.NotEmpty(counter.Snapshot().Rates);

        counter.ClearRates();
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 3_000_100, process: "a.exe")]), Attr);

        Assert.Equal(3_000_100, Row(counter, D0, "app:a.exe").Phone.Down); // the connection was not counted twice
        Assert.Empty(counter.Snapshot().Rates); // 100 B/s is idle
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: build FAIL — the controller has no `counters` parameter, `GetUsageAsync`, `TakeUsageIfChangedAsync`, `ClearUsageAsync`; `UsageCounter.ClearRates` is missing.

- [ ] **Step 3: Implement.**

`src/NetRoute.Core/SmartRouting/UsageData.cs`: append the record that is leaving `DataSavedCounter.cs`:

```csharp
/// Kept-off-4G bytes for one local day, per row key (the card's "kept off 4G today" and the per-item numbers on the Config tab).
public sealed record DailyStats(DateOnly Day, IReadOnlyDictionary<string, long> BytesByEntry)
{
    public long Total => BytesByEntry.Values.Sum();
}
```

Delete `src/NetRoute.Core/SmartRouting/DataSavedCounter.cs` and `tests/NetRoute.Core.Tests/DataSavedCounterTests.cs` (`git rm`).

`src/NetRoute.Core/SmartRouting/UsageCounter.cs`: add the public method and let `ResetBaselines` reuse it:

```csharp
    /// Forgets the live rates only (a failed poll must not leave rows looking busy). The baselines stay.
    public void ClearRates()
    {
        _recent.Clear();
        _rates = new Dictionary<string, UsageRate>();
    }

    public void ResetBaselines()
    {
        _lastSeen.Clear();
        ClearRates();
        _lastUp = _lastDown = _lastAdapter = null;
    }
```
(replace the existing `ResetBaselines` body with the one above.)

`src/NetRoute.Core/SmartRouting/SmartRoutingController.cs`:

1. Constant: `public const int ApiFailureLimit = 10;` (update its neighbouring comment to say the poll runs once a second, so this is about ten seconds).
2. Fields: replace `readonly DataSavedCounter _counter;` with

```csharp
    readonly UsageCounter _usage;
    readonly IAdapterCounters? _counters;
```
   and add `UsageAttribution _attribution;` and `bool _adapterReadFailed;` next to the other mutable fields.
3. Constructor: change the signature and body to

```csharp
    public SmartRoutingController(
        IReadOnlyList<RuleItem> catalog, ISingBoxHost host, Func<int, string, ISingBoxApi> createApi,
        Func<int> freePort, UsageSnapshot? restoredUsage, Action<string> log, TimeProvider? time = null,
        IAdapterCounters? counters = null)
    {
        _catalog = catalog;
        _host = host;
        _createApi = createApi;
        _freePort = freePort;
        _log = log;
        _time = time ?? TimeProvider.System;
        _counters = counters;
        _usage = new UsageCounter(restoredUsage, Today());
        _attribution = UsageAttribution.Build(catalog, new SmartRoutingSettings());
        _host.LineReceived += line => _ = ProcessLineAsync(line);
        _host.Exited += code => _ = HandleExitAsync(code);
        Status = new SmartRoutingStatus(SmartState.Off, null, RouteExit.Phone, false, true, [], 0, _usage.TodayKept(Today()));
    }
```
4. `ApplyAsync`: right after `_rules = RuleSet.Build(_catalog, smart);` add `_attribution = UsageAttribution.Build(_catalog, smart);`.
5. `PollStatsAsync`: the failed-poll branch starts with `_usage.ClearRates(); // a poll that fails must not leave rows looking busy`, and the success path becomes:

```csharp
                _apiFailures = 0;
                _usage.Update(
                    new UsagePoll(snapshot.Connections, snapshot.UploadTotal, snapshot.DownloadTotal, PhoneAdapterBytes(),
                        _appliedExit == RouteExit.Phone, _time.GetLocalNow()),
                    _attribution);
                Publish();
```
   with this helper next to it:

```csharp
    /// The phone adapter's Windows byte counter, or null when it cannot be read (missed bytes are then split by the default exit).
    long? PhoneAdapterBytes()
    {
        if (_counters is null || _net?.Adapters.Phone?.Name is not { } name) return null;
        try
        {
            var bytes = _counters.TotalBytes(name);
            _adapterReadFailed = false;
            return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_adapterReadFailed) _log($"Smart routing: reading the phone adapter's byte counter failed: {ex.Message}");
            _adapterReadFailed = true; // logged once per failure streak: this runs every second
            return null;
        }
    }
```
6. `DropApi()`: at its start add

```csharp
        if (_api is not null) _usage.NoteGap(Today()); // a running sing-box went away: the bytes just before that were not recorded
        _usage.ResetBaselines();
```
   (the next sing-box starts from zero totals and new connection ids; this is the only place baselines reset, so a one-off failed poll never recounts open connections: the Clash totals make up for polls that failed).
7. `Publish()`: use `_usage.TodayKept(Today())` instead of `_counter.Snapshot`.
8. Add the three public methods (next to `KeepWaitingAsync`):

```csharp
    /// A copy of the usage history and the live rates, for the Usage tab.
    public async Task<UsageSnapshot> GetUsageAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _usage.Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// The history, once per change since the last call; null when nothing changed. The caller saves it to usage.json.
    public async Task<UsageSnapshot?> TakeUsageIfChangedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _usage.TakeDirty() ? _usage.Snapshot() : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Wipes the usage history (and so today's kept-off-4G figure).
    public async Task ClearUsageAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _usage.Clear();
            SafePublish();
        }
        finally
        {
            _gate.Release();
        }
    }
```

`src/NetRoute.App/AppPaths.cs`: replace `StatsFile` with

```csharp
    public static readonly string UsageFile = Path.Combine(Root, "usage.json");
```

`src/NetRoute.App/App.xaml.cs` (keep the App building and saving; the cadence and the window come in Task 9):
- Fields: delete `long _lastSavedStatsTotal = -1;`; add `bool _usageUnreadable;` and `bool _usageSaveFailed;`.
- In `SetUpSmartRouting`, replace the `restored` line and the constructor call with:

```csharp
            var usage = UsageStore.Load(AppPaths.UsageFile, DateOnly.FromDateTime(DateTime.Now));
            _usageUnreadable = usage.Unreadable;
            if (usage.Unreadable) log.Error("usage.json could not be read; usage is kept in memory only this session and NOT saved");
            var smart = new SmartRoutingController(
                _catalog, host, (port, secret) => new SingBoxApi(port, secret), FreePort.Next, usage.Usage, log.Info,
                counters: new WindowsAdapterCounters());
```
  and delete the later line `_lastSavedStatsTotal = restored?.Total ?? -1;`.
- In the `_poll.Tick` handler replace `SaveStatsIfChanged();` with `await SaveUsageAsync();`.
- Replace the `SaveStatsIfChanged` method with:

```csharp
    /// Saves usage.json when something changed (or when force is set, or a previous save failed). Never throws.
    async Task SaveUsageAsync(bool force = false)
    {
        if (_smart is not { } smart || _usageUnreadable) return;
        try
        {
            var usage = force || _usageSaveFailed ? await smart.GetUsageAsync() : await smart.TakeUsageIfChangedAsync();
            if (usage is null) return;
            if (usage.Days.Count == 0 && !File.Exists(AppPaths.UsageFile)) return; // nothing recorded yet: no file for someone who never used it
            await Task.Run(() => UsageStore.Save(AppPaths.UsageFile, usage));
            _usageSaveFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _usageSaveFailed = true;
            _log?.Error("Saving usage failed", ex);
        }
    }
```
- `OnSessionEnding`: replace `SaveStatsIfChanged();` with `Task.Run(() => SaveUsageAsync(force: true)).Wait(TimeSpan.FromSeconds(3));`.
- `StopSmartRouting`: remove the `SaveStatsIfChanged();` line before the shutdown, and after the `ShutdownAsync` wait add `Task.Run(() => SaveUsageAsync(force: true)).Wait(TimeSpan.FromSeconds(3));`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings.
Run: `dotnet build src/NetRoute.App -c Release -o "<scratch>\nrw-v3-build"`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat(core,app): the controller records usage once per poll (replaces the kept-off-4G counter and stats.json)"
```
(add the trailer)

---

### Task 7: The report model

Everything the Usage tab shows, as a pure function: rows for a date range, sorted, with totals, "Goes via" state, the "Now" text and the banners. Plus the pure helper that stops rows jumping under the cursor.

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/UsageReport.cs`
- Test: `tests/NetRoute.Core.Tests/UsageReportTests.cs`

**Interfaces:**
- Consumes: `UsageSnapshot`, `UsageRow`, `UsageRate`, `UsageAttribution`, `UsageAssignment`, `GoesVia`, `ByteFormat.Human`, `RuleItem`, `SmartRoutingSettings`.
- Produces:
  - `enum UsageSortColumn { Name, Phone, Lan, Now }`; `readonly record struct UsageSort(UsageSortColumn Column, bool Descending)` with `static UsageSort Default` (Phone, largest first) and `UsageSort Click(UsageSortColumn column)` (the same column flips direction; a new column starts largest-first, names A-Z);
  - `record UsageReportRow(string Key, string Name, GoesVia Via, bool CanChange, long PhoneBytes, long LanBytes, UsageRate? Now)`;
  - `record UsageReportModel(int RangeDays, IReadOnlyList<UsageReportRow> Rows, long PhoneTotal, long LanTotal, long KeptOff, bool KeptOffIsLowerBound, long PhoneAdapterTotal, DateOnly? RecordingSince)`;
  - `UsageReport.Ranges` (`[1, 3, 7, 15, 30]`), `NormalizeRange(int)` (unknown → 7), `RangeLabel(int)` ("Today" / "N days"), `Build(UsageSnapshot usage, DateOnly today, int rangeDays, IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, bool canAssign, UsageSort sort)`, `NowText(UsageRate?)`;
  - `UsageRowOrder.Next(IReadOnlyList<string> shown, IReadOnlyList<string> sorted, bool freeze) → IReadOnlyList<string>`.

Rules: a row appears when it has bytes in the range **or** a live rate. `RecordingSince` is non-null only when the earliest recorded day is later than the first day of the range (so the page can say "Recording since …"). Totals and `KeptOff` sum the rows in the range, including `unattributed` and `other`; `KeptOffIsLowerBound` is true when any day in the range has `Gaps > 0` (sing-box stopped or restarted, so the figure is "at least"). `PhoneAdapterTotal` sums the phone-adapter bytes of the range's days.

- [ ] **Step 1: Write the failing tests** — `tests/NetRoute.Core.Tests/UsageReportTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class UsageReportTests
{
    static readonly DateOnly Today = new(2026, 10, 10);
    static readonly IReadOnlyList<RuleItem> Catalog =
        [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];

    static UsageSnapshot Snap(params (DateOnly Day, string Key, long Phone, long Lan, long Kept)[] items) => Snap(0, items);

    static UsageSnapshot Snap(long adapterPerDay, params (DateOnly Day, string Key, long Phone, long Lan, long Kept)[] items)
    {
        var days = items.GroupBy(i => i.Day).ToDictionary(
            g => g.Key,
            g => new UsageDay(
                g.ToDictionary(i => i.Key, i => new UsageRow(new Traffic(0, i.Phone), new Traffic(0, i.Lan), i.Kept)),
                adapterPerDay));
        return new UsageSnapshot(days, new Dictionary<string, UsageRate>());
    }

    static UsageSnapshot WithRates(UsageSnapshot s, params (string Key, UsageRate Rate)[] rates) =>
        s with { Rates = rates.ToDictionary(r => r.Key, r => r.Rate) };

    static UsageReportModel Build(UsageSnapshot usage, int range = 7, SmartRoutingSettings? settings = null,
                                  bool canAssign = true, UsageSort? sort = null) =>
        UsageReport.Build(usage, Today, range, Catalog, settings ?? new SmartRoutingSettings(), canAssign, sort ?? UsageSort.Default);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(7, 4)]
    [InlineData(15, 6)]
    [InlineData(30, 8)]
    public void A_range_is_today_plus_the_previous_days(int range, long expectedBytes)
    {
        // Days at offsets 0, 2, 3, 6, 7, 14, 15, 29 and 30 before today, 1 phone byte each.
        var usage = Snap(new[] { 0, 2, 3, 6, 7, 14, 15, 29, 30 }.Select(o => (Today.AddDays(-o), "app:a.exe", 1L, 0L, 0L)).ToArray());

        Assert.Equal(expectedBytes, Build(usage, range).PhoneTotal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(365)]
    public void An_unknown_range_falls_back_to_seven_days(int range)
    {
        var usage = Snap((Today.AddDays(-6), "app:a.exe", 1, 0, 0), (Today.AddDays(-7), "app:a.exe", 100, 0, 0));

        var model = Build(usage, range);

        Assert.Equal(7, model.RangeDays);
        Assert.Equal(1, model.PhoneTotal);
    }

    [Fact]
    public void Days_after_today_are_ignored()
    {
        Assert.Equal(0, Build(Snap((Today.AddDays(1), "app:a.exe", 5, 0, 0))).PhoneTotal);
    }

    [Fact]
    public void Rows_add_up_over_the_days_and_show_both_columns()
    {
        var usage = Snap((Today, "youtube", 10, 1000, 1000), (Today.AddDays(-1), "youtube", 5, 500, 500));

        var row = Assert.Single(Build(usage).Rows);

        Assert.Equal("YouTube", row.Name);
        Assert.Equal(15, row.PhoneBytes);
        Assert.Equal(1500, row.LanBytes);
    }

    [Fact]
    public void Totals_kept_off_and_adapter_total_cover_the_range_including_special_rows()
    {
        var usage = Snap(300,
            (Today, "youtube", 10, 1000, 1000),
            (Today, UsageAttribution.UnattributedKey, 40, 60, 60),
            (Today, UsageAttribution.OtherKey, 5, 0, 0),
            (Today.AddDays(-10), "youtube", 999, 999, 999));

        var model = Build(usage, 7);

        Assert.Equal(55, model.PhoneTotal);
        Assert.Equal(1060, model.LanTotal);
        Assert.Equal(1060, model.KeptOff);
        Assert.Equal(300, model.PhoneAdapterTotal); // only today's 300, the other day is outside the range
    }

    [Fact]
    public void A_row_with_only_a_live_rate_still_appears()
    {
        var usage = WithRates(Snap(), ("app:steam.exe", new UsageRate(RouteExit.Lan, 2_000_000)));

        var row = Assert.Single(Build(usage).Rows);

        Assert.Equal("app:steam.exe", row.Key);
        Assert.Equal(0, row.PhoneBytes + row.LanBytes);
        Assert.Equal(new UsageRate(RouteExit.Lan, 2_000_000), row.Now);
    }

    [Fact]
    public void A_row_that_moved_no_bytes_in_the_range_and_is_idle_is_left_out()
    {
        Assert.Empty(Build(Snap((Today.AddDays(-20), "app:a.exe", 5, 0, 0)), 7).Rows);
    }

    // ---- sorting ----

    static UsageSnapshot ThreeRows() => Snap(
        (Today, "app:alpha.exe", 100, 5, 0), (Today, "app:bravo.exe", 300, 1, 0), (Today, "app:charlie.exe", 200, 9, 0));

    [Fact]
    public void The_default_sort_is_phone_largest_first()
    {
        Assert.Equal(new[] { "bravo", "charlie", "alpha" }, Build(ThreeRows()).Rows.Select(r => r.Name));
    }

    [Theory]
    [InlineData(UsageSortColumn.Phone, false, new[] { "alpha", "charlie", "bravo" })]
    [InlineData(UsageSortColumn.Lan, true, new[] { "charlie", "alpha", "bravo" })]
    [InlineData(UsageSortColumn.Name, false, new[] { "alpha", "bravo", "charlie" })]
    [InlineData(UsageSortColumn.Name, true, new[] { "charlie", "bravo", "alpha" })]
    public void Rows_sort_by_the_chosen_column(UsageSortColumn column, bool descending, string[] expected)
    {
        Assert.Equal(expected, Build(ThreeRows(), sort: new UsageSort(column, descending)).Rows.Select(r => r.Name));
    }

    [Fact]
    public void Sorting_by_Now_puts_the_busiest_first_and_idle_rows_last()
    {
        var usage = WithRates(ThreeRows(),
            ("app:alpha.exe", new UsageRate(RouteExit.Phone, 10_000)),
            ("app:charlie.exe", new UsageRate(RouteExit.Lan, 50_000)));

        var names = Build(usage, sort: new UsageSort(UsageSortColumn.Now, true)).Rows.Select(r => r.Name);

        Assert.Equal(new[] { "charlie", "alpha", "bravo" }, names);
    }

    [Fact]
    public void Ties_are_broken_by_name()
    {
        var usage = Snap((Today, "app:zed.exe", 5, 0, 0), (Today, "app:abe.exe", 5, 0, 0));

        Assert.Equal(new[] { "abe", "zed" }, Build(usage).Rows.Select(r => r.Name));
    }

    [Fact]
    public void A_header_click_flips_the_same_column_and_starts_a_new_one_largest_first()
    {
        var phone = UsageSort.Default;

        Assert.Equal(new UsageSort(UsageSortColumn.Phone, false), phone.Click(UsageSortColumn.Phone));
        Assert.Equal(new UsageSort(UsageSortColumn.Lan, true), phone.Click(UsageSortColumn.Lan));
        Assert.Equal(new UsageSort(UsageSortColumn.Name, false), phone.Click(UsageSortColumn.Name));
        Assert.Equal(new UsageSort(UsageSortColumn.Name, true), phone.Click(UsageSortColumn.Name).Click(UsageSortColumn.Name));
    }

    // ---- assignment state ----

    [Fact]
    public void Goes_via_follows_the_settings_and_locks_rows_that_cannot_be_assigned()
    {
        var usage = Snap(
            (Today, "youtube", 0, 10, 0), (Today, "app:chrome.exe", 10, 0, 0),
            (Today, UsageAttribution.OtherKey, 1, 0, 0), (Today, UsageAttribution.UnattributedKey, 1, 0, 0));
        var settings = new SmartRoutingSettings().WithItem("youtube", false);

        var rows = Build(usage, settings: settings).Rows.ToDictionary(r => r.Key);

        Assert.Equal((GoesVia.Phone, true), (rows["youtube"].Via, rows["youtube"].CanChange));
        Assert.Equal((GoesVia.Phone, true), (rows["app:chrome.exe"].Via, rows["app:chrome.exe"].CanChange));
        Assert.False(rows[UsageAttribution.OtherKey].CanChange);
        Assert.False(rows[UsageAttribution.UnattributedKey].CanChange);
    }

    [Fact]
    public void Nothing_can_be_assigned_while_smart_routing_is_unavailable()
    {
        var usage = Snap((Today, "youtube", 0, 10, 0));

        Assert.False(Assert.Single(Build(usage, canAssign: false).Rows).CanChange);
    }

    [Fact]
    public void Kept_off_is_a_lower_bound_only_when_a_day_in_the_range_has_a_gap()
    {
        var rows = new Dictionary<string, UsageRow> { ["youtube"] = new(default, new Traffic(0, 10), 10) };
        UsageSnapshot WithGaps(DateOnly day, int gaps) =>
            new(new Dictionary<DateOnly, UsageDay> { [day] = new(rows, 0, gaps) }, new Dictionary<string, UsageRate>());

        Assert.True(Build(WithGaps(Today.AddDays(-1), 1), 3).KeptOffIsLowerBound);
        Assert.False(Build(WithGaps(Today.AddDays(-1), 0), 3).KeptOffIsLowerBound);
        Assert.False(Build(WithGaps(Today.AddDays(-10), 5), 3).KeptOffIsLowerBound); // the gap is outside the range
    }

    // ---- banners ----

    [Fact]
    public void Recording_since_is_set_only_when_fewer_days_were_recorded_than_the_range()
    {
        var usage = Snap((Today.AddDays(-2), "app:a.exe", 1, 0, 0), (Today, "app:a.exe", 1, 0, 0));

        Assert.Equal(Today.AddDays(-2), Build(usage, 7).RecordingSince);
        Assert.Null(Build(usage, 3).RecordingSince); // the range starts exactly on the first recorded day
        Assert.Null(Build(usage, 1).RecordingSince);
        Assert.Null(Build(Snap(), 7).RecordingSince); // nothing recorded at all
    }

    // ---- texts ----

    [Fact]
    public void Now_text()
    {
        Assert.Equal("idle", UsageReport.NowText(null));
        Assert.Equal("↕ 3 MB/s · LAN", UsageReport.NowText(new UsageRate(RouteExit.Lan, 3 * 1024 * 1024)));
        Assert.Equal("↕ 1.4 MB/s · phone", UsageReport.NowText(new UsageRate(RouteExit.Phone, 1_468_006)));
        Assert.Equal("↕ 2 KB/s · phone", UsageReport.NowText(new UsageRate(RouteExit.Phone, 2048)));
    }

    [Fact]
    public void Range_labels()
    {
        Assert.Equal(new[] { "Today", "3 days", "7 days", "15 days", "30 days" }, UsageReport.Ranges.Select(UsageReport.RangeLabel));
    }

    // ---- row order ----

    [Fact]
    public void Unfrozen_rows_follow_the_sorted_order()
    {
        Assert.Equal(new[] { "c", "a", "b" }, UsageRowOrder.Next(["a", "b", "c"], ["c", "a", "b"], freeze: false));
    }

    [Fact]
    public void Frozen_rows_keep_their_place_new_rows_are_appended_and_vanished_rows_leave()
    {
        var next = UsageRowOrder.Next(["a", "b", "c"], ["d", "c", "a"], freeze: true);

        Assert.Equal(new[] { "a", "c", "d" }, next);
    }

    [Fact]
    public void Freezing_with_nothing_shown_yet_gives_the_sorted_order()
    {
        Assert.Equal(new[] { "x", "y" }, UsageRowOrder.Next([], ["x", "y"], freeze: true));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageReportTests"`
Expected: build FAIL — `UsageReport`, `UsageSort`, `UsageRowOrder` … do not exist.

- [ ] **Step 3: Implement** — `src/NetRoute.Core/SmartRouting/UsageReport.cs`:

```csharp
namespace NetRoute.Core;

public enum UsageSortColumn { Name, Phone, Lan, Now }

public readonly record struct UsageSort(UsageSortColumn Column, bool Descending)
{
    /// Phone, largest first: the biggest consumers of mobile data on top.
    public static UsageSort Default => new(UsageSortColumn.Phone, true);

    /// What a click on a column header does: the same column flips direction; another column starts largest-first (names A to Z).
    public UsageSort Click(UsageSortColumn column) =>
        column == Column ? this with { Descending = !Descending } : new UsageSort(column, column != UsageSortColumn.Name);
}

public sealed record UsageReportRow(
    string Key, string Name, GoesVia Via, bool CanChange, long PhoneBytes, long LanBytes, UsageRate? Now);

/// RecordingSince: set only when the earliest recorded day is later than the first day of the range.
/// KeptOffIsLowerBound: a day in the range has a gap (sing-box stopped or restarted), so "kept off 4G" is "at least" that much.
public sealed record UsageReportModel(
    int RangeDays, IReadOnlyList<UsageReportRow> Rows, long PhoneTotal, long LanTotal, long KeptOff,
    bool KeptOffIsLowerBound, long PhoneAdapterTotal, DateOnly? RecordingSince);

/// Pure model for the Usage tab.
public static class UsageReport
{
    public static readonly IReadOnlyList<int> Ranges = [1, 3, 7, 15, 30];
    const int DefaultRange = 7;

    public static int NormalizeRange(int days) => Ranges.Contains(days) ? days : DefaultRange;

    public static string RangeLabel(int days) => days == 1 ? "Today" : $"{days} days";

    public static string NowText(UsageRate? rate) =>
        rate is null ? "idle" : $"↕ {ByteFormat.Human(rate.BytesPerSecond)}/s · {(rate.Exit == RouteExit.Lan ? "LAN" : "phone")}";

    public static UsageReportModel Build(
        UsageSnapshot usage, DateOnly today, int rangeDays, IReadOnlyList<RuleItem> catalog,
        SmartRoutingSettings settings, bool canAssign, UsageSort sort)
    {
        var range = NormalizeRange(rangeDays);
        var first = today.AddDays(-(range - 1));
        var attribution = UsageAttribution.Build(catalog, settings);

        var sums = new Dictionary<string, UsageRow>();
        long adapter = 0;
        var gap = false;
        foreach (var (day, data) in usage.Days)
        {
            if (day < first || day > today) continue;
            adapter += data.PhoneAdapterBytes;
            gap |= data.Gaps > 0;
            foreach (var (key, row) in data.Rows) sums[key] = sums.GetValueOrDefault(key, UsageRow.Empty) + row;
        }
        foreach (var key in usage.Rates.Keys) sums.TryAdd(key, UsageRow.Empty);

        var rows = new List<UsageReportRow>();
        foreach (var (key, sum) in sums)
        {
            var rate = usage.Rates.GetValueOrDefault(key);
            if (sum.Total == 0 && rate is null) continue;
            var assignment = UsageAssignment.Describe(catalog, settings, key);
            rows.Add(new UsageReportRow(
                key, attribution.DisplayName(key), assignment.Via, canAssign && assignment.CanChange,
                sum.Phone.Total, sum.Lan.Total, rate));
        }

        var recorded = usage.Days.Keys.OrderBy(d => d).ToList();
        DateOnly? since = recorded.Count > 0 && recorded[0] > first ? recorded[0] : null;
        return new UsageReportModel(
            range, Sort(rows, sort).ToList(), rows.Sum(r => r.PhoneBytes), rows.Sum(r => r.LanBytes),
            sums.Values.Sum(r => r.Kept), gap, adapter, since);
    }

    static IEnumerable<UsageReportRow> Sort(List<UsageReportRow> rows, UsageSort sort)
    {
        var names = StringComparer.OrdinalIgnoreCase;
        if (sort.Column == UsageSortColumn.Name)
        {
            return (sort.Descending ? rows.OrderByDescending(r => r.Name, names) : rows.OrderBy(r => r.Name, names))
                .ThenBy(r => r.Key, StringComparer.Ordinal);
        }
        var value = Value(sort.Column);
        return (sort.Descending ? rows.OrderByDescending(value) : rows.OrderBy(value))
            .ThenBy(r => r.Name, names).ThenBy(r => r.Key, StringComparer.Ordinal);
    }

    static Func<UsageReportRow, long> Value(UsageSortColumn column) => column switch
    {
        UsageSortColumn.Phone => r => r.PhoneBytes,
        UsageSortColumn.Lan => r => r.LanBytes,
        _ => r => r.Now?.BytesPerSecond ?? 0,
    };
}

/// Keeps the Usage tab's rows from jumping while the user is aiming at one.
public static class UsageRowOrder
{
    /// The order the rows should appear in. Normally the sorted order. While frozen (the mouse is over the list, or a
    /// drop-down is open) rows already shown keep their place and new rows are appended, so nothing moves under the cursor.
    public static IReadOnlyList<string> Next(IReadOnlyList<string> shown, IReadOnlyList<string> sorted, bool freeze)
    {
        if (!freeze) return sorted;
        var still = new HashSet<string>(sorted);
        var result = shown.Where(still.Contains).ToList();
        var have = new HashSet<string>(result);
        result.AddRange(sorted.Where(key => !have.Contains(key)));
        return result;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): usage report model (ranges, sorting, totals, assignment state, banners) and stable row order"
```
(add the trailer)


### Task 8: The two-tab window and the Usage tab

`SmartRoutingWindow` becomes a `TabControl`: **Usage** (first) and **Config** (today's page, plus "Clear usage history"). The Usage tab is a new `UsageTab` control that updates in place. This task is verified by build and by rendering the window (no unit-testable logic is left in the UI: ordering, sorting, totals and assignment state were pinned in Tasks 2 and 7).

**Files:**
- Create: `src/NetRoute.App/UsageTab.xaml`, `src/NetRoute.App/UsageTab.xaml.cs`
- Modify: `src/NetRoute.App/SmartRoutingWindow.xaml`, `src/NetRoute.App/SmartRoutingWindow.xaml.cs`

**Interfaces:**
- Consumes: `UsageReportModel`, `UsageReportRow`, `UsageSort`, `UsageSortColumn`, `UsageReport.Ranges/RangeLabel/NowText`, `UsageRowOrder.Next`, `GoesVia`, `ByteFormat.Human`, `Theme` palette keys (`TextSecondary`, `Warning`).
- Produces (`SmartRoutingWindow`):
  - `void RenderUsage(UsageReportModel model, UsageSort sort, string statusText, bool recording)`;
  - events `Action<int> UsageRangeChanged`, `Action<UsageSort> UsageSortChanged`, `Action<string, bool> AssignmentRequested` (row key, goes to LAN), `Action ClearUsageRequested`;
  - the existing `Render(PageModel)` and events are unchanged and now live on the Config tab.

- [ ] **Step 1: Implement the Usage tab.**

`src/NetRoute.App/UsageTab.xaml`:

```xml
<UserControl x:Class="NetRoute.App.UsageTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DockPanel Margin="4,12,4,0">
        <DockPanel DockPanel.Dock="Top">
            <TextBlock x:Name="StatusText" DockPanel.Dock="Right" VerticalAlignment="Center" Margin="12,0,0,0"
                       Foreground="{DynamicResource TextSecondary}" TextTrimming="CharacterEllipsis" />
            <StackPanel x:Name="RangeBar" Orientation="Horizontal" />
        </DockPanel>
        <TextBlock x:Name="Banner" DockPanel.Dock="Top" Margin="0,8,0,0" TextWrapping="Wrap"
                   Foreground="{DynamicResource Warning}" Visibility="Collapsed" />
        <Grid x:Name="HeaderGrid" DockPanel.Dock="Top" Margin="0,12,17,0" />
        <TextBlock x:Name="EmptyText" DockPanel.Dock="Top" Margin="6,12,0,0" Text="No usage recorded in this period yet."
                   Foreground="{DynamicResource TextSecondary}" Visibility="Collapsed" />
        <StackPanel DockPanel.Dock="Bottom" Margin="0,8,0,0">
            <Grid x:Name="TotalGrid" Margin="0,0,17,0" />
            <TextBlock x:Name="KeptText" Margin="6,8,0,0" />
            <TextBlock x:Name="AdapterText" Margin="6,2,0,0" Foreground="{DynamicResource TextSecondary}" />
            <TextBlock Margin="6,6,0,0" TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}"
                       Text="Numbers are what Smart routing saw. Short connections it missed are shown as “Unattributed”, so the totals stay right." />
        </StackPanel>
        <ScrollViewer VerticalScrollBarVisibility="Visible">
            <StackPanel x:Name="Rows" />
        </ScrollViewer>
    </DockPanel>
</UserControl>
```

`src/NetRoute.App/UsageTab.xaml.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NetRoute.Core;

namespace NetRoute.App;

/// The Usage tab. Updated in place and keyed by row key: a refresh once a second never rebuilds the list, steals focus
/// or closes an open "Goes via" drop-down. Rows only change places when the mouse is away from the list and no
/// drop-down is open, or right after the user clicked a column header.
public partial class UsageTab : UserControl
{
    const double ViaWidth = 100, ByteWidth = 90, NowWidth = 170;

    readonly Dictionary<int, ToggleButton> _rangeButtons = new();
    readonly Dictionary<UsageSortColumn, Button> _headers = new();
    readonly Dictionary<string, RowView> _rows = new();
    readonly TextBlock _totalPhone = new(), _totalLan = new();
    List<string> _order = [];
    UsageSort _sort = UsageSort.Default;
    bool _forceReorder;

    public UsageTab()
    {
        InitializeComponent();
        BuildRangeBar();
        BuildHeader();
        BuildTotals();
    }

    public event Action<int>? RangeChanged;
    public event Action<UsageSort>? SortChanged;
    public event Action<string, bool>? AssignmentRequested;

    public void Render(UsageReportModel model, UsageSort sort, string statusText, bool recording)
    {
        _sort = sort;
        StatusText.Text = statusText;
        foreach (var (days, button) in _rangeButtons) button.IsChecked = days == model.RangeDays;
        UpdateHeaders();

        var banner = new List<string>();
        if (!recording) banner.Add("Usage is recorded only while Smart routing is running. History stays visible.");
        if (model.RecordingSince is { } since)
            banner.Add($"Recording since {since.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}.");
        Banner.Text = string.Join("\n", banner);
        Banner.Visibility = banner.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = model.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var byKey = model.Rows.ToDictionary(r => r.Key);
        foreach (var key in _rows.Keys.Where(k => !byKey.ContainsKey(k)).ToList())
        {
            Rows.Children.Remove(_rows[key].Root);
            _rows.Remove(key);
        }
        foreach (var row in model.Rows)
        {
            if (!_rows.TryGetValue(row.Key, out var view))
            {
                view = _rows[row.Key] = new RowView(row.Key, (key, toLan) => AssignmentRequested?.Invoke(key, toLan));
                Rows.Children.Add(view.Root);
            }
            view.Apply(row);
        }

        var freeze = !_forceReorder && (Rows.IsMouseOver || _rows.Values.Any(v => v.DropDownOpen));
        _forceReorder = false;
        var next = UsageRowOrder.Next(_order, model.Rows.Select(r => r.Key).ToList(), freeze);
        if (!next.SequenceEqual(_order))
        {
            for (var i = 0; i < next.Count; i++)
            {
                var element = _rows[next[i]].Root;
                if (Rows.Children.IndexOf(element) == i) continue;
                Rows.Children.Remove(element);
                Rows.Children.Insert(i, element);
            }
            _order = next.ToList();
        }

        _totalPhone.Text = ByteFormat.Human(model.PhoneTotal);
        _totalLan.Text = ByteFormat.Human(model.LanTotal);
        KeptText.Text = $"Kept off 4G in this period: {(model.KeptOffIsLowerBound ? "at least " : "")}{ByteFormat.Human(model.KeptOff)}";
        KeptText.ToolTip = model.KeptOffIsLowerBound
            ? "Smart routing was stopped or restarted during this period; the bytes just before each stop were not recorded."
            : null;
        AdapterText.Text = "Phone adapter total in this period (exact, from Windows): "
            + (model.PhoneAdapterTotal > 0 ? ByteFormat.Human(model.PhoneAdapterTotal) : "not measured yet");
    }

    void BuildRangeBar()
    {
        RangeBar.Children.Add(Secondary(new TextBlock
        {
            Text = "Show usage for:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        }));
        foreach (var days in UsageReport.Ranges)
        {
            var chosen = days;
            var button = new ToggleButton
            {
                Content = UsageReport.RangeLabel(days), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 0),
            };
            button.Click += (_, _) =>
            {
                foreach (var (d, other) in _rangeButtons) other.IsChecked = d == chosen; // the model confirms it on the next render
                RangeChanged?.Invoke(chosen);
            };
            _rangeButtons[days] = button;
            RangeBar.Children.Add(button);
        }
    }

    void BuildHeader()
    {
        DefineColumns(HeaderGrid);
        AddHeader(UsageSortColumn.Name, 0, HorizontalAlignment.Left,
            "Each connection counts under one row: an application that has its own rule, otherwise the site rule it matches " +
            "(YouTube traffic from Chrome counts under YouTube), otherwise the application.");
        var via = Secondary(new TextBlock { Text = "Goes via", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(via, 1);
        HeaderGrid.Children.Add(via);
        AddHeader(UsageSortColumn.Phone, 2, HorizontalAlignment.Right, null);
        AddHeader(UsageSortColumn.Lan, 3, HorizontalAlignment.Right, null);
        AddHeader(UsageSortColumn.Now, 4, HorizontalAlignment.Left, null);
    }

    void AddHeader(UsageSortColumn column, int index, HorizontalAlignment align, string? tooltip)
    {
        var button = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 2, 6, 2),
            HorizontalContentAlignment = align, FontWeight = FontWeights.SemiBold, ToolTip = tooltip,
        };
        button.Click += (_, _) =>
        {
            _sort = _sort.Click(column);
            _forceReorder = true; // the user asked for this order: apply it even with the mouse over the list
            UpdateHeaders();
            SortChanged?.Invoke(_sort);
        };
        _headers[column] = button;
        Grid.SetColumn(button, index);
        HeaderGrid.Children.Add(button);
    }

    void UpdateHeaders()
    {
        foreach (var (column, button) in _headers)
        {
            var label = column switch
            {
                UsageSortColumn.Name => "Application / site",
                UsageSortColumn.Phone => "Phone",
                UsageSortColumn.Lan => "LAN",
                _ => "Now",
            };
            button.Content = column == _sort.Column ? $"{label} {(_sort.Descending ? "▼" : "▲")}" : label;
        }
    }

    void BuildTotals()
    {
        DefineColumns(TotalGrid);
        var label = new TextBlock { Text = "Total", FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0) };
        _totalPhone.HorizontalAlignment = _totalLan.HorizontalAlignment = HorizontalAlignment.Right;
        _totalPhone.Margin = _totalLan.Margin = new Thickness(0, 0, 6, 0);
        _totalPhone.FontWeight = _totalLan.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(_totalPhone, 2);
        Grid.SetColumn(_totalLan, 3);
        TotalGrid.Children.Add(label);
        TotalGrid.Children.Add(_totalPhone);
        TotalGrid.Children.Add(_totalLan);
    }

    /// Name | Goes via | Phone | LAN | Now. Fixed widths so the header, the rows and the totals line up.
    static void DefineColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ViaWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ByteWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ByteWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NowWidth) });
    }

    /// Dimmed text that follows the theme (never a fixed colour).
    static TextBlock Secondary(TextBlock text)
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return text;
    }

    /// One row of the list. Created once per row key and then only updated.
    sealed class RowView
    {
        readonly Grid _grid = new() { Margin = new Thickness(0, 2, 0, 2) };
        readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
        readonly ComboBox _via = new() { Width = ViaWidth - 8, Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        readonly TextBlock _phone = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBlock _lan = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBlock _now = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        bool _suppress;

        public RowView(string key, Action<string, bool> assign)
        {
            DefineColumns(_grid);
            _via.Items.Add("Phone");
            _via.Items.Add("LAN");
            AutomationProperties.SetName(_via, "Goes via");
            _via.SelectionChanged += (_, _) =>
            {
                if (!_suppress && _via.SelectedIndex >= 0) assign(key, _via.SelectedIndex == 1);
            };
            Place(_name, 0);
            Place(_via, 1);
            Place(_phone, 2);
            Place(_lan, 3);
            Place(_now, 4);
        }

        public UIElement Root => _grid;
        public bool DropDownOpen => _via.IsDropDownOpen;

        public void Apply(UsageReportRow row)
        {
            _name.Text = row.Name;
            _name.ToolTip = row.Name;
            _phone.Text = ByteFormat.Human(row.PhoneBytes);
            _lan.Text = ByteFormat.Human(row.LanBytes);
            _now.Text = UsageReport.NowText(row.Now);
            if (row.Now is null) _now.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            else _now.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            _via.IsEnabled = row.CanChange;
            if (_via.IsDropDownOpen) return; // never change the selection under the user's hand
            _suppress = true;
            _via.SelectedIndex = row.Via == GoesVia.Lan ? 1 : 0;
            _suppress = false;
        }

        void Place(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            _grid.Children.Add(element);
        }
    }
}
```

- [ ] **Step 2: Turn the window into two tabs.**

`src/NetRoute.App/SmartRoutingWindow.xaml` (replace the whole file):

```xml
<Window x:Class="NetRoute.App.SmartRoutingWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:NetRoute.App"
        Title="NetRoute — Smart routing" Width="780" Height="720" MinWidth="640" MinHeight="460"
        WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" FontSize="13" ThemeMode="System"
        Background="{DynamicResource WindowBackground}" Foreground="{DynamicResource TextPrimary}">
    <TabControl x:Name="Tabs" Margin="12" SelectedIndex="0">
        <TabItem Header="Usage">
            <local:UsageTab x:Name="Usage" />
        </TabItem>
        <TabItem Header="Config">
            <DockPanel Margin="4,12,4,0">
                <StackPanel DockPanel.Dock="Top">
                    <DockPanel>
                        <CheckBox x:Name="MasterToggle" DockPanel.Dock="Left" VerticalAlignment="Center" Click="OnMaster" />
                        <TextBlock Text="Smart routing — keep data-hungry traffic off 4G (Phone and LAN modes)" FontSize="15" FontWeight="SemiBold"
                                   Margin="8,0,0,0" VerticalAlignment="Center" TextWrapping="Wrap" />
                    </DockPanel>
                    <TextBlock x:Name="StatusText" Margin="0,8,0,0" TextWrapping="Wrap" />
                    <TextBlock x:Name="TotalText" Margin="0,4,0,0" Foreground="{DynamicResource TextSecondary}" />
                    <Button x:Name="UsePhoneButton" Content="Use phone until LAN is back (uses mobile data)" Margin="0,8,0,0"
                            HorizontalAlignment="Left" Padding="10,4" Visibility="Collapsed" Click="OnUsePhone" />
                    <TextBlock Text="Turned-off items go through the phone like everything else." Margin="0,8,0,0" Foreground="{DynamicResource TextSecondary}" />
                    <TextBlock Text="Changing a rule restarts Smart routing for about a second. Downloads already running may keep their old route until they reconnect."
                               Margin="0,2,0,4" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" />
                </StackPanel>
                <StackPanel DockPanel.Dock="Bottom" Margin="0,8,0,0">
                    <TextBlock Text="Usage history is kept for 35 days." Foreground="{DynamicResource TextSecondary}" />
                    <Button Content="Clear usage history…" HorizontalAlignment="Left" Padding="10,4" Margin="0,6,0,0" Click="OnClearUsage" />
                </StackPanel>
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel x:Name="Body" />
                </ScrollViewer>
            </DockPanel>
        </TabItem>
    </TabControl>
</Window>
```

`src/NetRoute.App/SmartRoutingWindow.xaml.cs`:
- constructor: after `Theme.Track(this);` add

```csharp
        Usage.RangeChanged += days => UsageRangeChanged?.Invoke(days);
        Usage.SortChanged += sort => UsageSortChanged?.Invoke(sort);
        Usage.AssignmentRequested += (key, toLan) => AssignmentRequested?.Invoke(key, toLan);
```
- events and methods (next to the other events / methods):

```csharp
    public event Action<int>? UsageRangeChanged;
    public event Action<UsageSort>? UsageSortChanged;
    public event Action<string, bool>? AssignmentRequested;
    public event Action? ClearUsageRequested;

    /// Updates the Usage tab in place; safe to call every second.
    public void RenderUsage(UsageReportModel model, UsageSort sort, string statusText, bool recording) =>
        Usage.Render(model, sort, statusText, recording);

    void OnClearUsage(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Delete all recorded usage? This cannot be undone.", "NetRoute — Clear usage history",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) ClearUsageRequested?.Invoke();
    }
```
- update the class summary comment: "Management window: a Usage tab and a Config tab (the page below). The Config tab is rebuilt from a PageModel only when its structure changes; otherwise updated in place."

- [ ] **Step 3: Build**

Run: `dotnet build src/NetRoute.App -c Release -o "<scratch>\nrw-v3-build"`
Expected: Build succeeded, 0 warnings, 0 errors.
Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green.

- [ ] **Step 4: Render and look at it (own windows only).** The v2 live-fix round has a throwaway harness at `<scratch>\themeharness\` (not in the repo; `Harness.csproj` compiles the App's window sources and `NetRoute.Core`, `Program.cs` shows the windows and saves `PrintWindow` captures of **its own windows** to PNG). Reuse it:
  1. In `Harness.csproj` add `UsageTab.xaml.cs` to the `Compile` list and `UsageTab.xaml` to the `Page` list (paths under `src/NetRoute.App`).
  2. In `Program.cs`, call `w1.RenderUsage(model, UsageSort.Default, "⚡ Smart routing ON · 12 rules · 52 MB kept off 4G today", recording: true)` with a `UsageReportModel` built by `UsageReport.Build` from a hand-made `UsageSnapshot` (about ten rows: Chrome, YouTube, Windows Update, Steam, Facebook, an "Other" and an "Unattributed" row, two with live rates; a `RecordingSince` banner; one row with `canAssign` off).
  3. Run it with `NRW_FORCE=dark`, then `light`, then unset (system); save the PNGs under `<scratch>\themeharness\shots\v3-*`.
  4. **Look at every PNG** (Read tool). Check: the Usage tab is first and selected; the header lines up with the rows and the totals; the numbers are right-aligned; both themes are readable (no dark text on a dark background); the "Goes via" boxes show Phone/LAN; the sort arrow shows on "Phone ▼"; the Config tab (select it in the harness with `w1.Tabs.SelectedIndex = 1` for a second capture) shows today's page plus the "Clear usage history…" button.
  5. Do **not** launch `NetRouteWidget.exe`, and do not capture any window the harness did not create.
  6. List what you could not verify (live updating with a drop-down open; row freezing under the mouse; real data).

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.App
git commit -m "feat(app): Usage tab (phone/LAN columns, ranges, sorting, Goes via, live Now) and the two-tab management window"
```
(add the trailer)

---

### Task 9: Wire the tabs to the controller

The App polls once a second, saves the usage file every 30 s, feeds the Usage tab from the controller, and applies the tab's actions (range, sort, assignment, clear).

**Files:**
- Modify: `src/NetRoute.App/App.xaml.cs`

**Interfaces:**
- Consumes: Tasks 6–8 (`GetUsageAsync`, `TakeUsageIfChangedAsync`, `ClearUsageAsync`, `UsageReport.Build`, `UsageAssignment.Set`, `SmartRoutingWindow.RenderUsage` and its four events, `SmartRoutingSettings.UsageRangeDays`).
- Produces: behaviour only. No new public API.

- [ ] **Step 1: Implement.** Edit `src/NetRoute.App/App.xaml.cs`:

(a) Fields: add

```csharp
    static readonly TimeSpan UsagePollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan UsageSaveInterval = TimeSpan.FromSeconds(30);
    readonly DispatcherTimer _usagePoll = new() { Interval = UsagePollInterval };
    readonly DispatcherTimer _usageSave = new() { Interval = UsageSaveInterval };
    bool _usagePolling;
    UsageSort _usageSort = UsageSort.Default;
    int _renderSeq;
    bool _renderRunning, _renderAgain;
```

(b) The 5/10 s `_poll.Tick` handler no longer polls stats or saves. Remove its `if (_smart is { } smart) { await smart.PollStatsAsync(); await SaveUsageAsync(); }` block (Task 6 left it there). After `_poll.Start();` in `OnStartup` add:

```csharp
            if (_smart is { } smartForUsage)
            {
                // One second: short connections would be missed by a slower poll (see the v3 spec, "Why a plain connection poll is not enough").
                _usagePoll.Tick += async (_, _) =>
                {
                    if (_usagePolling) return; // the previous poll is still waiting for the API
                    _usagePolling = true;
                    try
                    {
                        await smartForUsage.PollStatsAsync();
                    }
                    finally
                    {
                        _usagePolling = false;
                    }
                };
                _usageSave.Tick += async (_, _) => await SaveUsageAsync();
                _usagePoll.Start();
                _usageSave.Start();
            }
```
and in `OnExit` stop both timers next to `_poll.Stop();` (`_usagePoll.Stop(); _usageSave.Stop();`).

(c) Replace `RenderSmartWindow()` with the async version that also feeds the Usage tab (the controller call waits for its gate, so it runs without blocking the UI; a newer render supersedes an older one):

```csharp
    /// Renders both tabs. The Config tab is synchronous; the Usage tab needs a copy of the usage taken under the
    /// controller's gate, so it arrives a moment later. A render that is already running is not stacked: one more is queued.
    void RenderSmartWindow()
    {
        if (_smartWindow is null || _smart is null || _controller is null) return;
        if (_renderRunning)
        {
            _renderAgain = true;
            return;
        }
        _ = RenderSmartWindowAsync();
    }

    async Task RenderSmartWindowAsync()
    {
        _renderRunning = true;
        try
        {
            do
            {
                _renderAgain = false;
                if (_smartWindow is not { } window || _smart is not { } smart || _controller is not { } controller) return;
                var seq = ++_renderSeq;
                var settings = controller.Settings.SmartRouting;
                var status = smart.Status;
                window.Render(SmartRoutingPage.Build(_catalog, settings, status));

                var usage = await smart.GetUsageAsync(); // continues on the UI thread
                if (seq != _renderSeq || _smartWindow != window) continue;
                var canAssign = settings.Enabled && status.State is SmartState.Running or SmartState.Starting;
                var report = UsageReport.Build(
                    usage, DateOnly.FromDateTime(DateTime.Now), settings.UsageRangeDays, _catalog, settings, canAssign, _usageSort);
                window.RenderUsage(report, _usageSort, SmartRoutingPresenter.Row(status).Text, recording: status.State == SmartState.Running);
            }
            while (_renderAgain);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Error("Rendering the management window failed", ex);
        }
        finally
        {
            _renderRunning = false;
        }
    }
```
(`RenderSmart(status)` still calls `RenderSmartWindow()`, so the page refreshes after every poll, about once a second.)

(d) In `OpenSmartRouting`, wire the new events next to the existing ones:

```csharp
        window.UsageRangeChanged += days =>
        {
            _controller!.UpdateSettings(s => s with { SmartRouting = s.SmartRouting with { UsageRangeDays = UsageReport.NormalizeRange(days) } });
            RenderSmartWindow(); // no ApplyAsync: a range change must never touch sing-box
        };
        window.UsageSortChanged += sort =>
        {
            _usageSort = sort;
            RenderSmartWindow();
        };
        window.AssignmentRequested += (key, toLan) => ChangeSmart(s => UsageAssignment.Set(_catalog, s, key, toLan));
        window.ClearUsageRequested += async () =>
        {
            if (_smart is not { } smart) return;
            await smart.ClearUsageAsync();
            await SaveUsageAsync(force: true);
            RenderSmartWindow();
        };
```

(e) The usage file must still be written when the app quits (Task 6 did this); check that `Quit`/`OnExit`/`OnSessionEnding` all still reach `SaveUsageAsync(force: true)` exactly as Task 6 left them.

- [ ] **Step 2: Build and test**

Run: `dotnet build src/NetRoute.App -c Release -o "<scratch>\nrw-v3-build"` — Expected: 0 warnings, 0 errors.
Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green.
Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category=Integration"` — Expected: all green (these use the pinned sing-box with a local proxy only, never a TUN).

- [ ] **Step 3: Read the wiring once, line by line**, against these checks (no running the widget):
  - the 1 s poll cannot overlap itself (`_usagePolling`);
  - nothing awaits on the UI thread with `.Result`/`.Wait()` except the existing exit-time waits, which run inside `Task.Run`;
  - a range or sort change never calls `ApplyAsync`;
  - the assignment goes through `ChangeSmart`, so it restarts sing-box only when the rule set really changed (the controller's fingerprint decides);
  - `_renderAgain` cannot loop forever: it is set only by a render request that arrived while one was running.

- [ ] **Step 4: Commit**

```bash
git add src/NetRoute.App
git commit -m "feat(app): 1 s usage poll, 30 s usage save and the Usage tab wired to range, sort, Goes via and Clear"
```
(add the trailer)

---

### Task 10: Docs and the on-machine accuracy check

**Files:**
- Modify: `README.md`, `docs/superpowers/specs/2026-10-02-netroute-v3-usage-and-routing-design.md`
- Create: `tools/Check-UsageAccuracy.ps1`
- Test: none beyond a syntax check of the script (it is run by the controller with the user, on the real machine).

**Interfaces:**
- Consumes: the saved `%AppData%\NetRouteWidget\usage.json` (shape from Task 5).
- Produces: `tools\Check-UsageAccuracy.ps1 [-Url <string>] [-Count <int>] [-WaitSeconds <int>]`. **Read-only on the widget's files.** It downloads a known amount through the normal network path (so through sing-box when Smart routing is on), waits for the widget's 30 s save, and compares.

- [ ] **Step 1: The script.** `tools/Check-UsageAccuracy.ps1`:

```powershell
<#
.SYNOPSIS
  Checks the Usage tab's accuracy on this machine: downloads a known number of bytes and compares them with what the
  widget recorded in %AppData%\NetRouteWidget\usage.json.
.DESCRIPTION
  Run it with Smart routing ON and the widget running, on a quiet connection (other downloads add to "Unattributed").
  It only reads usage.json; it never touches the widget, sing-box or the network configuration.
  PASS = (the row for -RowKey + the "unattributed" row, phone + LAN) grew by 90%..110% of the downloaded bytes.
#>
param(
    [string]$Url = 'https://www.youtube.com/',
    [int]$Count = 6,
    [string]$RowKey = 'youtube',
    [int]$WaitSeconds = 40
)

$ErrorActionPreference = 'Stop'
$file = Join-Path $env:APPDATA 'NetRouteWidget\usage.json'
$today = (Get-Date).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)

function Get-Recorded {
    if (-not (Test-Path -LiteralPath $file)) { return 0L }
    $json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    $day = $json.days.$today
    if (-not $day) { return 0L }
    $sum = 0L
    foreach ($key in @($RowKey, 'unattributed')) {
        $row = $day.rows.$key
        if ($row) { $sum += [long]$row.phone.up + [long]$row.phone.down + [long]$row.lan.up + [long]$row.lan.down }
    }
    return $sum
}

if (-not (Get-Process NetRouteWidget -ErrorAction SilentlyContinue)) { throw 'The widget is not running.' }

Write-Host "Waiting for the widget to save the current numbers..."
Start-Sleep -Seconds $WaitSeconds
$before = Get-Recorded

$downloaded = 0L
for ($i = 1; $i -le $Count; $i++) {
    $bytes = & curl.exe -s -L -o NUL -w '%{size_download}' --max-time 30 $Url
    $downloaded += [long]$bytes
}
Write-Host ("Downloaded {0:N0} bytes in {1} requests from {2}" -f $downloaded, $Count, $Url)

Write-Host "Waiting for the widget to save (up to $WaitSeconds s)..."
Start-Sleep -Seconds $WaitSeconds
$after = Get-Recorded
$grew = $after - $before
$ratio = if ($downloaded -gt 0) { $grew / $downloaded } else { 0 }
Write-Host ("Recorded growth ('{0}' + unattributed): {1:N0} bytes = {2:P0} of the download" -f $RowKey, $grew, $ratio)

if ($ratio -ge 0.9 -and $ratio -le 1.1) { Write-Host 'PASS' -ForegroundColor Green; exit 0 }
Write-Host 'FAIL: outside 90%..110%. Re-run on a quiet connection; if it still fails, send the usage.json and the log.' -ForegroundColor Red
exit 1
```

- [ ] **Step 2: Syntax-check the script** (does not run it):

Run (PowerShell): `$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path tools/Check-UsageAccuracy.ps1), [ref]$null, [ref]$e); if ($e) { $e; exit 1 } else { 'syntax ok' }`
Expected: `syntax ok`.

- [ ] **Step 3: Update the spec** (`docs/superpowers/specs/2026-10-02-netroute-v3-usage-and-routing-design.md`):
  - `**Status:**` line → `Implemented on branch feat/v2-smart-routing; waiting for the user's on-machine verification.`
  - In "Storage", replace the structure bullet with: ``Structure: `{ "version": 1, "days": { "2026-10-02": { "rows": { "<row key>": { "phone": { "up": n, "down": n }, "lan": { "up": n, "down": n }, "kept": n } }, "phoneAdapter": n, "gaps": n } } }`. `kept` is the part of a row's LAN bytes that LAN-only rules kept off 4G; `phoneAdapter` is the day's bytes on the phone adapter as counted by Windows; `gaps` counts the times sing-box stopped or restarted that day (the bytes just before each stop were not recorded). The day key is culture-invariant `yyyy-MM-dd` (local date).``
  - In "Retention": replace "At most 500 rows per day; beyond that, the smallest rows fold into "Other"." with "At most 500 application/item rows per day plus the two special rows (`other`, `unattributed`); while counting, a new row beyond the cap goes into `other`; on load, the smallest rows beyond the cap fold into `other`."
  - In "How (accurate counting)" item 3, change the footer label to "Phone adapter total in this period (exact, from Windows)" (it follows the selected range, so it can be compared with the Phone column). Item 4: the "at least" label appears when a day in the range has `gaps > 0`.
  - In "Architecture", `SmartRoutingController` bullet: add that `DataSavedCounter` and `stats.json` are replaced (the old file is ignored, not migrated), and that `ApiFailureLimit` became 10 because the poll now runs once a second.

- [ ] **Step 4: README.** In `README.md`, add a "Usage tab" section (3–6 short paragraphs or bullets): what the two tabs show; that numbers are measured through Smart routing so history only grows while it runs; the Phone/LAN columns, the date ranges, the "Goes via" assignment and the "Now" column; the "Unattributed" row and the exact phone-adapter total; `usage.json` (35 days, only application names and byte totals) and "Clear usage history"; `tools\Check-UsageAccuracy.ps1`. Remove any mention of `stats.json` (the kept-off-4G figure now comes from `usage.json`).

- [ ] **Step 5: Final checks and commit**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings.
Run: `dotnet build src/NetRoute.App -c Release -o "<scratch>\nrw-v3-build"` — Expected: 0 warnings, 0 errors.

```bash
git add README.md docs tools
git commit -m "docs(v3): usage tab README, spec storage refinements and the on-machine accuracy check"
```
(add the trailer)

---

## After the last task (controller, not an implementer)

1. Whole-branch review on the most capable model with this plan's **Review Focus** section.
2. Stage a Release publish of HEAD in `<scratch>\nrw-v3-release` and re-issue the install command (`install-netroute-v2.ps1` with the new source folder); the user installs it.
3. With the user, on the machine: the manual checklist from the spec (play YouTube through the LAN and watch **LAN** and **Now**; switch an application to LAN and watch it move; open a "Goes via" drop-down and let the page refresh for a few seconds; unplug the LAN; check ranges after two days; restart the widget and see the history survive) and `tools\Check-UsageAccuracy.ps1`.
4. Update the draft PR #2 description and mark the spec "Implemented" only after the user confirms.
