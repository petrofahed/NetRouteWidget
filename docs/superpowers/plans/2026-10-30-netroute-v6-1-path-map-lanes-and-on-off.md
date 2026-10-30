# NetRoute v6.1: path map lanes and the on/off switch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship v6.1 of the v6 design:
- The path map draws one lane per chosen connection (LAN, Wi-Fi, Phone), so Wi-Fi shows up on the map.
- Every card row's dot turns that connection off and on again.
- Choose adapters also has an Enabled on/off toggle beside each adapter. OK applies all choices and toggles together; Cancel or closing discards them. The card dots remain shortcuts.
- An explicitly off connection's compact-widget row and mode button are hidden. Its configured Choose adapters toggle remains available to turn it on again; chosen but disconnected connections remain visible.

**Architecture:**
- **Off** is a new `DetectionIssue.TurnedOff`. `AdapterDetector.Detect` takes the saved `TurnedOff` list and empties those slots. Everything that already skips a Not-used slot (metrics, probes, heal, pin, Smart routing, path map) then skips an off one for free. Only the card and tray tell the two apart.
- **Path map:** `PathMonitor`, `PathSnapshot`, `PathMapView` and `PathMapCanvas` move from fixed `Lan`/`Phone` fields to per-`ConnectionId` lanes. `Lan`/`Phone` stay as compatibility accessors so existing tests keep reading them.

**Tech Stack:** .NET 10 (`net10.0-windows`), WPF and WinForms (tray), xUnit with hand-written fakes.

**Spec:** `docs/superpowers/specs/2026-10-22-netroute-v6-wifi-connection-design.md`. Read the sections "Path map", "Turning a connection off (v6.1)" and "Phases".

## Global Constraints

- Branch: `feature/v6-3-path-map-lanes`. Commit after every task.
- **Tests.**
  - Unit tests: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`.
  - The whole unit suite stays green at the end of every task.
  - App build: `dotnet build src/NetRoute.App`.
- **Connection-keyed code** (as in v6.0):
  - No new positional `(lan, phone)` or `(phone, lan)` pairs.
  - A switch over `ConnectionId` is exhaustive and throws `ArgumentOutOfRangeException` otherwise.
- **Lane order:** `Connections.FallbackOrder` (LAN, Wi-Fi, Phone).
- **Lane ids:** `"lan"`, `"wifi"`, `"phone"`. Node ids keep the `"<lane id>:"` prefix.
- **Which lanes show:** one per connection with `status.Adapters.IsUsed(id)`. A used connection with no adapter keeps today's "not connected" lane. A Not-used or off connection has no lane.
- **Copy (exact strings).**
  - Lane captions, drawn as given (the canvas no longer upper-cases them):
    - `"LAN PATH · FROM 192.168.86.42"`
    - `"PHONE PATH · NOT CONNECTED"`
    - `"WI-FI PATH (pz-tecno) · FROM 192.168.1.4"`, or `"WI-FI PATH · FROM 192.168.1.4"` when the network name is unknown.
  - Lane names (`LaneView.Name`, Internet rows, This PC rows): `Connections.Label(id)`, so "LAN", "Wi-Fi" and "Phone".
  - First-hop title: `"Router"` for LAN and Wi-Fi, `"Phone"` for Phone.
  - Hop-1 note for LAN and Wi-Fi: `"Your router: the first device on the way out."`.
  - Diagnosis when every shown lane is healthy:
    - 2 lanes: `"Both connections reach the internet."`
    - 3 lanes: `"All connections reach the internet."`
    - 1 lane: that lane's own `Line(...)`.
    - 0 lanes: `"No connection is in use."`
  - Raw off-row metadata for the tray: address column `"off"`, latency `""`, `Dot.Gray`, not active. The compact card hides the row and mode button; Choose adapters restores it.
  - Dot tooltip: `"Turn LAN off"` / `"Turn LAN on"`, using `Connections.Label(id)`.
  - Mode refused for an off connection: `"LAN is turned off"`, using `Connections.Label(id)`.
  - Log lines: `"LAN turned off"`, `"LAN turned on"`.
- **Settings:** new property `TurnedOff` (`IReadOnlyList<ConnectionId>`, default empty, written as enum names). `CurrentVersion` stays 6: an older v6.0 build ignores the unknown property.
- **Version:** `6.1.0`. The spec's later phases are renumbered (Smart routing v6.2, usage v6.3, network picker v6.4). Code comments that name an old phase number are updated with them.

## Review Focus

1. **Turning off the connection the mode is on** (LAN off in LAN mode). Expected: the mode falls back as for Not used and the LAN metric goes back to automatic. Owned by Task 2.
2. **Windows still routes through an off adapter** (Auto mode, LAN off, cable still in). Expected: the header says "Internet via LAN", not "other adapter (VPN?)". `ResolvePath` must still recognise the off adapter. Owned by Task 1.
3. **A turned-off slot that is also set to Not used.** Expected: Not used wins and there is no row. Owned by Task 1.
4. **Lane order and count with Wi-Fi chosen** but disconnected. Expected: 3 lanes, and Wi-Fi reads "WI-FI PATH · NOT CONNECTED". Owned by Task 5.
5. **The Local network tab when the LAN is off or Not used.** Expected: no crash, and the tab treats the LAN as not connected. Owned by Task 6.

---

### Task 1: The off state in Core detection and the card view

**Files:**
- Modify: `src/NetRoute.Core/AdapterDetector.cs`, `src/NetRoute.Core/Settings.cs`, `src/NetRoute.Core/NetworkStatus.cs`, `src/NetRoute.Core/StatusPresenter.cs`
- Test: `tests/NetRoute.Core.Tests/AdapterDetectorTests.cs`, `StatusPresenterTests.cs`, `SettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `DetectionIssue.TurnedOff` (appended to the enum).
  - `AdapterDetector.Detect(IReadOnlyList<AdapterInfo> adapters, AdapterOverrides overrides, IReadOnlyCollection<ConnectionId>? turnedOff = null)`.
  - `DetectionResult.IsTurnedOff(ConnectionId id)`.
  - `DetectionResult.IsUsed(id)`, now false for `NotUsed` and `TurnedOff`.
  - `DetectionResult.OffAdapters` (`IReadOnlyDictionary<ConnectionId, AdapterInfo>`, init, default empty; included in `Equals`/`GetHashCode`).
  - `AppSettings.TurnedOff` (`IReadOnlyList<ConnectionId>`, default `[]`).
  - `AdapterRow.IsOff` (new trailing `bool IsOff = false`).

- [ ] **Step 1: Write the failing tests**
  - `AdapterDetectorTests.Turned_off_slot_has_no_adapter_and_is_not_used`: with LAN and phone present and `turnedOff: [Lan]`:
    - `Issue(Lan) == TurnedOff`, `Adapter(Lan) is null`, `!IsUsed(Lan)`, `IsTurnedOff(Lan)`.
    - `OffAdapters[Lan].Index` is the LAN's index.
    - The phone slot is unchanged.
  - `AdapterDetectorTests.Not_used_wins_over_turned_off`: `LanMac = "none"` with `turnedOff: [Lan]` gives `Issue(Lan) == NotUsed`, and `OffAdapters` is empty.
  - `StatusPresenterTests.Off_connection_keeps_its_row_reading_off`: LAN off gives a LAN row equal to `new AdapterRow("LAN", "off", "", Dot.Gray, false, IsOff: true)`.
  - `StatusPresenterTests.Windows_routing_through_an_off_adapter_is_still_named`: `NetworkStatus.ResolvePath(lanIndex, detection with LAN off)` returns `InternetPath.Lan`.
  - `SettingsStoreTests.TurnedOff_round_trips`: save then load `TurnedOff = [Lan, Wifi]` gives the same list. A file without the property loads as empty.

- [ ] **Step 2: Run them and see them fail** (compile errors count as failing)

Run: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`

- [ ] **Step 3: Implement**
  - `Detect` runs as today, then replaces each slot in `turnedOff` whose issue is not `NotUsed` with `(null, TurnedOff)`. The adapter it would have had goes into `OffAdapters`.
  - Phone, Wi-Fi and LAN candidate exclusion still runs on the full set, so turning the phone off never lets the phone adapter be detected as the LAN.
  - `ResolvePath` checks `Present` first, then `OffAdapters`.
  - `StatusPresenter.Present` builds rows for `IsUsed(id) || IsTurnedOff(id)`. An off row is the off row from Global Constraints.
  - In `SettingsStore.Load`, a null `TurnedOff` becomes `[]`, and duplicates are dropped.

- [ ] **Step 4: Run the unit suite.** Expected: all pass.

- [ ] **Step 5: Commit** `feat(v6.1): turned-off connections in detection, status and settings`

### Task 2: RouteController turns connections off and on

**Files:**
- Modify: `src/NetRoute.Core/RouteController.cs`
- Test: `tests/NetRoute.Core.Tests/RouteControllerTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `RouteController.SetTurnedOffAsync(ConnectionId id, bool off, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing tests**
  - `Turning_the_lan_off_resets_its_metric_and_saves`:
    - `Settings.TurnedOff` contains `Lan`.
    - The fake metrics show the LAN reset to automatic.
    - The log contains `"LAN turned off"`.
    - `Status.Adapters.IsTurnedOff(Lan)`.
  - `Turning_off_the_mode_connection_falls_back`: in `Mode = Lan`, turning the LAN off sets `Settings.Mode` to the first used connection in `FallbackOrder`, as `SetOverridesAsync` does today.
  - `Turning_it_back_on_restores_the_slot`: `SetTurnedOffAsync(Lan, false)` removes it from `TurnedOff`, the LAN is detected again, and the log says `"LAN turned on"`.
  - `Mode_change_to_an_off_connection_is_refused`: `SetModeAsync(RoutingMode.Lan)` while the LAN is off returns `ApplyResult(false, "LAN is turned off")` and leaves `Settings.Mode` unchanged.
  - `Without_admin_the_switch_is_saved_only`: with `canModify: false`, the setting is saved and no metric is touched.

- [ ] **Step 2: Run them and see them fail**

- [ ] **Step 3: Implement**
  - Move the body of `SetOverridesAsync` (gate, `previous`, `ResetHealing`, save, re-detect, mode fallback, reset loop, refresh) into one private `ChangeAdaptersAsync(Func<AppSettings, AppSettings> change, string logLine, CancellationToken ct)`. `SetOverridesAsync` and `SetTurnedOffAsync` both call it.
  - Every `AdapterDetector.Detect` call in the controller passes `Settings.TurnedOff`. The re-detect inside `ChangeAdaptersAsync` uses the updated settings.
  - `SetModeAsync` refuses before taking the gate when `Connections.Of(mode)` is in `Settings.TurnedOff`.

- [ ] **Step 4: Run the unit suite.** Expected: all pass.

- [ ] **Step 5: Commit** `feat(v6.1): turn a connection off and on from the controller`

### Task 3: The switch on the card and tray

**Files:**
- Modify: `src/NetRoute.App/CardWindow.xaml`, `CardWindow.xaml.cs`, `TrayIcon.cs`, `App.xaml.cs`

**Interfaces:**
- Consumes: `AdapterRow.IsOff`, `RouteController.SetTurnedOffAsync`.
- Produces: `CardWindow.TurnedOffRequested` (`event Action<ConnectionId, bool>`, where the bool is "turn off").

**Owner refinement (2026-10-30):** add an Enabled on/off toggle beside each adapter in `AdapterPickerWindow.xaml`/`.cs`, retaining card dots. Pass saved TurnedOff into the dialog; return staged overrides/off flags only after successful OK validation. Keep Not-used toggles disabled/off, start newly chosen slots enabled, preserve off flags for real-to-real changes and Detect, and retain duplicate-slot validation for disabled choices. `App.ChooseAdapters` calls `RouteController.SetAdapterChoicesAsync` once; it shares `ChangeAdaptersAsync` with the existing override/dot APIs so fallback and metric resets see the final state. Preserve `SetOverridesAsync` semantics. Constrain dialog height to the work area, scroll its adapter fields and keep action buttons reachable. Focused controller tests cover combined final-state fallback, replaced adapter metric reset, multi-slot normalization and non-admin no-write behavior. Owner checks staged OK/Cancel/close, Detect, keyboard and layout on the real widget.

**Later owner refinement (2026-10-30):** an off connection disappears from the compact widget, both row and mode button. Keep raw `CardView.Rows`/`IsOff` for unchanged tray behavior; `VisibleRows` and `IsModeButtonVisible` drive the actual card renderer. Hide only explicit off flags, preserving chosen/disconnected visibility and existing Not-used Phone/LAN button behavior. Collapse row heights and every cell, and mask independent speed/total updates by the shown-row set. Mode-grid columns count visible mode buttons plus Smart; all-off has no empty connection gaps. Choose adapters remains available to re-enable. Focused presenter tests cover all three off/on transitions and restored order, disconnected rows, all-off and unchanged raw/tray metadata.

- [ ] **Step 1: Implement**
  - Each row dot gets `Cursor="Hand"` and a `MouseLeftButtonDown` handler that sets `e.Handled = true`, so the row's drag does not start. The handler raises `TurnedOffRequested(id, !row.IsOff)`.
  - Give the dot a hit area of at least 16 × 16: a transparent `Border` around the ellipse is fine.
  - Tooltip text is from Global Constraints. The dot works without administrator rights too: the controller then only saves the setting, and its probes stop.
  - `Render` hides the row and mode button of an off connection. The tray keeps its mode item visible but disabled.
  - In `App.xaml.cs`, wire `_card.TurnedOffRequested += (id, off) => _ = controller.SetTurnedOffAsync(id, off);`.

- [ ] **Step 2: Build.** Run `dotnet build src/NetRoute.App`. Expected: 0 errors and no new warnings.

- [ ] **Step 3: Commit** `feat(v6.1): card dot turns a connection off and on`

### Task 4: PathMonitor watches one lane per connection

**Files:**
- Modify: `src/NetRoute.Core/PathMap/PathMonitor.cs`, `src/NetRoute.Core/PathMap/PathMapPresenter.cs` (only the `PathSnapshot` record)
- Test: `tests/NetRoute.Core.Tests/PathMonitorTests.cs`

**Interfaces:**
- Produces:
  - `PathMonitor.SetAdapters(IReadOnlyDictionary<ConnectionId, AdapterInfo?> adapters)`. A missing key means null.
  - The old `SetAdapters(AdapterInfo? lan, AdapterInfo? phone)` stays as a compatibility overload that sets Wi-Fi to null.
  - `PathSnapshot(DateTimeOffset At, IReadOnlyDictionary<ConnectionId, LaneSnapshot> Lanes, DateTimeOffset? LastSampleAt)`, with compatibility accessors `Lan` and `Phone`.
  - A second constructor `PathSnapshot(At, LaneSnapshot lan, LaneSnapshot phone, LastSampleAt)`, kept for the tests. It fills Wi-Fi with an empty "Wi-Fi" lane.
  - `LaneSnapshot.Name` is `Connections.Label(id)`.

- [ ] **Step 1: Write the failing tests**
  - `Wifi_lane_is_traced_and_checked_from_its_own_address`: with Wi-Fi at `192.168.1.4`, the tracer and the endpoint probe are called with that source. `Snapshot().Lanes[Wifi].Adapter` is the Wi-Fi adapter.
  - `A_lane_without_an_adapter_is_not_probed`: `SetAdapters` with only Phone gives no probe from the LAN or Wi-Fi sources.

- [ ] **Step 2: Run them and see them fail**

- [ ] **Step 3: Implement**
  - `_lan`/`_phone` become `Dictionary<ConnectionId, Lane>` built over `Connections.FallbackOrder`.
  - Every `new[] { _lan, _phone }` loop iterates its values.
  - `Snapshot()` builds the dictionary.

- [ ] **Step 4: Run the unit suite.** Expected: all pass.

- [ ] **Step 5: Commit** `feat(v6.1): path monitor keeps one lane per connection`

### Task 5: PathMapPresenter presents one lane per used connection

**Files:**
- Modify: `src/NetRoute.Core/PathMap/PathMapPresenter.cs`
- Test: `tests/NetRoute.Core.Tests/PathMapPresenterTests.cs`

**Interfaces:**
- Consumes: Task 4's `PathSnapshot.Lanes`, and `NetworkStatus.WifiNetwork`.
- Produces:
  - `LaneView` gains a leading `ConnectionId Id`.
  - `PathMapView` holds `IReadOnlyList<LaneView> Lanes` in place of `Lan`/`Phone`, plus:
    - `LaneView? Lane(ConnectionId id)`
    - compatibility accessors `Lan` and `Phone`, which never throw: a lane that is not shown reads as a not-connected placeholder.

- [ ] **Step 1: Write and adjust the tests**
  - Change the two caption asserts to `"LAN PATH · FROM 192.168.86.42"` and `"LAN PATH · NOT CONNECTED"`.
  - `Wifi_lane_appears_between_lan_and_phone_when_wifi_is_chosen`:
    - The status has Wi-Fi present and `WifiNetwork = "pz-tecno"`.
    - The lane ids are `[Lan, Wifi, Phone]`.
    - The Wi-Fi caption is `"WI-FI PATH (pz-tecno) · FROM 192.168.1.4"`.
    - Its first node's title is `"Router"`.
  - `Not_used_and_off_connections_get_no_lane`:
    - LAN `NotUsed` gives the ids `[Phone]`.
    - LAN `TurnedOff` gives the ids `[Phone]`.
  - `Chosen_but_disconnected_wifi_reads_not_connected`: Wi-Fi with issue `NotFound` gives the caption `"WI-FI PATH · NOT CONNECTED"`, and the lane count is 3.
  - `Three_healthy_lanes_say_all`: the first diagnosis line is `"All connections reach the internet."`.
  - `Wifi_router_detail_has_the_router_note`: the detail for `"wifi:1"` has the note `"Your router: the first device on the way out."`.

- [ ] **Step 2: Run them and see them fail**

- [ ] **Step 3: Implement**
  - `Present` maps `Connections.FallbackOrder.Where(status.Adapters.IsUsed)` through `Analyse`.
  - `Describe` and `DetailFor` take the list of analysed lanes.
  - Copy follows Global Constraints.

- [ ] **Step 4: Run the unit suite.** Expected: all pass.

- [ ] **Step 5: Commit** `feat(v6.1): path map presents a lane per chosen connection, Wi-Fi included`

### Task 6: The canvas draws any number of lanes; the app feeds all three

**Files:**
- Modify: `src/NetRoute.App/PathMapCanvas.cs`, `src/NetRoute.App/App.xaml.cs`

**Interfaces:**
- Consumes: Task 4 and Task 5.

- [ ] **Step 1: Implement the canvas**
  - Lane y positions start at 108. The spacing between lanes is 184 for up to 2 lanes and 150 for 3, so the 2-lane layout is unchanged.
  - `MidY` is the midpoint of the first and last lane, or 190 when there are none.
  - `Height` is the last lane's y + 88, with a minimum of 380.
  - The This PC box and the Internet box grow by one row per lane past 2: +18 for This PC, +22 for Internet.
  - They list one row per lane, labelled `"{lane.Name} · {adapter}"`, as today.
  - Captions are drawn exactly as given.
  - The signature loops over `view.Lanes`.

- [ ] **Step 2: Implement the app wiring**
  - `monitor.SetAdapters(Connections.All.ToDictionary(id => id, id => adapters.Adapter(id)))` at both call sites.
  - The Local network tab reads `view.Lane(ConnectionId.Lan)?.Internet.State ?? HopDisplay.Hidden`.

- [ ] **Step 3: Build and check by eye.** Run `dotnet build src/NetRoute.App`; expect 0 errors. Then run the app with Wi-Fi chosen and open the path map. Expected: three lanes in order LAN, Wi-Fi, Phone, with nothing overlapping. Turning the LAN off removes its lane within a few seconds.

- [ ] **Step 4: Commit** `feat(v6.1): path map window draws a lane per connection`

### Task 7: Docs, version and phase renumbering

**Files:**
- Modify: `README.md`, `src/NetRoute.App/NetRoute.App.csproj` (`<Version>6.1.0</Version>`), `src/NetRoute.App/CardWindow.xaml.cs` (v6.2 → v6.3 in the comments), `src/NetRoute.Core/SmartRouting/SmartRoutingController.cs` (v6.1 → v6.2 in the comments), and the spec status line.

- [ ] **Step 1: Edit**
  - README: add a features row and a Versions entry for v6.1 (Wi-Fi lane on the map, and the on/off dot with its limitation from the spec), and update the path-map section.
  - Mark the spec status "v6.1 implemented".

- [ ] **Step 2: Run the unit suite and the app build.** Expected: green, 0 errors.

- [ ] **Step 3: Commit** `docs(v6.1): README, version 6.1.0, spec status`
