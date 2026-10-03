# NetRoute Widget v2 — Smart Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** While the PC uses the phone for internet, send data-hungry traffic through the LAN instead of 4G, using an embedded sing-box. Data-hungry means updates, cloud sync, game downloads, YouTube/Facebook/Instagram and the user's own rules. Every item is visible and can be switched on and off. LAN-routed traffic waits while the LAN is down, and a popup offers to use the phone until the LAN is back. A speed-test button is included.

**Architecture:**
- All logic stays pure and unit-tested in `NetRoute.Core`:
  - the rule catalog and settings;
  - the sing-box config builder;
  - the log parser and wait tracker;
  - the data-saved counter;
  - the `SmartRoutingController` state machine;
  - presenters.
- Thin Windows adapters run `sing-box.exe` and talk to its local Clash API, under `NetRoute.Core/Windows`.
- The WPF app adds a card row, a management window and a waiting popup.
- v1 metric routing stays underneath as the safety net. sing-box only adds a TUN overlay while Smart routing is on in Phone or Auto mode.

**Tech Stack:** .NET 10 (C#), WPF, xUnit, sing-box **1.14.2** (separate process; TUN, `direct` outbounds with `bind_interface`, `selector` groups, Clash API).

**Spec:** `docs/superpowers/specs/2026-10-02-netroute-v2-smart-routing-design.md`
**Spike findings (exact sing-box behaviour on the target machine):** `docs/superpowers/spikes/2026-10-02-singbox-feasibility.md`

## Global Constraints

- **Builds on v1:** branch `feat/v2-smart-routing` from `main`, at repo `F:\source\petrofahed\NetRouteWidget`. All v1 tests (103 unit, 6 integration) must keep passing.
- **SDK and platform:** the SDK is pinned in `global.json` (10.0.401) and every project targets `net10.0-windows`. No new third-party runtime NuGet packages are allowed. sing-box ships as a separate exe and is not linked.
- **sing-box pin:**
  - version **1.14.2**;
  - asset `sing-box-1.14.2-windows-amd64.zip`;
  - SHA-256 `C2D8BFFF918755808781DFDEEB8581B6C91EB3A243D9A7B55483CFC0C0684D32`;
  - URL `https://github.com/SagerNet/sing-box/releases/download/v1.14.2/sing-box-1.14.2-windows-amd64.zip`.
  - It is downloaded at build time into `.cache/sing-box/1.14.2/` (git-ignored), verified, and copied to `<output>\sing-box\sing-box.exe` along with its `LICENSE`.
- **sing-box tags and names:**
  - outbounds `phone` and `lan` (both `direct`, with `bind_interface` set to the adapter name);
  - selector `lan-only` = [`lan`, `phone`], default `lan`;
  - selector `default` = [`phone`, `lan`], default per the current exit;
  - `route.final` = `default`;
  - TUN `interface_name` = `NetRoute`, `address` = `172.19.0.1/30`, `auto_route` true, `strict_route` false;
  - log level `debug`, timestamps on, written to stdout.
- **TUN excluded ranges:** `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.0.0/16`, `224.0.0.0/4`, `255.255.255.255/32`, `fc00::/7`, `fe80::/10`, `ff00::/8`.
- **DNS:**
  - server `remote`: UDP `1.1.1.1`, detour `default`;
  - server `lan-dns`: UDP = the LAN adapter's DNS server, detour `lan`, used only for suffixes `lan`, `local`, `home`, `home.arpa`, `internal` (router-local names);
  - `final` = `remote`.
- **Clash API:** `127.0.0.1:<free port>` with a random secret. A fresh port and secret are picked on every start.
- **When sing-box runs:** only when **Smart routing is enabled AND the app is elevated AND the mode is Phone or Auto AND both adapters are detected AND the state is not Faulted**. LAN mode stops it.
- **Default exit:**
  - `lan` while v1 auto-heal is active in Phone mode (`NetworkStatus.IsHealing` and Mode == Phone);
  - `phone` otherwise. Auto mode uses `phone`.
- **"Use phone until LAN is back":** selector `lan-only` → `phone`. It reverts to `lan` after **3 consecutive** healthy LAN statuses (LAN detected and `LanLatencyMs` not null), with the toast "LAN back — LAN-only traffic is back on the LAN".
- **Waiting:**
  - the LAN is offline (LAN not detected, or `LanLatencyMs` null);
  - **and** a dial failure is seen on a connection whose matched rule routed it to `lan-only`, within the last 30 s.
  - The popup is raised **once per LAN outage** and suppressed after "Keep waiting".
- **Crash policy:** an unexpected sing-box exit triggers a restart. **3 exits within 5 minutes** puts the state in Faulted, with the message "Smart routing stopped — sing-box keeps crashing (see log)". Faulted clears when the user switches Smart routing off and on.
- **"Kept off 4G":** counts download + upload bytes of connections routed `lan`→`lan-only` while the default exit is `phone`. Per entry, per local day, persisted to `%AppData%\NetRouteWidget\stats.json`.
- **Settings:** `AppSettings.SmartRouting` defaults to `Enabled = false`. The built-in items' default is ON. Item overrides are stored by id.
- **Speed test:** `https://speed.cloudflare.com/__down?bytes=5000000` through each adapter (source-bound), with a 30 s timeout, showing Mbit/s. It runs only on a user click.
- **Commit trailer** (verbatim):
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3
  ```
- **`<scratch>`** in commands means `F:\Temp\claude\C--Users-petrofahed\f7547ae8-854b-4cab-929e-e8437d2356f3\scratchpad`. While the user runs the installed widget, never build into `src\NetRoute.App\bin\Debug` and never touch the installed copy.
- **Never** run anything that changes the real machine's network configuration in automated tests. TUN runs only in the manual checklist (Task 14), by the user.

## Review Focus

1. **Phone replugged while Smart routing runs:** the adapter is renamed, e.g. "Ethernet 5" → "Ethernet 6". Expected: the config is regenerated with the new name and sing-box restarts once. Pinned by: Task 7 `Adapter_rename_restarts_with_new_config`.
2. **sing-box missing or failing at start** (antivirus, deleted exe). Expected: the switch shows Faulted with the reason, and v1 internet is unaffected. Pinned by: Task 7 `Start_failure_counts_as_crash_and_faults_after_three`.
3. **User rule entered sloppily** (`https://www.YouTube.com/watch?v=x`, `*.dropbox.com`, `C:\Apps\qbittorrent.exe`). Expected: it is normalised to `youtube.com` / `dropbox.com` / `qbittorrent.exe`. Garbage is rejected with a reason. Pinned by: Task 3 `UserRule_normalises_and_validates`.
4. **v1 card while the TUN is active:** expected "Internet via PHONE", not "other adapter (VPN?)", and no toast storm when sing-box starts or stops. Pinned by: Task 8 `Tun_interface_is_reported_as_the_smart_routing_exit`.
5. **The same item matched by both process and domain** (OneDrive) must be attributed once, to that item, in counters and waiting. Pinned by: Task 5 `Tracker_maps_both_rule_indexes_to_one_entry` and Task 6 `Process_match_wins_and_counts_once`.

---

## File Structure

```
build/SingBox.targets                         download + verify + copy sing-box (imported by App and Tests)
THIRD-PARTY-NOTICES.md                        sing-box GPL-3.0 notice + source link
src/NetRoute.Core/
  rules/builtin.json                          built-in groups/items (copied to output)
  SmartRouting/RuleCatalog.cs                 RuleItem, RuleCatalog (parse/load builtin.json)
  SmartRouting/SmartRoutingSettings.cs        SmartRoutingSettings, UserRule, UserRuleType (+ value equality, normalisation)
  SmartRouting/RuleSet.cs                     RuleEntry, RuleSet.Build (effective enabled rules)
  SmartRouting/SingBoxConfigBuilder.cs        RouteExit, SingBoxConfigInput, SingBoxConfig, builder
  SmartRouting/SingBoxLogParser.cs            SingBoxLogEvent records, parser, LanWaitTracker
  SmartRouting/SingBoxConnection.cs           SingBoxConnection, ISingBoxHost, ISingBoxApi
  SmartRouting/DataSavedCounter.cs            per-entry daily counter + stats file
  SmartRouting/SmartRoutingController.cs      SmartState, SmartRoutingStatus, controller state machine
  SmartRouting/SmartRoutingPresenter.cs       card row text, page model, byte formatting
  SmartRouting/SpeedTest.cs                   ISpeedProbe, SpeedTestResult, SpeedTest.RunAsync
  Models.cs                                   AdapterInfo gains DnsServer (last, optional)
  Settings.cs                                 AppSettings gains SmartRouting
  RouteController.cs                          ExternalPathResolver hook
  Windows/WindowsAdapterSource.cs             fills DnsServer
  Windows/SingBoxHost.cs                      process host (stdout lines, exit)
  Windows/SingBoxApi.cs                       Clash API client (select, connections, ping)
  Windows/FreePort.cs                         free localhost TCP port
  Windows/HttpSpeedProbe.cs                   source-bound HTTP download speed probe
src/NetRoute.App/
  CardWindow.xaml(.cs)                        smart-routing row, ⚙ and ⚡ buttons
  SmartRoutingWindow.xaml(.cs)                management page
  AddRuleWindow.xaml(.cs)                     "+ App…" / "+ Website…" dialog
  WaitingPopup.xaml(.cs)                      tray-anchored popup with two buttons
  App.xaml.cs                                 wiring
tools/Restore-Network.cmd                     also stops sing-box.exe
tests/NetRoute.Core.Tests/
  RuleCatalogTests.cs  SmartRoutingSettingsTests.cs  RuleSetTests.cs  SingBoxConfigBuilderTests.cs
  SingBoxLogParserTests.cs  DataSavedCounterTests.cs  SmartRoutingControllerTests.cs
  SmartRoutingPresenterTests.cs  SpeedTestTests.cs  SmartFakes.cs  SingBoxIntegrationTests.cs
```

**Test commands:** `dotnet test --filter "Category!=Integration"` (unit) and `dotnet test --filter "Category=Integration"` (read-only/local-proxy integration).

---

### Task 1: Ship a pinned, verified sing-box

**Files:**
- Create: `build/SingBox.targets`, `THIRD-PARTY-NOTICES.md`
- Modify: `.gitignore` (append `.cache/`), `src/NetRoute.App/NetRoute.App.csproj`, `tests/NetRoute.Core.Tests/NetRoute.Core.Tests.csproj`
- Create: `tests/NetRoute.Core.Tests/SingBoxIntegrationTests.cs`

**Interfaces:**
- Produces: `<output>\sing-box\sing-box.exe` and `<output>\sing-box\LICENSE` in both the App and the test output folders. Later tasks locate the binary with `Path.Combine(AppContext.BaseDirectory, "sing-box", "sing-box.exe")`.

- [ ] **Step 1: Write the failing integration test**

`tests/NetRoute.Core.Tests/SingBoxIntegrationTests.cs`:

```csharp
using System.Diagnostics;

namespace NetRoute.Core.Tests;

/// Uses the real, pinned sing-box binary. Never creates a TUN or touches routing.
[Trait("Category", "Integration")]
public partial class SingBoxIntegrationTests
{
    internal static string SingBoxExe => Path.Combine(AppContext.BaseDirectory, "sing-box", "sing-box.exe");

    internal static (int ExitCode, string Output) RunSingBox(params string[] args)
    {
        var psi = new ProcessStartInfo(SingBoxExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30_000)) { p.Kill(true); Assert.Fail("sing-box timed out"); }
        return (p.ExitCode, output + err.Result);
    }

    [Fact]
    public void Pinned_sing_box_ships_next_to_the_assembly()
    {
        Assert.True(File.Exists(SingBoxExe), $"missing {SingBoxExe}");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "sing-box", "LICENSE")));

        var (exit, output) = RunSingBox("version");

        Assert.Equal(0, exit);
        Assert.Contains("sing-box version 1.14.2", output);
        Assert.Contains("with_clash_api", output);
    }
}
```

- [ ] **Step 2: Run it and verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Pinned_sing_box"`
Expected: FAIL with `missing …\sing-box\sing-box.exe`.

- [ ] **Step 3: Add the build targets**

`build/SingBox.targets`:

```xml
<Project>
  <PropertyGroup>
    <SingBoxVersion>1.14.2</SingBoxVersion>
    <SingBoxSha256>C2D8BFFF918755808781DFDEEB8581B6C91EB3A243D9A7B55483CFC0C0684D32</SingBoxSha256>
    <SingBoxZip>sing-box-$(SingBoxVersion)-windows-amd64.zip</SingBoxZip>
    <SingBoxCache>$(MSBuildThisFileDirectory)..\.cache\sing-box\$(SingBoxVersion)\</SingBoxCache>
  </PropertyGroup>

  <!-- Downloads once into the repo-level cache; the hash check fails the build on any mismatch. -->
  <Target Name="FetchSingBox" BeforeTargets="BeforeBuild" Condition="!Exists('$(SingBoxCache)sing-box.exe')">
    <DownloadFile SourceUrl="https://github.com/SagerNet/sing-box/releases/download/v$(SingBoxVersion)/$(SingBoxZip)"
                  DestinationFolder="$(SingBoxCache)" />
    <GetFileHash Files="$(SingBoxCache)$(SingBoxZip)" Algorithm="SHA256">
      <Output TaskParameter="Hash" PropertyName="_SingBoxHash" />
    </GetFileHash>
    <Error Condition="'$(_SingBoxHash)' != '$(SingBoxSha256)'"
           Text="sing-box download hash mismatch (got $(_SingBoxHash)); refusing to use it." />
    <Unzip SourceFiles="$(SingBoxCache)$(SingBoxZip)" DestinationFolder="$(SingBoxCache)unzipped" />
    <Copy SourceFiles="$(SingBoxCache)unzipped\sing-box-$(SingBoxVersion)-windows-amd64\sing-box.exe;$(SingBoxCache)unzipped\sing-box-$(SingBoxVersion)-windows-amd64\LICENSE"
          DestinationFolder="$(SingBoxCache)" />
  </Target>

  <ItemGroup>
    <None Include="$(SingBoxCache)sing-box.exe" Link="sing-box\sing-box.exe" Visible="false"
          CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    <None Include="$(SingBoxCache)LICENSE" Link="sing-box\LICENSE" Visible="false"
          CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

Add `<Import Project="..\..\build\SingBox.targets" />` just before `</Project>` in **both** `src/NetRoute.App/NetRoute.App.csproj` and `tests/NetRoute.Core.Tests/NetRoute.Core.Tests.csproj`.

Append `.cache/` to `.gitignore`.

`THIRD-PARTY-NOTICES.md`:

```markdown
# Third-party notices

## sing-box 1.14.2
- Project: https://github.com/SagerNet/sing-box (source for this exact version: https://github.com/SagerNet/sing-box/tree/v1.14.2)
- License: GNU General Public License v3.0 or later; full text shipped as `sing-box\LICENSE` next to the app.
- NetRoute Widget runs the unmodified official release binary as a separate process (downloaded at build time, SHA-256 verified); it does not link to or modify sing-box.
```

- [ ] **Step 4: Run it and verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~Pinned_sing_box"`
Expected: PASS. The first build downloads about 15 MB.

Then run `dotnet test --filter "Category!=Integration"`. Expected: PASS, 103.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "build: download, verify and ship pinned sing-box 1.14.2" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 2: Built-in rule catalog and adapter DNS

**Files:**
- Create: `src/NetRoute.Core/rules/builtin.json`, `src/NetRoute.Core/SmartRouting/RuleCatalog.cs`, `tests/NetRoute.Core.Tests/RuleCatalogTests.cs`
- Modify: `src/NetRoute.Core/NetRoute.Core.csproj` (copy the JSON to output), `src/NetRoute.Core/Models.cs` (`AdapterInfo` gains `string? DnsServer = null` as its last parameter), `src/NetRoute.Core/Windows/WindowsAdapterSource.cs` (fill it)

**Interfaces:**
- Produces:
  - `record RuleItem(string Id, string GroupId, string GroupName, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains, bool DefaultOn)`
  - `static class RuleCatalog`, with `IReadOnlyList<RuleItem> Parse(string json)`, `IReadOnlyList<RuleItem> Load(string path)`, and `string DefaultPath` = `Path.Combine(AppContext.BaseDirectory, "rules", "builtin.json")`
  - `AdapterInfo.DnsServer` (`string?`): the first IPv4 DNS server of the adapter

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/RuleCatalogTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RuleCatalogTests"`
Expected: build FAILS with `CS0103: The name 'RuleCatalog' does not exist`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/rules/builtin.json`:

```json
{
  "version": 1,
  "groups": [
    { "id": "updates", "name": "System updates", "items": [
      { "id": "windows-update", "name": "Windows Update & Delivery Optimization",
        "domains": ["windowsupdate.com", "update.microsoft.com", "delivery.mp.microsoft.com", "dl.delivery.mp.microsoft.com",
                    "download.windowsupdate.com", "tlu.dl.delivery.mp.microsoft.com", "do.dsp.mp.microsoft.com"] },
      { "id": "microsoft-store", "name": "Microsoft Store",
        "domains": ["storeedgefd.dsx.mp.microsoft.com", "displaycatalog.mp.microsoft.com", "store-images.s-microsoft.com"] } ] },
    { "id": "sync", "name": "Cloud sync", "items": [
      { "id": "onedrive", "name": "OneDrive",
        "processes": ["OneDrive.exe", "OneDrive.Sync.Service.exe"],
        "domains": ["onedrive.live.com", "storage.live.com", "onedrive.com", "1drv.ms"] },
      { "id": "google-drive", "name": "Google Drive", "processes": ["GoogleDriveFS.exe"] },
      { "id": "dropbox", "name": "Dropbox", "processes": ["Dropbox.exe"] },
      { "id": "icloud", "name": "iCloud", "processes": ["iCloudDrive.exe", "iCloudServices.exe", "iCloudPhotos.exe"] } ] },
    { "id": "video", "name": "Video & social", "items": [
      { "id": "youtube", "name": "YouTube",
        "domains": ["youtube.com", "googlevideo.com", "ytimg.com", "youtu.be", "youtube-nocookie.com"] },
      { "id": "facebook", "name": "Facebook",
        "domains": ["facebook.com", "fbcdn.net", "facebook.net", "fb.com", "fbsbx.com"] },
      { "id": "instagram", "name": "Instagram", "domains": ["instagram.com", "cdninstagram.com"] } ] },
    { "id": "games", "name": "Game launchers", "items": [
      { "id": "steam", "name": "Steam", "processes": ["steam.exe"], "domains": ["steamcontent.com", "steamstatic.com"] },
      { "id": "epic", "name": "Epic Games", "processes": ["EpicGamesLauncher.exe"],
        "domains": ["epicgames-download1.akamaized.net"] },
      { "id": "battlenet", "name": "Battle.net", "processes": ["Battle.net.exe", "Agent.exe"],
        "domains": ["blzddist1-a.akamaihd.net", "blizzard.com"] },
      { "id": "xbox", "name": "Xbox / Microsoft gaming", "processes": ["XboxPcApp.exe", "gamingservices.exe"],
        "domains": ["assets1.xboxlive.com"] } ] }
  ]
}
```

In `src/NetRoute.Core/NetRoute.Core.csproj`, add:

```xml
  <ItemGroup>
    <None Include="rules\builtin.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
```

`src/NetRoute.Core/SmartRouting/RuleCatalog.cs`:

```csharp
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
```

In `src/NetRoute.Core/Models.cs`, give `AdapterInfo` a new last parameter with a default value, so existing calls still compile:

```csharp
public sealed record AdapterInfo(
    int Index,
    string Name,
    string Description,
    AdapterKind Kind,
    bool IsUp,
    bool HasGateway,
    string? IPv4,
    string Mac,
    string? DnsServer = null);
```

In `src/NetRoute.Core/Windows/WindowsAdapterSource.cs`:
- compute the first IPv4 DNS server: `properties.DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()`;
- pass it as the last `AdapterInfo` argument.

- [ ] **Step 4: Run and verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 106.

- [ ] **Step 5: Commit** with subject `feat(core): built-in Smart routing rule catalog; adapters report DNS server`.

---

### Task 3: Smart routing settings, user rules and the effective rule set

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/SmartRoutingSettings.cs`, `src/NetRoute.Core/SmartRouting/RuleSet.cs`
- Modify: `src/NetRoute.Core/Settings.cs` (add `AppSettings.SmartRouting`, and repair a `null` value in `Load`)
- Create: `tests/NetRoute.Core.Tests/SmartRoutingSettingsTests.cs`, `tests/NetRoute.Core.Tests/RuleSetTests.cs`

**Interfaces:**
- Consumes: `RuleItem` (Task 2), `SettingsStore` and `AppSettings` (v1)
- Produces:
  - `enum UserRuleType { App, Website }`
  - `record UserRule(UserRuleType Type, string Value, bool Enabled = true)`, with:
    - `static bool TryCreate(UserRuleType type, string raw, out UserRule? rule, out string? error)`
    - `static string IdOf(UserRule r)`, which returns `"user:app:<lower value>"` or `"user:website:<lower value>"`
  - `record SmartRoutingSettings`, with:
    - properties `bool Enabled` (default false), `IReadOnlyDictionary<string,bool> Items`, `IReadOnlyList<UserRule> UserRules`
    - methods `bool IsItemOn(RuleItem item)`, `SmartRoutingSettings WithItem(string id, bool on)`, `WithUserRule(UserRule rule)` (adds, or replaces the same Type+Value), `WithoutUserRule(UserRule rule)`
    - value equality over its collections
  - `AppSettings.SmartRouting` (`SmartRoutingSettings`, default `new()`)
  - `record RuleEntry(string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains)`
  - `record RuleSet(IReadOnlyList<RuleEntry> Entries)`, with:
    - `static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings)`
    - `string Fingerprint`
    - `RuleEntry? FindByProcess(string? processName)`
    - `RuleEntry? FindByHost(string? host)`

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/SmartRoutingSettingsTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public sealed class SmartRoutingSettingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("netroute-smart-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static RuleItem Item(string id, bool defaultOn = true) => new(id, "g", "G", id, [], [id + ".com"], defaultOn);

    [Theory]
    [InlineData(UserRuleType.Website, "https://www.YouTube.com/watch?v=x", "youtube.com")]
    [InlineData(UserRuleType.Website, "*.dropbox.com", "dropbox.com")]
    [InlineData(UserRuleType.Website, "  Netflix.com:443/ ", "netflix.com")]
    [InlineData(UserRuleType.App, @"C:\Apps\qbittorrent.exe", "qbittorrent.exe")]
    [InlineData(UserRuleType.App, "\"Steam.EXE\"", "Steam.EXE")]
    public void UserRule_normalises_and_validates(UserRuleType type, string raw, string expected)
    {
        Assert.True(UserRule.TryCreate(type, raw, out var rule, out var error), error);
        Assert.Equal(new UserRule(type, expected), rule);
    }

    [Theory]
    [InlineData(UserRuleType.Website, "")]
    [InlineData(UserRuleType.Website, "not a site")]
    [InlineData(UserRuleType.Website, "localhost")]
    [InlineData(UserRuleType.App, "notepad")]
    [InlineData(UserRuleType.App, "   ")]
    public void UserRule_rejects_garbage_with_a_reason(UserRuleType type, string raw)
    {
        Assert.False(UserRule.TryCreate(type, raw, out var rule, out var error));
        Assert.Null(rule);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Items_default_to_the_catalog_and_can_be_overridden()
    {
        var settings = new SmartRoutingSettings();

        Assert.True(settings.IsItemOn(Item("youtube")));
        Assert.False(settings.IsItemOn(Item("off", defaultOn: false)));
        var changed = settings.WithItem("youtube", false);
        Assert.False(changed.IsItemOn(Item("youtube")));
        Assert.True(settings.IsItemOn(Item("youtube"))); // original untouched
    }

    [Fact]
    public void User_rules_are_added_replaced_and_removed_case_insensitively()
    {
        var settings = new SmartRoutingSettings()
            .WithUserRule(new UserRule(UserRuleType.Website, "dropbox.com"))
            .WithUserRule(new UserRule(UserRuleType.Website, "DropBox.com", Enabled: false));

        var only = Assert.Single(settings.UserRules);
        Assert.False(only.Enabled);
        Assert.Empty(settings.WithoutUserRule(new UserRule(UserRuleType.Website, "dropbox.com")).UserRules);
    }

    [Fact]
    public void Settings_with_smart_routing_round_trip_with_value_equality()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings
        {
            SmartRouting = new SmartRoutingSettings { Enabled = true }
                .WithItem("facebook", false)
                .WithUserRule(new UserRule(UserRuleType.App, "qbittorrent.exe")),
        };

        store.Save(settings);

        Assert.Equal(settings, store.Load().Settings);
        Assert.Contains("\"App\"", File.ReadAllText(Path.Combine(_dir, "settings.json"))); // enum written as text
    }

    [Theory]
    [InlineData("""{ "Mode": "Phone" }""")]
    [InlineData("""{ "Mode": "Phone", "SmartRouting": null }""")]
    public void Missing_or_null_smart_routing_loads_as_disabled_defaults(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);

        var loaded = new SettingsStore(path).Load();

        Assert.False(loaded.Recovered);
        Assert.Equal(new SmartRoutingSettings(), loaded.Settings.SmartRouting);
        Assert.False(loaded.Settings.SmartRouting.Enabled);
    }
}
```

`tests/NetRoute.Core.Tests/RuleSetTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class RuleSetTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com", "googlevideo.com"], true),
        new("facebook", "video", "Video", "Facebook", [], ["facebook.com"], true),
        new("onedrive", "sync", "Sync", "OneDrive", ["OneDrive.exe", "OneDrive.Sync.Service.exe"], ["onedrive.live.com"], true),
    ];

    [Fact]
    public void Build_keeps_enabled_items_and_enabled_user_rules()
    {
        var settings = new SmartRoutingSettings()
            .WithItem("facebook", false)
            .WithUserRule(new UserRule(UserRuleType.App, "qbittorrent.exe"))
            .WithUserRule(new UserRule(UserRuleType.Website, "netflix.com", Enabled: false));

        var set = RuleSet.Build(Catalog, settings);

        Assert.Equal(new[] { "youtube", "onedrive", "user:app:qbittorrent.exe" }, set.Entries.Select(e => e.Id));
        var user = set.Entries[2];
        Assert.Equal(("qbittorrent.exe", new[] { "qbittorrent.exe" }), (user.Name, user.Processes.ToArray()));
        Assert.Empty(user.Domains);
    }

    [Theory]
    [InlineData("youtube.com", "youtube")]
    [InlineData("rr1---sn-q4flrnld.googlevideo.com", "youtube")]
    [InlineData("notyoutube.com", null)]
    [InlineData(null, null)]
    public void FindByHost_matches_domain_suffixes_only(string? host, string? expected)
    {
        Assert.Equal(expected, RuleSet.Build(Catalog, new()).FindByHost(host)?.Id);
    }

    [Fact]
    public void FindByProcess_is_case_insensitive()
    {
        var set = RuleSet.Build(Catalog, new());

        Assert.Equal("onedrive", set.FindByProcess("onedrive.sync.service.EXE")?.Id);
        Assert.Null(set.FindByProcess("chrome.exe"));
    }

    [Fact]
    public void Fingerprint_changes_when_rules_change()
    {
        var a = RuleSet.Build(Catalog, new()).Fingerprint;
        var b = RuleSet.Build(Catalog, new SmartRoutingSettings().WithItem("youtube", false)).Fingerprint;

        Assert.NotEqual(a, b);
        Assert.Equal(a, RuleSet.Build(Catalog, new()).Fingerprint);
    }
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SmartRoutingSettingsTests|FullyQualifiedName~RuleSetTests"`
Expected: the build FAILS with `CS0246: … 'UserRule' could not be found`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/SmartRouting/SmartRoutingSettings.cs`:

```csharp
using System.Text.RegularExpressions;

namespace NetRoute.Core;

public enum UserRuleType { App, Website }

/// A user-added "keep this off 4G" rule: an app by .exe file name, or a website by domain (subdomains included).
public sealed partial record UserRule(UserRuleType Type, string Value, bool Enabled = true)
{
    public static string IdOf(UserRule rule) =>
        $"user:{rule.Type.ToString().ToLowerInvariant()}:{rule.Value.ToLowerInvariant()}";

    public static bool TryCreate(UserRuleType type, string raw, out UserRule? rule, out string? error)
    {
        rule = null;
        var value = type == UserRuleType.App ? NormaliseApp(raw) : NormaliseWebsite(raw);
        error = value is null
            ? type == UserRuleType.App ? "Enter an app file name ending in .exe, e.g. qbittorrent.exe"
                                       : "Enter a website like youtube.com"
            : null;
        if (value is not null) rule = new UserRule(type, value);
        return value is not null;
    }

    static string? NormaliseApp(string raw)
    {
        var name = Path.GetFileName(raw.Trim().Trim('"'));
        return name.Length > 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? name
            : null;
    }

    static string? NormaliseWebsite(string raw)
    {
        var host = Scheme().Replace(raw.Trim().ToLowerInvariant(), "");
        host = host.Split('/', '?', '#')[0];
        host = Port().Replace(host, "").TrimStart('*').TrimStart('.');
        if (host.StartsWith("www.")) host = host[4..];
        return Hostname().IsMatch(host) ? host : null;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://")] private static partial Regex Scheme();
    [GeneratedRegex(@":\d+$")] private static partial Regex Port();
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}$")] private static partial Regex Hostname();
}

public sealed record SmartRoutingSettings
{
    public bool Enabled { get; init; }
    public IReadOnlyDictionary<string, bool> Items { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyList<UserRule> UserRules { get; init; } = [];

    /// Built-in items not mentioned in Items fall back to their catalog default (ON).
    public bool IsItemOn(RuleItem item) => Items.TryGetValue(item.Id, out var on) ? on : item.DefaultOn;

    public SmartRoutingSettings WithItem(string id, bool on) =>
        this with { Items = new Dictionary<string, bool>(Items) { [id] = on } };

    public SmartRoutingSettings WithUserRule(UserRule rule) =>
        this with { UserRules = [.. UserRules.Where(r => !SameRule(r, rule)), rule] };

    public SmartRoutingSettings WithoutUserRule(UserRule rule) =>
        this with { UserRules = [.. UserRules.Where(r => !SameRule(r, rule))] };

    static bool SameRule(UserRule a, UserRule b) =>
        a.Type == b.Type && string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);

    public bool Equals(SmartRoutingSettings? other) =>
        other is not null && Enabled == other.Enabled
        && Items.Count == other.Items.Count
        && Items.All(kv => other.Items.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && UserRules.SequenceEqual(other.UserRules);

    public override int GetHashCode() => HashCode.Combine(Enabled, Items.Count, UserRules.Count);
}
```

`src/NetRoute.Core/SmartRouting/RuleSet.cs`:

```csharp
namespace NetRoute.Core;

public sealed record RuleEntry(string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains);

/// The enabled "LAN only" rules right now: built-in items switched on plus enabled user rules.
public sealed record RuleSet(IReadOnlyList<RuleEntry> Entries)
{
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings)
    {
        var entries = catalog.Where(settings.IsItemOn)
            .Select(i => new RuleEntry(i.Id, i.Name, i.Processes, i.Domains))
            .ToList();
        foreach (var rule in settings.UserRules.Where(r => r.Enabled))
        {
            entries.Add(rule.Type == UserRuleType.App
                ? new RuleEntry(UserRule.IdOf(rule), rule.Value, [rule.Value], [])
                : new RuleEntry(UserRule.IdOf(rule), rule.Value, [], [rule.Value]));
        }
        return new RuleSet(entries);
    }

    public string Fingerprint =>
        string.Join("|", Entries.Select(e => $"{e.Id}:{string.Join(",", e.Processes)}:{string.Join(",", e.Domains)}"));

    public RuleEntry? FindByProcess(string? processName) =>
        processName is null ? null
            : Entries.FirstOrDefault(e => e.Processes.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)));

    public RuleEntry? FindByHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        var h = host.ToLowerInvariant();
        return Entries.FirstOrDefault(e => e.Domains.Any(d => h == d || h.EndsWith("." + d, StringComparison.Ordinal)));
    }
}
```

In `src/NetRoute.Core/Settings.cs`:
- add `public SmartRoutingSettings SmartRouting { get; init; } = new();` to `AppSettings`;
- in `Load`, right after `Enum.IsDefined`, repair an explicit `null`. The parameter must stay non-nullable for callers.

```csharp
            if (settings.SmartRouting is null) settings = settings with { SmartRouting = new SmartRoutingSettings() };
```

Add `#pragma warning disable CS8073` around that check if the compiler flags it. Otherwise keep it as written; System.Text.Json can set a non-nullable reference property to null.

- [ ] **Step 4: Run and verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS (all; the v1 `SettingsStoreTests` round-trip must still pass).

- [ ] **Step 5: Commit** with subject `feat(core): Smart routing settings, user rules and effective rule set`.

---

### Task 4: sing-box config builder

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/SingBoxConfigBuilder.cs`, `tests/NetRoute.Core.Tests/SingBoxConfigBuilderTests.cs`
- Modify: `tests/NetRoute.Core.Tests/SingBoxIntegrationTests.cs` (add `sing-box check` on a built config)

**Interfaces:**
- Consumes: `RuleSet` and `RuleEntry` (Task 3)
- Produces:
  - `enum RouteExit { Phone, Lan }`
  - `record SingBoxConfigInput(RuleSet Rules, string PhoneInterface, string LanInterface, string? LanDns, RouteExit DefaultExit, bool LanRulesOnPhone, int ApiPort, string ApiSecret)`
  - `record SingBoxConfig(string Json, IReadOnlyDictionary<int, string> RuleIndexToEntryId)`
  - `static class SingBoxConfigBuilder`, with:
    - `Build(SingBoxConfigInput) → SingBoxConfig`
    - consts `PhoneTag="phone"`, `LanTag="lan"`, `LanOnlyTag="lan-only"`, `DefaultTag="default"`
    - `string[] ExcludedRanges`

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/SingBoxConfigBuilderTests.cs`:

```csharp
using System.Text.Json.Nodes;

namespace NetRoute.Core.Tests;

public class SingBoxConfigBuilderTests
{
    static readonly RuleSet Rules = new(
    [
        new RuleEntry("youtube", "YouTube", [], ["youtube.com", "googlevideo.com"]),
        new RuleEntry("onedrive", "OneDrive", ["OneDrive.exe"], ["onedrive.live.com"]),
    ]);

    static SingBoxConfigInput Input(RouteExit exit = RouteExit.Phone, bool lanRulesOnPhone = false, string? lanDns = "192.168.86.1") =>
        new(Rules, "Ethernet 5", "Ethernet", lanDns, exit, lanRulesOnPhone, 41234, "s3cret");

    static JsonNode Parse(SingBoxConfig c) => JsonNode.Parse(c.Json)!;

    static JsonNode Outbound(JsonNode root, string tag) =>
        root["outbounds"]!.AsArray().Single(o => (string)o!["tag"]! == tag)!;

    [Fact]
    public void Outbounds_bind_adapters_and_selectors_default_per_state()
    {
        var root = Parse(SingBoxConfigBuilder.Build(Input()));

        Assert.Equal("Ethernet 5", (string)Outbound(root, "phone")["bind_interface"]!);
        Assert.Equal("Ethernet", (string)Outbound(root, "lan")["bind_interface"]!);
        Assert.Equal("lan", (string)Outbound(root, "lan-only")["default"]!);
        Assert.Equal("phone", (string)Outbound(root, "default")["default"]!);
        Assert.Equal("default", (string)root["route"]!["final"]!);

        var healed = Parse(SingBoxConfigBuilder.Build(Input(RouteExit.Lan, lanRulesOnPhone: true)));
        Assert.Equal("phone", (string)Outbound(healed, "lan-only")["default"]!);
        Assert.Equal("lan", (string)Outbound(healed, "default")["default"]!);
    }

    [Fact]
    public void Each_entry_gets_separate_process_and_domain_rules_mapped_to_its_id()
    {
        var config = SingBoxConfigBuilder.Build(Input());
        var rules = Parse(config)["route"]!["rules"]!.AsArray();

        Assert.Equal("sniff", (string)rules[0]!["action"]!);
        Assert.Equal("hijack-dns", (string)rules[1]!["action"]!);
        Assert.True((bool)rules[2]!["ip_is_private"]!);
        // youtube: domains only -> index 3; onedrive: process -> 4, domains -> 5
        Assert.Equal(new Dictionary<int, string> { [3] = "youtube", [4] = "onedrive", [5] = "onedrive" }, config.RuleIndexToEntryId);
        Assert.Equal("lan-only", (string)rules[4]!["outbound"]!);
        Assert.Equal("OneDrive.exe", (string)rules[4]!["process_name"]![0]!);
        Assert.Null(rules[4]!["domain_suffix"]); // never AND a process with domains
        Assert.Equal("onedrive.live.com", (string)rules[5]!["domain_suffix"]![0]!);
    }

    [Fact]
    public void Tun_excludes_private_ranges_and_api_listens_on_loopback()
    {
        var root = Parse(SingBoxConfigBuilder.Build(Input()));
        var tun = root["inbounds"]![0]!;

        Assert.Equal(("tun", "NetRoute", true, false),
            ((string)tun["type"]!, (string)tun["interface_name"]!, (bool)tun["auto_route"]!, (bool)tun["strict_route"]!));
        Assert.Equal(SingBoxConfigBuilder.ExcludedRanges, tun["route_exclude_address"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("192.168.0.0/16", SingBoxConfigBuilder.ExcludedRanges);
        Assert.Equal("127.0.0.1:41234", (string)root["experimental"]!["clash_api"]!["external_controller"]!);
        Assert.Equal("s3cret", (string)root["experimental"]!["clash_api"]!["secret"]!);
        Assert.Equal("debug", (string)root["log"]!["level"]!);
    }

    [Fact]
    public void Router_local_names_use_the_lan_dns_only_when_known()
    {
        var withLan = Parse(SingBoxConfigBuilder.Build(Input()))["dns"]!;
        Assert.Equal("remote", (string)withLan["final"]!);
        var lanDns = withLan["servers"]!.AsArray().Single(s => (string)s!["tag"]! == "lan-dns")!;
        Assert.Equal(("192.168.86.1", "lan"), ((string)lanDns["server"]!, (string)lanDns["detour"]!));
        Assert.Equal("lan-dns", (string)withLan["rules"]![0]!["server"]!);

        var without = Parse(SingBoxConfigBuilder.Build(Input(lanDns: null)))["dns"]!;
        Assert.Single(without["servers"]!.AsArray());
        Assert.Empty(without["rules"]!.AsArray());
    }

    [Fact]
    public void Empty_rule_set_still_produces_a_valid_shape()
    {
        var config = SingBoxConfigBuilder.Build(Input() with { Rules = new RuleSet([]) });

        Assert.Empty(config.RuleIndexToEntryId);
        Assert.Equal(3, Parse(config)["route"]!["rules"]!.AsArray().Count);
    }
}
```

Append to `tests/NetRoute.Core.Tests/SingBoxIntegrationTests.cs`, inside the class:

```csharp
    [Fact]
    public void Built_config_passes_sing_box_check()
    {
        var config = SingBoxConfigBuilder.Build(new SingBoxConfigInput(
            RuleSet.Build(RuleCatalog.Load(RuleCatalog.DefaultPath), new SmartRoutingSettings()),
            "Ethernet 5", "Ethernet", "192.168.86.1", RouteExit.Phone, false, 41234, "s3cret"));
        var path = Path.Combine(Path.GetTempPath(), $"netroute-check-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, config.Json);
        try
        {
            var (exit, output) = RunSingBox("check", "-c", path);
            Assert.True(exit == 0, output);
        }
        finally { File.Delete(path); }
    }
```

- [ ] **Step 2: Run and verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SingBoxConfigBuilderTests"`
Expected: the build FAILS with `CS0103: … 'SingBoxConfigBuilder'`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/SmartRouting/SingBoxConfigBuilder.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetRoute.Core;

public enum RouteExit { Phone, Lan }

public sealed record SingBoxConfigInput(
    RuleSet Rules, string PhoneInterface, string LanInterface, string? LanDns,
    RouteExit DefaultExit, bool LanRulesOnPhone, int ApiPort, string ApiSecret);

/// RuleIndexToEntryId maps route.rules indexes (as sing-box logs them in "router: match[N]") to RuleEntry ids.
public sealed record SingBoxConfig(string Json, IReadOnlyDictionary<int, string> RuleIndexToEntryId);

/// Builds the sing-box 1.14 config. Shape verified by the feasibility spike; see docs/superpowers/spikes.
public static class SingBoxConfigBuilder
{
    public const string PhoneTag = "phone";
    public const string LanTag = "lan";
    public const string LanOnlyTag = "lan-only";
    public const string DefaultTag = "default";

    public static readonly string[] ExcludedRanges =
    [
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4", "255.255.255.255/32",
        "fc00::/7", "fe80::/10", "ff00::/8",
    ];

    static readonly string[] RouterLocalSuffixes = ["lan", "local", "home", "home.arpa", "internal"];

    public static SingBoxConfig Build(SingBoxConfigInput input)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = LanTag },
        };
        var ruleMap = new Dictionary<int, string>();
        foreach (var entry in input.Rules.Entries)
        {
            // sing-box ANDs process_name with domain fields inside one rule, so they get separate rules.
            if (entry.Processes.Count > 0)
            {
                ruleMap[rules.Count] = entry.Id;
                rules.Add(new JsonObject { ["process_name"] = Strings(entry.Processes), ["outbound"] = LanOnlyTag });
            }
            if (entry.Domains.Count > 0)
            {
                ruleMap[rules.Count] = entry.Id;
                rules.Add(new JsonObject { ["domain_suffix"] = Strings(entry.Domains), ["outbound"] = LanOnlyTag });
            }
        }

        var dnsServers = new JsonArray
        {
            new JsonObject { ["type"] = "udp", ["tag"] = "remote", ["server"] = "1.1.1.1", ["detour"] = DefaultTag },
        };
        var dnsRules = new JsonArray();
        if (input.LanDns is { } lanDns)
        {
            dnsServers.Add(new JsonObject { ["type"] = "udp", ["tag"] = "lan-dns", ["server"] = lanDns, ["detour"] = LanTag });
            dnsRules.Add(new JsonObject { ["domain_suffix"] = Strings(RouterLocalSuffixes), ["server"] = "lan-dns" });
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "debug", ["timestamp"] = true },
            ["dns"] = new JsonObject { ["servers"] = dnsServers, ["rules"] = dnsRules, ["final"] = "remote" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "NetRoute",
                    ["address"] = Strings(["172.19.0.1/30"]),
                    ["auto_route"] = true, ["strict_route"] = false,
                    ["route_exclude_address"] = Strings(ExcludedRanges),
                },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "direct", ["tag"] = PhoneTag, ["bind_interface"] = input.PhoneInterface },
                new JsonObject { ["type"] = "direct", ["tag"] = LanTag, ["bind_interface"] = input.LanInterface },
                new JsonObject
                {
                    ["type"] = "selector", ["tag"] = LanOnlyTag, ["outbounds"] = Strings([LanTag, PhoneTag]),
                    ["default"] = input.LanRulesOnPhone ? PhoneTag : LanTag,
                },
                new JsonObject
                {
                    ["type"] = "selector", ["tag"] = DefaultTag, ["outbounds"] = Strings([PhoneTag, LanTag]),
                    ["default"] = input.DefaultExit == RouteExit.Lan ? LanTag : PhoneTag,
                },
            },
            ["route"] = new JsonObject { ["rules"] = rules, ["final"] = DefaultTag },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{input.ApiPort}", ["secret"] = input.ApiSecret,
                },
            },
        };
        return new SingBoxConfig(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ruleMap);
    }

    static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
```

- [ ] **Step 4: Run and verify they pass**

Run:
- `dotnet test --filter "FullyQualifiedName~SingBoxConfigBuilderTests"`. Expected: PASS.
- `dotnet test --filter "FullyQualifiedName~Built_config_passes_sing_box_check"`. Expected: PASS. If sing-box rejects a field (e.g. a DNS `detour` to a selector), print its error in the report, adjust the builder minimally to what `sing-box check` accepts, update the matching unit-test assertion, and list it as a deviation.
- `dotnet test --filter "Category!=Integration"`. Expected: PASS.

- [ ] **Step 5: Commit** with subject `feat(core): sing-box config builder (TUN, LAN-only and default selectors, rule map)`.

---

### Task 5: sing-box log parser and LAN wait tracker

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/SingBoxLogParser.cs`, `tests/NetRoute.Core.Tests/SingBoxLogParserTests.cs`

**Interfaces:**
- Consumes: `SingBoxConfigBuilder.LanOnlyTag`, `LanTag` and `SingBoxConfig.RuleIndexToEntryId` (Task 4)
- Produces:
  - `abstract record SingBoxLogEvent(string ConnectionId)`, with subtypes `RuleMatched(string ConnectionId, int RuleIndex, string Outbound)` and `DialFailed(string ConnectionId, string Outbound)`
  - `static class SingBoxLogParser`, with `SingBoxLogEvent? Parse(string line)`
  - `sealed class LanWaitTracker(IReadOnlyDictionary<int,string> ruleIndexToEntryId, TimeProvider? time = null)`, with:
    - `string? Observe(SingBoxLogEvent e)`, which returns the entry id when a LAN-only connection failed
    - `IReadOnlyList<string> RecentFailures(TimeSpan window)`
    - `void Clear()`

- [ ] **Step 1: Write the failing tests**

The lines below are copied verbatim from sing-box 1.14.2 output captured during planning, including the ANSI colour codes it writes to stdout.

`tests/NetRoute.Core.Tests/SingBoxLogParserTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public class SingBoxLogParserTests
{
    const string Esc = "\u001b";
    static readonly string Match = $"+0300 2026-10-02 16:50:47 {Esc}[37mDEBUG{Esc}[0m [{Esc}[38;5;151m2975681671{Esc}[0m 1ms] router: match[1] domain_suffix=example.com => route(lan-only)";
    static readonly string Sniff = $"+0300 2026-10-02 16:50:47 {Esc}[37mDEBUG{Esc}[0m [{Esc}[38;5;151m2975681671{Esc}[0m 0ms] router: match[0] => sniff";
    static readonly string FailSelector = $"+0300 2026-10-02 16:50:47 {Esc}[31mERROR{Esc}[0m [{Esc}[38;5;151m2975681671{Esc}[0m 150ms] connection: open connection to example.com:443 using outbound/selector[lan-only]: (dial tcp 172.66.147.243:443: route ip+net : no such network interface)";
    const string FailDirectPlain = "+0300 2026-10-02 16:47:37 ERROR [3855044730 5.0s] connection: open connection to 142.251.163.119:443 using outbound/direct[lan]: dial tcp 142.251.163.119:443: i/o timeout";
    const string Info = "+0300 2026-10-02 16:12:35 INFO [220090278 3ms] outbound/direct[phone]: outbound connection to 104.18.20.213:80";

    [Fact]
    public void Parses_rule_match_through_ansi_codes()
    {
        Assert.Equal(new RuleMatched("2975681671", 1, "lan-only"), SingBoxLogParser.Parse(Match));
    }

    [Fact]
    public void Parses_dial_failures_for_selector_and_direct_outbounds()
    {
        Assert.Equal(new DialFailed("2975681671", "lan-only"), SingBoxLogParser.Parse(FailSelector));
        Assert.Equal(new DialFailed("3855044730", "lan"), SingBoxLogParser.Parse(FailDirectPlain));
    }

    [Theory]
    [InlineData("")]
    [InlineData("random text")]
    public void Ignores_unrelated_lines(string line)
    {
        Assert.Null(SingBoxLogParser.Parse(line));
        Assert.Null(SingBoxLogParser.Parse(Sniff));
        Assert.Null(SingBoxLogParser.Parse(Info));
    }

    [Fact]
    public void Tracker_reports_the_entry_of_a_failed_lan_only_connection()
    {
        var tracker = new LanWaitTracker(new Dictionary<int, string> { [1] = "youtube" });

        Assert.Null(tracker.Observe(SingBoxLogParser.Parse(Match)!));
        Assert.Equal("youtube", tracker.Observe(SingBoxLogParser.Parse(FailSelector)!));
        Assert.Equal(new[] { "youtube" }, tracker.RecentFailures(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Tracker_ignores_failures_of_connections_not_routed_lan_only()
    {
        var tracker = new LanWaitTracker(new Dictionary<int, string> { [1] = "youtube" });

        tracker.Observe(new RuleMatched("9", 1, "default"));   // matched an index but not routed lan-only
        Assert.Null(tracker.Observe(new DialFailed("9", "phone")));
        Assert.Null(tracker.Observe(new DialFailed("unknown", "lan")));
        Assert.Empty(tracker.RecentFailures(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Tracker_maps_both_rule_indexes_to_one_entry()
    {
        var tracker = new LanWaitTracker(new Dictionary<int, string> { [4] = "onedrive", [5] = "onedrive" });

        tracker.Observe(new RuleMatched("a", 4, "lan-only"));
        tracker.Observe(new RuleMatched("b", 5, "lan-only"));
        tracker.Observe(new DialFailed("a", "lan-only"));
        tracker.Observe(new DialFailed("b", "lan-only"));

        Assert.Equal(new[] { "onedrive" }, tracker.RecentFailures(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Old_failures_fall_out_of_the_window_and_clear_empties()
    {
        var time = new FakeTimeProvider();
        var tracker = new LanWaitTracker(new Dictionary<int, string> { [1] = "youtube" }, time);
        tracker.Observe(new RuleMatched("1", 1, "lan-only"));
        tracker.Observe(new DialFailed("1", "lan-only"));

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Empty(tracker.RecentFailures(TimeSpan.FromSeconds(30)));

        tracker.Observe(new RuleMatched("2", 1, "lan-only"));
        tracker.Observe(new DialFailed("2", "lan-only"));
        tracker.Clear();
        Assert.Empty(tracker.RecentFailures(TimeSpan.FromSeconds(30)));
    }
}
```

- [ ] **Step 2: Run and verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SingBoxLogParserTests"`
Expected: the build FAILS with `CS0246: … 'RuleMatched'`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/SmartRouting/SingBoxLogParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace NetRoute.Core;

public abstract record SingBoxLogEvent(string ConnectionId);
public sealed record RuleMatched(string ConnectionId, int RuleIndex, string Outbound) : SingBoxLogEvent(ConnectionId);
public sealed record DialFailed(string ConnectionId, string Outbound) : SingBoxLogEvent(ConnectionId);

/// Parses the two sing-box 1.14 debug lines Smart routing needs (format captured from the real binary).
public static partial class SingBoxLogParser
{
    public static SingBoxLogEvent? Parse(string line)
    {
        var clean = Ansi().Replace(line, "");
        var m = MatchLine().Match(clean);
        if (m.Success)
            return new RuleMatched(m.Groups["id"].Value, int.Parse(m.Groups["idx"].Value), m.Groups["out"].Value);
        var f = FailLine().Match(clean);
        return f.Success ? new DialFailed(f.Groups["id"].Value, f.Groups["out"].Value) : null;
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial Regex Ansi();
    [GeneratedRegex(@"\[(?<id>\d+) [^\]]*\] router: match\[(?<idx>\d+)\] .*=> route\((?<out>[^)]+)\)")] private static partial Regex MatchLine();
    [GeneratedRegex(@"ERROR \[(?<id>\d+) [^\]]*\] connection: open connection to .* using outbound/\w+\[(?<out>[^\]]+)\]")] private static partial Regex FailLine();
}

/// Remembers which entry each LAN-only connection matched, and reports entries whose LAN-only dials failed.
public sealed class LanWaitTracker(IReadOnlyDictionary<int, string> ruleIndexToEntryId, TimeProvider? time = null)
{
    const int MaxTrackedConnections = 4096;
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Dictionary<string, string> _entryByConnection = new();
    readonly Dictionary<string, DateTimeOffset> _lastFailure = new();

    public string? Observe(SingBoxLogEvent e)
    {
        switch (e)
        {
            case RuleMatched m when m.Outbound == SingBoxConfigBuilder.LanOnlyTag
                                    && ruleIndexToEntryId.TryGetValue(m.RuleIndex, out var entry):
                if (_entryByConnection.Count >= MaxTrackedConnections) _entryByConnection.Clear();
                _entryByConnection[m.ConnectionId] = entry;
                return null;
            case DialFailed f when _entryByConnection.Remove(f.ConnectionId, out var failed):
                _lastFailure[failed] = _time.GetUtcNow();
                return failed;
            default:
                return null;
        }
    }

    public IReadOnlyList<string> RecentFailures(TimeSpan window)
    {
        var cutoff = _time.GetUtcNow() - window;
        return _lastFailure.Where(kv => kv.Value >= cutoff).Select(kv => kv.Key).Order().ToList();
    }

    public void Clear() => _lastFailure.Clear();
}
```

- [ ] **Step 4: Run and verify they pass**

Run: `dotnet test --filter "Category!=Integration"`. Expected: PASS.

- [ ] **Step 5: Commit** with subject `feat(core): parse sing-box rule matches and LAN-only dial failures`.

---

### Task 6: sing-box abstractions and the "kept off 4G" counter

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/SingBoxConnection.cs`, `src/NetRoute.Core/SmartRouting/DataSavedCounter.cs`, `tests/NetRoute.Core.Tests/DataSavedCounterTests.cs`

**Interfaces:**
- Consumes: `RuleSet` (Task 3), `SingBoxConfigBuilder` tags (Task 4)
- Produces:
  - `record SingBoxConnection(string Id, string? Host, string? ProcessName, IReadOnlyList<string> Chains, long Upload, long Download)`, with `bool IsLanOnly`. Clash API chains are innermost-first, e.g. `["lan","lan-only"]`.
  - `interface ISingBoxHost`, with:
    - `bool IsRunning`
    - `Task StartAsync(string configJson, CancellationToken ct = default)`
    - `Task StopAsync()`
    - `event Action<string>? LineReceived`
    - `event Action<int>? Exited`
    - Contract: `Exited` is raised for every process exit. For an exit caused by `StopAsync` it is raised **before** `StopAsync` completes.
  - `interface ISingBoxApi`, with `Task<bool> SelectAsync(string group, string outbound, CancellationToken ct = default)` and `Task<IReadOnlyList<SingBoxConnection>?> GetConnectionsAsync(CancellationToken ct = default)`. `GetConnectionsAsync` returns `null` when the API is unreachable.
  - `record DailyStats(DateOnly Day, IReadOnlyDictionary<string,long> BytesByEntry)`, with `long Total`
  - `sealed class DataSavedCounter(DailyStats? restored, DateOnly today)`, with:
    - `DailyStats Snapshot`
    - `void Update(IEnumerable<SingBoxConnection> connections, RuleSet rules, bool phoneIsDefault, DateOnly today)`
    - `static DailyStats? LoadFile(string path)`
    - `static void SaveFile(string path, DailyStats stats)`

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/DataSavedCounterTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public sealed class DataSavedCounterTests : IDisposable
{
    static readonly DateOnly Day1 = new(2026, 10, 2);
    static readonly RuleSet Rules = new(
    [
        new RuleEntry("youtube", "YouTube", [], ["youtube.com", "googlevideo.com"]),
        new RuleEntry("onedrive", "OneDrive", ["OneDrive.exe"], ["onedrive.live.com"]),
    ]);
    readonly string _dir = Directory.CreateTempSubdirectory("netroute-stats-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static SingBoxConnection Lan(string id, string? host, string? process, long up, long down) =>
        new(id, host, process, ["lan", "lan-only"], up, down);

    [Fact]
    public void Counts_lan_only_byte_deltas_while_phone_is_default()
    {
        var counter = new DataSavedCounter(null, Day1);

        counter.Update([Lan("1", "rr1.googlevideo.com", "chrome.exe", 100, 900)], Rules, phoneIsDefault: true, Day1);
        counter.Update([Lan("1", "rr1.googlevideo.com", "chrome.exe", 150, 1850)], Rules, phoneIsDefault: true, Day1);

        Assert.Equal(2000, counter.Snapshot.BytesByEntry["youtube"]);
        Assert.Equal(2000, counter.Snapshot.Total);
    }

    [Fact]
    public void Process_match_wins_and_counts_once()
    {
        var counter = new DataSavedCounter(null, Day1);

        counter.Update([Lan("1", "youtube.com", "OneDrive.exe", 0, 500)], Rules, true, Day1);

        Assert.Equal(new Dictionary<string, long> { ["onedrive"] = 500 }, counter.Snapshot.BytesByEntry);
    }

    [Fact]
    public void Ignores_traffic_when_lan_is_default_or_not_lan_only_or_unknown()
    {
        var counter = new DataSavedCounter(null, Day1);

        counter.Update([Lan("1", "youtube.com", null, 0, 500)], Rules, phoneIsDefault: false, Day1);
        counter.Update([new SingBoxConnection("2", "youtube.com", null, ["phone", "default"], 0, 700)], Rules, true, Day1);
        counter.Update([Lan("3", "example.org", "chrome.exe", 0, 900)], Rules, true, Day1);

        Assert.Equal(0, counter.Snapshot.Total);
    }

    [Fact]
    public void A_new_day_starts_from_zero_and_old_restored_stats_are_ignored()
    {
        var restoredYesterday = new DailyStats(Day1.AddDays(-1), new Dictionary<string, long> { ["youtube"] = 9 });
        var counter = new DataSavedCounter(restoredYesterday, Day1);
        Assert.Equal(0, counter.Snapshot.Total);

        counter.Update([Lan("1", "youtube.com", null, 0, 100)], Rules, true, Day1);
        counter.Update([Lan("1", "youtube.com", null, 0, 300)], Rules, true, Day1.AddDays(1));

        Assert.Equal(Day1.AddDays(1), counter.Snapshot.Day);
        Assert.Equal(200, counter.Snapshot.Total);
    }

    [Fact]
    public void Stats_file_round_trips_and_garbage_loads_as_null()
    {
        var path = Path.Combine(_dir, "stats.json");
        var stats = new DailyStats(Day1, new Dictionary<string, long> { ["youtube"] = 42 });

        DataSavedCounter.SaveFile(path, stats);
        var loaded = DataSavedCounter.LoadFile(path);

        Assert.Equal(Day1, loaded!.Day);
        Assert.Equal(42, loaded.BytesByEntry["youtube"]);
        File.WriteAllText(path, "{ nope");
        Assert.Null(DataSavedCounter.LoadFile(path));
        Assert.Null(DataSavedCounter.LoadFile(Path.Combine(_dir, "missing.json")));
    }
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DataSavedCounterTests"`
Expected: the build FAILS with `CS0246: … 'SingBoxConnection'`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/SmartRouting/SingBoxConnection.cs`:

```csharp
namespace NetRoute.Core;

/// One live connection as reported by sing-box's Clash API.
public sealed record SingBoxConnection(
    string Id, string? Host, string? ProcessName, IReadOnlyList<string> Chains, long Upload, long Download)
{
    /// Chains are innermost-first, e.g. ["lan", "lan-only"] for traffic a LAN-only rule sent out of the LAN.
    public bool IsLanOnly =>
        Chains.Count >= 2 && Chains[0] == SingBoxConfigBuilder.LanTag && Chains[^1] == SingBoxConfigBuilder.LanOnlyTag;
}

/// Runs sing-box.exe. Exited fires for every exit; for StopAsync-caused exits it fires before StopAsync completes.
public interface ISingBoxHost
{
    bool IsRunning { get; }
    Task StartAsync(string configJson, CancellationToken ct = default);
    Task StopAsync();
    event Action<string>? LineReceived;
    event Action<int>? Exited;
}

/// sing-box's local Clash API.
public interface ISingBoxApi
{
    Task<bool> SelectAsync(string group, string outbound, CancellationToken ct = default);

    /// Null when the API cannot be reached.
    Task<IReadOnlyList<SingBoxConnection>?> GetConnectionsAsync(CancellationToken ct = default);
}
```

`src/NetRoute.Core/SmartRouting/DataSavedCounter.cs`:

```csharp
using System.Text.Json;

namespace NetRoute.Core;

public sealed record DailyStats(DateOnly Day, IReadOnlyDictionary<string, long> BytesByEntry)
{
    public long Total => BytesByEntry.Values.Sum();
}

/// Bytes that LAN-only rules kept off 4G today, per entry. Counts connection byte deltas between polls,
/// only while the phone is the default exit (otherwise that traffic would not have used 4G anyway).
public sealed class DataSavedCounter
{
    readonly Dictionary<string, long> _lastSeen = new();
    Dictionary<string, long> _today;
    DateOnly _day;

    public DataSavedCounter(DailyStats? restored, DateOnly today)
    {
        _day = today;
        _today = restored is not null && restored.Day == today ? new(restored.BytesByEntry) : new();
    }

    public DailyStats Snapshot => new(_day, new Dictionary<string, long>(_today));

    public void Update(IEnumerable<SingBoxConnection> connections, RuleSet rules, bool phoneIsDefault, DateOnly today)
    {
        if (today != _day)
        {
            _day = today;
            _today = new();
        }

        var alive = new HashSet<string>();
        foreach (var c in connections)
        {
            alive.Add(c.Id);
            var total = c.Upload + c.Download;
            var delta = total - _lastSeen.GetValueOrDefault(c.Id);
            _lastSeen[c.Id] = total;
            if (!phoneIsDefault || !c.IsLanOnly || delta <= 0) continue;

            var entry = rules.FindByProcess(c.ProcessName) ?? rules.FindByHost(c.Host); // process match wins
            if (entry is not null) _today[entry.Id] = _today.GetValueOrDefault(entry.Id) + delta;
        }
        foreach (var gone in _lastSeen.Keys.Where(id => !alive.Contains(id)).ToList()) _lastSeen.Remove(gone);
    }

    public static DailyStats? LoadFile(string path)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredStats>(File.ReadAllText(path));
            return stored is null ? null : new DailyStats(DateOnly.Parse(stored.Day), stored.BytesByEntry ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    public static void SaveFile(string path, DailyStats stats)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new StoredStats(stats.Day.ToString("yyyy-MM-dd"), new Dictionary<string, long>(stats.BytesByEntry))));
        File.Move(temp, path, overwrite: true);
    }

    sealed record StoredStats(string Day, Dictionary<string, long>? BytesByEntry);
}
```

- [ ] **Step 4: Run them and verify they pass**

Run: `dotnet test --filter "Category!=Integration"`. Expected: PASS.

- [ ] **Step 5: Commit** with subject `feat(core): sing-box host/API abstractions and kept-off-4G counter`.

---

### Task 7: Smart routing controller (state machine)

> **Implemented differently from the code below (review fixes, binding).** The controller code and tests in this task are the starting point. The merged version differs in these ways; later tasks rely on them:
> - `KeepWaiting()` became `Task KeepWaitingAsync()`. `Task ShutdownAsync()` was added: it latches the controller and stops sing-box, and the App calls it at quit and session end. `StopAsync()` stays for tests.
> - sing-box keeps running while the LAN adapter is missing (last-known LAN name/DNS), so LAN-only traffic **waits** instead of leaking to 4G. Losing the phone still stops it.
> - "Use phone" and "keep waiting" are only accepted during a real LAN outage. The popup is raised only after the LAN has been offline for `LanOfflinePopupDelay` (10 s).
> - Control-API health: after `ApiStartGrace` (30 s) three null polls restart sing-box and count as a crash. All awaits use `ConfigureAwait(false)`, and every entry point logs and publishes instead of throwing.
> - Both selectors set `interrupt_exist_connections` (Task 4 builder).

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/SmartRoutingController.cs`, `tests/NetRoute.Core.Tests/SmartFakes.cs`, `tests/NetRoute.Core.Tests/SmartRoutingControllerTests.cs`

**Interfaces:**
- Consumes:
  - `RuleItem`, `RuleSet`, `SmartRoutingSettings` and `AppSettings` (Tasks 2–3)
  - `SingBoxConfigBuilder` (Task 4)
  - `SingBoxLogParser` and `LanWaitTracker` (Task 5)
  - `ISingBoxHost`, `ISingBoxApi`, `DataSavedCounter` and `DailyStats` (Task 6)
  - v1 `NetworkStatus`, `RoutingMode` and `TestAdapters`
- Produces:
  - `enum SmartState { Off, Unavailable, Starting, Running, Faulted }`
  - `record SmartRoutingStatus(SmartState State, string? Message, RouteExit DefaultExit, bool LanRulesOnPhone, bool LanOnline, IReadOnlyList<string> WaitingNames, int RuleCount, DailyStats Today)`, with `bool IsActive` (State == Running) and `bool Waiting`
  - `sealed class SmartRoutingController`:
    - constructor `SmartRoutingController(IReadOnlyList<RuleItem> catalog, ISingBoxHost host, Func<int, string, ISingBoxApi> createApi, Func<int> freePort, DailyStats? restoredStats, Action<string> log, TimeProvider? time = null)`
    - members:
      - `SmartRoutingStatus Status`
      - `event Action<SmartRoutingStatus>? StatusChanged`
      - `event Action<IReadOnlyList<string>>? WaitingDetected` (once per LAN outage)
      - `event Action<string>? Notify`
      - `Task ApplyAsync(NetworkStatus net, AppSettings settings, CancellationToken ct = default)`
      - `Task UseLanRulesOnPhoneAsync()`
      - `void KeepWaiting()`
      - `Task PollStatsAsync(CancellationToken ct = default)`
      - `Task StopAsync()`
      - `internal Task ProcessLineAsync(string line)`
      - `internal Task HandleExitAsync(int code)`
    - constants: `CrashLimit = 3`, `CrashWindow = 5 min`, `WaitWindow = 30 s`, `LanHealthyChecksToRevert = 3`
    - status messages, verbatim:
      - Unavailable: `"Needs administrator rights"`, `"Not needed in LAN mode"`, `"Needs both phone and LAN connected"`
      - Faulted: `"Smart routing stopped — sing-box keeps crashing (see log)"`
      - Notify on revert: `"LAN back — LAN-only traffic is back on the LAN"`

- [ ] **Step 1: Write the fakes and the failing tests**

`tests/NetRoute.Core.Tests/SmartFakes.cs`:

```csharp
namespace NetRoute.Core.Tests;

sealed class FakeSingBoxHost : ISingBoxHost
{
    public bool IsRunning { get; private set; }
    public List<string> Starts { get; } = new();
    public int Stops { get; private set; }
    public Exception? StartThrows { get; set; }
    public event Action<string>? LineReceived;
    public event Action<int>? Exited;

    public Task StartAsync(string configJson, CancellationToken ct = default)
    {
        if (StartThrows is { } ex) throw ex;
        Starts.Add(configJson);
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stops++;
        IsRunning = false;
        Exited?.Invoke(0); // contract: raised before StopAsync completes
        return Task.CompletedTask;
    }

    /// Simulates a crash: process gone. Tests then call controller.HandleExitAsync(code).
    public void Crash() => IsRunning = false;

    public void RaiseLine(string line) => LineReceived?.Invoke(line);
}

sealed class FakeSingBoxApi : ISingBoxApi
{
    public List<(string Group, string Outbound)> Selects { get; } = new();
    public List<SingBoxConnection> Connections { get; } = new();

    public Task<bool> SelectAsync(string group, string outbound, CancellationToken ct = default)
    {
        Selects.Add((group, outbound));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<SingBoxConnection>?> GetConnectionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SingBoxConnection>?>(Connections.ToList());
}
```

`tests/NetRoute.Core.Tests/SmartRoutingControllerTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public class SmartRoutingControllerTests
{
    // One domain-only item: its rule is route.rules index 3 (0 sniff, 1 hijack-dns, 2 private).
    static readonly IReadOnlyList<RuleItem> Catalog = [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];
    const string MatchYouTube = "+0300 2026-10-02 16:50:47 DEBUG [42 1ms] router: match[3] domain_suffix=youtube.com => route(lan-only)";
    const string FailLanOnly = "+0300 2026-10-02 16:50:52 ERROR [42 5.0s] connection: open connection to 1.2.3.4:443 using outbound/selector[lan-only]: dial tcp 1.2.3.4:443: i/o timeout";

    readonly FakeSingBoxHost _host = new();
    readonly FakeSingBoxApi _api = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
    readonly List<IReadOnlyList<string>> _popups = new();
    readonly List<string> _notes = new();

    SmartRoutingController Create()
    {
        var c = new SmartRoutingController(Catalog, _host, (_, _) => _api, () => 40000, null, _ => { }, _time);
        c.WaitingDetected += _popups.Add;
        c.Notify += _notes.Add;
        return c;
    }

    static AppSettings On(bool enabled = true) => new() { SmartRouting = new SmartRoutingSettings { Enabled = enabled } };

    static NetworkStatus Net(RoutingMode mode = RoutingMode.Phone, int phoneIndex = 31, bool lan = true, int? lanMs = 12,
                             bool healing = false, bool canModify = true) =>
        new(mode,
            new DetectionResult(TestAdapters.Phone(phoneIndex), DetectionIssue.None,
                lan ? TestAdapters.Lan() with { DnsServer = "192.168.86.1" } : null, lan ? DetectionIssue.None : DetectionIssue.NotFound),
            InternetPath.Phone, InternetPath.None, 30, lan ? lanMs : null, canModify, null, IsHealing: healing);

    [Fact]
    public async Task Disabled_does_not_start()
    {
        var c = Create();

        await c.ApplyAsync(Net(), On(false));

        Assert.Empty(_host.Starts);
        Assert.Equal(SmartState.Off, c.Status.State);
    }

    [Fact]
    public async Task Starts_in_phone_mode_with_a_config_bound_to_both_adapters()
    {
        var c = Create();

        await c.ApplyAsync(Net(), On());

        var json = Assert.Single(_host.Starts);
        Assert.Contains("\"Ethernet 31\"", json);
        Assert.Contains("\"Ethernet\"", json);
        Assert.Contains("127.0.0.1:40000", json);
        Assert.Equal((SmartState.Running, 1, RouteExit.Phone), (c.Status.State, c.Status.RuleCount, c.Status.DefaultExit));
    }

    [Theory]
    [InlineData(RoutingMode.Lan, true, true, "Not needed in LAN mode")]
    [InlineData(RoutingMode.Phone, false, true, "Needs administrator rights")]
    [InlineData(RoutingMode.Phone, true, false, "Needs both phone and LAN connected")]
    public async Task Is_unavailable_and_stopped_when_not_applicable(RoutingMode mode, bool canModify, bool lan, string message)
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(mode, lan: lan, canModify: canModify), On());

        Assert.Equal(1, _host.Stops);
        Assert.Equal((SmartState.Unavailable, message), (c.Status.State, c.Status.Message));
    }

    [Fact]
    public async Task Same_inputs_do_not_restart_and_auto_mode_uses_phone()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(RoutingMode.Auto), On());

        Assert.Single(_host.Starts);
        Assert.Equal(RouteExit.Phone, c.Status.DefaultExit);
    }

    [Fact]
    public async Task Heal_switches_the_default_selector_without_restart()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(healing: true), On());

        Assert.Single(_host.Starts);
        Assert.Contains(("default", "lan"), _api.Selects);
        Assert.Equal(RouteExit.Lan, c.Status.DefaultExit);
    }

    [Fact]
    public async Task Adapter_rename_restarts_with_new_config()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(phoneIndex: 36), On()); // replugged phone: "Ethernet 36"

        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(1, _host.Stops);
        Assert.Contains("\"Ethernet 36\"", _host.Starts[1]);
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Rule_change_restarts()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(), On() with { SmartRouting = On().SmartRouting.WithItem("youtube", false) });

        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(0, c.Status.RuleCount);
    }

    [Fact]
    public async Task Unexpected_exit_restarts_then_faults_after_three_in_five_minutes()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        for (var i = 0; i < 3; i++)
        {
            _host.Crash();
            await c.HandleExitAsync(1);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(3, _host.Starts.Count); // initial + 2 restarts; the 3rd crash faults instead
        Assert.Equal(SmartState.Faulted, c.Status.State);
        Assert.Equal("Smart routing stopped — sing-box keeps crashing (see log)", c.Status.Message);
        Assert.Contains("Smart routing stopped — sing-box keeps crashing (see log)", _notes);

        await c.ApplyAsync(Net(), On(false));
        await c.ApplyAsync(Net(), On()); // user switches it off and on again
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Crashes_spread_beyond_the_window_do_not_fault()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        for (var i = 0; i < 3; i++)
        {
            _host.Crash();
            await c.HandleExitAsync(1);
            _time.Advance(TimeSpan.FromMinutes(3));
        }

        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Start_failure_counts_as_crash_and_faults_after_three()
    {
        var c = Create();
        _host.StartThrows = new FileNotFoundException("sing-box.exe not found");

        await c.ApplyAsync(Net(), On());
        Assert.NotEqual(SmartState.Running, c.Status.State);
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(), On());

        Assert.Equal(SmartState.Faulted, c.Status.State);
    }

    [Fact]
    public async Task Waiting_popup_is_raised_once_per_lan_outage()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On()); // LAN offline

        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);
        await c.ProcessLineAsync(MatchYouTube.Replace("[42", "[43"));
        await c.ProcessLineAsync(FailLanOnly.Replace("[42", "[43"));

        Assert.Equal(new[] { "YouTube" }, Assert.Single(_popups));
        Assert.Equal(new[] { "YouTube" }, c.Status.WaitingNames);
    }

    [Fact]
    public async Task Failures_while_lan_is_online_do_not_count_as_waiting()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);

        Assert.Empty(_popups);
        Assert.Empty(c.Status.WaitingNames);
    }

    [Fact]
    public async Task Keep_waiting_suppresses_until_the_lan_returns()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        c.KeepWaiting();
        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);
        Assert.Empty(_popups);

        for (var i = 0; i < 3; i++) await c.ApplyAsync(Net(), On()); // LAN healthy x3 -> outage over
        await c.ApplyAsync(Net(lanMs: null), On());                    // new outage
        await c.ProcessLineAsync(MatchYouTube.Replace("[42", "[44"));
        await c.ProcessLineAsync(FailLanOnly.Replace("[42", "[44"));

        Assert.Single(_popups);
    }

    [Fact]
    public async Task Use_phone_until_lan_back_switches_selector_and_reverts_after_three_healthy_checks()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());

        await c.UseLanRulesOnPhoneAsync();

        Assert.Contains(("lan-only", "phone"), _api.Selects);
        Assert.True(c.Status.LanRulesOnPhone);
        Assert.Empty(c.Status.WaitingNames);

        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(), On());
        Assert.True(c.Status.LanRulesOnPhone);
        await c.ApplyAsync(Net(), On());

        Assert.False(c.Status.LanRulesOnPhone);
        Assert.Equal(("lan-only", "lan"), _api.Selects.Last());
        Assert.Contains("LAN back — LAN-only traffic is back on the LAN", _notes);
    }

    [Fact]
    public async Task Stats_poll_counts_lan_only_bytes_while_phone_is_default()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.Connections.Add(new SingBoxConnection("1", "youtube.com", "chrome.exe", ["lan", "lan-only"], 10, 990));

        await c.PollStatsAsync();

        Assert.Equal(1000, c.Status.Today.Total);
    }

    [Fact]
    public async Task Stop_is_not_counted_as_a_crash()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.StopAsync();
        await c.ApplyAsync(Net(), On());

        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Host_events_are_wired_to_the_controller()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());

        _host.RaiseLine(MatchYouTube);
        _host.RaiseLine(FailLanOnly);

        Assert.True(SpinWait.SpinUntil(() => _popups.Count == 1, TimeSpan.FromSeconds(5)));
    }
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SmartRoutingControllerTests"`
Expected: the build FAILS with `CS0246: … 'SmartRoutingController'`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/SmartRouting/SmartRoutingController.cs`:

```csharp
namespace NetRoute.Core;

public enum SmartState { Off, Unavailable, Starting, Running, Faulted }

public sealed record SmartRoutingStatus(
    SmartState State, string? Message, RouteExit DefaultExit, bool LanRulesOnPhone, bool LanOnline,
    IReadOnlyList<string> WaitingNames, int RuleCount, DailyStats Today)
{
    public bool IsActive => State == SmartState.Running;
    public bool Waiting => WaitingNames.Count > 0;
}

/// Owns the sing-box lifecycle for Smart routing: when it runs, with which config, which exit the selectors use,
/// crash/restart policy, LAN-outage "waiting" detection and the kept-off-4G counter.
/// All state is guarded by one gate; host events arrive on other threads and queue behind it.
public sealed class SmartRoutingController
{
    public const int CrashLimit = 3;
    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WaitWindow = TimeSpan.FromSeconds(30);
    public const int LanHealthyChecksToRevert = 3;

    const string FaultMessage = "Smart routing stopped — sing-box keeps crashing (see log)";

    readonly IReadOnlyList<RuleItem> _catalog;
    readonly ISingBoxHost _host;
    readonly Func<int, string, ISingBoxApi> _createApi;
    readonly Func<int> _freePort;
    readonly Action<string> _log;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly List<DateTimeOffset> _crashes = new();
    readonly Queue<string> _recentLines = new();
    readonly DataSavedCounter _counter;

    ISingBoxApi? _api;
    LanWaitTracker? _tracker;
    RuleSet _rules = new([]);
    NetworkStatus? _net;
    AppSettings? _settings;
    string? _runningKey;
    bool _stopping;
    bool _faulted;
    bool _lastEnabled;
    RouteExit _wantedExit = RouteExit.Phone;
    RouteExit _appliedExit = RouteExit.Phone;
    bool _lanRulesOnPhone;
    bool _appliedLanRulesOnPhone;
    bool _lanOnline = true;
    int _lanHealthyCount;
    bool _popupRaised;
    bool _keepWaiting;

    public SmartRoutingController(
        IReadOnlyList<RuleItem> catalog, ISingBoxHost host, Func<int, string, ISingBoxApi> createApi,
        Func<int> freePort, DailyStats? restoredStats, Action<string> log, TimeProvider? time = null)
    {
        _catalog = catalog;
        _host = host;
        _createApi = createApi;
        _freePort = freePort;
        _log = log;
        _time = time ?? TimeProvider.System;
        _counter = new DataSavedCounter(restoredStats, Today());
        _host.LineReceived += line => _ = ProcessLineAsync(line);
        _host.Exited += code => _ = HandleExitAsync(code);
        Status = new SmartRoutingStatus(SmartState.Off, null, RouteExit.Phone, false, true, [], 0, _counter.Snapshot);
    }

    public SmartRoutingStatus Status { get; private set; }
    public event Action<SmartRoutingStatus>? StatusChanged;
    public event Action<IReadOnlyList<string>>? WaitingDetected;
    public event Action<string>? Notify;

    public async Task ApplyAsync(NetworkStatus net, AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _net = net;
            _settings = settings;
            var smart = settings.SmartRouting;
            if (smart.Enabled && !_lastEnabled)
            {
                _faulted = false; // switching it on again clears a fault
                _crashes.Clear();
            }
            _lastEnabled = smart.Enabled;
            _rules = RuleSet.Build(_catalog, smart);
            UpdateLanHealth(net);

            var (state, _) = Desired();
            if (state != SmartState.Running)
            {
                await StopIfRunningAsync();
                Publish();
                return;
            }

            _wantedExit = net.Mode == RoutingMode.Phone && net.IsHealing ? RouteExit.Lan : RouteExit.Phone;
            if (!_host.IsRunning || _runningKey != KeyFor(net)) await StartAsync(ct);
            else await SyncSelectorsAsync(ct);
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UseLanRulesOnPhoneAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _lanRulesOnPhone = true;
            _popupRaised = true;
            _log("Smart routing: LAN-only traffic uses the phone until the LAN is back");
            await SyncSelectorsAsync(default);
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void KeepWaiting() => _keepWaiting = true;

    public async Task PollStatsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_api is null || !_host.IsRunning) return;
            if (await _api.GetConnectionsAsync(ct) is not { } connections) return;
            _counter.Update(connections, _rules, _appliedExit == RouteExit.Phone, Today());
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopIfRunningAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ProcessLineAsync(string line)
    {
        await _gate.WaitAsync();
        try
        {
            _recentLines.Enqueue(line);
            if (_recentLines.Count > 20) _recentLines.Dequeue();
            if (_tracker is null || SingBoxLogParser.Parse(line) is not { } ev) return;
            if (_tracker.Observe(ev) is not null) Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task HandleExitAsync(int code)
    {
        if (_stopping) return;
        await _gate.WaitAsync();
        try
        {
            if (_stopping || _host.IsRunning) return; // a stop we asked for, or already restarted
            _runningKey = null;
            _api = null;
            var lastError = _recentLines.LastOrDefault(l => l.Contains("FATAL") || l.Contains("ERROR"));
            RecordCrash($"sing-box exited unexpectedly (code {code}){(lastError is null ? "" : ": " + lastError)}");
            if (Desired().State == SmartState.Running) await StartAsync(default);
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    (SmartState State, string? Message) Desired()
    {
        if (_net is not { } net || _settings is not { } settings || !settings.SmartRouting.Enabled) return (SmartState.Off, null);
        if (_faulted) return (SmartState.Faulted, FaultMessage);
        if (!net.CanModify) return (SmartState.Unavailable, "Needs administrator rights");
        if (net.Mode == RoutingMode.Lan) return (SmartState.Unavailable, "Not needed in LAN mode");
        if (net.Adapters.Phone is null || net.Adapters.Lan is null) return (SmartState.Unavailable, "Needs both phone and LAN connected");
        return (SmartState.Running, null);
    }

    string KeyFor(NetworkStatus net) =>
        $"{net.Adapters.Phone!.Name}|{net.Adapters.Lan!.Name}|{net.Adapters.Lan.DnsServer}|{_rules.Fingerprint}";

    async Task StartAsync(CancellationToken ct)
    {
        await StopIfRunningAsync();
        var net = _net!;
        var port = _freePort();
        var secret = Guid.NewGuid().ToString("N");
        var config = SingBoxConfigBuilder.Build(new SingBoxConfigInput(
            _rules, net.Adapters.Phone!.Name, net.Adapters.Lan!.Name, net.Adapters.Lan.DnsServer,
            _wantedExit, _lanRulesOnPhone, port, secret));
        _tracker = new LanWaitTracker(config.RuleIndexToEntryId, _time);
        _api = _createApi(port, secret);
        _log($"Smart routing: starting sing-box ({_rules.Entries.Count} rules, exit {_wantedExit})");
        try
        {
            await _host.StartAsync(config.Json, ct);
            _runningKey = KeyFor(net);
            _appliedExit = _wantedExit;
            _appliedLanRulesOnPhone = _lanRulesOnPhone;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runningKey = null;
            _api = null;
            RecordCrash($"sing-box could not start: {ex.Message}");
        }
    }

    async Task StopIfRunningAsync()
    {
        if (!_host.IsRunning)
        {
            _runningKey = null;
            return;
        }
        _stopping = true;
        try
        {
            await _host.StopAsync();
        }
        finally
        {
            _stopping = false;
        }
        _runningKey = null;
        _api = null;
        _log("Smart routing: sing-box stopped");
    }

    async Task SyncSelectorsAsync(CancellationToken ct)
    {
        if (_api is null) return;
        if (_appliedExit != _wantedExit
            && await _api.SelectAsync(SingBoxConfigBuilder.DefaultTag, Tag(_wantedExit), ct))
        {
            _appliedExit = _wantedExit;
            _log($"Smart routing: default exit is now {_wantedExit}");
        }
        if (_appliedLanRulesOnPhone != _lanRulesOnPhone
            && await _api.SelectAsync(SingBoxConfigBuilder.LanOnlyTag,
                   _lanRulesOnPhone ? SingBoxConfigBuilder.PhoneTag : SingBoxConfigBuilder.LanTag, ct))
        {
            _appliedLanRulesOnPhone = _lanRulesOnPhone;
        }
    }

    static string Tag(RouteExit exit) => exit == RouteExit.Lan ? SingBoxConfigBuilder.LanTag : SingBoxConfigBuilder.PhoneTag;

    void UpdateLanHealth(NetworkStatus net)
    {
        var healthy = net.Adapters.Lan is not null && net.LanLatencyMs is not null;
        if (!healthy)
        {
            _lanHealthyCount = 0;
            _lanOnline = false;
            return;
        }
        if (_lanOnline || ++_lanHealthyCount < LanHealthyChecksToRevert) return;

        _lanOnline = true;
        _popupRaised = false;
        _keepWaiting = false;
        _tracker?.Clear();
        if (!_lanRulesOnPhone) return;
        _lanRulesOnPhone = false;
        Notify?.Invoke("LAN back — LAN-only traffic is back on the LAN");
        _ = Task.CompletedTask; // selectors are synced by ApplyAsync right after this call
    }

    void RecordCrash(string message)
    {
        var now = _time.GetUtcNow();
        _crashes.Add(now);
        _crashes.RemoveAll(t => now - t > CrashWindow);
        _log("Smart routing: " + message);
        if (_crashes.Count < CrashLimit || _faulted) return;
        _faulted = true;
        _log("Smart routing: " + FaultMessage);
        Notify?.Invoke(FaultMessage);
    }

    void Publish()
    {
        var (desired, message) = Desired();
        var state = desired == SmartState.Running && !(_host.IsRunning && _runningKey is not null) ? SmartState.Starting : desired;
        IReadOnlyList<string> waiting = state == SmartState.Running && !_lanOnline && !_lanRulesOnPhone && _tracker is not null
            ? _tracker.RecentFailures(WaitWindow)
                .Select(id => _rules.Entries.FirstOrDefault(e => e.Id == id)?.Name ?? id).ToList()
            : [];

        Status = new SmartRoutingStatus(state, message, _appliedExit, _lanRulesOnPhone, _lanOnline, waiting,
            _rules.Entries.Count, _counter.Snapshot);
        StatusChanged?.Invoke(Status);

        if (waiting.Count == 0 || _popupRaised || _keepWaiting) return;
        _popupRaised = true;
        WaitingDetected?.Invoke(waiting);
    }

    DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
}
```

Implementation notes for the engineer:
- **Reverting "use phone".** In `ApplyAsync`, `UpdateLanHealth` runs **before** the `Desired()`/start branch. A revert (`_lanRulesOnPhone = false`) is then pushed to sing-box by `SyncSelectorsAsync` (or by a restart) in the same call. Remove the no-op `_ = Task.CompletedTask;` line if your analyzer flags it.
- **Restart vs. select.** `StartAsync` passes the current `_wantedExit` and `_lanRulesOnPhone` into the config, so a restart never needs a follow-up select.

- [ ] **Step 4: Run them and verify they pass**

Run: `dotnet test --filter "Category!=Integration"`. Expected: PASS. Every test in `SmartRoutingControllerTests` must pass, including `Host_events_are_wired_to_the_controller`.

- [ ] **Step 5: Commit** with subject `feat(core): Smart routing controller (lifecycle, crash policy, waiting, use-phone, stats)`.

---

### Task 8: v1 integration (TUN path, adapter filter) and Smart routing presenters

> **Review focus for this task:** the sing-box TUN adapter (`NetRoute`, description `sing-tun`) must never be detected as the phone or LAN. If it were, the controller would oscillate: stop → TUN gone → start → TUN back. Both `AdapterDetector.Detect` and `Candidates` must ignore it, and the test must cover a TUN that is up with a gateway.

**Files:**
- Modify: `src/NetRoute.Core/RouteController.cs`, `src/NetRoute.Core/AdapterDetector.cs`
- Create: `src/NetRoute.Core/SmartRouting/SmartRoutingPresenter.cs`, `tests/NetRoute.Core.Tests/SmartRoutingPresenterTests.cs`
- Modify: `tests/NetRoute.Core.Tests/RouteControllerTests.cs`, `tests/NetRoute.Core.Tests/AdapterDetectorTests.cs`

**Interfaces:**
- Consumes: `SmartRoutingStatus`, `SmartState` and `RouteExit` (Task 7); `RuleItem` and `SmartRoutingSettings` (Tasks 2–3); `DailyStats` (Task 6)
- Produces:
  - `RouteController.ExternalPathResolver` (`Func<int, InternetPath?>?`, settable). When it returns a value for the best interface index, that value is used instead of `NetworkStatus.ResolvePath`. This applies to both IPv4 and IPv6, in `RefreshAsync` and `SetModeAsync`.
  - `AdapterDetector` treats name/description markers `"NetRoute"` and `"sing-tun"` as virtual.
  - `static class ByteFormat`, with `string Human(long bytes)`
  - `enum SmartTone { Normal, Muted, Warning }`, `record SmartRow(string Text, SmartTone Tone)`
  - `static class SmartRoutingPresenter`, with `SmartRow Row(SmartRoutingStatus status)` and `string WaitingText(IReadOnlyList<string> names)`
  - `record PageItem(string Id, string Name, bool On, long TodayBytes)`
  - `record PageGroup(string Id, string Name, bool? On, long TodayBytes, IReadOnlyList<PageItem> Items)`
  - `record PageUserRule(UserRule Rule, long TodayBytes)`
  - `record PageModel(bool Enabled, SmartRow Status, bool CanUsePhone, long TotalToday, IReadOnlyList<PageGroup> Groups, IReadOnlyList<PageUserRule> UserRules)`
  - `static class SmartRoutingPage`, with `PageModel Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, SmartRoutingStatus status)` and `SmartRoutingSettings WithGroup(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string groupId, bool on)`

- [ ] **Step 1: Write the failing tests**

Append to `RouteControllerTests` (inside the class):

```csharp
    [Fact]
    public async Task Tun_interface_is_reported_as_the_smart_routing_exit()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        controller.ExternalPathResolver = index => index == 97 ? InternetPath.Phone : null;

        _routes.BestV4 = 97; // sing-box TUN became the best route
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Empty(_toasts);
    }
```

Append to `AdapterDetectorTests`:

```csharp
    [Fact]
    public void Smart_routing_tun_adapter_is_ignored()
    {
        var tun = new AdapterInfo(97, "NetRoute", "sing-tun", AdapterKind.Ethernet, true, true, "172.19.0.1", "");

        var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), tun], AdapterOverrides.None);

        Assert.Equal((31, 10), (result.Phone?.Index, result.Lan?.Index));
        Assert.DoesNotContain(AdapterDetector.Candidates([tun]), _ => true);
    }
```

`tests/NetRoute.Core.Tests/SmartRoutingPresenterTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class SmartRoutingPresenterTests
{
    static readonly DailyStats NoStats = new(new DateOnly(2026, 10, 2), new Dictionary<string, long>());

    static SmartRoutingStatus Status(SmartState state = SmartState.Running, string? message = null, bool lanRulesOnPhone = false,
                                     IReadOnlyList<string>? waiting = null, int rules = 4, DailyStats? today = null) =>
        new(state, message, RouteExit.Phone, lanRulesOnPhone, true, waiting ?? [], rules, today ?? NoStats);

    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(1536, "2 KB")]
    [InlineData(5 * 1024 * 1024 + 300_000, "5.3 MB")]
    [InlineData(1_288_490_189, "1.2 GB")]
    public void Bytes_are_human_readable(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Human(bytes));

    [Fact]
    public void Card_row_texts_per_state()
    {
        var stats = new DailyStats(NoStats.Day, new Dictionary<string, long> { ["youtube"] = 1_288_490_189 });

        Assert.Equal(new SmartRow("⚡ Smart routing ON · 4 rules · 1.2 GB kept off 4G today", SmartTone.Normal),
            SmartRoutingPresenter.Row(Status(today: stats)));
        Assert.Equal(new SmartRow("⚡ Smart routing off", SmartTone.Muted), SmartRoutingPresenter.Row(Status(SmartState.Off)));
        Assert.Equal(new SmartRow("⚡ Smart routing starting…", SmartTone.Muted), SmartRoutingPresenter.Row(Status(SmartState.Starting)));
        Assert.Equal(new SmartRow("⚡ Smart routing paused — Not needed in LAN mode", SmartTone.Muted),
            SmartRoutingPresenter.Row(Status(SmartState.Unavailable, "Not needed in LAN mode")));
        Assert.Equal(new SmartRow("⚠ Smart routing stopped — sing-box keeps crashing (see log)", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(SmartState.Faulted, "Smart routing stopped — sing-box keeps crashing (see log)")));
        Assert.Equal(new SmartRow("⏸ LAN-only traffic waiting (LAN offline)", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(waiting: ["YouTube"])));
        Assert.Equal(new SmartRow("⚠ Using phone for all traffic — LAN offline", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(lanRulesOnPhone: true)));
    }

    [Fact]
    public void Waiting_text_names_the_items()
    {
        Assert.Equal("YouTube is waiting — the LAN is offline.", SmartRoutingPresenter.WaitingText(["YouTube"]));
        Assert.Equal("YouTube, Windows Update are waiting — the LAN is offline.",
            SmartRoutingPresenter.WaitingText(["YouTube", "Windows Update"]));
    }

    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video & social", "YouTube", [], ["youtube.com"], true),
        new("facebook", "video", "Video & social", "Facebook", [], ["facebook.com"], true),
        new("steam", "games", "Game launchers", "Steam", ["steam.exe"], [], true),
    ];

    [Fact]
    public void Page_groups_items_with_states_and_today_bytes()
    {
        var settings = new SmartRoutingSettings { Enabled = true }
            .WithItem("facebook", false)
            .WithUserRule(new UserRule(UserRuleType.Website, "dropbox.com"));
        var stats = new DailyStats(NoStats.Day, new Dictionary<string, long> { ["youtube"] = 300, ["user:website:dropbox.com"] = 7 });

        var page = SmartRoutingPage.Build(Catalog, settings, Status(today: stats));

        Assert.True(page.Enabled);
        Assert.Equal(307, page.TotalToday);
        var video = page.Groups[0];
        Assert.Equal(("video", "Video & social", (bool?)null, 300L), (video.Id, video.Name, video.On, video.TodayBytes));
        Assert.Equal(new[] { true, false }, video.Items.Select(i => i.On));
        Assert.True(page.Groups[1].On);
        Assert.Equal(7, Assert.Single(page.UserRules).TodayBytes);
        Assert.False(page.CanUsePhone);
    }

    [Fact]
    public void Can_use_phone_only_while_running_with_lan_offline()
    {
        var offline = Status() with { LanOnline = false };

        Assert.True(SmartRoutingPage.Build(Catalog, new() { Enabled = true }, offline).CanUsePhone);
        Assert.False(SmartRoutingPage.Build(Catalog, new() { Enabled = true }, offline with { LanRulesOnPhone = true }).CanUsePhone);
    }

    [Fact]
    public void Group_toggle_sets_every_item_in_the_group()
    {
        var settings = SmartRoutingPage.WithGroup(Catalog, new SmartRoutingSettings(), "video", false);

        Assert.False(settings.IsItemOn(Catalog[0]));
        Assert.False(settings.IsItemOn(Catalog[1]));
        Assert.True(settings.IsItemOn(Catalog[2]));
    }
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: the build FAILS with `CS1061: 'RouteController' does not contain … 'ExternalPathResolver'` and similar.

- [ ] **Step 3: Implement**

In `src/NetRoute.Core/RouteController.cs`:
- add the property:

```csharp
    /// Lets Smart routing report its own TUN as the phone/LAN path it currently uses, so the card and toasts
    /// don't show "other adapter (VPN?)" while sing-box carries traffic. Null result = not ours, use the default.
    public Func<int, InternetPath?>? ExternalPathResolver { get; set; }

    InternetPath PathFor(int? bestIndex, DetectionResult adapters) =>
        bestIndex is { } index && ExternalPathResolver?.Invoke(index) is { } external
            ? external
            : NetworkStatus.ResolvePath(bestIndex, adapters);
```

- replace **every** `NetworkStatus.ResolvePath(` call inside `RouteController` with `PathFor(`. There are four: IPv4 and IPv6, in both `RefreshAsync` and `SetModeAsync`.

In `src/NetRoute.Core/AdapterDetector.cs`, add `"NetRoute"` and `"sing-tun"` to `VirtualMarkers`.

`src/NetRoute.Core/SmartRouting/SmartRoutingPresenter.cs`:

```csharp
using System.Globalization;

namespace NetRoute.Core;

public static class ByteFormat
{
    public static string Human(long bytes) => bytes switch
    {
        < 1024L * 1024 => $"{Math.Round(bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} MB",
        _ => $"{(bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} GB",
    };
}

public enum SmartTone { Normal, Muted, Warning }

public sealed record SmartRow(string Text, SmartTone Tone);

public static class SmartRoutingPresenter
{
    public static SmartRow Row(SmartRoutingStatus s) => s.State switch
    {
        SmartState.Off => new("⚡ Smart routing off", SmartTone.Muted),
        SmartState.Starting => new("⚡ Smart routing starting…", SmartTone.Muted),
        SmartState.Unavailable => new($"⚡ Smart routing paused — {s.Message}", SmartTone.Muted),
        SmartState.Faulted => new($"⚠ {s.Message}", SmartTone.Warning),
        _ when s.Waiting => new("⏸ LAN-only traffic waiting (LAN offline)", SmartTone.Warning),
        _ when s.LanRulesOnPhone => new("⚠ Using phone for all traffic — LAN offline", SmartTone.Warning),
        _ => new($"⚡ Smart routing ON · {s.RuleCount} rules · {ByteFormat.Human(s.Today.Total)} kept off 4G today", SmartTone.Normal),
    };

    public static string WaitingText(IReadOnlyList<string> names) =>
        $"{string.Join(", ", names)} {(names.Count == 1 ? "is" : "are")} waiting — the LAN is offline.";
}

public sealed record PageItem(string Id, string Name, bool On, long TodayBytes);

public sealed record PageGroup(string Id, string Name, bool? On, long TodayBytes, IReadOnlyList<PageItem> Items);

public sealed record PageUserRule(UserRule Rule, long TodayBytes);

public sealed record PageModel(
    bool Enabled, SmartRow Status, bool CanUsePhone, long TotalToday,
    IReadOnlyList<PageGroup> Groups, IReadOnlyList<PageUserRule> UserRules);

/// Pure model for the management page.
public static class SmartRoutingPage
{
    public static PageModel Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, SmartRoutingStatus status)
    {
        long Bytes(string id) => status.Today.BytesByEntry.GetValueOrDefault(id);

        var groups = catalog.GroupBy(i => (i.GroupId, i.GroupName)).Select(g =>
        {
            var items = g.Select(i => new PageItem(i.Id, i.Name, settings.IsItemOn(i), Bytes(i.Id))).ToList();
            bool? on = items.All(i => i.On) ? true : items.Any(i => i.On) ? null : false;
            return new PageGroup(g.Key.GroupId, g.Key.GroupName, on, items.Sum(i => i.TodayBytes), items);
        }).ToList();
        var rules = settings.UserRules.Select(r => new PageUserRule(r, Bytes(UserRule.IdOf(r)))).ToList();

        return new PageModel(
            settings.Enabled, SmartRoutingPresenter.Row(status),
            CanUsePhone: status.State == SmartState.Running && !status.LanOnline && !status.LanRulesOnPhone,
            status.Today.Total, groups, rules);
    }

    public static SmartRoutingSettings WithGroup(
        IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, string groupId, bool on) =>
        catalog.Where(i => i.GroupId == groupId).Aggregate(settings, (s, i) => s.WithItem(i.Id, on));
}
```

- [ ] **Step 4: Run them and verify they pass**

Run: `dotnet test --filter "Category!=Integration"`. Expected: PASS, with all v1 tests unchanged.

- [ ] **Step 5: Commit** with subject `feat(core): TUN path resolver, tun adapter filter, Smart routing presenters`.

---

### Task 9: Windows sing-box host, Clash API client and free port

> **Implemented differently from the code below (review fixes, binding).** `SingBoxHost` takes only `exePath`: it runs `sing-box run -c stdin` and feeds the config on stdin, so no config file is ever written (the elevated process must not read a user-writable file). The process is assigned to a shared kill-on-close Windows Job Object, so it dies with the widget (verified by experiment). `StopAsync` is bounded (10 s, `TimeoutException`), `KillOrphans` waits for exit, and subscriber exceptions are isolated. `KillOnParentExit` and `internal CurrentProcess` are exposed for tests.

**Files:**
- Create: `src/NetRoute.Core/Windows/SingBoxHost.cs`, `src/NetRoute.Core/Windows/SingBoxApi.cs`, `src/NetRoute.Core/Windows/FreePort.cs`
- Create: `tests/NetRoute.Core.Tests/SingBoxApiParseTests.cs` (unit)
- Modify: `tests/NetRoute.Core.Tests/SingBoxIntegrationTests.cs`, adding a local-proxy end-to-end test. It is non-admin and creates no TUN.

**Interfaces:**
- Consumes: `ISingBoxHost`, `ISingBoxApi` and `SingBoxConnection` (Task 6); `SingBoxLogParser`, `RuleMatched` and `DialFailed` (Task 5)
- Produces (namespace `NetRoute.Core.Windows`):
  - `sealed class SingBoxHost(string exePath, string configPath) : ISingBoxHost`, with `static int KillOrphans(string exePath)`. KillOrphans kills leftover `sing-box.exe` processes started from `exePath` and returns how many it killed.
  - `sealed class SingBoxApi(int port, string secret) : ISingBoxApi, IDisposable`, with `internal static IReadOnlyList<SingBoxConnection> ParseConnections(string json)`
  - `static class FreePort`, with `int Next()`

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/SingBoxApiParseTests.cs` uses JSON captured from sing-box 1.14.2:

```csharp
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class SingBoxApiParseTests
{
    [Fact]
    public void Parses_connections_with_process_file_names()
    {
        const string json = """
            {"connections":[{"chains":["phone","default"],"download":100777,"id":"749c46b5","metadata":{"destinationIP":"","destinationPort":"443","host":"www.wikipedia.org","network":"tcp","processPath":"C:\\Program Files\\Git\\ucrt64\\bin\\curl.exe","sourceIP":"127.0.0.1","sourcePort":"53649","type":"mixed/mixed-in"},"rule":"final","rulePayload":"","start":"2026-10-02T16:50:47+03:00","upload":485},
                             {"chains":["lan","lan-only"],"download":5,"id":"b","metadata":{"host":"","destinationIP":"142.250.1.1","processPath":""},"upload":1}],
             "downloadTotal":100777,"uploadTotal":485}
            """;

        var conns = SingBoxApi.ParseConnections(json);

        Assert.Equal(2, conns.Count);
        Assert.Equal(new SingBoxConnection("749c46b5", "www.wikipedia.org", "curl.exe", ["phone", "default"], 485, 100777) with { Chains = conns[0].Chains }, conns[0]);
        Assert.Equal(new[] { "phone", "default" }, conns[0].Chains);
        Assert.Null(conns[1].Host);
        Assert.Null(conns[1].ProcessName);
        Assert.True(conns[1].IsLanOnly);
    }

    [Theory]
    [InlineData("""{"connections":null}""")]
    [InlineData("""{}""")]
    public void Missing_connections_parse_as_empty(string json) => Assert.Empty(SingBoxApi.ParseConnections(json));
}
```

Append to `SingBoxIntegrationTests` (inside the class). This drives the real binary through a local proxy inbound, so it needs neither admin nor a TUN:

```csharp
    [Fact]
    public async Task Host_api_and_parser_work_end_to_end_through_a_local_proxy()
    {
        var proxyPort = NetRoute.Core.Windows.FreePort.Next();
        var apiPort = NetRoute.Core.Windows.FreePort.Next();
        var config = $$"""
            { "log": { "level": "debug", "timestamp": true },
              "inbounds": [ { "type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": {{proxyPort}} } ],
              "outbounds": [
                { "type": "direct", "tag": "phone" },
                { "type": "direct", "tag": "lan", "bind_interface": "NoSuchAdapter" },
                { "type": "selector", "tag": "lan-only", "outbounds": ["lan", "phone"], "default": "lan" },
                { "type": "selector", "tag": "default", "outbounds": ["phone", "lan"], "default": "phone" } ],
              "route": { "rules": [ { "action": "sniff" }, { "domain_suffix": ["example.com"], "outbound": "lan-only" } ], "final": "default" },
              "experimental": { "clash_api": { "external_controller": "127.0.0.1:{{apiPort}}", "secret": "itest" } } }
            """;
        var host = new NetRoute.Core.Windows.SingBoxHost(SingBoxExe, Path.Combine(Path.GetTempPath(), $"netroute-itest-{Guid.NewGuid():N}.json"));
        var events = new System.Collections.Concurrent.ConcurrentQueue<SingBoxLogEvent>();
        var exits = 0;
        host.LineReceived += l => { if (SingBoxLogParser.Parse(l) is { } e) events.Enqueue(e); };
        host.Exited += _ => Interlocked.Increment(ref exits);
        using var api = new NetRoute.Core.Windows.SingBoxApi(apiPort, "itest");

        await host.StartAsync(config);
        try
        {
            Assert.True(host.IsRunning);
            Assert.True(SpinWait.SpinUntil(() => api.GetConnectionsAsync().Result is not null, TimeSpan.FromSeconds(10)), "API never came up");
            using var client = new HttpClient(new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true })
                { Timeout = TimeSpan.FromSeconds(15) };

            await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("https://example.com/")); // lan-only -> missing adapter
            Assert.True(SpinWait.SpinUntil(() => events.OfType<DialFailed>().Any(), TimeSpan.FromSeconds(10)));
            var matched = events.OfType<RuleMatched>().First(m => m.Outbound == "lan-only");
            Assert.Equal(1, matched.RuleIndex);
            Assert.Contains(events.OfType<DialFailed>(), f => f.ConnectionId == matched.ConnectionId && f.Outbound == "lan-only");

            Assert.True(await api.SelectAsync("lan-only", "phone"));
            using var ok = await client.GetAsync("https://example.com/"); // now via the working direct outbound
            Assert.True(ok.IsSuccessStatusCode);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.False(host.IsRunning);
        Assert.Equal(1, exits); // raised before StopAsync returned
    }
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SingBoxApiParseTests"`
Expected: the build FAILS with `CS0234: … 'NetRoute.Core.Windows.SingBoxApi'`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/Windows/FreePort.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

public static class FreePort
{
    public static int Next()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
```

`src/NetRoute.Core/Windows/SingBoxHost.cs`:

```csharp
using System.Diagnostics;

namespace NetRoute.Core.Windows;

/// Runs sing-box.exe hidden, streams its stdout/stderr lines, and reports exits.
/// Exited fires for every exit; when StopAsync caused it, it fires before StopAsync completes.
public sealed class SingBoxHost(string exePath, string configPath) : ISingBoxHost
{
    Process? _process;
    Task? _monitor;

    public bool IsRunning => _process is { HasExited: false };
    public event Action<string>? LineReceived;
    public event Action<int>? Exited;

    public Task StartAsync(string configJson, CancellationToken ct = default)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("sing-box.exe not found next to the app", exePath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, configJson);

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) LineReceived?.Invoke(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) LineReceived?.Invoke(line); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _monitor = MonitorAsync(process);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_process is not { } process) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // already gone
        if (_monitor is { } monitor) await monitor.ConfigureAwait(false);
        process.Dispose();
        _process = null;
        _monitor = null;
    }

    async Task MonitorAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        process.WaitForExit(); // drain redirected output
        Exited?.Invoke(process.ExitCode);
    }

    /// Leftovers from a widget that crashed would keep routing with a stale config; stop them at startup.
    public static int KillOrphans(string exePath)
    {
        var killed = 0;
        foreach (var p in Process.GetProcessesByName("sing-box"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return killed;
    }
}
```

`src/NetRoute.Core/Windows/SingBoxApi.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NetRoute.Core.Windows;

/// Client for sing-box's local Clash API (127.0.0.1, bearer secret).
public sealed class SingBoxApi : ISingBoxApi, IDisposable
{
    readonly HttpClient _http;

    public SingBoxApi(int port, string secret)
    {
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(3),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<bool> SelectAsync(string group, string outbound, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync($"proxies/{Uri.EscapeDataString(group)}", new { name = outbound }, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<SingBoxConnection>?> GetConnectionsAsync(CancellationToken ct = default)
    {
        try
        {
            return ParseConnections(await _http.GetStringAsync("connections", ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    internal static IReadOnlyList<SingBoxConnection> ParseConnections(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("connections", out var list) || list.ValueKind != JsonValueKind.Array) return [];

        var result = new List<SingBoxConnection>();
        foreach (var c in list.EnumerateArray())
        {
            var meta = c.GetProperty("metadata");
            var host = meta.TryGetProperty("host", out var h) && h.GetString() is { Length: > 0 } hs ? hs : null;
            var path = meta.TryGetProperty("processPath", out var p) && p.GetString() is { Length: > 0 } ps ? ps : null;
            result.Add(new SingBoxConnection(
                c.GetProperty("id").GetString()!, host, path is null ? null : Path.GetFileName(path),
                c.GetProperty("chains").EnumerateArray().Select(x => x.GetString()!).ToList(),
                c.GetProperty("upload").GetInt64(), c.GetProperty("download").GetInt64()));
        }
        return result;
    }

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 4: Run them and verify they pass**

Run:
- `dotnet test --filter "FullyQualifiedName~SingBoxApiParseTests"`. Expected: PASS.
- `dotnet test --filter "FullyQualifiedName~Host_api_and_parser_work_end_to_end"`. Expected: PASS. It needs internet for example.com. If example.com is unreachable from this network, report that instead of weakening the test.
- `dotnet test --filter "Category!=Integration"`. Expected: PASS.

- [ ] **Step 5: Commit** with subject `feat(core): Windows sing-box host, Clash API client, orphan cleanup`.

---

### Task 10: Speed test

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/SpeedTest.cs`, `src/NetRoute.Core/Windows/HttpSpeedProbe.cs`, `tests/NetRoute.Core.Tests/SpeedTestTests.cs`

**Interfaces:**
- Consumes: v1 `DetectionResult` and `AdapterInfo`
- Produces:
  - `interface ISpeedProbe`, with `Task<double?> MeasureMbpsAsync(IPAddress source, CancellationToken ct)`
  - `record SpeedTestResult(double? PhoneMbps, double? LanMbps)`
  - `static class SpeedTest`, with `Task<SpeedTestResult> RunAsync(ISpeedProbe probe, DetectionResult adapters, CancellationToken ct = default)` and `string Describe(SpeedTestResult r)`
  - `sealed class NetRoute.Core.Windows.HttpSpeedProbe(Uri url, TimeSpan timeout) : ISpeedProbe`, with `static HttpSpeedProbe Default()` (Cloudflare, 5 MB, 30 s)

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/SpeedTestTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class SpeedTestTests
{
    sealed class FixedProbe : ISpeedProbe
    {
        public List<IPAddress> Sources { get; } = new();
        public Task<double?> MeasureMbpsAsync(IPAddress source, CancellationToken ct)
        {
            Sources.Add(source);
            return Task.FromResult<double?>(source.ToString() == "192.168.42.11" ? 20.24 : null);
        }
    }

    [Fact]
    public async Task Runs_both_adapters_from_their_own_addresses()
    {
        var probe = new FixedProbe();
        var adapters = new DetectionResult(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None);

        var result = await SpeedTest.RunAsync(probe, adapters);

        Assert.Equal(new SpeedTestResult(20.24, null), result);
        Assert.Equal(new[] { "192.168.42.11", "192.168.86.42" }, probe.Sources.Select(s => s.ToString()).Order());
        Assert.Equal("⚡ Phone 20.2 Mbit/s · LAN —", SpeedTest.Describe(result));
    }

    [Fact]
    public async Task Missing_adapter_is_not_measured()
    {
        var probe = new FixedProbe();

        var result = await SpeedTest.RunAsync(probe, DetectionResult.Empty);

        Assert.Equal(new SpeedTestResult(null, null), result);
        Assert.Empty(probe.Sources);
    }

    [Fact]
    public async Task Http_probe_measures_a_local_download_from_a_bound_source()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            await stream.ReadAsync(buffer); // request headers (small)
            var body = new byte[2 * 1024 * 1024];
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        });

        var mbps = await new HttpSpeedProbe(new Uri($"http://127.0.0.1:{port}/down"), TimeSpan.FromSeconds(10))
            .MeasureMbpsAsync(IPAddress.Loopback, CancellationToken.None);

        await server;
        Assert.NotNull(mbps);
        Assert.True(mbps > 0);
    }

    [Fact]
    public async Task Http_probe_returns_null_when_nothing_answers()
    {
        var port = FreePort.Next();

        var mbps = await new HttpSpeedProbe(new Uri($"http://127.0.0.1:{port}/down"), TimeSpan.FromSeconds(3))
            .MeasureMbpsAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Null(mbps);
    }
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SpeedTestTests"`
Expected: the build FAILS with `CS0246: … 'ISpeedProbe'`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/SmartRouting/SpeedTest.cs`:

```csharp
using System.Globalization;
using System.Net;

namespace NetRoute.Core;

public interface ISpeedProbe
{
    /// Download speed in Mbit/s from the given local address, or null on failure.
    Task<double?> MeasureMbpsAsync(IPAddress source, CancellationToken ct);
}

public sealed record SpeedTestResult(double? PhoneMbps, double? LanMbps);

public static class SpeedTest
{
    public static async Task<SpeedTestResult> RunAsync(ISpeedProbe probe, DetectionResult adapters, CancellationToken ct = default)
    {
        var phone = Measure(probe, adapters.Phone, ct);
        var lan = Measure(probe, adapters.Lan, ct);
        return new SpeedTestResult(await phone, await lan);
    }

    public static string Describe(SpeedTestResult r) => $"⚡ Phone {Mbps(r.PhoneMbps)} · LAN {Mbps(r.LanMbps)}";

    static Task<double?> Measure(ISpeedProbe probe, AdapterInfo? adapter, CancellationToken ct) =>
        adapter?.IPv4 is { } ip ? probe.MeasureMbpsAsync(IPAddress.Parse(ip), ct) : Task.FromResult<double?>(null);

    static string Mbps(double? value) =>
        value is { } v ? $"{v.ToString("0.0", CultureInfo.InvariantCulture)} Mbit/s" : "—";
}
```

`src/NetRoute.Core/Windows/HttpSpeedProbe.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Downloads a test file through one adapter (socket bound to its address; strong-host routing keeps it there).
public sealed class HttpSpeedProbe(Uri url, TimeSpan timeout) : ISpeedProbe
{
    public static HttpSpeedProbe Default() =>
        new(new Uri("https://speed.cloudflare.com/__down?bytes=5000000"), TimeSpan.FromSeconds(30));

    public async Task<double?> MeasureMbpsAsync(IPAddress source, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, source.AddressFamily, token);
                var socket = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = timeout };
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0) total += read;
            var seconds = stopwatch.Elapsed.TotalSeconds;
            return total == 0 || seconds <= 0 ? null : total * 8 / seconds / 1_000_000;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && ex is HttpRequestException or TaskCanceledException or SocketException or IOException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run them and verify they pass**

Run: `dotnet test --filter "Category!=Integration"`. Expected: PASS.

- [ ] **Step 5: Commit** with subject `feat(core): on-demand speed test through each adapter`.

---

### Task 11: App wiring, the card's Smart routing row and the speed test button

**Files:**
- Modify: `src/NetRoute.App/AppPaths.cs`, `src/NetRoute.App/App.xaml.cs`, `src/NetRoute.App/CardWindow.xaml`, `src/NetRoute.App/CardWindow.xaml.cs`

**Interfaces:**
- Consumes:
  - `SmartRoutingController` and `SmartRoutingStatus` (Task 7)
  - `RuleCatalog` (Task 2)
  - `SingBoxHost`, `SingBoxApi` and `FreePort` (Task 9)
  - `DataSavedCounter` (Task 6)
  - `SmartRoutingPresenter`, `SmartRow`, `SmartTone` and `RouteController.ExternalPathResolver` (Task 8)
  - `SpeedTest` and `HttpSpeedProbe` (Task 10)
- Produces:
  - `CardWindow` events `SmartSettingsRequested`, `SpeedTestRequested`, and methods `RenderSmart(SmartRow row)`, `ShowSpeed(string text)`
  - `App` members that Tasks 12–13 use:
    - `SmartRoutingController? _smart`
    - `IReadOnlyList<RuleItem> _catalog`
    - `void UpdateSmart(Func<SmartRoutingSettings, SmartRoutingSettings> change)`
    - `void RenderSmart(SmartRoutingStatus status)`
  - `AppPaths.SingBoxExe`, `AppPaths.SingBoxConfig`, `AppPaths.StatsFile`

- [ ] **Step 1: Paths and card UI**

In `src/NetRoute.App/AppPaths.cs`, add:

```csharp
    public static readonly string SingBoxExe = Path.Combine(AppContext.BaseDirectory, "sing-box", "sing-box.exe");
        public static readonly string StatsFile = Path.Combine(Root, "stats.json");
```

In `src/NetRoute.App/CardWindow.xaml`, directly after the mode-buttons `</UniformGrid>`, insert:

```xml
            <Border Height="1" Margin="0,8" Background="{DynamicResource CardBorder}" />
            <DockPanel>
                <Button DockPanel.Dock="Right" Content="⚡" Style="{StaticResource IconButton}" Click="OnSpeedTest"
                        ToolTip="Speed test of both connections (uses about 5 MB of mobile data)" />
                <Button DockPanel.Dock="Right" Content="⚙" Style="{StaticResource IconButton}" Click="OnSmartSettings"
                        ToolTip="Smart routing: choose what stays off 4G" />
                <TextBlock x:Name="SmartText" Style="{StaticResource Secondary}" TextTrimming="CharacterEllipsis"
                           Cursor="Hand" MouseLeftButtonUp="OnSmartTextClick" Text="⚡ Smart routing off" />
            </DockPanel>
            <TextBlock x:Name="SpeedText" Visibility="Collapsed" Margin="0,4,0,0" Style="{StaticResource Secondary}" />
```

In `src/NetRoute.App/CardWindow.xaml.cs`, add these events next to the existing ones:

```csharp
    public event Action? SmartSettingsRequested;
    public event Action? SpeedTestRequested;
```

and these methods and handlers:

```csharp
    public void RenderSmart(SmartRow row)
    {
        SmartText.Text = row.Text;
        SmartText.SetResourceReference(TextBlock.ForegroundProperty, row.Tone == SmartTone.Warning ? "Warning" : "TextSecondary");
        SmartText.Opacity = row.Tone == SmartTone.Muted ? 0.7 : 1.0;
    }

    public void ShowSpeed(string text)
    {
        SpeedText.Text = text;
        SpeedText.Visibility = Visibility.Visible;
    }

    void OnSmartSettings(object sender, RoutedEventArgs e) => SmartSettingsRequested?.Invoke();

    void OnSmartTextClick(object sender, MouseButtonEventArgs e) => SmartSettingsRequested?.Invoke();

    void OnSpeedTest(object sender, RoutedEventArgs e) => SpeedTestRequested?.Invoke();
```

- [ ] **Step 2: Wire the controller in `App.xaml.cs`**

Add these fields:

```csharp
    SmartRoutingController? _smart;
    IReadOnlyList<RuleItem> _catalog = [];
    long _lastSavedStatsTotal = -1;
    bool _speedTestRunning;
```

Inside the startup `try`, right after `controller.AutoSwitched += …`, add:

```csharp
            _catalog = RuleCatalog.Load(RuleCatalog.DefaultPath);
            var orphans = SingBoxHost.KillOrphans(AppPaths.SingBoxExe);
            if (orphans > 0) log.Info($"Stopped {orphans} leftover sing-box process(es)");
            var host = new SingBoxHost(AppPaths.SingBoxExe); // config is passed on stdin: nothing is written to a user-writable file
            host.LineReceived += line =>
            {
                if (line.Contains("ERROR") || line.Contains("FATAL") || line.Contains("WARN")) log.Info("sing-box: " + line);
            };
            var smart = _smart = new SmartRoutingController(
                _catalog, host, (port, secret) => new SingBoxApi(port, secret), FreePort.Next,
                DataSavedCounter.LoadFile(AppPaths.StatsFile), log.Info);
            smart.StatusChanged += s => Dispatcher.BeginInvoke(new Action(() => RenderSmart(s)));
            smart.Notify += m => Dispatcher.BeginInvoke(new Action(() => _tray?.Notify(m)));
            smart.WaitingDetected += names => Dispatcher.BeginInvoke(new Action(() =>
                _tray?.Notify(SmartRoutingPresenter.WaitingText(names))));
            controller.ExternalPathResolver = index =>
                smart.Status.IsActive && index == TunIndex() // TUN looked up by name on every call: its index changes on each sing-box start
                    ? (smart.Status.DefaultExit == RouteExit.Lan ? InternetPath.Lan : InternetPath.Phone) : null;
            controller.StatusChanged += status => _ = smart.ApplyAsync(status, controller.Settings);
```

After `_card = new CardWindow();` and its existing subscriptions, add:

```csharp
            _card.SpeedTestRequested += RunSpeedTest;
```

In the `_poll.Tick` handler, after `await controller.RefreshAsync(…)`, add:

```csharp
                await smart.PollStatsAsync();
                SaveStatsIfChanged();
```

Add these members to `App`:

```csharp
    static int? TunIndex()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.Name != "NetRoute") continue;
            try { return nic.GetIPProperties().GetIPv4Properties()?.Index; }
            catch (NetworkInformationException) { return null; }
        }
        return null;
    }

    void UpdateSmart(Func<SmartRoutingSettings, SmartRoutingSettings> change)
    {
        _controller!.UpdateSettings(s => s with { SmartRouting = change(s.SmartRouting) });
        _ = _smart!.ApplyAsync(_controller.Status, _controller.Settings);
    }

    void RenderSmart(SmartRoutingStatus status) => _card?.RenderSmart(SmartRoutingPresenter.Row(status));

    void SaveStatsIfChanged()
    {
        if (_smart is not { } smart || smart.Status.Today.Total == _lastSavedStatsTotal) return;
        try
        {
            DataSavedCounter.SaveFile(AppPaths.StatsFile, smart.Status.Today);
            _lastSavedStatsTotal = smart.Status.Today.Total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Error("Saving stats failed", ex);
        }
    }

    async void RunSpeedTest()
    {
        if (_speedTestRunning || _controller is null) return;
        _speedTestRunning = true;
        _card?.ShowSpeed("⚡ Measuring… (about 5 MB of mobile data)");
        try
        {
            var result = await SpeedTest.RunAsync(HttpSpeedProbe.Default(), _controller.Status.Adapters);
            _card?.ShowSpeed($"{SpeedTest.Describe(result)}  ({DateTime.Now:HH:mm})");
            _log?.Info("Speed test: " + SpeedTest.Describe(result));
        }
        finally
        {
            _speedTestRunning = false;
        }
    }

    /// Stops sing-box for good at quit (ShutdownAsync latches the controller). The controller awaits with
    /// ConfigureAwait(false), so waiting here on the UI thread cannot deadlock.
    void StopSmartRouting()
    {
        if (_smart is not { } smart) return;
        SaveStatsIfChanged();
        Task.Run(() => smart.ShutdownAsync()).Wait(TimeSpan.FromSeconds(5)); // ShutdownAsync latches: nothing restarts sing-box afterwards
    }
```

Call `StopSmartRouting();`:
- as the first line of `Quit()`;
- as the first line of `OnSessionEnding(...)`;
- in `OnExit(...)`, before `_tray?.Dispose()`. It is harmless if sing-box has already stopped.

Also add `using System.Net.NetworkInformation;` if it is not already present (it is).

- [ ] **Step 3: Build and run the tests**

```powershell
dotnet build src/NetRoute.App -o <scratch>\nrw-v2-build
dotnet test --filter "Category!=Integration"
```

Expected: 0 warnings, all tests pass, and `<scratch>\nrw-v2-build\sing-box\sing-box.exe` and `rules\builtin.json` exist.

- [ ] **Step 4: Non-elevated smoke check**

Only do this when your shell is **not** elevated.
1. Launch the scratch build.
2. After about 8 s, confirm the log shows no ERROR.
3. Confirm `Get-NetAdapter -Name NetRoute` returns nothing. Smart routing is off by default and not elevated, so no TUN should exist.
4. Stop the process. Do not touch the installed copy in Program Files.

- [ ] **Step 5: Commit** with subject `feat(app): wire Smart routing controller, card row and speed test`.

---

### Task 12: Smart routing management page

**Files:**
- Create: `src/NetRoute.App/SmartRoutingWindow.xaml`, `src/NetRoute.App/SmartRoutingWindow.xaml.cs`, `src/NetRoute.App/AddRuleWindow.xaml`, `src/NetRoute.App/AddRuleWindow.xaml.cs`
- Modify: `src/NetRoute.App/App.xaml.cs`

**Interfaces:**
- Consumes: `SmartRoutingPage`, `PageModel`, `PageGroup`, `PageItem`, `PageUserRule` and `ByteFormat` (Task 8); `UserRule` and `UserRuleType` (Task 3); `App.UpdateSmart`, `_smart` and `_catalog` (Task 11)
- Produces:
  - `SmartRoutingWindow`, with `void Render(PageModel model)` and the events:
    - `MasterToggled(bool)`
    - `ItemToggled(string id, bool on)`
    - `GroupToggled(string groupId, bool on)`
    - `UserRuleToggled(UserRule rule, bool on)`
    - `UserRuleRemoved(UserRule rule)`
    - `AddRuleRequested(UserRuleType type)`
    - `UsePhoneRequested()`
  - `AddRuleWindow(UserRuleType type)`, with `UserRule? Result`

- [ ] **Step 1: Create the window**

`src/NetRoute.App/SmartRoutingWindow.xaml`:

```xml
<Window x:Class="NetRoute.App.SmartRoutingWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="NetRoute — Smart routing" Width="600" Height="680" MinWidth="460" MinHeight="420"
        WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" FontSize="13">
    <DockPanel Margin="16">
        <StackPanel DockPanel.Dock="Top">
            <DockPanel>
                <CheckBox x:Name="MasterToggle" DockPanel.Dock="Left" VerticalAlignment="Center" Click="OnMaster" />
                <TextBlock Text="Smart routing — keep data-hungry traffic off 4G (Phone mode)" FontSize="15" FontWeight="SemiBold"
                           Margin="8,0,0,0" VerticalAlignment="Center" TextWrapping="Wrap" />
            </DockPanel>
            <TextBlock x:Name="StatusText" Margin="0,8,0,0" TextWrapping="Wrap" />
            <TextBlock x:Name="TotalText" Margin="0,4,0,0" Foreground="Gray" />
            <Button x:Name="UsePhoneButton" Content="Use phone until LAN is back (uses mobile data)" Margin="0,8,0,0"
                    HorizontalAlignment="Left" Padding="10,4" Visibility="Collapsed" Click="OnUsePhone" />
            <TextBlock Text="Turned-off items go through the phone like everything else." Margin="0,8,0,4" Foreground="Gray" />
        </StackPanel>
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <StackPanel x:Name="Body" />
        </ScrollViewer>
    </DockPanel>
</Window>
```

`src/NetRoute.App/SmartRoutingWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using NetRoute.Core;

namespace NetRoute.App;

/// Management page. Rebuilt from a PageModel on every change; keeps which groups are expanded.
public partial class SmartRoutingWindow : Window
{
    readonly HashSet<string> _expanded = new() { "video" };

    public SmartRoutingWindow() => InitializeComponent();

    public event Action<bool>? MasterToggled;
    public event Action<string, bool>? ItemToggled;
    public event Action<string, bool>? GroupToggled;
    public event Action<UserRule, bool>? UserRuleToggled;
    public event Action<UserRule>? UserRuleRemoved;
    public event Action<UserRuleType>? AddRuleRequested;
    public event Action? UsePhoneRequested;

    public void Render(PageModel model)
    {
        MasterToggle.IsChecked = model.Enabled;
        StatusText.Text = model.Status.Text;
        TotalText.Text = $"Kept off 4G today: {ByteFormat.Human(model.TotalToday)}";
        UsePhoneButton.Visibility = model.CanUsePhone ? Visibility.Visible : Visibility.Collapsed;

        Body.Children.Clear();
        foreach (var group in model.Groups) Body.Children.Add(GroupView(group, model.Enabled));
        Body.Children.Add(UserRulesView(model));
    }

    UIElement GroupView(PageGroup group, bool enabled)
    {
        var groupToggle = new CheckBox { IsThreeState = false, IsChecked = group.On, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        groupToggle.Click += (_, _) => GroupToggled?.Invoke(group.Id, group.On != true);
        var header = new DockPanel();
        header.Children.Add(groupToggle);
        var bytes = new TextBlock { Text = ByteFormat.Human(group.TodayBytes), Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(bytes, Dock.Right);
        header.Children.Add(bytes);
        header.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0) });

        var items = new StackPanel { Margin = new Thickness(28, 4, 0, 4) };
        foreach (var item in group.Items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var toggle = new CheckBox { IsChecked = item.On, IsEnabled = enabled, Content = item.Name };
            toggle.Click += (_, _) => ItemToggled?.Invoke(item.Id, toggle.IsChecked == true);
            var itemBytes = new TextBlock { Text = item.On ? ByteFormat.Human(item.TodayBytes) : "→ via phone", Foreground = System.Windows.Media.Brushes.Gray };
            DockPanel.SetDock(itemBytes, Dock.Right);
            row.Children.Add(itemBytes);
            row.Children.Add(toggle);
            items.Children.Add(row);
        }

        var expander = new Expander { Header = header, Content = items, IsExpanded = _expanded.Contains(group.Id), Margin = new Thickness(0, 4, 0, 4) };
        expander.Expanded += (_, _) => _expanded.Add(group.Id);
        expander.Collapsed += (_, _) => _expanded.Remove(group.Id);
        return expander;
    }

    UIElement UserRulesView(PageModel model)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var header = new DockPanel();
        var addWebsite = new Button { Content = "+ Website…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), IsEnabled = model.Enabled };
        addWebsite.Click += (_, _) => AddRuleRequested?.Invoke(UserRuleType.Website);
        var addApp = new Button { Content = "+ App…", Padding = new Thickness(8, 2, 8, 2), IsEnabled = model.Enabled };
        addApp.Click += (_, _) => AddRuleRequested?.Invoke(UserRuleType.App);
        DockPanel.SetDock(addWebsite, Dock.Right);
        DockPanel.SetDock(addApp, Dock.Right);
        header.Children.Add(addWebsite);
        header.Children.Add(addApp);
        header.Children.Add(new TextBlock { Text = "My rules", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);

        if (model.UserRules.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No rules yet — add an app or a website.", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(28, 4, 0, 0) });

        foreach (var rule in model.UserRules)
        {
            var row = new DockPanel { Margin = new Thickness(28, 2, 0, 2) };
            var remove = new Button { Content = "🗑", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Remove" };
            remove.Click += (_, _) => UserRuleRemoved?.Invoke(rule.Rule);
            var bytes = new TextBlock { Text = ByteFormat.Human(rule.TodayBytes), Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(bytes, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(bytes);
            var toggle = new CheckBox
            {
                IsChecked = rule.Rule.Enabled, IsEnabled = model.Enabled,
                Content = $"{(rule.Rule.Type == UserRuleType.App ? "App" : "Website")}: {rule.Rule.Value}",
            };
            toggle.Click += (_, _) => UserRuleToggled?.Invoke(rule.Rule, toggle.IsChecked == true);
            row.Children.Add(toggle);
            panel.Children.Add(row);
        }
        return panel;
    }

    void OnMaster(object sender, RoutedEventArgs e) => MasterToggled?.Invoke(MasterToggle.IsChecked == true);

    void OnUsePhone(object sender, RoutedEventArgs e) => UsePhoneRequested?.Invoke();
}
```

`src/NetRoute.App/AddRuleWindow.xaml`:

```xml
<Window x:Class="NetRoute.App.AddRuleWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Add rule" Width="440" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" FontFamily="Segoe UI" FontSize="13">
    <StackPanel Margin="16">
        <TextBlock x:Name="Prompt" TextWrapping="Wrap" />
        <DockPanel Margin="0,8,0,0">
            <Button x:Name="BrowseButton" DockPanel.Dock="Right" Content="Browse…" Margin="6,0,0,0" Padding="8,2" Click="OnBrowse" />
            <ComboBox x:Name="Input" IsEditable="True" />
        </DockPanel>
        <TextBlock x:Name="Error" Foreground="Firebrick" Margin="0,6,0,0" TextWrapping="Wrap" Visibility="Collapsed" />
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="Add" Width="80" IsDefault="True" Click="OnAdd" />
            <Button Content="Cancel" Width="80" Margin="8,0,0,0" IsCancel="True" />
        </StackPanel>
    </StackPanel>
</Window>
```

`src/NetRoute.App/AddRuleWindow.xaml.cs`:

```csharp
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using NetRoute.Core;

namespace NetRoute.App;

public partial class AddRuleWindow : Window
{
    readonly UserRuleType _type;

    public AddRuleWindow(UserRuleType type)
    {
        InitializeComponent();
        _type = type;
        if (type == UserRuleType.App)
        {
            Title = "Add app";
            Prompt.Text = "App to keep off 4G — pick a running app or type its .exe name:";
            Input.ItemsSource = Process.GetProcesses()
                .Select(p => { using (p) return p.ProcessName + ".exe"; })
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            Title = "Add website";
            Prompt.Text = "Website to keep off 4G (its subdomains are included), e.g. netflix.com:";
            BrowseButton.Visibility = Visibility.Collapsed;
        }
    }

    public UserRule? Result { get; private set; }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) Input.Text = dialog.FileName;
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (UserRule.TryCreate(_type, Input.Text ?? "", out var rule, out var error))
        {
            Result = rule;
            DialogResult = true;
            return;
        }
        Error.Text = error;
        Error.Visibility = Visibility.Visible;
    }
}
```

- [ ] **Step 2: Wire it in `App.xaml.cs`**

Add this field: `SmartRoutingWindow? _smartWindow;`

After `_card.SpeedTestRequested += RunSpeedTest;`, add: `_card.SmartSettingsRequested += OpenSmartRouting;`

Replace `RenderSmart` with:

```csharp
    void RenderSmart(SmartRoutingStatus status)
    {
        _card?.RenderSmart(SmartRoutingPresenter.Row(status));
        RenderSmartWindow();
    }

    void RenderSmartWindow()
    {
        if (_smartWindow is null || _smart is null || _controller is null) return;
        _smartWindow.Render(SmartRoutingPage.Build(_catalog, _controller.Settings.SmartRouting, _smart.Status));
    }

    void OpenSmartRouting()
    {
        if (_smartWindow is not null)
        {
            _smartWindow.Activate();
            return;
        }
        var window = _smartWindow = new SmartRoutingWindow();
        window.MasterToggled += on => ChangeSmart(s => s with { Enabled = on });
        window.ItemToggled += (id, on) => ChangeSmart(s => s.WithItem(id, on));
        window.GroupToggled += (groupId, on) => ChangeSmart(s => SmartRoutingPage.WithGroup(_catalog, s, groupId, on));
        window.UserRuleToggled += (rule, on) => ChangeSmart(s => s.WithUserRule(rule with { Enabled = on }));
        window.UserRuleRemoved += rule => ChangeSmart(s => s.WithoutUserRule(rule));
        window.AddRuleRequested += type =>
        {
            var dialog = new AddRuleWindow(type) { Owner = window };
            if (dialog.ShowDialog() == true && dialog.Result is { } rule) ChangeSmart(s => s.WithUserRule(rule));
        };
        window.UsePhoneRequested += () => _ = _smart!.UseLanRulesOnPhoneAsync();
        window.Closed += (_, _) => _smartWindow = null;
        RenderSmartWindow();
        window.Show();
    }

    void ChangeSmart(Func<SmartRoutingSettings, SmartRoutingSettings> change)
    {
        UpdateSmart(change);
        RenderSmartWindow(); // immediate feedback; the controller's StatusChanged re-renders again once applied
    }
```

- [ ] **Step 3: Build and run the tests**

```powershell
dotnet build src/NetRoute.App -o <scratch>\nrw-v2-build
dotnet test --filter "Category!=Integration"
```

Expected: 0 warnings, all tests pass.

- [ ] **Step 4: Non-elevated smoke check**

1. Launch the scratch build (non-elevated).
2. Click ⚙ on the card. The page opens with four groups, every item ticked, and "My rules" empty.
3. Toggle Facebook off, then close and reopen the page. Facebook is still off, and `%AppData%\NetRouteWidget\settings.json` has `"facebook": false`.
4. "+ Website…": enter `https://www.Netflix.com/x`. It is added as `netflix.com`.
5. "+ App…": enter `notepad`. The error text appears.
6. Stop the app.

- [ ] **Step 5: Commit** with subject `feat(app): Smart routing management page and add-rule dialog`.

---

### Task 13: "Waiting" popup

**Files:**
- Create: `src/NetRoute.App/WaitingPopup.xaml`, `src/NetRoute.App/WaitingPopup.xaml.cs`
- Modify: `src/NetRoute.App/App.xaml.cs`

**Interfaces:**
- Consumes: `SmartRoutingController.WaitingDetected`, `UseLanRulesOnPhoneAsync()`, `KeepWaitingAsync()` and `Status` (Task 7); `SmartRoutingPresenter.WaitingText` (Task 8); `CardPlacement.Margin` (v1)
- Produces: `WaitingPopup(string text)`, with events `UsePhoneClicked` and `KeepWaitingClicked`

- [ ] **Step 1: Create the popup**

`src/NetRoute.App/WaitingPopup.xaml`:

```xml
<Window x:Class="NetRoute.App.WaitingPopup"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="380" SizeToContent="Height" WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ShowActivated="False" ResizeMode="NoResize" FontFamily="Segoe UI" FontSize="12">
    <Border CornerRadius="10" Padding="14,12" BorderThickness="1" Background="#F21F1F1F" BorderBrush="#33FFFFFF">
        <StackPanel>
            <TextBlock Text="NetRoute" Foreground="#FFB0B0B0" FontSize="11" />
            <TextBlock x:Name="Message" Foreground="#FFF3F3F3" FontSize="13" TextWrapping="Wrap" Margin="0,4,0,10" />
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <Button Content="Use phone until LAN is back" Padding="10,4" Click="OnUsePhone" />
                <Button Content="Keep waiting" Padding="10,4" Margin="8,0,0,0" Click="OnKeepWaiting" />
            </StackPanel>
            <TextBlock Text="Using the phone spends mobile data." Foreground="#FFB0B0B0" FontSize="11" Margin="0,6,0,0" HorizontalAlignment="Right" />
        </StackPanel>
    </Border>
</Window>
```

`src/NetRoute.App/WaitingPopup.xaml.cs`:

```csharp
using System.Windows;
using NetRoute.Core;

namespace NetRoute.App;

/// Tray-anchored popup with real buttons (tray balloons can't have buttons).
public partial class WaitingPopup : Window
{
    public WaitingPopup(string text)
    {
        InitializeComponent();
        Message.Text = text;
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - CardPlacement.Margin;
            Top = work.Bottom - ActualHeight - CardPlacement.Margin;
        };
    }

    public event Action? UsePhoneClicked;
    public event Action? KeepWaitingClicked;

    public void SetText(string text) => Message.Text = text;

    void OnUsePhone(object sender, RoutedEventArgs e) { UsePhoneClicked?.Invoke(); Close(); }

    void OnKeepWaiting(object sender, RoutedEventArgs e) { KeepWaitingClicked?.Invoke(); Close(); }
}
```

- [ ] **Step 2: Wire it in `App.xaml.cs`**

Add this field: `WaitingPopup? _waitingPopup;`

Replace the `smart.WaitingDetected += …` subscription from Task 11 with:

```csharp
            smart.WaitingDetected += names => Dispatcher.BeginInvoke(new Action(() => ShowWaitingPopup(names)));
```

Add:

```csharp
    void ShowWaitingPopup(IReadOnlyList<string> names)
    {
        var text = SmartRoutingPresenter.WaitingText(names);
        if (_waitingPopup is not null)
        {
            _waitingPopup.SetText(text);
            return;
        }
        var popup = _waitingPopup = new WaitingPopup(text);
        popup.UsePhoneClicked += () => _ = _smart!.UseLanRulesOnPhoneAsync();
        popup.KeepWaitingClicked += () => _ = _smart!.KeepWaitingAsync();
        popup.Closed += (_, _) => _waitingPopup = null;
        popup.Show();
    }
```

At the end of `RenderSmart(SmartRoutingStatus status)`, close a stale popup:

```csharp
        if (_waitingPopup is not null && (status.LanOnline || status.LanRulesOnPhone || !status.IsActive)) _waitingPopup.Close();
```

- [ ] **Step 3: Build and test.** Run `dotnet build src/NetRoute.App -o <scratch>\nrw-v2-build` and `dotnet test --filter "Category!=Integration"`. Expected: 0 warnings, all tests pass.

- [ ] **Step 4: Commit** with subject `feat(app): waiting popup with use-phone / keep-waiting`.

---

### Task 14: Break-glass, docs, install and on-machine verification

**Files:**
- Modify: `tools/Restore-Network.cmd`, `tests/NetRoute.Core.Tests/RestoreScriptTests.cs`, `README.md`, `docs/superpowers/specs/2026-10-02-netroute-v2-smart-routing-design.md` (status line)

**Interfaces:**
- Consumes: everything above.
- Produces: an installed v2 build in `C:\Program Files\NetRouteWidget`, a verified checklist, and an updated PR #2.

- [ ] **Step 1: The restore script also stops sing-box**

In `tools/Restore-Network.cmd`:
- In the admin path, right after the `taskkill /IM NetRouteWidget.exe /F` line, add:

```bat
echo Stopping sing-box (Smart routing)...
taskkill /IM sing-box.exe /F >nul 2>&1
```

- In the `:check` section, before `exit /b 0`, add:

```bat
tasklist /FI "IMAGENAME eq sing-box.exe" 2>nul | find /I "sing-box.exe" >nul && (echo   sing-box: running) || (echo   sing-box: not running)
```

In `RestoreScriptTests.Dry_run_reports_and_changes_nothing`, add `Assert.Contains("sing-box:", output);`.

Then run `dotnet test --filter "Category=Integration"`. Expected: PASS.

- [ ] **Step 2: README**

In `README.md`, under "What it does" or a new "Smart routing" section, add:

```markdown
## Smart routing (v2)

Keep data-hungry traffic off 4G while the PC uses the phone:

- **Built-in items, all on by default** (each has its own toggle):
  - System updates: Windows Update, Microsoft Store
  - Cloud sync: OneDrive, Google Drive, Dropbox, iCloud
  - Video & social: YouTube, Facebook, Instagram
  - Game launchers: Steam, Epic, Battle.net, Xbox
- **Your own rules:** add any app (`.exe`) or website (subdomains are included).
- **How it works:** matching traffic goes through the LAN. If the LAN is down it **waits**, and a popup offers "Use phone until LAN is back".
- **What it saves:** the card and the ⚙ page show how much was kept off 4G today.
- **Speed test:** ⚡ on the card measures both connections (about 5 MB of mobile data).

Smart routing runs the official [sing-box](https://github.com/SagerNet/sing-box) 1.14.2 as a helper process (see THIRD-PARTY-NOTICES.md). It needs administrator rights and is only active in Phone or Auto mode. Turning it off, quitting the widget, or running `Restore-Network.cmd` stops it.
```

In the spec, set the status line to `**Status:** Implemented (v2)`.

- [ ] **Step 3: Publish to a staging folder** (non-elevated):

```powershell
dotnet publish src/NetRoute.App -c Release -r win-x64 --self-contained false -o <scratch>\nrw-v2-release
```

Confirm it contains `NetRouteWidget.exe`, `sing-box\sing-box.exe`, `sing-box\LICENSE`, `rules\builtin.json` and `Restore-Network.cmd`.

- [ ] **Step 4: User installs and verifies.** This is done by the human partner and is never automated.
1. From an **admin** PowerShell, close the widget and copy the staging folder over `C:\Program Files\NetRouteWidget`. Use the same installer script as v1, pointed at the new staging folder.
2. Start the widget. Click ⚙ and switch **Smart routing** on.
   - Expected: the card shows "⚡ Smart routing ON · 13 rules · 0 KB kept off 4G today", and a `NetRoute` adapter exists (`Get-NetAdapter -Name NetRoute`).
   - The v1 header still reads "Internet via PHONE", not "other adapter".
3. **YouTube:** play a video. In the log, sing-box lines show `route(lan-only)`, and the card's kept-off counter grows.
4. **Other sites:** they load over the phone.
5. **LAN unplugged:**
   - YouTube stalls and the waiting popup appears: "YouTube is waiting — the LAN is offline."
   - Click "Use phone until LAN is back". YouTube resumes, and the card shows "⚠ Using phone for all traffic — LAN offline".
   - Replug the LAN. After about 15–30 s the toast "LAN back — LAN-only traffic is back on the LAN" appears.
6. **Facebook toggle:** turn Facebook off in ⚙. Facebook traffic goes through the phone at once (the counter stops growing for it).
7. **Mode switch:** click **LAN** mode. The `NetRoute` adapter disappears (sing-box stopped). Click **Phone** and it comes back.
8. **Phone replug:** the adapter is renamed, and sing-box restarts once with the new name (log line "Smart routing: starting sing-box").
9. **Quit the widget:** no `sing-box.exe` remains (`Get-Process sing-box` finds nothing), and internet works through v1 routing.
10. **Speed test:** ⚡ shows both speeds.
11. **Replug stability:** replug the phone 3 times within 5 minutes. Smart routing stays Running, with no Faulted message and no restart storm (each replug restarts sing-box once).
12. **UDP/QUIC failure line:** with the LAN unplugged, play a YouTube video (it uses QUIC) and check that the sing-box lines for its failures are recognised: the waiting popup must appear even when only UDP connections fail. If not, extend `SingBoxLogParser` with the packet-connection failure format.
13. **Rule index check:** the sing-box log's `router: match[N]` index equals the position in `route.rules` (the Task 9 integration test pins this; confirm with a live YouTube line).
14. **Hung sing-box:** end the sing-box process from Task Manager while the widget runs. Internet recovers within seconds (TUN removed, v1 routing), and the widget restarts sing-box.

15. **Widget killed:** kill `NetRouteWidget.exe` in Task Manager while Smart routing runs. `sing-box.exe` disappears with it (job object), the `NetRoute` adapter vanishes, and internet keeps working over v1 routing. Also check after a normal Quit that `Get-NetAdapter -Name NetRoute` and `Get-NetRoute -InterfaceAlias NetRoute` show nothing.
16. **Speed test with Smart routing ON (phone as default exit):** click ⚡ and check the sing-box debug log: no `speed.cloudflare.com` connection may appear (the probe's sockets are bound to the adapters' own addresses and must bypass the TUN). The phone and LAN numbers must also clearly differ. If the probe shows up in the log, the TUN diverted it: set `IP_UNICAST_IF` (interface index resolved from the source address) on the socket before `Connect`, in `HttpSpeedProbe`, or document the test as "measured with Smart routing off".

17. **Final-review additions (run these too):**
    - **(a) Hard kills.** After killing sing-box in Task Manager, killing the widget, and running the restore script, `Get-NetAdapter -Name NetRoute` and `Get-NetRoute -InterfaceAlias NetRoute` must both show nothing.
    - **(b) Probes bypass the TUN.** With Smart routing on, the sing-box log must show no widget connections to `1.1.1.1:443` or `8.8.8.8:443`. With the router's internet down but the cable still in, the LAN must show as offline.
    - **(c) Kaspersky.** It allows the TUN. The kill-on-close job still works while Kaspersky runs.
    - **(d) Start with the LAN unplugged.** After a restart of the widget with the cable out, LAN-only items wait and the popup appears. Unmatched traffic still works over the phone.
    - **(e) Restore, then logon.** Run `Restore-Network.cmd` and answer N to removing the startup task. After the next logon Smart routing is OFF.
    - **(f) Rule case.** An app rule typed in the wrong case (for example `idman.exe` for `IDMan.exe`) still routes to the LAN.
    - **(g) IPv6.** Precondition: the phone adapter has no global IPv6 address (`Get-NetIPAddress -InterfaceAlias "Ethernet 5" -AddressFamily IPv6`). If it does, tell the developer.
    - **(h) TUN name.** The adapter is named exactly `NetRoute` (a stale one would make a new one `NetRoute 2` and break `TunIndex()`).
    - **(i) Flaky LAN.** Under heavy LAN load, no false waiting popups.
    - **(j) Carrier block in Phone mode.** The v1 heal flips the Smart routing default exit to the LAN within about 15–30 s, and the card shows it.
    - **(k) LAN mode.** Switch to LAN mode with Smart routing on: the `NetRoute` adapter stays; unplug the LAN, unmatched traffic goes over the phone while YouTube/Windows Update wait.
    - **(l) Auto mode.** Switch to Auto: the card says "Smart routing paused in Auto mode" and the `NetRoute` adapter disappears.


- [ ] **Step 5: Commit, push and update the PR**

```powershell
git add -A
git commit -m "docs: README Smart routing; restore script stops sing-box; mark v2 implemented" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
git push
```

Tick the remaining checklist items in PR #2's description. Mark it **ready for review** only after the user confirms the Step 4 checklist.
