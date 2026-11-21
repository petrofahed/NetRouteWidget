# Adapter Ledger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The card shows exact, always-on per-adapter byte totals (Phone, LAN, Wi-Fi) read from Windows' own counters, so they match what a tool like BitMeter sees; the Usage tab adds up to them.

**Architecture:** A new `AdapterLedger` (pure Core class, own state) turns once-a-second adapter readings into per-connection daily up/down totals. An `AdapterLedgerSampler` produces those readings (slot-to-adapter mapping plus one counter enumeration per tick). The app owns both, independent of Smart routing. Totals persist as an optional `adapters` field per day in `usage.json` (file `Version` stays 1). The Usage tab derives a "Not counted per app" row at report time.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF, xUnit 2.9.3. Core tests: `tests/NetRoute.Core.Tests`.

**Spec:** `docs/superpowers/specs/2026-11-21-netroute-adapter-ledger-design.md` (read it first, including "Calls made in review").

## Global Constraints

- Base: **tip of `codex/v6-2-smart-routing`** (it has `ConnectionId { Lan, Wifi, Phone }`, Wi-Fi card row, `AdapterDetector.Detect(adapters, overrides, turnedOff)`). Create the worktree by hand (Claude's `isolation: "worktree"` fails here on drive-letter case): `git worktree add ../NetRouteWidget-ledger -b feature/adapter-ledger codex/v6-2-smart-routing`. Open the PR against `codex/v6-2-smart-routing`; retarget to `main` once v6.2 merges.
- Never launch the app and never build into the default App output: the owner's installed widget runs elevated and locks it. Build the app only with `dotnet build src/NetRoute.App/NetRoute.App.csproj -o <scratch dir>`.
- `usage.json` `Version` **stays 1**; the new data is an optional per-day `adapters` property. An old file (no `adapters`) must load; the current reader must ignore nothing it needs.
- Count **only** the Phone, LAN and Wi-Fi slots' adapters. Never the NetRoute TUN, VMware, VPN or TAP adapters. A slot that is turned off in the widget still counts.
- Up = `BytesSent`, Down = `BytesReceived`. Day = local date of the reading. Retention = `UsageSnapshot.RetentionDays` (35).
- First reading of a slot, or a changed adapter name for a slot: baseline only, count **nothing**. Same-name reading lower than the last: reset, count the **new reading** as growth. Adapter absent this tick: keep the baseline, count nothing.
- No new setting, no administrator requirement, no save per second (existing 30 s save timer).
- Per-app rows, "kept off 4G" and routing are not changed. Wi-Fi bytes stay in the LAN bucket of the per-app rows ("two-bucket history until v6.3").

## Review Focus

Failure modes the spec implies that no obvious test covers; each has its test in the task named:

1. **Two slots resolve to the same adapter name** (a misdetection) must not count the bytes twice. (Task 3)
2. **Clear usage history while baselines exist** must zero the totals without re-adding the since-boot counter on the next tick. (Task 2)
3. **A damaged `adapters` field** (negative numbers, wrong types, bad `since`) must not discard the day's rows or fail the load. (Task 4)
4. **A reading that arrives after local midnight** goes to the new day and keeps the baseline; a clock stepped backwards never makes a negative or duplicate day. (Task 2)
5. **Per-day remainder:** a pre-ledger day in the range (rows, no ledger) must not make a later day's remainder wrong, and a day where rows exceed the ledger shows no negative remainder. (Task 7)

---

### Task 0: Identify the running build and the card-versus-file mismatch (time-boxed, first)

Time box: 1 hour. Purpose: the remainder row is "ledger minus per-app rows", so a real bug in `UsageCounter` or `TodayTotals` would make it wrong. Observation to explain: on 2026-11-21 17:32:33 the card showed 5.6 MB phone / 7.4 MB LAN while `%AppData%\NetRouteWidget\usage.json` held 28.66 MB / 22.5 MB, although the card total is `UsageCounter.TodayTotals` and the file is `UsageCounter.Snapshot`.

**Files:**
- Modify: `docs/superpowers/specs/2026-11-21-netroute-adapter-ledger-design.md` (append the result under "Open points")

- [ ] **Step 1: Identify the running build.** Ask the owner to run in an **elevated** PowerShell `(Get-Process NetRouteWidget).Path` and `(Get-Item <that path>).VersionInfo.ProductVersion`. The product version ends in `+<commit>`; match it with `git log` on `codex/v6-2-smart-routing` and `git worktree list`. Candidate builds live under `F:\source\petrofahed\pr-team-evidence\`.
- [ ] **Step 2: Diff that commit's card-total path against the branch tip.** `git diff <commit> codex/v6-2-smart-routing -- src/NetRoute.Core/SmartRouting/UsageCounter.cs src/NetRoute.Core/SmartRouting/SmartRoutingController.cs src/NetRoute.App/App.xaml.cs src/NetRoute.App/CardWindow.xaml.cs`; look at every place `_todayTotals` is assigned and when `RenderTotals` is called.
- [ ] **Step 3: Try to reproduce in a test.** Add `Today_totals_equal_the_sum_of_the_snapshot_rows` to `tests/NetRoute.Core.Tests/UsageCounterTests.cs`: drive a `UsageCounter` through `Update` (with a phone adapter delta that causes a take-back), `AddMeasured`, `NoteGap` and a day rollover, and after each step assert `TodayTotals(day).PhoneBytes` equals the sum of `Snapshot().Days[day].Rows` phone totals (same for LAN). Run `dotnet test tests/NetRoute.Core.Tests --filter FullyQualifiedName~UsageCounterTests`. Expected: PASS on the current code (they share the same cells).
- [ ] **Step 4: Record the result.** Append to the spec's Open points: which build was running, whether its card path differs, and whether a bug was found. If a bug is found in `UsageCounter`/`TodayTotals`, stop and report it to the owner as a separate fix to do before Task 4; do not fold it into this work. If nothing is found inside the time box, say so and continue.
- [ ] **Step 5: Commit** the spec note and the test: `git commit -m "test: pin today's totals to the snapshot rows; note the card/file mismatch result"`.

---

### Task 1: Per-direction counters for every adapter

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/IAdapterCounters.cs`
- Modify: `src/NetRoute.Core/Windows/WindowsAdapterCounters.cs`
- Modify: `tests/NetRoute.Core.Tests/SmartFakes.cs` (`FakeAdapterCounters`)
- Test: `tests/NetRoute.Core.Tests/WindowsAdapterCountersTests.cs`

**Interfaces:**
- Produces: `IReadOnlyDictionary<string, Traffic> IAdapterCounters.ReadAll()` — interface name (case-insensitive key) to `Traffic(Up: BytesSent, Down: BytesReceived)`, from **one** `NetworkInterface.GetAllNetworkInterfaces()` enumeration; throws `NetworkInformationException` if Windows cannot enumerate. `TotalBytes` stays unchanged.
- Produces: `FakeAdapterCounters.All` (`Dictionary<string, Traffic>`, case-insensitive) and `FakeAdapterCounters.ReadAll()` returning it (or throwing `Throws`).

- [ ] **Step 1: Write the failing test** in `WindowsAdapterCountersTests.cs`:

```csharp
[Fact]
public void ReadAll_reports_every_live_adapter_with_non_negative_directions()
{
    var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
        n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
    if (nic is null) return;

    var all = new WindowsAdapterCounters().ReadAll();

    Assert.True(all.TryGetValue(nic.Name.ToUpperInvariant(), out var traffic)); // key is case-insensitive
    Assert.True(traffic.Up >= 0 && traffic.Down >= 0);
}
```
- [ ] **Step 2: Run** `dotnet test tests/NetRoute.Core.Tests --filter FullyQualifiedName~WindowsAdapterCountersTests`. Expected: FAIL (no `ReadAll`).
- [ ] **Step 3: Implement** `ReadAll()` on the interface and `WindowsAdapterCounters` (one enumeration; `GetIPStatistics()` per interface; skip an interface whose statistics throw `NetworkInformationException`), and the fake's `All`/`ReadAll`.
- [ ] **Step 4: Run** the filter again. Expected: PASS. Run the whole Core test project to confirm the fake change broke nothing.
- [ ] **Step 5: Commit** `feat(core): read up/down byte counters for every adapter in one pass`.

---

### Task 2: `AdapterLedger`

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/AdapterLedger.cs`
- Test: `tests/NetRoute.Core.Tests/AdapterLedgerTests.cs`

**Interfaces:**
- Consumes: `Traffic(long Up, long Down)` (UsageData.cs), `ConnectionId`, `UsageSnapshot.RetentionDays`.
- Produces (all in `namespace NetRoute.Core`):
  - `public sealed record AdapterReading(string Name, Traffic Bytes);` — a slot's adapter name and its raw counters.
  - `public sealed record LedgerDay(IReadOnlyDictionary<ConnectionId, Traffic> Totals, TimeOnly? Since);` — a day's totals per connection; `Since` = local time of that day's first reading.
  - `public sealed class AdapterLedger`:
    - `AdapterLedger(IReadOnlyDictionary<DateOnly, LedgerDay>? restored, DateOnly today)`
    - `void Update(IReadOnlyDictionary<ConnectionId, AdapterReading> readings, DateTimeOffset now)` — slots absent from `readings` had no adapter this tick.
    - `Traffic Today(ConnectionId id)` — today's total (today = the day of the last `Update`, else the constructor's `today`).
    - `IReadOnlyDictionary<DateOnly, LedgerDay> Snapshot()` — immutable copy of all days (including a day that has only `Since`).
    - `bool TakeDirty()` — true once after any change.
    - `void Clear()` — zeroes every stored day's totals and drops `Since`, **keeps the baselines**, marks dirty.

- [ ] **Step 1: Write the failing tests** in `AdapterLedgerTests.cs` (helper `R(name, up, down)` builds an `AdapterReading`; `At(day, h, m, s)` builds a `DateTimeOffset`):

```csharp
[Fact] public void The_first_reading_counts_nothing_and_sets_since() {
    var l = new AdapterLedger(null, D1);
    l.Update(Map((ConnectionId.Phone, R("Ethernet 5", 100, 900))), At(D1, 8, 13, 0));
    Assert.Equal(default, l.Today(ConnectionId.Phone));
    Assert.Equal(new TimeOnly(8, 13, 0), l.Snapshot()[D1].Since);
}
[Fact] public void Growth_adds_up_and_down_separately() {
    // baseline (100, 900), then (150, 1400) -> Up 50, Down 500; then (160, 1400) -> Up 60, Down 500
}
[Fact] public void A_lower_reading_with_the_same_name_counts_the_new_reading() {
    // baseline (1000, 2000); then (10, 30) -> Today == Traffic(10, 30)
}
[Fact] public void A_changed_adapter_name_rebaselines_without_counting() {
    // Ethernet (1000,1000) then "Ethernet 7" (5_000_000_000, 5_000_000_000) -> Today == default; next tick +1 on up counts 1
}
[Fact] public void An_absent_slot_keeps_its_baseline() {
    // reading, tick with an empty map, reading +40 down -> Today.Down == 40 (not the whole counter, not 0)
}
[Fact] public void Each_connection_is_counted_independently() { /* Phone and Lan grow differently in one Update */ }
[Fact] public void A_reading_after_midnight_goes_to_the_new_day_and_keeps_the_baseline() {
    // baseline 23:59:59 on D1, +100 down at 00:00:01 on D2 -> D2 total Down 100, D1 total unchanged
}
[Fact] public void A_clock_stepped_back_never_makes_a_negative_total() {
    // update at D2 10:00 then D1 09:00 (earlier date): totals never decrease and no exception
}
[Fact] public void Clear_zeroes_the_totals_and_does_not_recount_the_boot_total() {  // Review Focus 2
    // baseline (1000,1000); +500 down -> 500; Clear(); next tick same counter -> Today == default; next tick +7 -> Down == 7
}
[Fact] public void Snapshot_and_restore_round_trip() { /* new AdapterLedger(l.Snapshot(), D1).Today(...) equals the original */ }
[Fact] public void Days_past_the_retention_are_dropped_from_the_snapshot() { /* restored day older than 35 days is gone */ }
[Fact] public void TakeDirty_is_true_once_after_a_change() { }
```
Fill the bodies with the exact numbers in the comments.
- [ ] **Step 2: Run** `dotnet test tests/NetRoute.Core.Tests --filter FullyQualifiedName~AdapterLedgerTests`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** `AdapterLedger.cs` with the types above. Internal state: per-slot last `(Name, Traffic)` baseline; per-day mutable totals and `Since`; apply the growth rules from Global Constraints; day = `DateOnly.FromDateTime(now.DateTime)`; prune on day change like `UsageCounter.Prune`. A step back in time is just another reading attributed to its own (earlier) day; it must not make any total negative.
- [ ] **Step 4: Run** the filter. Expected: all PASS.
- [ ] **Step 5: Commit** `feat(core): AdapterLedger counts exact per-adapter bytes per day`.

---

### Task 3: `AdapterLedgerSampler`

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/AdapterLedgerSampler.cs`
- Test: `tests/NetRoute.Core.Tests/AdapterLedgerSamplerTests.cs`

**Interfaces:**
- Consumes: `IAdapterSource.GetAdapters()` (Abstractions.cs), `AdapterDetector.Detect(adapters, overrides, turnedOff: null)` (all slots populated), `IAdapterCounters.ReadAll()` (Task 1), `AdapterReading` (Task 2), the existing test fakes for adapters (`TestAdapters.cs`).
- Produces: `public sealed class AdapterLedgerSampler`:
  - `AdapterLedgerSampler(IAdapterSource adapters, IAdapterCounters counters, Func<AdapterOverrides> overrides, Action<string> log, Func<DateTimeOffset> now)`
  - `public static readonly TimeSpan MappingRefresh = TimeSpan.FromSeconds(10);`
  - `IReadOnlyDictionary<ConnectionId, AdapterReading> Sample()` — a reading for every slot that has an adapter name and a counter row.
  - `void InvalidateMapping()` — the next `Sample` re-detects (the app calls it on network-change events).

- [ ] **Step 1: Write the failing tests** (fake `IAdapterSource` with settable adapters; `FakeAdapterCounters` from Task 1):

```csharp
[Fact] public void A_slot_with_an_adapter_gets_that_adapters_counters() { }          // Phone -> "Ethernet 5" counters
[Fact] public void A_turned_off_connection_is_still_sampled() { }                     // overrides/turned-off flags do not matter: Detect is called with turnedOff null
[Fact] public void The_mapping_is_redetected_only_after_the_refresh_interval_or_an_invalidate() { } // count GetAdapters calls with a controllable clock
[Fact] public void A_vanished_adapter_keeps_its_last_name_until_it_is_replaced() { }  // issue NotFound -> fall back to remembered name; counter row present -> still read
[Fact] public void A_not_used_slot_forgets_its_name() { }                             // overrides mark Wi-Fi Not used -> no reading even though a name was remembered
[Fact] public void Two_slots_that_resolve_to_one_adapter_name_are_counted_once() { }   // Review Focus 1: only one of the two slots appears in the result
[Fact] public void A_counter_failure_yields_no_readings_and_logs_once_per_streak() { } // ReadAll throws NetworkInformationException twice -> empty maps, one log line; recovery then failure logs again
[Fact] public void A_detection_failure_keeps_the_previous_mapping() { }
```
Fill the bodies; use the spec's names (Phone "Ethernet 5", Lan "Ethernet").
- [ ] **Step 2: Run** the filter `FullyQualifiedName~AdapterLedgerSamplerTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Mapping refresh at most every `MappingRefresh` or after `InvalidateMapping`; remember a name per slot when `Detect` returns an adapter; use the remembered name only when the slot's issue is `NotFound`; forget it on `NotUsed`/`Ambiguous`. If two slots resolve to the same name (case-insensitive), keep the first in `Connections.All` order. Log a failure once per consecutive-failure streak.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(core): sample each connection's adapter counters independent of Smart routing`.

---

### Task 4: Persist the ledger in `usage.json`

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/UsageData.cs` (`UsageDay`, `UsageSnapshot`)
- Modify: `src/NetRoute.Core/SmartRouting/UsageStore.cs`
- Test: `tests/NetRoute.Core.Tests/UsageStoreTests.cs`

**Interfaces:**
- Consumes: `LedgerDay` (Task 2).
- Produces:
  - `UsageDay` gains a fourth positional parameter: `LedgerDay? Ledger = null` (all existing constructor calls keep compiling).
  - `UsageSnapshot WithLedger(IReadOnlyDictionary<DateOnly, LedgerDay> ledger)` — returns a snapshot where each listed day carries that ledger; a day not in the snapshot is added with empty rows, `PhoneAdapterBytes` 0, `Gaps` 0. Days outside `ledger` are untouched.
  - `IReadOnlyDictionary<DateOnly, LedgerDay> UsageSnapshot.LedgerDays()` — the days that carry a ledger.
  - JSON: per-day optional `adapters`: `{"phone":{"up":N,"down":N},"lan":{…},"wifi":{…},"since":"HH:mm:ss"}`; absent = no ledger. `UsageStore.Version` stays 1.

- [ ] **Step 1: Write the failing tests** in `UsageStoreTests.cs`:

```csharp
[Fact] public void A_ledger_round_trips_through_the_file() { }                       // Phone (1,2), Lan (3,4), Wifi (5,6), Since 08:13:00 on D0
[Fact] public void A_day_with_only_ledger_data_is_saved() { }                        // no rows, PhoneAdapterBytes 0 -> still in the file
[Fact] public void A_file_without_adapters_still_loads_with_no_ledger() { }          // existing Sample() round trip: day.Ledger is null
[Fact] public void The_file_version_is_still_1() { }                                 // saved text contains "\"version\": 1" (match the existing style: parse JSON, read version)
[Fact] public void A_damaged_adapters_field_keeps_the_rows() { }                     // Review Focus 3: negative numbers clamp to 0, a non-object "adapters" or a bad "since" is ignored; rows and PhoneAdapter survive; Unreadable false
[Fact] public void Capping_a_runaway_day_keeps_its_ledger() { }                      // > MaxRowsPerDay rows -> CapRows folds into "other" and the Ledger is preserved
[Fact] public void WithLedger_adds_missing_days_and_leaves_other_days_alone() { }
```
- [ ] **Step 2: Run** `dotnet test tests/NetRoute.Core.Tests --filter FullyQualifiedName~UsageStoreTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Add an optional `AdaptersDto? Adapters = null` to the day DTO (check that a file without the property deserializes; if the optional record parameter is not honoured, add a settable property instead). `Save` skips a day only when it has no rows, no `PhoneAdapterBytes` **and** no `Ledger`. `Read` clamps each number to ≥ 0 and ignores an unparseable `since`; wrap the adapters read so a bad shape cannot throw out of `Load` (rows must still load). Change `CapRows` to `day with { Rows = rows }` so the ledger survives. `UsageSnapshot.Prune`/restore keep `Ledger`.
- [ ] **Step 4: Run** the filter, then the whole Core project. Expected: PASS.
- [ ] **Step 5: Commit** `feat(core): store the adapter ledger in usage.json without bumping its version`.

---

### Task 5: Own and run the ledger in the app

**Files:**
- Modify: `src/NetRoute.App/App.xaml.cs`

**Interfaces:**
- Consumes: `AdapterLedger` (Task 2), `AdapterLedgerSampler` (Task 3), `UsageSnapshot.WithLedger/LedgerDays` (Task 4), `WindowsAdapterSource`, `WindowsAdapterCounters`, `controller.Settings.Overrides`.
- Produces: fields `AdapterLedger? _ledger` and `AdapterLedgerSampler? _sampler`; a `DispatcherTimer _ledgerPoll` (1 s) that starts **whether or not `_smart` exists**; `SaveUsageAsync` that works with `_smart == null` and saves when the ledger **or** the usage counter is dirty.

Manual-verification task: the WPF app has no UI tests. Verification is the build plus the owner's checks in Task 9.

- [ ] **Step 1: Load once, before Smart is set up.** Move the `UsageStore.Load(...)` block (currently inside the Smart setup `try`) so it runs first and its result is kept in a field; `_usageUnreadable` is set from it as today. The Smart controller still receives `usage.Usage`. Create `_ledger = new AdapterLedger(usage.Usage.LedgerDays(), DateOnly.FromDateTime(DateTime.Now))` and the sampler (`new WindowsAdapterSource()`, `new WindowsAdapterCounters()`, `() => controller.Settings.Overrides`, `log.Info`, `() => DateTimeOffset.Now`).
- [ ] **Step 2: Poll.** `_ledgerPoll.Tick` calls `_ledger.Update(_sampler.Sample(), DateTimeOffset.Now)` inside a try/catch that logs (`log.Error("Ledger poll failed", ex)`) and never throws. Start it next to `_poll.Start()`, not inside the `if (_smart is ...)` block. Call `_sampler.InvalidateMapping()` from `OnNetworkEvent`.
- [ ] **Step 3: Save.** In `SaveUsageAsync`: take the usage snapshot from Smart when present (else `UsageSnapshot.Empty`), decide "changed" as `smartChanged || _ledger.TakeDirty()` (force or a previous failed save forces both), then `usage.WithLedger(_ledger.Snapshot())`, and keep the existing "nothing recorded yet: no file" and `_usageUnreadable` guards. A ledger-only save must work with `_smart == null`.
- [ ] **Step 4: Clear.** In the `ClearUsageRequested` handler, also call `_ledger.Clear()` (the handler currently returns early when `_smart` is null; clear the ledger in that case too) before the forced save. Update the confirmation text where the Clear button asks (search `Clear usage history`) to say the exact adapter totals are cleared as well.
- [ ] **Step 5: Build** `dotnet build src/NetRoute.App/NetRoute.App.csproj -o <scratch dir>`. Expected: build succeeds, 0 errors. Run the whole Core test project. Expected: PASS.
- [ ] **Step 6: Commit** `feat(app): run the adapter ledger independent of Smart routing and save it`.

---

### Task 6: Card totals come from the ledger

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/CardTotalsPresenter.cs`
- Modify: `src/NetRoute.App/CardWindow.xaml.cs` (`RenderTotals`)
- Modify: `src/NetRoute.App/App.xaml.cs` (`RenderTotals`, its call sites)
- Test: `tests/NetRoute.Core.Tests/CardTotalsPresenterTests.cs`

**Interfaces:**
- Consumes: `AdapterLedger.Today(ConnectionId)` (Task 2), `ByteFormat.Human`.
- Produces: `public static class CardTotalsPresenter { public static IReadOnlyDictionary<ConnectionId, string> Labels(Func<ConnectionId, Traffic> today); }` — for each of `Connections.All`, `ByteFormat.Human(total)` when `Up + Down > 0`, otherwise `""`. `CardWindow.RenderTotals(IReadOnlyDictionary<ConnectionId, string> labels)` replaces the two-argument version: a connection's `Total` cell shows its label only when it is in `_shown`, and `WifiTotal` is a real value now (no "—" placeholder).

- [ ] **Step 1: Write the failing tests:**

```csharp
[Fact] public void A_connection_with_bytes_gets_a_human_label() {
    var labels = CardTotalsPresenter.Labels(id => id == ConnectionId.Lan ? new Traffic(1_000_000, 2_000_000) : default);
    Assert.Equal(ByteFormat.Human(3_000_000), labels[ConnectionId.Lan]);
}
[Fact] public void A_connection_with_no_bytes_is_blank() { /* Phone and Wifi labels == "" */ }
[Fact] public void Every_connection_has_a_label_entry() { /* all three ids are keys */ }
[Fact] public void Wifi_bytes_show_on_the_wifi_label() { }
```
- [ ] **Step 2: Run** `--filter FullyQualifiedName~CardTotalsPresenterTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the presenter, the new `CardWindow.RenderTotals`, and change `App.RenderTotals` to use `CardTotalsPresenter.Labels(_ledger.Today)` with **no dependency on `_smart`**. Call it from the ledger tick (after `Update`) and from the existing call sites (startup, card shown). Leave `RenderLiveSpeeds` calling it only if still needed after this step; do not change speeds here.
- [ ] **Step 4: Run** the presenter tests and the whole Core project (PASS); build the app with `-o` (0 errors).
- [ ] **Step 5: Commit** `feat(app): the card shows exact adapter totals, including Wi-Fi`.

---

### Task 7: Usage tab: exact footer, derived "Not counted per app" row, note

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/UsageAttribution.cs` (new key + display name)
- Modify: `src/NetRoute.Core/SmartRouting/UsageExceptions.cs` (the key cannot be an exception)
- Modify: `src/NetRoute.Core/SmartRouting/UsageReport.cs`
- Modify: `src/NetRoute.App/UsageTab.xaml.cs` (footer, banner)
- Test: `tests/NetRoute.Core.Tests/UsageReportTests.cs`

**Interfaces:**
- Consumes: `UsageDay.Ledger` (Task 4), existing `UsageReport.Build`.
- Produces:
  - `UsageAttribution.NotCountedKey = "notcounted"`; `DisplayName("notcounted")` = `"Not counted per app"`.
  - `UsageReportModel` gains `IReadOnlyDictionary<ConnectionId, Traffic> LedgerTotals` (sum over the range, **only days that have a ledger**) and `DateTime? LedgerSince` (the first-ever ledger day plus its `Since`, only when that day lies inside the range and has a `Since`). Defaults empty/null so existing callers compile.
  - `UsageReport.AdapterFooter(UsageReportModel model) -> string`: with ledger data `"Exact adapter totals in this period (from Windows): Phone 98 MB · LAN 3.2 GB · Wi-Fi 0 B"` plus `" (counted since HH:mm on d MMM)"` when `LedgerSince` is set (use `CultureInfo.CurrentCulture`, same as the existing "Recording since" note); without ledger data the existing `"Phone adapter total in this period (exact, from Windows): …"` text.
  - `UsageReport.CountingNote` (const string): `"Adapter totals are exact while the widget runs. Per-app rows are counted only while Smart routing runs or this tab is open. The Now columns are per app and can differ from the card's speed."`
  - **Remainder row:** per **day that has a ledger**, per direction, `max(0, ledger − rows)`; Phone bucket = ledger Phone vs rows' Phone traffic, **LAN bucket = ledger Lan + ledger Wifi** vs rows' LAN traffic. Summed over the range and added as one `UsageReportRow` (key `NotCountedKey`, `CanChange` false, `Now` null) when its total > 0 **and** it passes the name filter like any row. Its bytes are included in `PhoneTotal`/`LanTotal`/up totals like any row. Never stored.

- [ ] **Step 1: Write the failing tests** (extend the existing `Snap` helper with an optional per-day ledger):

```csharp
[Fact] public void Remainder_is_ledger_minus_rows_per_exit_and_direction() { }          // ledger Phone (up 10, down 100), rows Phone (0, 60) -> remainder Phone up 10, down 40
[Fact] public void Lan_remainder_uses_lan_plus_wifi() { }                                // ledger Lan 100 + Wifi 50 vs rows Lan 120 -> remainder 30
[Fact] public void Rows_larger_than_the_ledger_give_no_negative_remainder() { }          // Review Focus 5: clamps at 0, row absent when both exits 0
[Fact] public void A_day_without_a_ledger_adds_no_remainder_and_does_not_distort_the_next_day() { } // Review Focus 5
[Fact] public void The_remainder_row_is_subject_to_the_name_filter() { }                 // filter "chrome" hides it; filter "counted" shows it
[Fact] public void The_remainder_row_cannot_become_an_exception() { }                    // Exception.CanChange == false
[Fact] public void The_totals_include_the_remainder() { }
[Fact] public void LedgerTotals_cover_only_days_with_a_ledger_in_the_range() { }
[Fact] public void LedgerSince_is_set_only_when_the_first_ledger_day_is_in_the_range() { }
[Fact] public void The_footer_lists_exact_totals_and_since_when_known() { }
[Fact] public void The_footer_falls_back_to_the_phone_adapter_text_without_a_ledger() { }
```
- [ ] **Step 2: Run** `--filter FullyQualifiedName~UsageReportTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the model changes, remainder computation inside `UsageReport.Build`, the footer and note, and in `UsageTab.xaml.cs` replace the `AdapterText` assignment with `UsageReport.AdapterFooter(model)` and add `UsageReport.CountingNote` to the banner list.
- [ ] **Step 4: Run** the filter, then the whole Core project (PASS); build the app with `-o` (0 errors).
- [ ] **Step 5: Commit** `feat: Usage tab shows exact adapter totals and a derived Not counted per app row`.

---

### Task 8: Live speed on the card from the ledger (droppable)

Drop this task if the owner declines it; Tasks 0-7 and 9 stand without it.

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/AdapterLedger.cs` (rates)
- Create: `src/NetRoute.Core/SmartRouting/CardSpeedPresenter.cs`
- Modify: `src/NetRoute.App/CardWindow.xaml.cs` (`RenderSpeeds`), `src/NetRoute.App/App.xaml.cs`
- Delete: `src/NetRoute.Core/SmartRouting/LiveSpeedDisplay.cs` and `tests/NetRoute.Core.Tests/LiveSpeedDisplayTests.cs` once nothing uses them
- Test: `tests/NetRoute.Core.Tests/AdapterLedgerTests.cs`, `tests/NetRoute.Core.Tests/CardSpeedPresenterTests.cs`

**Interfaces:**
- Produces: `IReadOnlyDictionary<ConnectionId, long> AdapterLedger.Rates` — bytes per second per connection (up + down) over `UsageCounter.RateWindow` with `UsageCounter.RateWindowMargin`, a connection below `UsageCounter.ActiveBytesPerSecond` reads 0; refreshed by every `Update`, never persisted. `CardSpeedPresenter.Labels(IReadOnlyDictionary<ConnectionId, long> rates)` → `IReadOnlyDictionary<ConnectionId, string>`: `UsageReport.NowText(rate)` when rate > 0, else `""`. `CardWindow.RenderSpeeds(IReadOnlyDictionary<ConnectionId, string> labels)` replaces the two-argument version (a connection's `Speed` cell shows its label only when it is in `_shown`; no Wi-Fi placeholder).

- [ ] **Step 1: Write the failing tests:**

```csharp
// AdapterLedgerTests
[Fact] public void Rates_are_the_bytes_over_the_last_three_seconds_per_connection() { }   // +3000 down over three 1 s ticks -> 1000 B/s... assert against the window rule, not wall time
[Fact] public void A_connection_below_the_active_threshold_reads_zero() { }
[Fact] public void Rates_fade_to_zero_when_ticks_stop_carrying_bytes() { }
[Fact] public void A_baseline_tick_adds_no_rate() { }
// CardSpeedPresenterTests
[Fact] public void A_busy_connection_shows_a_speed_and_an_idle_one_is_blank() { }
```
- [ ] **Step 2: Run** the two filters. Expected: FAIL.
- [ ] **Step 3: Implement** `Rates` in the ledger (queue of `(At, id, bytes)`, same in-window rule as `UsageCounter.InWindow`), the presenter, the new `RenderSpeeds`, and in `App`: call it from the ledger tick; remove the card's use of `smart.LiveSpeed`/`LiveSpeedDisplay` and delete `LiveSpeedDisplay` and its tests when unused. Leave `SmartRoutingController.LiveSpeed` itself alone. The Usage tab's per-app "Now" columns keep their sing-box source.
- [ ] **Step 4: Run** the filters and the whole Core project (PASS); build the app with `-o` (0 errors).
- [ ] **Step 5: Commit** `feat(app): card live speed comes from the adapter counters`.

---

### Task 9: Docs and the comparison script

**Files:**
- Create: `tools/Compare-AdapterLedger.ps1`
- Modify: `README.md` (rows/lines about the card, measurement limits, Usage tab)
- Modify: `docs/superpowers/specs/2026-11-21-netroute-adapter-ledger-design.md` (Status line)

**Interfaces:**
- Produces: `tools\Compare-AdapterLedger.ps1 -PhoneAdapter <name> -LanAdapter <name> [-WifiAdapter <name>] [-Seconds 90]` (read-only; works unelevated): (1) reads today's ledger totals from `%AppData%\NetRouteWidget\usage.json`; (2) over `-Seconds`, measures the growth of those adapters' Windows counters (`Get-NetAdapterStatistics`) and of the ledger in the file, and prints both per adapter with a PASS/FAIL (within 5 %, because the file is saved every 30 s; skip a connection whose adapter growth is under 100 KB); (3) if `C:\ProgramData\Bitmeter2\BitmeterHistory.xml` exists, prints BitMeter's day total (day buffer at `bufferPointer`) for context, without a verdict, with a note that BitMeter's adapter selection may differ.

- [ ] **Step 1: Write the script** with the parameters and output above; it must not write anything.
- [ ] **Step 2: Run it** on a machine with the widget running: `powershell -File tools\Compare-AdapterLedger.ps1 -PhoneAdapter 'Ethernet 5' -LanAdapter 'Ethernet'`. Expected: prints both figures per adapter and PASS for connections with traffic. (Owner-run after installing a build with Tasks 0-7; this machine's installed widget must not be replaced by the agent.)
- [ ] **Step 3: Update `README.md`:** the card row (`On the card`), the "Current measurement limits" paragraph and the Usage tab section now say that the card shows exact Windows adapter totals per connection including Wi-Fi, counted while the widget runs; the Usage tab has the "Not counted per app" row; per-app rows keep their rules; restarts lose the bytes while the widget is closed; mention the script. Set the spec's **Status** line to "Approved; implemented by `docs/superpowers/plans/2026-11-21-netroute-adapter-ledger.md`. Real-machine comparison with BitMeter still to be run by the owner."
- [ ] **Step 4: Run the whole Core test project** (`dotnet test tests/NetRoute.Core.Tests`) and build the app with `-o`. Expected: all PASS, 0 build errors.
- [ ] **Step 5: Commit** `docs: adapter ledger README, status and comparison script`.
