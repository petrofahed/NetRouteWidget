# NetRoute Widget v4 — Smart routing profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two Smart routing profiles that also set the routing mode: **Phone + exceptions** (today's behaviour: everything through the phone, listed items through the LAN) and **LAN + exceptions** (everything through the LAN, listed items — the AI tools, `omantel.om` — through the phone), each with its own editable exception list, plus "update" items for VS Code / Claude / Codex that always go through the LAN.

**Architecture:** The profile *is* the routing mode that v1 already stores (`RoutingMode.Phone` / `Lan`). `RuleSet.Build` gains the profile and returns the entries active for it; each `RuleEntry` carries its exit (LAN or phone) and a carve-out flag; `SingBoxConfigBuilder` points phone-exit rules at the `phone` outbound and keeps `lan-only` for LAN-exit rules; carve-outs (update items) are LAN-exit, active in both profiles and ordered before the phone rules. The controller rebuilds the rule set on every `ApplyAsync` from `net.Mode`. The Config tab edits either profile's list through a selector; the card's Phone / LAN buttons are the profile switch.

**Tech Stack:** .NET 10 (C#), WPF, xUnit with `FakeTimeProvider`, sing-box 1.14.2.

**Spec:** `docs/superpowers/specs/2026-10-03-netroute-v4-profiles-design.md` (read it first; it is the authority). v3 plan `docs/superpowers/plans/2026-10-02-netroute-v3-usage-and-routing.md` describes the code this builds on.

## Global Constraints

- **Branch:** `feat/v2-smart-routing` at `F:\source\petrofahed\NetRouteWidget` (v4 builds on the unmerged v2/v3 work). Work only there; never push, never create branches. All existing tests keep passing except those a task explicitly edits.
- **SDK and platform:** SDK pinned in `global.json` (10.0.401), projects target `net10.0-windows`. **No new NuGet packages.**
- **Never** launch, stop or touch any running or installed widget (`C:\Program Files\NetRouteWidget`, or a copy started from a scratch folder). **Never** run sing-box with a TUN. **Never** run `tools\Restore-Network.cmd` without `/check`. Nothing in this plan may change the machine's network configuration.
- **App builds go only to the scratch folder** (never `src\NetRoute.App\bin\Debug`): `dotnet build src/NetRoute.App -c Release -o "F:\Temp\claude\f--source-petrofahed-NetRouteWidget\f8bc83b0-9094-42d6-a498-2922eade94e2\scratchpad\nrw-v4-build"`.
- **Unit tests:** `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`. Expected after every task: all green, **0 warnings** (466 tests at the start of v4).
- **Exits:** `RouteExit.Lan` = a rule/item/list that sends traffic through the LAN, `RouteExit.Phone` = through the phone. A **profile** is named by its *default* exit: `RouteExit.Phone` = "Phone + exceptions", `RouteExit.Lan` = "LAN + exceptions".
- **Back-compat:** a `settings.json` written by v3 (no `PhoneUserRules`) and a catalog without `exit`/`carveOut` must load and behave exactly as before.
- **Privacy:** nothing new is persisted except the user's own `PhoneUserRules` (app file names / domains, like `UserRules`).
- **Theme:** every colour in new WPF code comes from the theme palette (`DynamicResource` / `SetResourceReference` with keys from `Theme.cs`: `TextPrimary`, `TextSecondary`, `Accent`, `Warning`, `ButtonBackground`, ...). No literal colours.
- **Threading:** nothing in Core uses `.Result` or `.Wait()`; every `await` inside the controller keeps `ConfigureAwait(false)`.
- **Commit trailer** (verbatim on every commit, nothing else added):
  ```
  Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
  ```
  PowerShell/bash: `git commit -m "<subject>" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"`

## Review Focus

Inputs and conditions the spec implies but no happy-path test covers, most likely first. Each has a pinning test in the task that owns the code.

1. **Switching profile while connections are open** (Phone → LAN mode and back): the rule set changes, sing-box restarts, no stale rules survive, the second config contains the phone rules and not the LAN ones. Pinned by Task 4 `Switching_to_lan_mode_restarts_with_the_phone_rules`.
2. **The same app or site in both lists** (e.g. `chrome.exe` is a LAN exception and also a phone exception): entry ids must not clash, usage must keep one row. Pinned by Task 2 `The_same_app_in_both_lists_has_distinct_entry_ids_and_one_usage_row`.
3. **An update domain also covered by a phone process rule** (`code.exe` → phone, `update.code.visualstudio.com` → LAN): the carve-out must win. Pinned by Task 2 `Carve_outs_come_first_in_the_lan_profile` and Task 3 `A_carve_out_rule_precedes_the_phone_rules`.
4. **Old files**: `settings.json` without `PhoneUserRules`, `builtin.json` without `exit`/`carveOut` load unchanged. Pinned by Task 1 `A_group_without_exit_defaults_to_lan_and_not_carve_out` and Task 2 `A_settings_file_without_phone_rules_loads_with_none`.
5. **LAN unplugged in LAN + exceptions**: unmatched traffic heals onto the phone as LAN mode does today, phone exceptions are unaffected, nothing "waits" and no waiting popup is raised. Pinned by Task 4 `Lan_profile_with_the_lan_unplugged_heals_to_the_phone_and_never_waits`.

(Not unit-testable, so checked by hand in Task 7: switching both ways with the real widget; the guessed process names and update domains.)

---

## File Structure

```
src/NetRoute.Core/
  SmartRouting/RuleCatalog.cs              RuleItem + Exit, CarveOut; parse group/item "exit" and "carveOut"        (Task 1)
  rules/builtin.json                       + groups: AI tools, Omantel (phone), AI & dev tool updates (carve-out)   (Task 1)
  SmartRouting/SmartRoutingSettings.cs     + PhoneUserRules, With/WithoutPhoneUserRule, UserRule.PhoneIdOf         (Task 2)
  SmartRouting/RuleSet.cs                  RuleEntry + Exit, CarveOut; Build(catalog, settings, profile); BuildAll   (Task 2)
  SmartRouting/UsageAttribution.cs         keys/names for phone-list rules; disabled App rules of both lists       (Task 2)
  SmartRouting/SingBoxConfigBuilder.cs     phone-exit rules -> "phone" outbound                                     (Task 3)
  SmartRouting/SmartRoutingController.cs   Build with the profile; Status.Profile                                  (Task 4)
  SmartRouting/SmartRoutingPresenter.cs    status row names the profile; PageModel editing profile; captions       (Tasks 4, 5)
src/NetRoute.App/
  SmartRoutingWindow.xaml(.cs)             "Editing:" selector, per-profile wording                                  (Task 6)
  CardWindow.xaml                          profile tooltips on the Phone / LAN buttons                              (Task 6)
  App.xaml.cs                              editing profile, edits routed to the right list                           (Task 6)
README.md, docs/superpowers/specs/...      docs                                                                      (Task 7)
tools/Make-AppIcon.ps1, Assets/NetRouteWidget.ico, csproj, window Icon=                                  (Task 8)
UsageExceptions.cs (replaces UsageAssignment), UsageReport, UsageTab/Window/App wiring               (Task 9; run it after Task 6, before Task 7)
RuleCatalog merge/LoadMerged, AppPaths.UserRulesFile, rules.user.json, Config 'Edit rules file' link   (Task 10; run it after Task 9, before Task 7)
tests/NetRoute.Core.Tests/
  RuleCatalogExitTests.cs (new)  RuleSetTests.cs  SmartRoutingSettingsTests.cs  UsageAttributionTests.cs
  SingBoxConfigBuilderTests.cs  SmartRoutingControllerTests.cs  SmartRoutingPresenterTests.cs
```

**Entry ids** (used everywhere): built-in items keep their item id (`claude`, `vscode-updates`, ...); a LAN-list user rule keeps `user:app:x.exe` / `user:website:x.com`; a **phone-list** user rule is `phone-user:app:x.exe` / `phone-user:website:x.com` (`UserRule.PhoneIdOf`). **Usage row keys** are unchanged: an App rule of either list counts under `app:x.exe`, a Website rule of either list under `user:website:x.com`; built-in items under their id.

---

### Task 1: Catalog — exit and carve-out, and the new built-in groups

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/RuleCatalog.cs`, `src/NetRoute.Core/rules/builtin.json`
- Test: `tests/NetRoute.Core.Tests/RuleCatalogExitTests.cs` (new)

**Interfaces:**
- Produces: `RuleItem(string Id, string GroupId, string GroupName, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains, bool DefaultOn, RouteExit Exit = RouteExit.Lan, bool CarveOut = false)` (the two new members are trailing with defaults, so every existing `new RuleItem(...)` / `new("youtube", ...)` call still compiles). `RuleCatalog.Parse` reads `"exit": "lan"|"phone"` and `"carveOut": true|false` on a **group** (inherited by its items) and on an **item** (overrides the group). A carve-out must be LAN-exit.

- [ ] **Step 1: Write the failing tests** — `tests/NetRoute.Core.Tests/RuleCatalogExitTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~RuleCatalogExitTests"`
Expected: build FAIL — `RuleItem` does not contain `Exit` / `CarveOut`.

- [ ] **Step 3: Implement.**

`src/NetRoute.Core/SmartRouting/RuleCatalog.cs`: replace the `RuleItem` record and `Parse`, add two helpers (leave `Load`, `DefaultPath`, `Required`, `Strings` as they are):

```csharp
/// One switchable thing with an exit: a LAN exception ("keep off 4G", the default) or a phone exception (used by the
/// LAN + exceptions profile), matched by process names and/or domain suffixes. A carve-out is a LAN item that stays
/// active in both profiles and is matched before the phone rules (e.g. an app's update download).
public sealed record RuleItem(
    string Id, string GroupId, string GroupName, string Name,
    IReadOnlyList<string> Processes, IReadOnlyList<string> Domains, bool DefaultOn,
    RouteExit Exit = RouteExit.Lan, bool CarveOut = false);
```

```csharp
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
```

`src/NetRoute.Core/rules/builtin.json`: add these three groups at the end of the `groups` array (after the `games` group; keep the file valid JSON — add a comma after the `games` group's closing brace):

```json
    ,
    { "id": "ai-tools", "name": "AI tools", "exit": "phone", "items": [
      { "id": "claude", "name": "Claude / Claude Code",
        "processes": ["claude.exe"], "domains": ["claude.ai", "anthropic.com"] },
      { "id": "chatgpt", "name": "OpenAI / ChatGPT",
        "processes": ["chatgpt.exe"], "domains": ["openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com"] },
      { "id": "codex", "name": "Codex", "processes": ["codex.exe"] },
      { "id": "vscode", "name": "Visual Studio Code", "processes": ["Code.exe"] },
      { "id": "copilot", "name": "GitHub Copilot", "domains": ["githubcopilot.com", "copilot.microsoft.com"] },
      { "id": "gemini", "name": "Gemini",
        "domains": ["gemini.google.com", "aistudio.google.com", "generativelanguage.googleapis.com"] },
      { "id": "cursor", "name": "Cursor", "processes": ["Cursor.exe"], "domains": ["cursor.sh", "cursor.com"] },
      { "id": "perplexity", "name": "Perplexity", "domains": ["perplexity.ai"] } ] },
    { "id": "isp", "name": "Omantel", "exit": "phone", "items": [
      { "id": "omantel", "name": "Omantel (*.omantel.om)", "domains": ["omantel.om"] } ] },
    { "id": "ai-updates", "name": "AI & dev tool updates (always LAN)", "carveOut": true, "items": [
      { "id": "vscode-updates", "name": "VS Code updates",
        "domains": ["update.code.visualstudio.com", "vscode.download.prss.microsoft.com", "az764295.vo.msecnd.net",
                    "marketplace.visualstudio.com", "gallerycdn.vsassets.io"] },
      { "id": "claude-updates", "name": "Claude updates", "domains": ["downloads.claude.ai"] },
      { "id": "codex-updates", "name": "Codex updates",
        "domains": ["registry.npmjs.org", "release-assets.githubusercontent.com", "objects.githubusercontent.com"] } ] }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings. (The new items are phone/carve-out items; every existing test builds `RuleSet.Build(Catalog, ...)` from its own small catalogs, so none changes.)

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): catalog items have an exit and can be carve-outs; AI, Omantel and update items" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Phone-list rules, the rule set per profile, and usage keys

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/SmartRoutingSettings.cs`, `src/NetRoute.Core/SmartRouting/RuleSet.cs`, `src/NetRoute.Core/SmartRouting/UsageAttribution.cs`
- Test: `tests/NetRoute.Core.Tests/RuleSetTests.cs`, `tests/NetRoute.Core.Tests/SmartRoutingSettingsTests.cs`, `tests/NetRoute.Core.Tests/UsageAttributionTests.cs`

**Interfaces:**
- Consumes: Task 1 `RuleItem.Exit/CarveOut`; `RouteExit`.
- Produces:
  - `UserRule.PhoneIdOf(UserRule) → string` (`"phone-" + IdOf(rule)`);
  - `SmartRoutingSettings.PhoneUserRules` (`IReadOnlyList<UserRule>`, default `[]`, part of `Equals` and `IsValid`), `WithPhoneUserRule(UserRule)`, `WithoutPhoneUserRule(UserRule)`;
  - `RuleEntry(string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains, RouteExit Exit = RouteExit.Lan, bool CarveOut = false)`;
  - `RuleSet.Build(catalog, settings, RouteExit profileDefault)`; the existing 2-argument `Build(catalog, settings)` stays and means `RouteExit.Phone` (today's behaviour);
  - `RuleSet.BuildAll(catalog, settings)` now includes both user-rule lists;
  - `RuleSet.Fingerprint` includes each entry's exit and carve-out flag.

- [ ] **Step 1: Write the failing tests.**

Append to `tests/NetRoute.Core.Tests/RuleSetTests.cs` (inside the class; keep its usings and helpers):

```csharp
    static readonly IReadOnlyList<RuleItem> V4Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),                                       // LAN exception
        new("claude", "ai", "AI", "Claude", ["claude.exe"], ["claude.ai"], true, RouteExit.Phone),                  // phone exception
        new("vscode", "ai", "AI", "VS Code", ["Code.exe"], [], true, RouteExit.Phone),
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    [Fact]
    public void The_phone_profile_activates_the_lan_items_and_carve_outs_and_the_lan_rules()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "qbittorrent.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.App, "ignored.exe")],
        };

        var set = RuleSet.Build(V4Catalog, settings, RouteExit.Phone);

        Assert.Equal(new[] { "youtube", "vscode-updates", "user:app:qbittorrent.exe" }, set.Entries.Select(e => e.Id));
        Assert.All(set.Entries, e => Assert.Equal(RouteExit.Lan, e.Exit));
    }

    [Fact]
    public void Carve_outs_come_first_in_the_lan_profile()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "ignored.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.Website, "example.com")],
        };

        var set = RuleSet.Build(V4Catalog, settings, RouteExit.Lan);

        // The update domain (LAN) is matched before the code.exe rule (phone), so a VS Code update never rides the phone.
        Assert.Equal(new[] { "vscode-updates", "claude", "vscode", "phone-user:website:example.com" }, set.Entries.Select(e => e.Id));
        Assert.Equal(new[] { RouteExit.Lan, RouteExit.Phone, RouteExit.Phone, RouteExit.Phone }, set.Entries.Select(e => e.Exit));
        Assert.True(set.Entries[0].CarveOut);
    }

    [Fact]
    public void Switched_off_items_and_disabled_rules_are_left_out_of_both_profiles()
    {
        var settings = new SmartRoutingSettings
        {
            PhoneUserRules = [new UserRule(UserRuleType.App, "x.exe", Enabled: false)],
        }.WithItem("claude", false).WithItem("vscode-updates", false);

        var set = RuleSet.Build(V4Catalog, settings, RouteExit.Lan);

        Assert.Equal(new[] { "vscode" }, set.Entries.Select(e => e.Id));
    }

    [Fact]
    public void The_two_argument_build_is_the_phone_profile()
    {
        var settings = new SmartRoutingSettings();

        Assert.Equal(
            RuleSet.Build(V4Catalog, settings, RouteExit.Phone).Fingerprint,
            RuleSet.Build(V4Catalog, settings).Fingerprint);
    }

    [Fact]
    public void BuildAll_includes_both_lists_and_every_catalog_item()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "chrome.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.App, "chrome.exe")],
        };

        var all = RuleSet.BuildAll(V4Catalog, settings);

        Assert.Equal(
            new[] { "youtube", "claude", "vscode", "vscode-updates", "user:app:chrome.exe", "phone-user:app:chrome.exe" },
            all.Entries.Select(e => e.Id));
    }

    [Fact]
    public void The_fingerprint_changes_with_the_profile()
    {
        var settings = new SmartRoutingSettings();

        Assert.NotEqual(
            RuleSet.Build(V4Catalog, settings, RouteExit.Phone).Fingerprint,
            RuleSet.Build(V4Catalog, settings, RouteExit.Lan).Fingerprint);
    }
```

Append to `tests/NetRoute.Core.Tests/SmartRoutingSettingsTests.cs` (inside the class):

```csharp
    [Fact]
    public void A_settings_file_without_phone_rules_loads_with_none()
    {
        var loaded = System.Text.Json.JsonSerializer.Deserialize<SmartRoutingSettings>("""{"Enabled":true,"UserRules":[{"Type":0,"Value":"a.exe","Enabled":true}]}""");

        Assert.Empty(loaded!.PhoneUserRules);
        Assert.Single(loaded.UserRules);
        Assert.True(loaded.IsValid());
    }

    [Fact]
    public void Phone_rules_round_trip_replace_and_remove_independently_of_the_lan_rules()
    {
        var rule = new UserRule(UserRuleType.App, "claude.exe");
        var s = new SmartRoutingSettings().WithUserRule(rule).WithPhoneUserRule(rule);

        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<SmartRoutingSettings>(json)!;

        Assert.Equal(s, back);
        Assert.Single(back.PhoneUserRules);

        var toggled = back.WithPhoneUserRule(rule with { Enabled = false });
        Assert.False(Assert.Single(toggled.PhoneUserRules).Enabled);
        Assert.True(Assert.Single(toggled.UserRules).Enabled);

        var removed = toggled.WithoutPhoneUserRule(rule);
        Assert.Empty(removed.PhoneUserRules);
        Assert.Single(removed.UserRules);
    }

    [Fact]
    public void Phone_rules_take_part_in_equality_and_validity()
    {
        Assert.NotEqual(new SmartRoutingSettings(), new SmartRoutingSettings().WithPhoneUserRule(new UserRule(UserRuleType.App, "a.exe")));
        Assert.False((new SmartRoutingSettings { PhoneUserRules = null! }).IsValid());
        Assert.False(new SmartRoutingSettings { PhoneUserRules = [new UserRule(UserRuleType.App, " ")] }.IsValid());
    }

    [Fact]
    public void A_phone_rule_has_its_own_id()
    {
        var rule = new UserRule(UserRuleType.Website, "Example.com");

        Assert.Equal("user:website:example.com", UserRule.IdOf(rule));
        Assert.Equal("phone-user:website:example.com", UserRule.PhoneIdOf(rule));
    }
```

Append to `tests/NetRoute.Core.Tests/UsageAttributionTests.cs` (inside the class; it has `Catalog` and `Build(settings)` helpers — add a v4 catalog next to them):

```csharp
    static readonly IReadOnlyList<RuleItem> V4Catalog =
    [
        new("claude", "ai", "AI", "Claude", ["claude.exe"], ["claude.ai"], true, RouteExit.Phone),
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    [Fact]
    public void Phone_exception_items_have_their_own_row_in_both_profiles()
    {
        var attribution = UsageAttribution.Build(V4Catalog, new SmartRoutingSettings());

        Assert.Equal("claude", attribution.Resolve("claude.exe", "claude.ai"));
        Assert.Equal("vscode-updates", attribution.Resolve("code.exe", "update.code.visualstudio.com"));
        Assert.Equal("Claude", attribution.DisplayName("claude"));
    }

    [Fact]
    public void The_same_app_in_both_lists_has_distinct_entry_ids_and_one_usage_row()
    {
        var rule = new UserRule(UserRuleType.App, "chrome.exe");
        var settings = new SmartRoutingSettings().WithUserRule(rule).WithPhoneUserRule(rule);

        var all = RuleSet.BuildAll(V4Catalog, settings);
        var attribution = UsageAttribution.Build(V4Catalog, settings);

        Assert.Equal(2, all.Entries.Count(e => e.Name == "chrome.exe"));
        Assert.Equal(all.Entries.Count, all.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal("app:chrome.exe", attribution.Resolve("Chrome.exe", "example.com"));
        Assert.Equal("chrome", attribution.DisplayName("app:chrome.exe"));
        Assert.Equal("app:chrome.exe", UsageAttribution.RowKeyOf(rule));
    }

    [Fact]
    public void A_phone_website_rule_counts_under_the_same_row_as_a_lan_website_rule()
    {
        var rule = new UserRule(UserRuleType.Website, "example.com");
        var attribution = UsageAttribution.Build(V4Catalog, new SmartRoutingSettings().WithPhoneUserRule(rule));

        Assert.Equal("user:website:example.com", attribution.Resolve("chrome.exe", "www.example.com"));
        Assert.Equal("example.com", attribution.DisplayName("user:website:example.com"));
    }

    [Fact]
    public void A_disabled_phone_app_rule_does_not_claim_its_application()
    {
        var settings = new SmartRoutingSettings().WithPhoneUserRule(new UserRule(UserRuleType.App, "chrome.exe", Enabled: false));
        var attribution = UsageAttribution.Build(V4Catalog, settings);

        Assert.Equal("vscode-updates", attribution.Resolve("chrome.exe", "update.code.visualstudio.com"));
        Assert.Equal("app:chrome.exe", attribution.Resolve("chrome.exe", "example.com"));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~RuleSetTests|FullyQualifiedName~SmartRoutingSettingsTests|FullyQualifiedName~UsageAttributionTests"`
Expected: build FAIL — `PhoneUserRules`, `PhoneIdOf`, `RuleSet.Build(..., RouteExit)`, `RuleEntry` with exit do not exist.

- [ ] **Step 3: Implement.**

`src/NetRoute.Core/SmartRouting/SmartRoutingSettings.cs`:
- in `UserRule` add under `IdOf`:

```csharp
    /// Id of a rule in the phone list ("LAN + exceptions"): the prefix keeps it apart from the same app or site in the LAN list.
    public static string PhoneIdOf(UserRule rule) => "phone-" + IdOf(rule);
```
- in `SmartRoutingSettings` add after `UserRules`:

```csharp
    /// The "LAN + exceptions" list: apps and websites the user sends through the PHONE while the LAN carries everything
    /// else. (UserRules is the "Phone + exceptions" list, whose rules go through the LAN.)
    public IReadOnlyList<UserRule> PhoneUserRules { get; init; } = [];

    public SmartRoutingSettings WithPhoneUserRule(UserRule rule) =>
        this with { PhoneUserRules = [.. PhoneUserRules.Where(r => !SameRule(r, rule)), rule] };

    public SmartRoutingSettings WithoutPhoneUserRule(UserRule rule) =>
        this with { PhoneUserRules = [.. PhoneUserRules.Where(r => !SameRule(r, rule))] };
```
- `IsValid`:

```csharp
    public bool IsValid() =>
        Items is not null && UserRules is not null && PhoneUserRules is not null
        && UserRules.All(ValidRule) && PhoneUserRules.All(ValidRule);

    static bool ValidRule(UserRule r) => r is not null && !string.IsNullOrWhiteSpace(r.Value) && Enum.IsDefined(r.Type);
```
- `Equals`: add `&& PhoneUserRules.SequenceEqual(other.PhoneUserRules)` after the `UserRules` comparison (leave `GetHashCode`).

`src/NetRoute.Core/SmartRouting/RuleSet.cs` — replace the top part of the file (everything above `FindByProcess`) with:

```csharp
namespace NetRoute.Core;

/// Exit: where a connection that matches this entry goes. CarveOut: a LAN entry that stays active in the LAN profile and
/// is matched before the phone entries (an app's update download).
public sealed record RuleEntry(
    string Id, string Name, IReadOnlyList<string> Processes, IReadOnlyList<string> Domains,
    RouteExit Exit = RouteExit.Lan, bool CarveOut = false);

/// The rules active right now. In the Phone profile: the LAN items switched on plus the enabled LAN user rules (traffic
/// that must stay off 4G). In the LAN profile: the carve-outs (update items, LAN), then the phone items switched on and
/// the enabled phone user rules (traffic that must use the phone).
public sealed record RuleSet(IReadOnlyList<RuleEntry> Entries)
{
    /// The Phone profile ("Phone + exceptions"), which is what Smart routing was before profiles existed.
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Build(catalog, settings, RouteExit.Phone);

    /// profileDefault is the profile's default exit: Phone = "Phone + exceptions", Lan = "LAN + exceptions".
    public static RuleSet Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, RouteExit profileDefault)
    {
        var on = catalog.Where(settings.IsItemOn).ToList();
        if (profileDefault == RouteExit.Phone)
            return Create(on.Where(i => i.Exit == RouteExit.Lan), settings.UserRules.Where(r => r.Enabled), []);

        var items = on.Where(i => i.CarveOut).Concat(on.Where(i => i.Exit == RouteExit.Phone && !i.CarveOut));
        return Create(items, [], settings.PhoneUserRules.Where(r => r.Enabled));
    }

    /// Every built-in item and every user rule of both lists, switched on or off. Usage uses it so that traffic of a
    /// switched-off item still lands in that item's row (it just goes through the default exit).
    public static RuleSet BuildAll(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        Create(catalog, settings.UserRules, settings.PhoneUserRules);

    static RuleSet Create(IEnumerable<RuleItem> items, IEnumerable<UserRule> lanRules, IEnumerable<UserRule> phoneRules)
    {
        var entries = items.Select(i => new RuleEntry(i.Id, i.Name, i.Processes, i.Domains, i.Exit, i.CarveOut)).ToList();
        foreach (var rule in lanRules) entries.Add(UserEntry(UserRule.IdOf(rule), rule, RouteExit.Lan));
        foreach (var rule in phoneRules) entries.Add(UserEntry(UserRule.PhoneIdOf(rule), rule, RouteExit.Phone));
        return new RuleSet(entries);
    }

    static RuleEntry UserEntry(string id, UserRule rule, RouteExit exit) =>
        rule.Type == UserRuleType.App
            ? new RuleEntry(id, rule.Value, [rule.Value], [], exit)
            : new RuleEntry(id, rule.Value, [], [rule.Value], exit);

    public string Fingerprint =>
        string.Join("|", Entries.Select(e =>
            $"{e.Id}:{e.Exit}:{e.CarveOut}:{string.Join(",", e.Processes)}:{string.Join(",", e.Domains)}"));
```

(Keep `FindByProcess` and `FindByHost` exactly as they are.) Update any existing test that asserts the old `Fingerprint` string shape (`grep -n Fingerprint tests` — `RuleSetTests.Fingerprint_changes_when_rules_change` only compares values and needs no change).

`src/NetRoute.Core/SmartRouting/UsageAttribution.cs`:
- add `const string PhonePrefix = "phone-";`
- constructor filter and `KeyOf`:

```csharp
        foreach (var entry in all.Entries.Where(e => !BareId(e).StartsWith(UserAppPrefix, StringComparison.Ordinal)))
            _names.TryAdd(KeyOf(entry), entry.Name);
```
```csharp
    /// The entry id without the phone-list prefix: the same app or site counts under one row whichever list holds its rule.
    static string BareId(RuleEntry entry) =>
        entry.Id.StartsWith(PhonePrefix, StringComparison.Ordinal) ? entry.Id[PhonePrefix.Length..] : entry.Id;

    /// A user App rule (either list) shares the application row's key, so assigning or unassigning it never splits the history.
    static string KeyOf(RuleEntry entry)
    {
        var id = BareId(entry);
        return id.StartsWith(UserAppPrefix, StringComparison.Ordinal) ? AppPrefix + id[UserAppPrefix.Length..] : id;
    }
```
  (delete the old `KeyOf`).
- `Build`: drop disabled App rules from **both** lists:

```csharp
    public static UsageAttribution Build(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings) =>
        new(RuleSet.BuildAll(catalog, settings with
        {
            // A disabled App rule routes nothing, so it must not claim its process ahead of the site rules; its traffic
            // falls through to them and then to the same "app:x.exe" row, so no history is lost.
            UserRules = [.. settings.UserRules.Where(r => r.Enabled || r.Type != UserRuleType.App)],
            PhoneUserRules = [.. settings.PhoneUserRules.Where(r => r.Enabled || r.Type != UserRuleType.App)],
        }));
```
- update the class doc comment's step 1 to mention "(a built-in item, or a user App rule of either list)".

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`
Expected: all green, 0 warnings. Other code that constructs `RuleEntry` positionally (`SingBoxConfigBuilderTests`, `SmartFakes`) keeps compiling because the new members have defaults.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): phone exception list, rule set per profile (carve-outs first), usage keys for both lists" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: sing-box config — phone-exit rules use the phone

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/SingBoxConfigBuilder.cs`
- Test: `tests/NetRoute.Core.Tests/SingBoxConfigBuilderTests.cs`

**Interfaces:**
- Consumes: Task 2 `RuleEntry.Exit`.
- Produces: in `route.rules`, an entry with `Exit == Phone` gets `"outbound": "phone"`; `Exit == Lan` keeps `"outbound": "lan-only"`. The order of `route.rules` follows `RuleSet.Entries` (carve-outs first), and `RuleIndexToEntryId` indexes stay in step.

- [ ] **Step 1: Write the failing tests** — append to `SingBoxConfigBuilderTests` (it already has `Parse`, `Input`-style helpers; add a local input builder for custom rules):

```csharp
    static SingBoxConfig BuildWith(params RuleEntry[] entries) =>
        SingBoxConfigBuilder.Build(new SingBoxConfigInput(new RuleSet(entries), "Ethernet 5", "Ethernet", null, RouteExit.Lan, false, 41234, "s3cret"));

    static JsonArray RouteRules(SingBoxConfig c) => Parse(c)["route"]!["rules"]!.AsArray();

    [Fact]
    public void A_phone_exit_entry_routes_to_the_phone_outbound_and_a_lan_entry_to_lan_only()
    {
        var config = BuildWith(
            new RuleEntry("claude", "Claude", ["claude.exe"], ["claude.ai"], RouteExit.Phone),
            new RuleEntry("youtube", "YouTube", [], ["youtube.com"], RouteExit.Lan));

        var rules = RouteRules(config).Where(r => r!["outbound"] is not null && (string)r["outbound"]! is "phone" or "lan-only").ToList();

        // claude: one process rule + one domain rule (both phone); youtube: one domain rule (lan-only)
        Assert.Equal(new[] { "phone", "phone", "lan-only" }, rules.Select(r => (string)r!["outbound"]!));
    }

    [Fact]
    public void A_carve_out_rule_precedes_the_phone_rules()
    {
        var config = BuildWith(
            new RuleEntry("vscode-updates", "VS Code updates", [], ["update.code.visualstudio.com"], RouteExit.Lan, CarveOut: true),
            new RuleEntry("vscode", "VS Code", ["Code.exe"], [], RouteExit.Phone));

        var rules = RouteRules(config);
        var update = rules.Select((r, i) => (r, i)).Single(x => x.r!["domain_suffix"]?.AsArray().Any(d => (string)d! == "update.code.visualstudio.com") == true);
        var process = rules.Select((r, i) => (r, i)).Single(x => x.r!["process_path_regex"] is not null);

        Assert.True(update.i < process.i);
        Assert.Equal("lan-only", (string)update.r!["outbound"]!);
        Assert.Equal("phone", (string)process.r!["outbound"]!);
        Assert.Equal("vscode-updates", config.RuleIndexToEntryId[update.i]);
        Assert.Equal("vscode", config.RuleIndexToEntryId[process.i]);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~SingBoxConfigBuilderTests"`
Expected: FAIL — the phone-exit entry still produces `lan-only`.

- [ ] **Step 3: Implement.** In `SingBoxConfigBuilder.Build`, replace the two `["outbound"] = LanOnlyTag` rule lines inside the `foreach (var entry in input.Rules.Entries)` loop:

```csharp
        foreach (var entry in input.Rules.Entries)
        {
            // A LAN entry goes through the LAN-only selector (it can wait for the LAN); a phone entry goes straight to the phone.
            var outbound = entry.Exit == RouteExit.Phone ? PhoneTag : LanOnlyTag;
            // sing-box ANDs process fields with domain fields inside one rule, so they get separate rules.
            if (entry.Processes.Count > 0)
            {
                ruleMap[rules.Count] = entry.Id;
                rules.Add(new JsonObject { ["process_path_regex"] = Strings(entry.Processes.Select(ProcessPathRegex)), ["outbound"] = outbound });
            }
            if (entry.Domains.Count > 0)
            {
                ruleMap[rules.Count] = entry.Id;
                rules.Add(new JsonObject { ["domain_suffix"] = Strings(entry.Domains), ["outbound"] = outbound });
            }
        }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core tests/NetRoute.Core.Tests
git commit -m "feat(core): phone-exit exceptions route to the phone outbound in the sing-box config" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The controller follows the profile; the status names it

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/SmartRoutingController.cs`, `src/NetRoute.Core/SmartRouting/SmartRoutingPresenter.cs`
- Test: `tests/NetRoute.Core.Tests/SmartRoutingControllerTests.cs`, `tests/NetRoute.Core.Tests/SmartRoutingPresenterTests.cs`

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: `SmartRoutingStatus` gains a trailing `RouteExit Profile = RouteExit.Phone` (the active profile: `Lan` while the routing mode is LAN, otherwise `Phone`); `SmartRoutingController.ApplyAsync` builds `_rules` with `RuleSet.Build(_catalog, smart, profile)` where `profile = net.Mode == RoutingMode.Lan ? RouteExit.Lan : RouteExit.Phone`; the normal status row reads `⚡ Smart routing ON · Phone + exceptions · N rules · X kept off 4G today` / `⚡ Smart routing ON · LAN + exceptions · N rules`.

- [ ] **Step 1: Write the failing tests.**

In `SmartRoutingControllerTests.cs`: add a v4 catalog and let `Create` take a catalog (change the helper signature; existing calls keep working):

```csharp
    static readonly IReadOnlyList<RuleItem> V4Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),
        new("claude", "ai", "AI", "Claude", ["claude.exe"], ["claude.ai"], true, RouteExit.Phone),
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    SmartRoutingController Create(Func<int>? freePort = null, IAdapterCounters? counters = null, UsageSnapshot? restored = null,
                                  IReadOnlyList<RuleItem>? catalog = null)
    {
        var c = new SmartRoutingController(catalog ?? Catalog, _host, (_, _) => _api, freePort ?? (() => 40000), restored, _logs.Add, _time, counters);
        c.WaitingDetected += _popups.Add;
        c.Notify += _notes.Add;
        return c;
    }
```

Add these tests (before the closing brace):

```csharp
    // ---- profiles (v4) ----

    [Fact]
    public async Task Phone_mode_runs_the_lan_exceptions_and_the_carve_outs()
    {
        var c = Create(catalog: V4Catalog);

        await c.ApplyAsync(Net(RoutingMode.Phone), On());

        var json = Assert.Single(_host.Starts);
        Assert.Contains("youtube.com", json);
        Assert.Contains("update.code.visualstudio.com", json);
        Assert.DoesNotContain("claude.ai", json);
        Assert.Equal(RouteExit.Phone, c.Status.Profile);
        Assert.Equal(2, c.Status.RuleCount);
    }

    [Fact]
    public async Task Switching_to_lan_mode_restarts_with_the_phone_rules()
    {
        var c = Create(catalog: V4Catalog);
        await c.ApplyAsync(Net(RoutingMode.Phone), On());

        await c.ApplyAsync(Net(RoutingMode.Lan), On());

        Assert.Equal(2, _host.Starts.Count);
        var json = _host.Starts[^1];
        Assert.Contains("claude.ai", json);
        Assert.Contains("update.code.visualstudio.com", json); // the carve-out is active in both profiles
        Assert.DoesNotContain("youtube.com", json);            // a LAN exception is pointless when everything is LAN
        Assert.Equal(RouteExit.Lan, c.Status.Profile);
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Switching_back_to_phone_mode_restores_the_lan_rules()
    {
        var c = Create(catalog: V4Catalog);
        await c.ApplyAsync(Net(RoutingMode.Lan), On());

        await c.ApplyAsync(Net(RoutingMode.Phone), On());

        Assert.Equal(2, _host.Starts.Count);
        Assert.Contains("youtube.com", _host.Starts[^1]);
        Assert.DoesNotContain("claude.ai", _host.Starts[^1]);
    }

    [Fact]
    public async Task Lan_profile_with_the_lan_unplugged_heals_to_the_phone_and_never_waits()
    {
        var c = Create(catalog: V4Catalog);

        await c.ApplyAsync(Net(RoutingMode.Lan, lan: false), On());
        _time.Advance(SmartRoutingController.LanOfflinePopupDelay + TimeSpan.FromSeconds(1));
        await c.ApplyAsync(Net(RoutingMode.Lan, lan: false), On());

        Assert.Equal(SmartState.Running, c.Status.State);
        Assert.Equal(RouteExit.Phone, c.Status.DefaultExit); // unmatched traffic heals onto the phone, as LAN mode does today
        Assert.Contains("claude.ai", _host.Starts[^1]);       // the phone exceptions are still there
        Assert.Empty(_popups);
        Assert.Empty(c.Status.WaitingNames);
    }

    [Fact]
    public async Task A_rule_edit_in_the_inactive_list_does_not_restart_sing_box()
    {
        var c = Create(catalog: V4Catalog);
        var settings = On();
        await c.ApplyAsync(Net(RoutingMode.Phone), settings);

        var edited = new AppSettings { SmartRouting = settings.SmartRouting.WithPhoneUserRule(new UserRule(UserRuleType.App, "x.exe")) };
        await c.ApplyAsync(Net(RoutingMode.Phone), edited);

        Assert.Single(_host.Starts); // the phone list is inactive in the Phone profile
    }
```

In `SmartRoutingPresenterTests.cs` update the expected normal row (line ~30) to the new wording and add the LAN row test:

```csharp
        Assert.Equal(new SmartRow("⚡ Smart routing ON · Phone + exceptions · 4 rules · 1.2 GB kept off 4G today", SmartTone.Normal),
```
(keep the surrounding arguments as they are), and add (use the same status-construction helper the existing test uses; add `Profile: RouteExit.Lan` to it):

```csharp
    [Fact]
    public void The_lan_profile_row_names_the_profile_and_omits_the_kept_figure()
    {
        // build the status exactly like the neighbouring test does, with Profile: RouteExit.Lan
        Assert.Equal(new SmartRow("⚡ Smart routing ON · LAN + exceptions · 9 rules", SmartTone.Normal), SmartRoutingPresenter.Row(LanProfileStatus(9)));
    }
```
where `LanProfileStatus(int rules)` is a local static helper that returns `new SmartRoutingStatus(SmartState.Running, null, RouteExit.Lan, false, true, [], rules, new DailyStats(new DateOnly(2026, 10, 3), new Dictionary<string, long>()), Profile: RouteExit.Lan)`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~SmartRoutingControllerTests|FullyQualifiedName~SmartRoutingPresenterTests"`
Expected: build FAIL — `SmartRoutingStatus.Profile` does not exist.

- [ ] **Step 3: Implement.**

`SmartRoutingController.cs`:
1. Record: `public sealed record SmartRoutingStatus(SmartState State, string? Message, RouteExit DefaultExit, bool LanRulesOnPhone, bool LanOnline, IReadOnlyList<string> WaitingNames, int RuleCount, DailyStats Today, string? Detail = null, RouteExit Profile = RouteExit.Phone)`.
2. In `ApplyAsync` replace `_rules = RuleSet.Build(_catalog, smart);` with:

```csharp
                _rules = RuleSet.Build(_catalog, smart, ProfileOf(net));
```
3. Add next to `WantedExit`:

```csharp
    /// The Smart routing profile is the routing mode: LAN mode = "LAN + exceptions", Phone (and Auto, which pauses) = "Phone + exceptions".
    static RouteExit ProfileOf(NetworkStatus? net) => net?.Mode == RoutingMode.Lan ? RouteExit.Lan : RouteExit.Phone;
```
4. In `Publish()` pass the profile as the last constructor argument: `..., state == SmartState.Faulted ? _lastCrashDetail : null, ProfileOf(_net));`

`SmartRoutingPresenter.cs`: replace the last arm of `Row`:

```csharp
        _ when s.Profile == RouteExit.Lan =>
            new($"⚡ Smart routing ON · LAN + exceptions · {s.RuleCount} rules", SmartTone.Normal),
        _ => new($"⚡ Smart routing ON · Phone + exceptions · {s.RuleCount} rules · {ByteFormat.Human(s.Today.Total)} kept off 4G today", SmartTone.Normal),
```

Search the other tests for the old text (`grep -rn "Smart routing ON" tests`) and update each expectation.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings.
Run: `dotnet build src/NetRoute.App -c Release -o "F:\Temp\claude\f--source-petrofahed-NetRouteWidget\f8bc83b0-9094-42d6-a498-2922eade94e2\scratchpad\nrw-v4-build"` — Expected: 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(core): Smart routing follows the profile (routing mode); the status row names it" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The Config page model edits either profile's list

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/SmartRoutingPresenter.cs`
- Test: `tests/NetRoute.Core.Tests/SmartRoutingPresenterTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 4 (`RuleItem.Exit/CarveOut`, `PhoneUserRules`, `Status.Profile`).
- Produces:
  - `PageItem(string Id, string Name, bool On, long TodayBytes, RouteExit Exit = RouteExit.Lan, bool CarveOut = false)`;
  - `PageModel(..., IReadOnlyList<PageUserRule> UserRules, RouteExit EditingProfile = RouteExit.Phone, RouteExit ActiveProfile = RouteExit.Phone)`;
  - `SmartRoutingPage.Build(catalog, settings, status, RouteExit editing = RouteExit.Phone)`: the Phone view lists the LAN-exit items (carve-outs included) and `settings.UserRules`; the LAN view lists the phone-exit items **plus the carve-outs** and `settings.PhoneUserRules`;
  - `SmartRoutingPage.ItemCaption(PageItem item, RouteExit editing) → string`: the dimmed text at the right of an item row.

- [ ] **Step 1: Write the failing tests** — append to `SmartRoutingPresenterTests` (add a v4 catalog and a status helper if the file lacks them; use `Profile`-aware status like Task 4's helper):

```csharp
    static readonly IReadOnlyList<RuleItem> V4Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),
        new("claude", "ai", "AI", "Claude", ["claude.exe"], [], true, RouteExit.Phone),
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    static SmartRoutingStatus StatusFor(RouteExit profile, Dictionary<string, long>? kept = null) =>
        new(SmartState.Running, null, profile, false, true, [], 3, new DailyStats(new DateOnly(2026, 10, 3), kept ?? new()), Profile: profile);

    [Fact]
    public void The_phone_view_lists_the_lan_items_and_carve_outs_with_the_lan_rules()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "a.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.App, "b.exe")],
        };

        var page = SmartRoutingPage.Build(V4Catalog, settings, StatusFor(RouteExit.Phone), RouteExit.Phone);

        Assert.Equal(new[] { "youtube", "vscode-updates" }, page.Groups.SelectMany(g => g.Items).Select(i => i.Id));
        Assert.Equal(new[] { "a.exe" }, page.UserRules.Select(r => r.Rule.Value));
        Assert.Equal(RouteExit.Phone, page.EditingProfile);
        Assert.Equal(RouteExit.Phone, page.ActiveProfile);
    }

    [Fact]
    public void The_lan_view_lists_the_phone_items_and_carve_outs_with_the_phone_rules()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "a.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.App, "b.exe")],
        };

        var page = SmartRoutingPage.Build(V4Catalog, settings, StatusFor(RouteExit.Phone), RouteExit.Lan);

        Assert.Equal(new[] { "claude", "vscode-updates" }, page.Groups.SelectMany(g => g.Items).Select(i => i.Id));
        Assert.Equal(new[] { "b.exe" }, page.UserRules.Select(r => r.Rule.Value));
        Assert.Equal(RouteExit.Lan, page.EditingProfile);
        Assert.Equal(RouteExit.Phone, page.ActiveProfile); // editing the other list does not change the active profile
    }

    [Fact]
    public void A_phone_rule_shows_its_usage_row_bytes()
    {
        var settings = new SmartRoutingSettings { PhoneUserRules = [new UserRule(UserRuleType.App, "B.exe")] };

        var page = SmartRoutingPage.Build(V4Catalog, settings, StatusFor(RouteExit.Lan, new() { ["app:b.exe"] = 700 }), RouteExit.Lan);

        Assert.Equal(700, Assert.Single(page.UserRules).TodayBytes);
    }

    [Fact]
    public void Group_toggles_work_on_the_carve_out_group_from_either_view()
    {
        var s = SmartRoutingPage.WithGroup(V4Catalog, new SmartRoutingSettings(), "upd", on: false);

        Assert.False(s.IsItemOn(V4Catalog.Single(i => i.Id == "vscode-updates")));
    }

    [Theory]
    [InlineData(RouteExit.Phone, RouteExit.Lan, false, true, "→ via phone")]  // Phone view, LAN item switched off
    [InlineData(RouteExit.Lan, RouteExit.Phone, false, true, "→ via LAN")]    // LAN view, phone item switched off
    [InlineData(RouteExit.Lan, RouteExit.Phone, false, false, "→ phone")]     // LAN view, phone item on
    [InlineData(RouteExit.Lan, RouteExit.Lan, true, false, "→ LAN")]          // LAN view, carve-out on
    [InlineData(RouteExit.Lan, RouteExit.Lan, true, true, "→ via phone")]     // LAN view, carve-out off: its app's phone rule catches it
    public void Item_captions_say_where_the_item_goes(RouteExit editing, RouteExit exit, bool carveOut, bool off, string expected)
    {
        var item = new PageItem("x", "X", On: !off, TodayBytes: 0, exit, carveOut);

        Assert.Equal(expected, SmartRoutingPage.ItemCaption(item, editing));
    }

    [Fact]
    public void A_phone_view_item_that_is_on_shows_its_bytes()
    {
        var item = new PageItem("youtube", "YouTube", On: true, TodayBytes: 2048);

        Assert.Equal("2 KB", SmartRoutingPage.ItemCaption(item, RouteExit.Phone));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~SmartRoutingPresenterTests"`
Expected: build FAIL — `PageItem` has no `Exit`, `PageModel.EditingProfile` / `Build(..., editing)` / `ItemCaption` do not exist.

- [ ] **Step 3: Implement** in `SmartRoutingPresenter.cs` (replace the page records and `SmartRoutingPage.Build`; keep `NextGroupState` and `WithGroup`):

```csharp
public sealed record PageItem(string Id, string Name, bool On, long TodayBytes, RouteExit Exit = RouteExit.Lan, bool CarveOut = false);

public sealed record PageGroup(string Id, string Name, bool? On, long TodayBytes, IReadOnlyList<PageItem> Items);

public sealed record PageUserRule(UserRule Rule, long TodayBytes);

/// EditingProfile: which profile's list the page shows (Phone = "Phone + exceptions", Lan = "LAN + exceptions").
/// ActiveProfile: the profile Smart routing is running (the routing mode).
public sealed record PageModel(
    bool Enabled, SmartRow Status, bool CanUsePhone, long TotalToday,
    IReadOnlyList<PageGroup> Groups, IReadOnlyList<PageUserRule> UserRules,
    RouteExit EditingProfile = RouteExit.Phone, RouteExit ActiveProfile = RouteExit.Phone);
```

```csharp
    public static PageModel Build(
        IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, SmartRoutingStatus status, RouteExit editing = RouteExit.Phone)
    {
        long Bytes(string id) => status.Today.BytesByEntry.GetValueOrDefault(id);

        // The Phone view edits the LAN exceptions; the LAN view edits the phone exceptions. Carve-outs (update items) belong to both.
        var visible = editing == RouteExit.Phone
            ? catalog.Where(i => i.Exit == RouteExit.Lan)
            : catalog.Where(i => i.Exit == RouteExit.Phone || i.CarveOut);
        var groups = visible.GroupBy(i => (i.GroupId, i.GroupName)).Select(g =>
        {
            var items = g.Select(i => new PageItem(i.Id, i.Name, settings.IsItemOn(i), Bytes(i.Id), i.Exit, i.CarveOut)).ToList();
            bool? on = items.All(i => i.On) ? true : items.Any(i => i.On) ? null : false;
            return new PageGroup(g.Key.GroupId, g.Key.GroupName, on, items.Sum(i => i.TodayBytes), items);
        }).ToList();
        var userRules = editing == RouteExit.Phone ? settings.UserRules : settings.PhoneUserRules;
        var rules = userRules.Select(r => new PageUserRule(r, Bytes(UsageAttribution.RowKeyOf(r)))).ToList();

        return new PageModel(
            settings.Enabled, SmartRoutingPresenter.Row(status),
            CanUsePhone: status.State == SmartState.Running && !status.LanOnline && !status.LanRulesOnPhone,
            status.Today.Total, groups, rules, editing, status.Profile);
    }

    /// The dimmed text at the right of an item row. The Phone view shows what the item kept off 4G today, or where it goes
    /// when switched off; the LAN view says where the item goes. A switched-off carve-out falls to its app's phone rule.
    public static string ItemCaption(PageItem item, RouteExit editing)
    {
        if (editing == RouteExit.Phone)
            return item.On ? ByteFormat.Human(item.TodayBytes) : "→ via phone";
        if (item.CarveOut) return item.On ? "→ LAN" : "→ via phone";
        return item.On ? "→ phone" : "→ via LAN";
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings. Existing callers of `SmartRoutingPage.Build(catalog, settings, status)` (App) still compile through the default.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(core): Config page model for either profile's list, with captions" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The editing selector, edits routed to the right list, card tooltips

**Files:**
- Modify: `src/NetRoute.App/SmartRoutingWindow.xaml`, `src/NetRoute.App/SmartRoutingWindow.xaml.cs`, `src/NetRoute.App/CardWindow.xaml`, `src/NetRoute.App/App.xaml.cs`

**Interfaces:**
- Consumes: Task 5 `PageModel.EditingProfile/ActiveProfile`, `SmartRoutingPage.ItemCaption`, `SmartRoutingSettings.WithPhoneUserRule/WithoutPhoneUserRule`.
- Produces: `SmartRoutingWindow.EditingProfileChanged` (`event Action<RouteExit>?`); App routes `UserRuleToggled`, `UserRuleRemoved`, `AddRuleRequested` to `UserRules` (Phone view) or `PhoneUserRules` (LAN view).
- This task is verified by build and by rendering the window (no unit-testable logic is left in the UI: lists, captions and rule routing keys are pinned in Tasks 2 and 5).

- [ ] **Step 1: The selector and wording in the Config tab.**

`SmartRoutingWindow.xaml`, Config tab: add, as the **first** child of the top `StackPanel` (above the master toggle row):

```xml
                    <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
                        <TextBlock Text="Editing:" VerticalAlignment="Center" Margin="0,0,8,0" Foreground="{DynamicResource TextSecondary}" />
                        <ToggleButton x:Name="EditPhoneProfile" Content="Phone + exceptions" Padding="10,4" Margin="0,0,4,0" Click="OnEditPhoneProfile" />
                        <ToggleButton x:Name="EditLanProfile" Content="LAN + exceptions" Padding="10,4" Click="OnEditLanProfile" />
                    </StackPanel>
```

Replace the two static hint `TextBlock`s ("Turned-off items go through the phone like everything else." and the rule-change note) with one named block for the first and keep the second:

```xml
                    <TextBlock x:Name="HintText" Margin="0,8,0,0" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" />
```
(the "Changing a rule restarts Smart routing for about a second…" text stays as it is).

`SmartRoutingWindow.xaml.cs`:
- add `public event Action<RouteExit>? EditingProfileChanged;` and the handlers:

```csharp
    void OnEditPhoneProfile(object sender, RoutedEventArgs e) => EditingProfileChanged?.Invoke(RouteExit.Phone);
    void OnEditLanProfile(object sender, RoutedEventArgs e) => EditingProfileChanged?.Invoke(RouteExit.Lan);
```
- `StructureKey`: append the editing profile first (`sb.Append("E").Append((int)model.EditingProfile).Append('\n');`) so switching the view rebuilds the lists.
- `Update(PageModel model)`: set the selector and the wording (the buttons' `IsChecked` is set from the model, so a click is "undone" until the App confirms, exactly like the card's mode buttons):

```csharp
        var lanView = model.EditingProfile == RouteExit.Lan;
        EditPhoneProfile.IsChecked = !lanView;
        EditLanProfile.IsChecked = lanView;
        EditPhoneProfile.Content = "Phone + exceptions" + (model.ActiveProfile == RouteExit.Phone ? "  ● active" : "");
        EditLanProfile.Content = "LAN + exceptions" + (model.ActiveProfile == RouteExit.Lan ? "  ● active" : "");
        TotalText.Text = lanView
            ? "Everything else goes through the LAN. These go through the phone."
            : $"Kept off 4G today: {ByteFormat.Human(model.TotalToday)}";
        HintText.Text = lanView
            ? "Turned-off items go through the LAN like everything else (a turned-off update item falls back to its app's phone rule)."
            : "Turned-off items go through the phone like everything else.";
```
  (replace the existing `TotalText.Text = ...` line).
- In the per-item loop use the caption: replace `bytes.Text = item.On ? ByteFormat.Human(item.TodayBytes) : "→ via phone";` with `bytes.Text = SmartRoutingPage.ItemCaption(item, model.EditingProfile);`.
- The "My rules" header text becomes `lanView ? "My phone rules" : "My rules"` (`UserRulesView(model)` already receives the model) and the empty text `"No rules yet — add an app or a website."` stays.

- [ ] **Step 2: App wiring.** In `App.xaml.cs`:
- field: `RouteExit? _editingProfile;` (null = follow the active profile) and

```csharp
    RouteExit EditingProfile => _editingProfile ?? (_smart?.Status.Profile ?? RouteExit.Phone);
```
- `RenderSmartWindowAsync`: pass it: `window.Render(SmartRoutingPage.Build(_catalog, settings, status, EditingProfile));`
- In `OpenSmartRouting()` set `_editingProfile = null;` when a new window is created, and wire:

```csharp
        window.EditingProfileChanged += profile =>
        {
            _editingProfile = profile;
            RenderSmartWindow();
        };
        window.UserRuleToggled += (rule, on) => ChangeSmart(s => EditingProfile == RouteExit.Lan
            ? s.WithPhoneUserRule(rule with { Enabled = on })
            : s.WithUserRule(rule with { Enabled = on }));
        window.UserRuleRemoved += rule => ChangeSmart(s => EditingProfile == RouteExit.Lan
            ? s.WithoutPhoneUserRule(rule)
            : s.WithoutUserRule(rule));
```
  (replace the existing `UserRuleToggled` / `UserRuleRemoved` lines) and in the `AddRuleRequested` handler replace `ChangeSmart(s => s.WithUserRule(rule))` with `ChangeSmart(s => EditingProfile == RouteExit.Lan ? s.WithPhoneUserRule(rule) : s.WithUserRule(rule))`.
- The `RenderSmart(status)` path already re-renders the window after every status; when the routing mode changes the active profile marker moves on its own.

- [ ] **Step 3: Card tooltips.** `CardWindow.xaml`: add to the two mode buttons

```xml
<ToggleButton x:Name="PhoneButton" Content="Phone" Tag="Phone" Style="{StaticResource ModeButton}" Click="OnMode"
              ToolTip="Phone + exceptions: everything through the phone; your LAN list (and update downloads) through the LAN" />
<ToggleButton x:Name="LanButton" Content="LAN" Tag="Lan" Style="{StaticResource ModeButton}" Click="OnMode"
              ToolTip="LAN + exceptions: everything through the LAN; your phone list (AI tools, Omantel) through the phone" />
```
(leave the Auto button as it is).

- [ ] **Step 4: Build, test, render.**

Run: `dotnet build src/NetRoute.App -c Release -o "F:\Temp\claude\f--source-petrofahed-NetRouteWidget\f8bc83b0-9094-42d6-a498-2922eade94e2\scratchpad\nrw-v4-build"` — Expected: 0 warnings, 0 errors.
Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings.
Render with the throwaway harness at `F:\Temp\claude\f--source-petrofahed-NetRouteWidget\f8bc83b0-9094-42d6-a498-2922eade94e2\scratchpad\themeharness\` (own windows only; update its `Program.cs` for the new `PageModel` arguments): capture the **Config tab** in dark and light for (a) the Phone view with a few LAN items and one user rule and (b) the LAN view (AI tools group with 8 items, Omantel, the updates group, one phone rule, the "● active" marker on the other button). **Read every PNG** and check: selector readable in both themes, the checked button obvious, captions right-aligned and not clipped ("→ phone", "→ LAN", "→ via phone"), group names fully visible, no literal colours. List what you could not verify (live click round trips, the real mode switch).

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.App
git commit -m "feat(app): Config tab edits either profile's list; edits go to the right list; card tooltips name the profiles" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Docs and the on-machine checklist

**Files:**
- Modify: `README.md`, `docs/superpowers/specs/2026-10-03-netroute-v4-profiles-design.md`
- Test: none beyond the final checks.

- [ ] **Step 1: README.** Add a "Profiles" section (3-6 short bullets): the two profiles and that the card's Phone / LAN buttons switch them; what each exception list does; the starting phone list (AI tools, Omantel); the update items that always use the LAN; the Config tab's "Editing:" selector; the Usage tab's right-click menu ("Send to exception" / "Exclude from exception") and the tag on exception rows; that switching restarts Smart routing for about a second; and the honest limit that the built-in process names and update domains are best guesses that can be edited through "My rules" or fixed in `rules\builtin.json`.

- [ ] **Step 2: Spec.** Set the `**Status:**` line to `Implemented on branch feat/v2-smart-routing; waiting for the user's on-machine verification.`

- [ ] **Step 3: Final checks and commit.**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings.
Run: `dotnet build src/NetRoute.App -c Release -o "F:\Temp\claude\f--source-petrofahed-NetRouteWidget\f8bc83b0-9094-42d6-a498-2922eade94e2\scratchpad\nrw-v4-build"` — Expected: 0 warnings, 0 errors.

```bash
git add README.md docs
git commit -m "docs(v4): profiles in the README; spec marked implemented pending on-machine verification" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: An application icon (exe, taskbar, window title bars)

Requested by the user after the plan was approved: the tray icon is a green globe, but the application itself (the exe in Explorer and the taskbar button of the management window) has no icon of its own, so Windows shows a generic blue window. Give the application the same globe.

**Files:**
- Create: `tools/Make-AppIcon.ps1`, `src/NetRoute.App/Assets/NetRouteWidget.ico` (generated, committed)
- Modify: `src/NetRoute.App/NetRoute.App.csproj`, `src/NetRoute.App/CardWindow.xaml`, `src/NetRoute.App/SmartRoutingWindow.xaml`, `src/NetRoute.App/AddRuleWindow.xaml`, `src/NetRoute.App/AdapterPickerWindow.xaml`, `src/NetRoute.App/WaitingPopup.xaml`
- Test: none (visual); verified by extracting the icons and reading them.

**Interfaces:**
- Produces: a multi-size `.ico` (16, 24, 32, 48, 64, 128, 256 px, 32-bit PNG entries) of the tray globe drawn in the Phone green (`#2EA043`) with the white meridian and equator, exactly the geometry of `TrayIcon.Draw` scaled from its 32 px grid; the exe's `ApplicationIcon`; every window's `Icon`.

- [ ] **Step 1: The generator** — `tools/Make-AppIcon.ps1` (read-only on everything except the output file; draws with System.Drawing, assembles the ICO by hand):

```powershell
<#
.SYNOPSIS
  Generates src\NetRoute.App\Assets\NetRouteWidget.ico: the tray globe (Phone green) at 16..256 px.
.DESCRIPTION
  Same geometry as TrayIcon.Draw (32 px grid: filled circle 2,2,28,28; meridian ellipse 10,3,12,26; equator 3,16 to 29,16;
  white 2 px lines), scaled per size. -PreviewPng also writes the 256 px image so it can be looked at.
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\src\NetRoute.App\Assets\NetRouteWidget.ico'),
    [string]$PreviewPng
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-GlobePng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 32.0
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x2E, 0xA0, 0x43))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.0, 2 * $k))
    $g.FillEllipse($brush, [single](2 * $k), [single](2 * $k), [single](28 * $k), [single](28 * $k))
    $g.DrawEllipse($pen, [single](10 * $k), [single](3 * $k), [single](12 * $k), [single](26 * $k))
    $g.DrawLine($pen, [single](3 * $k), [single](16 * $k), [single](29 * $k), [single](16 * $k))
    $g.Dispose(); $brush.Dispose(); $pen.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$images = foreach ($s in $sizes) { [pscustomobject]@{ Size = $s; Png = (New-GlobePng $s) } }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$stream = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)       # ICONDIR: reserved, type 1 = icon, count
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { [byte]0 } else { [byte]$i.Size }                   # 0 means 256
    $w.Write($dim); $w.Write($dim); $w.Write([byte]0); $w.Write([byte]0)             # width, height, colours, reserved
    $w.Write([uint16]1); $w.Write([uint16]32)                                         # planes, bits per pixel
    $w.Write([uint32]$i.Png.Length); $w.Write([uint32]$offset)                        # image size, offset
    $offset += $i.Png.Length
}
foreach ($i in $images) { $w.Write($i.Png) }
$w.Flush(); $w.Dispose(); $stream.Dispose()
if ($PreviewPng) { [System.IO.File]::WriteAllBytes($PreviewPng, ($images | Where-Object Size -eq 256).Png) }
Write-Host "Wrote $Out ($($images.Count) sizes)"
```

- [ ] **Step 2: Generate and look at it.**

Run (PowerShell, from the repo root): `powershell -NoProfile -File tools\Make-AppIcon.ps1 -PreviewPng "<scratch>\appicon-256.png"` — Expected: `Wrote ...NetRouteWidget.ico (7 sizes)`. Then load it back to prove it is a valid icon: `[void](New-Object System.Drawing.Icon "src\NetRoute.App\Assets\NetRouteWidget.ico")` (no exception). **Read the preview PNG** and check: a green disc with a white vertical ellipse and a white horizontal line, transparent corners.

- [ ] **Step 3: Wire it in.**

`src/NetRoute.App/NetRoute.App.csproj`: in the first `PropertyGroup` add `<ApplicationIcon>Assets\NetRouteWidget.ico</ApplicationIcon>`, and add a new `ItemGroup`:

```xml
  <ItemGroup>
    <Resource Include="Assets\NetRouteWidget.ico" />
  </ItemGroup>
```

Every window's root element (`CardWindow.xaml`, `SmartRoutingWindow.xaml`, `AddRuleWindow.xaml`, `AdapterPickerWindow.xaml`, `WaitingPopup.xaml`) gets the attribute `Icon="Assets/NetRouteWidget.ico"` (the card and popup have no taskbar button but appear in Alt+Tab and keep the icon consistent).

- [ ] **Step 4: Build, test, look at the real exe icon.**

Run: `dotnet build src/NetRoute.App -c Release -o "<scratch>\nrw-v4-build"` — Expected: 0 warnings, 0 errors.
Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings.
Extract the icon Windows will show for the built exe and read it: in PowerShell `Add-Type -AssemblyName System.Drawing; [System.Drawing.Icon]::ExtractAssociatedIcon("<scratch>\nrw-v4-build\NetRouteWidget.exe").ToBitmap().Save("<scratch>\exe-icon.png")` then read `exe-icon.png`: it must show the green globe, not the generic blue application icon. Also render the management window with the harness (own windows only) and confirm the title bar shows the globe. **Do not launch NetRouteWidget.exe.**

- [ ] **Step 5: Commit**

```bash
git add tools/Make-AppIcon.ps1 src/NetRoute.App
git commit -m "feat(app): application icon (the tray globe) for the exe, the taskbar button and every window" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Right-click a Usage row to send it to / exclude it from the exception list, with a tag on exceptions

Requested by the user after the plan was approved: in the Usage list, right-click a row → **Send to exception**; if the row is already an exception → **Exclude from exception**; and a visible **tag** on every row that is an exception so you can see it is routed. "Exception" always means *the active profile's exception list* (Phone + exceptions: items that go through the LAN; LAN + exceptions: items that go through the phone). Execute this task **after Task 6 and before Task 7**.

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/UsageExceptions.cs`
- Delete: `src/NetRoute.Core/SmartRouting/UsageAssignment.cs`, `tests/NetRoute.Core.Tests/UsageAssignmentTests.cs` (the v3 "Goes via" helper, unused by the UI since the column was removed; `UsageExceptions` replaces it)
- Modify: `src/NetRoute.Core/SmartRouting/UsageReport.cs`, `src/NetRoute.App/UsageTab.xaml.cs`, `src/NetRoute.App/SmartRoutingWindow.xaml.cs`, `src/NetRoute.App/App.xaml.cs`
- Test: `tests/NetRoute.Core.Tests/UsageExceptionsTests.cs` (new), `tests/NetRoute.Core.Tests/UsageReportTests.cs`

**Interfaces:**
- Consumes: Task 2 (`PhoneUserRules`, `WithPhoneUserRule`, `RuleItem.Exit/CarveOut`), Task 4 (`SmartRoutingStatus.Profile`).
- Produces:
  - `readonly record struct ExceptionState(bool InException, bool CanChange, RouteExit Destination)`;
  - `UsageExceptions.Describe(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, RouteExit profile, string rowKey) → ExceptionState`;
  - `UsageExceptions.Toggle(catalog, settings, profile, rowKey) → SmartRoutingSettings` (unchanged settings when `!CanChange`);
  - `UsageReportRow(string Key, string Name, ExceptionState Exception, long PhoneBytes, long LanBytes, UsageRate? Now)` with `bool CanChange => Exception.CanChange` (replaces the `GoesVia Via, bool CanChange` members);
  - `UsageReport.Build(usage, today, rangeDays, catalog, settings, canAssign, sort, string? filter = null, RouteExit profile = RouteExit.Phone)`;
  - `UsageReport.TagText(ExceptionState) → string?` (`"→ LAN"` / `"→ phone"` when `InException`, else null) and `UsageReport.MenuText(ExceptionState) → string`;
  - `UsageTab.ExceptionToggleRequested` / `SmartRoutingWindow.UsageExceptionToggleRequested` (`event Action<string>?`, the row key).

**Semantics** (profile = the active profile, `RouteExit.Phone` = "Phone + exceptions"):
- A row that is a **built-in item** belonging to the active list (Phone profile: `Exit == Lan`, carve-outs included; LAN profile: `Exit == Phone` or a carve-out) is an exception while the item is switched on; toggling switches the item on/off (the same switch as the Config tab). `Destination` = the item's `Exit`. An item of the *other* list is not changeable here (`CanChange = false`).
- A row `app:<exe>` is an exception when the active profile's user-rule list holds an **enabled** App rule for that exe; toggling flips `Enabled` of the existing rule, or creates an enabled rule when there is none (creating fails for a name that is not a valid `.exe`, then `CanChange` is false). `Destination` = the opposite of the profile (Phone profile → LAN; LAN profile → phone).
- A row `user:website:<host>` is an exception when the active list holds an enabled Website rule for it; toggling flips `Enabled`. A website rule that lives only in the other list is not changeable here.
- `other` and `unattributed` are never changeable.
- `canAssign` (Smart routing unavailable/off) disables every row's menu: `Build` ANDs it into `CanChange`.

- [ ] **Step 1: Write the failing tests.**

`tests/NetRoute.Core.Tests/UsageExceptionsTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class UsageExceptionsTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),                                      // LAN exception
        new("claude", "ai", "AI", "Claude", ["claude.exe"], [], true, RouteExit.Phone),                            // phone exception
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    [Fact]
    public void A_lan_item_is_an_exception_of_the_phone_profile_while_switched_on()
    {
        var on = new SmartRoutingSettings();
        var off = on.WithItem("youtube", false);

        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, on, RouteExit.Phone, "youtube"));
        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, off, RouteExit.Phone, "youtube"));
        Assert.False(UsageExceptions.Toggle(Catalog, on, RouteExit.Phone, "youtube").IsItemOn(Catalog[0]));
        Assert.True(UsageExceptions.Toggle(Catalog, off, RouteExit.Phone, "youtube").IsItemOn(Catalog[0]));
    }

    [Fact]
    public void An_item_of_the_other_list_cannot_be_changed_from_here()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(false, false, RouteExit.Phone), UsageExceptions.Describe(Catalog, s, RouteExit.Phone, "claude"));
        Assert.Equal(new ExceptionState(false, false, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "youtube"));
        Assert.Equal(s, UsageExceptions.Toggle(Catalog, s, RouteExit.Phone, "claude"));
    }

    [Fact]
    public void In_the_lan_profile_phone_items_and_carve_outs_are_the_exceptions()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(true, true, RouteExit.Phone), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "claude"));
        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "vscode-updates"));
        Assert.False(UsageExceptions.Toggle(Catalog, s, RouteExit.Lan, "claude").IsItemOn(Catalog[1]));
    }

    [Fact]
    public void An_application_row_creates_then_disables_then_re_enables_a_rule_in_the_active_list()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Phone, "app:chrome.exe"));
        var added = UsageExceptions.Toggle(Catalog, s, RouteExit.Phone, "app:chrome.exe");
        Assert.Equal(new UserRule(UserRuleType.App, "chrome.exe", true), Assert.Single(added.UserRules));
        Assert.Empty(added.PhoneUserRules);
        Assert.True(UsageExceptions.Describe(Catalog, added, RouteExit.Phone, "app:chrome.exe").InException);

        var removed = UsageExceptions.Toggle(Catalog, added, RouteExit.Phone, "app:chrome.exe");
        Assert.False(Assert.Single(removed.UserRules).Enabled); // the rule stays listed on the Config tab, switched off
        Assert.False(UsageExceptions.Describe(Catalog, removed, RouteExit.Phone, "app:chrome.exe").InException);

        var again = UsageExceptions.Toggle(Catalog, removed, RouteExit.Phone, "app:chrome.exe");
        Assert.True(Assert.Single(again.UserRules).Enabled);
    }

    [Fact]
    public void In_the_lan_profile_an_application_goes_to_the_phone_list()
    {
        var added = UsageExceptions.Toggle(Catalog, new SmartRoutingSettings(), RouteExit.Lan, "app:qbittorrent.exe");

        Assert.Empty(added.UserRules);
        Assert.Equal(new UserRule(UserRuleType.App, "qbittorrent.exe", true), Assert.Single(added.PhoneUserRules));
        Assert.Equal(new ExceptionState(true, true, RouteExit.Phone), UsageExceptions.Describe(Catalog, added, RouteExit.Lan, "app:qbittorrent.exe"));
        // The same app is not an exception of the other profile.
        Assert.False(UsageExceptions.Describe(Catalog, added, RouteExit.Phone, "app:qbittorrent.exe").InException);
    }

    [Fact]
    public void A_website_rule_row_toggles_its_rule_in_the_active_list_only()
    {
        var s = new SmartRoutingSettings().WithUserRule(new UserRule(UserRuleType.Website, "dropbox.com"));

        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Phone, "user:website:dropbox.com"));
        Assert.False(Assert.Single(UsageExceptions.Toggle(Catalog, s, RouteExit.Phone, "user:website:dropbox.com").UserRules).Enabled);
        // In the LAN profile that rule belongs to the other list: not changeable here.
        Assert.False(UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "user:website:dropbox.com").CanChange);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("unattributed")]
    [InlineData("app:")]
    [InlineData("app:notanexe")]
    [InlineData("user:website:not-a-rule.com")]
    public void Rows_that_cannot_be_an_exception_are_locked_and_never_change_the_settings(string key)
    {
        var s = new SmartRoutingSettings();

        Assert.False(UsageExceptions.Describe(Catalog, s, RouteExit.Phone, key).CanChange);
        Assert.Equal(s, UsageExceptions.Toggle(Catalog, s, RouteExit.Phone, key));
    }
}
```

`tests/NetRoute.Core.Tests/UsageReportTests.cs`: the old `Goes_via_follows_the_settings_and_locks_rows_that_cannot_be_assigned` test (which reads `.Via`) is replaced by the tests below; the `Nothing_can_be_assigned_while_smart_routing_is_unavailable` test keeps its meaning (it reads `.CanChange`, which still exists). Add (the file has `Snap`, `Build(usage, range, settings, canAssign, sort)` helpers; give its `Build` wrapper two more optional parameters `string? filter = null, RouteExit profile = RouteExit.Phone` and pass them through):

```csharp
    [Fact]
    public void Rows_carry_the_exception_state_of_the_active_profile()
    {
        var usage = Snap(
            (Today, "youtube", 0, 10, 0), (Today, "app:chrome.exe", 10, 0, 0),
            (Today, UsageAttribution.OtherKey, 1, 0, 0), (Today, UsageAttribution.UnattributedKey, 1, 0, 0));
        var settings = new SmartRoutingSettings().WithItem("youtube", false);

        var rows = Build(usage, settings: settings).Rows.ToDictionary(r => r.Key);

        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), rows["youtube"].Exception);     // switched off: not an exception now
        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), rows["app:chrome.exe"].Exception);
        Assert.False(rows[UsageAttribution.OtherKey].CanChange);
        Assert.False(rows[UsageAttribution.UnattributedKey].CanChange);
    }

    [Fact]
    public void The_profile_selects_which_list_the_rows_are_exceptions_of()
    {
        var usage = Snap((Today, "app:chrome.exe", 10, 0, 0));
        var settings = new SmartRoutingSettings().WithPhoneUserRule(new UserRule(UserRuleType.App, "chrome.exe"));

        Assert.False(Build(usage, settings: settings).Rows.Single().Exception.InException);
        Assert.True(Build(usage, settings: settings, profile: RouteExit.Lan).Rows.Single().Exception.InException);
    }

    [Fact]
    public void Tag_and_menu_texts()
    {
        Assert.Equal("→ LAN", UsageReport.TagText(new ExceptionState(true, true, RouteExit.Lan)));
        Assert.Equal("→ phone", UsageReport.TagText(new ExceptionState(true, true, RouteExit.Phone)));
        Assert.Null(UsageReport.TagText(new ExceptionState(false, true, RouteExit.Lan)));
        Assert.Equal("Exclude from exception", UsageReport.MenuText(new ExceptionState(true, true, RouteExit.Lan)));
        Assert.Equal("Send to exception (→ LAN)", UsageReport.MenuText(new ExceptionState(false, true, RouteExit.Lan)));
        Assert.Equal("Send to exception (→ phone)", UsageReport.MenuText(new ExceptionState(false, true, RouteExit.Phone)));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageExceptionsTests|FullyQualifiedName~UsageReportTests"`
Expected: build FAIL — `ExceptionState`, `UsageExceptions`, `UsageReportRow.Exception`, `UsageReport.TagText/MenuText` do not exist.

- [ ] **Step 3: Implement the Core.**

`git rm src/NetRoute.Core/SmartRouting/UsageAssignment.cs tests/NetRoute.Core.Tests/UsageAssignmentTests.cs`, then create `src/NetRoute.Core/SmartRouting/UsageExceptions.cs`:

```csharp
namespace NetRoute.Core;

/// Whether a usage row is an exception of the ACTIVE profile (so a tag is shown), whether the user may add/remove it from
/// there, and where it goes while it is an exception.
public readonly record struct ExceptionState(bool InException, bool CanChange, RouteExit Destination);

/// The Usage tab's right-click menu: "Send to exception" / "Exclude from exception". It reads and writes the same switches
/// and rule lists as the Config tab. profile = the active profile's default exit (Phone = "Phone + exceptions").
public static class UsageExceptions
{
    public static ExceptionState Describe(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, RouteExit profile, string key)
    {
        var destination = profile == RouteExit.Phone ? RouteExit.Lan : RouteExit.Phone;
        if (catalog.FirstOrDefault(i => i.Id == key) is { } item)
        {
            var inList = Belongs(item, profile);
            return new(inList && settings.IsItemOn(item), inList, item.Exit);
        }
        var list = ListFor(settings, profile);
        if (AppExe(key) is { } exe) return new(FindApp(list, exe)?.Enabled == true, true, destination);
        if (FindWebsite(list, key) is { } site) return new(site.Enabled, true, destination);
        return new(false, false, destination);
    }

    /// The settings with the row switched to the other state; unchanged for a row that cannot be changed from here.
    public static SmartRoutingSettings Toggle(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings settings, RouteExit profile, string key)
    {
        var state = Describe(catalog, settings, profile, key);
        if (!state.CanChange) return settings;
        var turnOn = !state.InException;

        if (catalog.FirstOrDefault(i => i.Id == key) is { } item) return settings.WithItem(item.Id, turnOn);
        var list = ListFor(settings, profile);
        if (AppExe(key) is { } exe)
        {
            if (FindApp(list, exe) is { } rule) return With(settings, profile, rule with { Enabled = turnOn });
            return turnOn && UserRule.TryCreate(UserRuleType.App, exe, out var created, out _)
                ? With(settings, profile, created!)
                : settings;
        }
        return FindWebsite(list, key) is { } site ? With(settings, profile, site with { Enabled = turnOn }) : settings;
    }

    /// The Phone profile's exceptions are the LAN items; the LAN profile's are the phone items plus the carve-outs.
    static bool Belongs(RuleItem item, RouteExit profile) =>
        profile == RouteExit.Phone ? item.Exit == RouteExit.Lan : item.Exit == RouteExit.Phone || item.CarveOut;

    static IReadOnlyList<UserRule> ListFor(SmartRoutingSettings s, RouteExit profile) =>
        profile == RouteExit.Phone ? s.UserRules : s.PhoneUserRules;

    static SmartRoutingSettings With(SmartRoutingSettings s, RouteExit profile, UserRule rule) =>
        profile == RouteExit.Phone ? s.WithUserRule(rule) : s.WithPhoneUserRule(rule);

    /// The exe file name of an "app:" row, or null when the key is not an application row or the name is not a valid exe name.
    static string? AppExe(string key)
    {
        if (!key.StartsWith(UsageAttribution.AppPrefix, StringComparison.Ordinal)) return null;
        var exe = key[UsageAttribution.AppPrefix.Length..];
        return UserRule.TryCreate(UserRuleType.App, exe, out _, out _) ? exe : null;
    }

    static UserRule? FindApp(IReadOnlyList<UserRule> list, string exe) =>
        list.FirstOrDefault(r => r.Type == UserRuleType.App && string.Equals(r.Value, exe, StringComparison.OrdinalIgnoreCase));

    static UserRule? FindWebsite(IReadOnlyList<UserRule> list, string key) =>
        list.FirstOrDefault(r => r.Type == UserRuleType.Website && UserRule.IdOf(r) == key);
}
```

`src/NetRoute.Core/SmartRouting/UsageReport.cs`:
- `UsageReportRow`: replace the record with `public sealed record UsageReportRow(string Key, string Name, ExceptionState Exception, long PhoneBytes, long LanBytes, UsageRate? Now) { public bool CanChange => Exception.CanChange; }`.
- `Build`: keep the existing optional `filter` parameter and add `RouteExit profile = RouteExit.Phone` after it; build each row's state with `var exception = UsageExceptions.Describe(catalog, settings, profile, key); if (!canAssign) exception = exception with { CanChange = false };`; remove the `UsageAssignment.Describe` call and any `GoesVia` use.
- Add:

```csharp
    /// The tag shown next to a row that is an exception: where its traffic is routed.
    public static string? TagText(ExceptionState state) =>
        state.InException ? Destination(state.Destination) : null;

    public static string MenuText(ExceptionState state) =>
        state.InException ? "Exclude from exception" : $"Send to exception ({Destination(state.Destination)})";

    static string Destination(RouteExit exit) => exit == RouteExit.Lan ? "→ LAN" : "→ phone";
```

- [ ] **Step 4: Run to verify the Core passes.**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings. (Fix any other test or code that still refers to `UsageAssignment`, `GoesVia` or `UsageReportRow.Via`.)

- [ ] **Step 5: The UI.**

`UsageTab.xaml.cs`:
- add `public event Action<string>? ExceptionToggleRequested;`.
- In `RowView`: replace the plain name `TextBlock` cell with a `DockPanel` (column 0) holding the name (fills, trimmed) and, docked right, a small tag chip — a `Border` (`BorderThickness` 1, `CornerRadius` 3, `Padding` 5,0, `Margin` 8,0,0,0, `VerticalAlignment` Center, `Visibility` Collapsed unless the row has a tag) whose `BorderBrush` and inner `TextBlock.Foreground` use the `Accent` theme resource via `SetResourceReference`; the chip's text is `UsageReport.TagText(row.Exception)`. The row `Grid` gets a `ContextMenu` with one `MenuItem` whose `Header` is `UsageReport.MenuText(row.Exception)` and whose `IsEnabled` is `row.CanChange`; clicking it raises the row's `toggle(key)` callback (passed into the `RowView` constructor as the removed `assign` callback was). `Apply(row)` refreshes the tag, header and enabled state (setting an equal value is a no-op).
- Reordering must not happen under an open menu: `RowView.MenuOpen` (set from the menu's `Opened`/`Closed` events) joins the existing freeze condition in `Render` (`RowScroll.IsMouseOver || Rows.IsKeyboardFocusWithin || any MenuOpen`).
- A right-click must not be swallowed: leave the row `Grid`'s transparent background as it is (hit-testing).

`SmartRoutingWindow.xaml.cs`: forward it — `Usage.ExceptionToggleRequested += key => UsageExceptionToggleRequested?.Invoke(key);` and declare `public event Action<string>? UsageExceptionToggleRequested;`.

`App.xaml.cs`: in `RenderSmartWindowAsync` pass the profile to the report: `UsageReport.Build(usage, DateOnly.FromDateTime(DateTime.Now), settings.UsageRangeDays, _catalog, settings, canAssign, _usageSort, _usageFilter, status.Profile)`; in `OpenSmartRouting` wire

```csharp
        window.UsageExceptionToggleRequested += key =>
        {
            var profile = _smart?.Status.Profile ?? RouteExit.Phone;
            ChangeSmart(s => UsageExceptions.Toggle(_catalog, s, profile, key));
        };
```

- [ ] **Step 6: Build, test, render.**

Run: `dotnet build src/NetRoute.App -c Release -o "<scratch>\nrw-v4-build"` — Expected: 0 warnings, 0 errors. `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — all green, 0 warnings.
Render the Usage tab with the harness (own windows only; update its `Program.cs` for the new `UsageReportRow` shape): dark and light, with a few rows tagged "→ LAN" (Phone profile) and, in a second capture, "→ phone" (LAN profile); a long row name next to a tag at the window's minimum width. **Read every PNG**: the tag is readable in both themes, does not push the Phone/LAN columns, the long name truncates before the tag. State plainly what you could not verify (opening the real context menu, the click round trip).

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "feat(app): right-click a Usage row to send it to / exclude it from the exception list; exception rows carry a tag" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Manage the lists from JSON — a user rules file merged over the built-in catalog

Requested by the user after the plan was approved ("I see some hard-coded lists … can manage all those from json"). The built-in lists already live in `rules/builtin.json` (not in C#), but that file is in the install folder: it is overwritten by every install and awkward to edit. Add a file the user owns, `%AppData%\NetRouteWidget\rules.user.json`, with the same schema, merged over the built-in catalog at startup. Execute this task **after Task 9, before Task 7**.

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/RuleCatalog.cs`, `src/NetRoute.App/AppPaths.cs`, `src/NetRoute.App/App.xaml.cs`, `src/NetRoute.App/SmartRoutingWindow.xaml`, `src/NetRoute.App/SmartRoutingWindow.xaml.cs`
- Test: `tests/NetRoute.Core.Tests/RuleCatalogMergeTests.cs` (new)

**Interfaces:**
- Consumes: Task 1 `RuleItem.Exit/CarveOut`, `RuleCatalog.Parse`.
- Produces:
  - `RuleCatalog.Merge(IReadOnlyList<RuleItem> builtin, IReadOnlyList<RuleItem> user, IReadOnlySet<string> removed) → IReadOnlyList<RuleItem>`;
  - `RuleCatalog.ParseUser(string json) → UserCatalog` where `sealed record UserCatalog(IReadOnlyList<RuleItem> Items, IReadOnlySet<string> Removed)`;
  - `RuleCatalog.LoadMerged(string builtinPath, string userPath, Action<string> log) → IReadOnlyList<RuleItem>`;
  - `AppPaths.UserRulesFile`;
  - `SmartRoutingWindow.EditRulesFileRequested` (`event Action?`).

**Merge rules** (the user file uses exactly the schema of `builtin.json`: `groups` → `items`, with the same `exit` / `carveOut` / `defaultOn` fields):
- Items are matched by `id`. A user item whose id exists in the built-in catalog **replaces** it completely (name, group, processes, domains, exit, carveOut, defaultOn) and keeps its position in the list. A user item with a new id is **added** (at the end, in its group; a new group id creates a new group after the built-in ones).
- `{ "id": "xbox", "remove": true }` **removes** a built-in item (only `id` is required for such an item; naming an unknown id is ignored).
- A missing user file means no change. A user file that is malformed, has a bad `exit`, a phone carve-out or a duplicate id is **reported to the log and ignored as a whole** (the built-in catalog is used): the user's own file can never stop Smart routing from starting. A broken built-in catalog still fails as before.

- [ ] **Step 1: Write the failing tests** — `tests/NetRoute.Core.Tests/RuleCatalogMergeTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~RuleCatalogMergeTests"`
Expected: build FAIL — `ParseUser`, `Merge`, `LoadMerged`, `UserCatalog` do not exist.

- [ ] **Step 3: Implement the Core.** In `RuleCatalog.cs` generalise `Parse` into a shared reader and add the three members (read the current file first: Task 1 added `ExitOf` / `Flag` and the group/item inheritance — keep that logic):

```csharp
/// What a user rules file contributes: items to add or replace, and built-in ids to remove.
public sealed record UserCatalog(IReadOnlyList<RuleItem> Items, IReadOnlySet<string> Removed);
```

- `Parse(json)` becomes `ReadCatalog(json, allowRemove: false).Items` (unchanged behaviour and errors).
- `ParseUser(json)` = `ReadCatalog(json, allowRemove: true)`. In that mode an item with `"remove": true` needs only `"id"` (no `name`) and its id goes into `Removed` instead of `Items`; every other item is read exactly like a built-in item (same `exit` / `carveOut` / `defaultOn` inheritance and the same `InvalidDataException` for a bad exit, a phone carve-out, a duplicate id among the user's own items).
- Merge and load:

```csharp
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
```

- [ ] **Step 4: Run to verify the Core passes**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — Expected: all green, 0 warnings.

- [ ] **Step 5: App wiring and the "Edit rules file…" link.**

`src/NetRoute.App/AppPaths.cs`: add `public static readonly string UserRulesFile = Path.Combine(Root, "rules.user.json");`.
`src/NetRoute.App/App.xaml.cs`: where the catalog is loaded (`_catalog = RuleCatalog.Load(RuleCatalog.DefaultPath);` inside `SetUpSmartRouting`) use `RuleCatalog.LoadMerged(RuleCatalog.DefaultPath, AppPaths.UserRulesFile, log.Info)` (keep the existing try/catch for a broken built-in catalog). In `OpenSmartRouting` wire:

```csharp
        window.EditRulesFileRequested += () => OpenRulesFile();
```
and add

```csharp
    /// Opens the user's rules file in the default editor, creating a small template first. Changes apply after a restart.
    void OpenRulesFile()
    {
        try
        {
            if (!File.Exists(AppPaths.UserRulesFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.UserRulesFile)!);
                File.WriteAllText(AppPaths.UserRulesFile,
                    "{\n  \"version\": 1,\n  \"groups\": [\n    { \"id\": \"my-apps\", \"name\": \"My apps\", \"exit\": \"phone\", \"items\": [] }\n  ]\n}\n");
            }
            try
            {
                Process.Start(new ProcessStartInfo(AppPaths.UserRulesFile) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.UserRulesFile}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log?.Error("Could not open the rules file", ex);
        }
    }
```

`SmartRoutingWindow.xaml`: in the Config tab's bottom `StackPanel` (above "Clear usage history…") add `<TextBlock Margin="0,6,0,0"><Hyperlink Click="OnEditRulesFile">Edit rules file… (restart to apply)</Hyperlink></TextBlock>`; `SmartRoutingWindow.xaml.cs`: `public event Action? EditRulesFileRequested;` and `void OnEditRulesFile(object sender, RoutedEventArgs e) => EditRulesFileRequested?.Invoke();`.

- [ ] **Step 6: Build, test, render.**

Run: `dotnet build src/NetRoute.App -c Release -o "<scratch>\nrw-v4-build"` — Expected: 0 warnings, 0 errors. `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"` — all green, 0 warnings. Render the Config tab with the harness (own windows only) and confirm the link is visible and themed in dark and light. **Do not click it or launch the widget.** List what you could not verify (opening the real editor, the restart round trip).

- [ ] **Step 7: Docs and commit.** README: a short "Editing the lists" section — the built-in lists are in `rules\builtin.json` (overwritten by installs); your own changes go in `%AppData%\NetRouteWidget\rules.user.json`, same format, merged by item `id` (replace, add, `"remove": true`), a broken file is ignored with a log line, restart to apply, and the Config tab's "Edit rules file…" link creates a template and opens it. Spec: add the same paragraph under "Data model".

```bash
git add -A src tests README.md docs
git commit -m "feat: manage the lists from JSON — rules.user.json merged over the built-in catalog" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## After the last task (controller, not an implementer)

1. Whole-branch review on the most capable model with this plan's **Review Focus** section.
2. Stage a Release publish of HEAD in `<scratch>\nrw-v4-release`; with the user's approval close the running widget and start the new build elevated (UAC), exactly as for v3.
3. With the user, on the machine: (a) press **LAN** on the card, play/ask Claude, Codex, VS Code — they must show under **Phone** in the Usage tab while a browser download and Windows traffic show under **LAN**; (b) open `https://www.omantel.om` — through the phone; (c) trigger a VS Code update check / `claude` update — the Usage tab must show the `vscode-updates` / `claude-updates` rows under **LAN**; (d) press **Phone** again — YouTube etc. back on the LAN as before; (e) unplug the LAN in LAN mode — traffic heals onto the phone, AI tools stay on the phone, no waiting popup; (f) in the Config tab edit the *other* profile's list and confirm sing-box does not restart; (g) restart the widget — the profile (mode) and both lists survive. Fix the process names / domains in `rules\builtin.json` from what the Usage tab and the sing-box log show.
