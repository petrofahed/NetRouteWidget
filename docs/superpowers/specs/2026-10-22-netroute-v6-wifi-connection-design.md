# NetRoute Widget v6: Wi-Fi as a third connection — Design

**Date:** 2026-10-22
**Status:** Approved; v6.0 implemented and reviewed (Tasks 1-14). On 2026-10-30 the user moved the path map lanes forward and added a per-connection on/off switch: they ship as **v6.1**, and the later phases are renumbered (Smart routing v6.2, usage v6.3, network picker v6.4). v6.1 implemented (plan `2026-10-30-netroute-v6-1-path-map-lanes-and-on-off.md`). The cable-pull heal check on the real machine is still to be run by the user.
**Builds on:**
- `2026-10-02-netroute-widget-v1-design.md`: card, modes, metrics, auto-heal.
- `2026-10-02-netroute-v2-smart-routing-design.md`: sing-box, waiting list, popup.
- `2026-10-02-netroute-v3-usage-and-routing-design.md`: usage counting.
- `2026-10-03-netroute-v4-profiles-design.md`: "Phone + exceptions" and "LAN + exceptions".
- `2026-10-13-netroute-v5-path-map-design.md`: path map lanes.

Mockups that were agreed: the "Proposal: Wi-Fi as a third connection" row of the design canvas (screens 4, 5 and 6), <https://claude.ai/artifact/BQBmNNTaogukWi6zr7yE2m>.

## Goal

Make **Wi-Fi a full, equal third connection** next to Phone and LAN. Stop treating routing as "phone versus LAN". Every connection follows the same rules: it can be the main connection, it can be the target of an exception rule, and it is counted and drawn the same way.

## Why (what the user said)

- The home line is unstable. The cause is upstream of Google Wifi (the ISP router or the line), and it gets worse when many cameras and devices upload.
- The user wants to try a second path to the internet without giving up the cable:
  - **The LAN stays the LAN.** The Ethernet cable to Google Wifi keeps serving local devices (cameras, NAS, printer).
  - **The PC's Wi-Fi adapter becomes its own connection.**
    - Starting point: Wi-Fi joined to **pzhome** (Google Wifi).
    - Later: Wi-Fi joined to the ISP router's own network, so the PC reaches the ISP router directly, bypassing Google Wifi.
- "If Wi-Fi is enabled we should be able to route through it if wished. Routing will be a standard profile and a generic approach."
- Choose adapters must show **what is actually selected**, not a hidden "Automatic". A **Detect** button may fill the boxes, but the result is visible.
- The Wi-Fi row is named after the **network** ("pzhome"), not the adapter ("Wi-Fi 2"). Clicking it picks which network Wi-Fi joins.

## Decisions

From the conversation:

1. **Three connections: LAN, Wi-Fi, Phone.**
   - Each has its own widget row and its own mode button: **Phone | LAN | Wi-Fi | ⚡ Smart**.
   - The Wi-Fi row and button appear only when a Wi-Fi adapter is chosen.
2. **One profile per connection.** "Phone + exceptions", "LAN + exceptions" and "Wi-Fi + exceptions". In each, an exception rule can send an app to **any other connection**.
3. **Everything is counted and drawn per connection:**
   - live speed
   - data used
   - the Usage tab
   - the speed test
   - path map lanes
4. **Auto-heal falls back in a fixed order, LAN → Wi-Fi → Phone**, so mobile data is the last resort.
5. **Choose adapters shows concrete adapters plus a Detect button** (mockup screen 4).
6. **The Wi-Fi row shows the network name.** Clicking it opens a network list (mockup screen 6).

The designer's calls, open to change, are marked **(call)** where they appear below.

## Goals, non-goals, anti-goals

**Goals:**
- The behaviour above.
- The code is **generic over connections**: adding a fourth connection type later should be mechanical.

**Non-goals:**
- More than one adapter of the same kind (two Wi-Fi sticks).
- Typing Wi-Fi passwords inside NetRoute.
- Wi-Fi in the Local network tab.
- IPv6 changes beyond what v5.x already does.

**Anti-goals.** Each of these would make the feature a failure even if it "works":

- **Local devices stop going over the cable.** In any mode, traffic to the LAN's own subnet (cameras on 192.168.86.x) must keep using Ethernet, even when Wi-Fi sits on the same subnet. See "Local traffic stays on the cable".
- **A connection silently goes to the wrong exit.** Today, adding an enum value compiles fine and then falls through to "phone" or "lan" in about a dozen places (the inventory is in "Generic model"). Wi-Fi traffic booked as phone data, or a Wi-Fi rule sent over 4G, is the main risk.
- **Settings or user rules are lost.** That includes losing them on a downgrade to v5.3.

## Generic model

### `ConnectionId`

Replace the two-way types with one connection identity:

- `enum ConnectionId { Lan, Wifi, Phone }`.
- `RouteExit` is removed, or kept as an alias of `ConnectionId`.
- `RoutingMode` becomes `{ Lan, Wifi, Phone, Auto }`. Auto stays "Windows default".
- `Connections.FallbackOrder = [Lan, Wifi, Phone]` drives healing and the graded backup metrics.
- `Connections.CardOrder = [Phone, Lan, Wifi]` drives the card rows, mode buttons, tray items and Usage-tab column groups. It keeps today's order and appends Wi-Fi, as in mockup screen 5.
- Path map lanes keep LAN on top: LAN, Wi-Fi, Phone.
- Nothing else hard-codes an order.
- **Phone is the only metered connection.** Use `Connections.IsMetered(id)` wherever the code says "mobile data" or "4G".

### Rules for the code

1. **Per-connection data lives in collections keyed by `ConnectionId`**, for example `IReadOnlyDictionary<ConnectionId, T>` or a small `PerConnection<T>`.
   - No positional `(phone, lan)` or `(lan, phone)` pairs.
   - Watch for the existing order trap: `PathMonitor.SetAdapters(lan, phone)` versus `SmartRoutingController`'s `_traffic.SetAdapters(phone, lan)`.
2. **No binary fall-through over connection ids.**
   - No `x == Lan ? … : …`, and no `_ =>` arm that maps an unknown connection to Phone or LAN.
   - A switch over `ConnectionId` must be exhaustive and throw on anything else.
3. **The two-way spots to generalise.** These come from the inventory:
   - **Core types:** `MetricTarget`, `MetricPolicy`, `RoutingEngine.Targets`, `NetworkStatus` (per-connection latency, `ResolvePath`, `IsFallback`), `DetectionResult`/`AdapterOverrides`, `InternetPath`.
   - **RouteController:** `_phoneMisses`/`_lanMisses`, `MeasureAsync`, `EffectiveMode`, `UpdateHealing`, `SetOverridesAsync`.
   - **Smart routing:**
     - `SingBoxConfigBuilder`: tags, selectors, `ip_is_private`, `lan-dns`.
     - `SmartRoutingController`: `Tag`, `ProfileOf`, `WantedExit`, `Desired`, `KeyFor`, the LAN-outage state, `PhoneAdapterBytes`.
     - `RuleSet` and `UserRule`.
     - `SmartRoutingPresenter`, `UsageExceptions`, `UsageAttribution` (the "phone-" id prefix).
   - **Usage:** `UsageCounter` (`Update` L102, `Add`, `AddMeasured`, `RebuildRates`, `RebuildLive`), `UsageData`/`UsageReport` records and sort columns, `UsageReconciler`, `TrafficClassifier`, `WindowsTrafficTracker`, `SpeedTest`.
   - **Path map:** `PathMonitor`, `PathSnapshot`, `PathMapView`, `PathMapPresenter` (the "lan:"/"phone:" id prefixes), `PathMapCanvas` (fixed two-lane geometry).
   - **UI:** `StatusPresenter`, `CardWindow`, `TrayIcon`, `AdapterPickerWindow`, `SmartRoutingWindow`, `UsageTab`, `WaitingPopup`.
   - **App wiring:** `App.xaml.cs` (`ExternalPathResolver`, the `shown == Lan` checks, `RenderLiveSpeeds`, `RenderTotals`).

## Choose adapters (mockup screen 4)

**Layout.** Three labelled sections:
- **Phone**
- **LAN (cable)**
- **Wi-Fi**, which has two boxes: the adapter and the network.

Each box shows a **concrete adapter**. There is no "Automatic" entry. Under each box, one line says what it is right now: address, gateway, plus "USB tethering", link speed, or band and signal.

**Wi-Fi network box.**
- Shows the network the adapter is joined to.
- Changing it joins that network. This is the same action as the widget's picker.
- An adapter that is down or not joined shows "not connected".
- With no wireless adapter present, the Wi-Fi section reads "No Wi-Fi adapter".

**Detect automatically.** The button fills the boxes once. The user sees the result and can change any box.
- **Phone:** the USB-tethering driver, as today.
- **LAN:** a wired adapter with a gateway that is not the phone, as today.
- **Wi-Fi:** the only non-virtual wireless adapter. With several, it leaves the box unchanged and says "several Wi-Fi adapters, pick one".

**First open (call).** If settings hold no explicit choice for a slot, that box is pre-filled with what Detect finds. OK then saves concrete values.

**After saving (call).** NetRoute uses exactly the saved adapters. A saved adapter that is not present shows as "(not connected)" in the dialog, and its row on the card reads "not connected". This is today's behaviour for a saved choice.

**"Not used" (call).** Every box has a **Not used** entry, stored explicitly. A slot set to Not used gets no row, no button, no metric changes and no probes.

**Defaults when nothing is saved:**
- **Phone and LAN (call).** They keep today's behaviour: nothing saved means detect.
- **Wi-Fi (call).** Nothing saved means **Not used**. The Wi-Fi row and button appear only after the user picks an adapter, either by hand or with Detect followed by OK. That matches "if Wi-Fi is enabled". Upgrading alone never changes which buttons show or how Wi-Fi is routed.
- **Detect finds nothing.** The box shows Not used.

**On OK,** any adapter that stops being one of the three is reset to the automatic metric. That's today's `SetOverridesAsync` behaviour, generalised to all slots.

**Identity:**
- The phone is matched by description, as today, because tethering MACs can be random.
- LAN and Wi-Fi are matched by MAC.
- An adapter can fill only one slot.

## Widget (mockup screens 5 and 6)

**Rows.**
- One row per chosen connection, in `CardOrder` (Phone, LAN, Wi-Fi), as in the mockup.
- The Wi-Fi row's name is the **network name**, with a Wi-Fi glyph and a chevron. Address, live speed, latency and today's total are the same columns as the other rows.

**Network name source,** in order of preference:
1. The connected SSID.
2. The Windows connection profile name for that adapter (Network List Manager or `MSFT_NetConnectionProfile`). It is usually equal to the SSID, needs no Location permission, and is the fallback when Windows denies Wi-Fi access.
3. "Wi-Fi" when the adapter is not joined.

**Buttons.** A `UniformGrid` with one column per shown connection plus Smart: four with Wi-Fi, three without.

**Header and tray.**
- The header reads "Internet via Wi-Fi" in Wi-Fi mode, and "Internet via Wi-Fi (LAN lost internet)" when healing onto it. Healing text and toasts follow the same templates as today, built from connection names, with no hard-coded pairs.
- The tray menu gets a "Wi-Fi" item.
- Tray colours: Phone green, LAN blue, **Wi-Fi purple (call)**, Auto grey.

**Network picker (screen 6).** Clicking the Wi-Fi name opens a list of visible networks: name, signal, and "Connected", "Saved" or "Not saved".
- Picking a **saved** network joins it with that Windows profile.
- Picking an **unsaved** network opens Windows Wi-Fi settings (`ms-settings:network-wifi`). NetRoute never asks for passwords.
- The footer has "Disconnect Wi-Fi" and "Open Windows Wi-Fi settings".
- The list shows "Scanning…" while it fills. A fresh scan takes a few seconds.

**Location permission (Windows 11 24H2 and later).** Microsoft documents that `WlanScan`, `WlanGetAvailableNetworkList` and `WlanQueryInterface(current_connection)` return `ERROR_ACCESS_DENIED` until the user allows Location for the app. The first call shows a one-time system prompt.
- The picker opens with an access check (`AppCapability.CheckAccess("wiFiControl")` or the access error itself).
- When access is denied, the picker shows one line: "Windows hides nearby Wi-Fi networks until Location is allowed for NetRoute". A button opens `ms-settings:privacy-location`.
- "Disconnect" and "Open Windows Wi-Fi settings" still work.
- The row label falls back to the profile name.
- The user is on 23H2 today, where the calls work, but the design must not depend on that.

## Routing modes and metrics

**Interface metrics.**
- **Main connection:** metric 5.
- **The others:** graded backups in `FallbackOrder`, skipping the main. The first gets 50, the second 60 **(call)**.
- So Wi-Fi mode gives Wi-Fi 5, LAN 50, Phone 60. Phone mode gives Phone 5, LAN 50, Wi-Fi 60.
- No routes are deleted, as today.

**Auto mode.** Unchanged: every managed adapter goes back to the automatic metric.

### Auto-heal

Healing works on an ordered list instead of a single pair.

- **When the main connection fails** (same 60 s rule, thresholds and backoff as today), heal to the **first connection in `FallbackOrder`**, skipping the main, whose own probe is currently answering. If none is answering, there is no heal.
- **When the heal target fails,** move to the next answering connection in order. If none is left, end the heal and restore the main connection's metrics. Today a dying backup simply ends the heal; this generalises that.
- **Stepping down (call).** When the heal target fails, the move to the next answering connection is **immediate**, as today's "backup died" rule is. It does not wait for the 60 s rule.
- **Stepping back up (call).** While the main is still down, a **higher-priority** connection that answers 3 checks in a row takes over from the current target, for example Wi-Fi taking back from the Phone. This keeps mobile data the last resort. It is the same 3-check count used to revert today.
- **Backoff (call).** Moves inside one heal do not count towards the heal backoff or the sticky heal. Only entering a heal does.
- **Recovery** (going back to the main) and sticky-heal behaviour are otherwise unchanged.
- **Known limit.** LAN and Wi-Fi currently share Google Wifi and the ISP router, so they usually fail together, and healing then reaches the Phone. That is correct behaviour. It improves once Wi-Fi is on the ISP router directly.
- **Probing.** `MeasureAsync` probes every chosen connection in parallel. The probe is already per source address. "no reply" smoothing (2 misses) is kept per connection.

### Local traffic stays on the cable

This is a hard requirement.

**The problem.** With Wi-Fi on pzhome, Ethernet and Wi-Fi are both on 192.168.86.0/24. An on-link subnet route follows interface metric, so in Wi-Fi mode (Wi-Fi at 5) traffic to the cameras would leave over Wi-Fi.

**Requirement.** In **every** mode, with Smart routing on or off, traffic to the LAN adapter's own IPv4 subnet uses the LAN adapter whenever the LAN is connected. "Every mode" means Phone, Wi-Fi, LAN, and healing. Auto is excluded: it means hands-off.

**Smart routing never sees local traffic.** `SingBoxConfigBuilder.ExcludedRanges` (10/8, 172.16/12, 192.168/16 and others) goes to sing-box as `route_exclude_address`. Camera traffic therefore never enters the TUN, and Windows' own routes decide it **whether Smart is on or off**. The `ip_is_private` rule never sees it. The route pin below is therefore the **only** mechanism, in every Smart state.

**Router-local DNS.** The `lan-dns` detour (names like `*.lan`, `*.home`) keeps going to the LAN's DNS server over the LAN, as today.

**The pin.** Interface metrics alone cannot split internet traffic from local traffic, so the builder adds a **route-level pin**. Two ways to do it:
- lower the metric of the LAN's on-link subnet route; or
- raise the metric of the non-main default routes.

Either way, the pin must meet all of these:
- **Applied only when needed:** another connection shares the LAN's subnet and is preferred by metric.
- **Checked on every refresh and re-applied,** as metrics are today, so it survives DHCP renewals and reconnects.
- **Kept in `ActiveStore`,** never persisted.
- **Reverted** on mode change to Auto, on quit, and by `Restore-Network.cmd`. The script needs a new step for this.
- **Spiked first:** a short spike on the user's machine (Ethernet and Wi-Fi both on pzhome) proves the chosen route change makes `Find-NetRoute -RemoteIPAddress 192.168.86.x` pick Ethernet while `1.1.1.1` picks Wi-Fi. Run it **with sing-box running and without**. That result decides the mechanism. If neither works, stop and come back to the user before building Phase 1's routing part.

**Wi-Fi on a different subnet** (the later ISP-router setup, 192.168.1.x). There is no overlap, so no pin is needed, and Windows' direct routes already keep each subnet on its own adapter.

## Smart routing

**Profiles.** One per connection: "Phone + exceptions", "LAN + exceptions", "Wi-Fi + exceptions". The card's buttons select the profile, as today. The active profile's main connection takes everything except its exceptions.

**sing-box.**
- One `direct` outbound per chosen connection, bound to its adapter, tagged by connection id (`lan`, `wifi`, `phone`).
- One `only-<id>` selector per connection, listing `[<id>, <main>]`. Rules point at `only-<id>`, which waits or falls back.
- One `default` selector over all connections.
- The config key that restarts sing-box includes every chosen adapter's name.

**Generic principle (call): never spill onto mobile data automatically.**
- A rule whose target is a non-metered connection (LAN or Wi-Fi) falls back automatically to the **next answering non-metered connection in `FallbackOrder`**. When none answers, it waits.
- It reaches the Phone only through the user's "Use <x> until <target> is back" button.
- A rule whose target is the Phone falls back to the profile's current internet path.

**Selectors.**
- `only-<id>` lists `[<id>, the other non-metered connections in FallbackOrder]`. The phone is added only while the user's button is active.
- `only-phone` lists `[phone, <current path>]`.
- This replaces today's two-entry `lan-only`, and it covers "LAN profile healed onto Wi-Fi": LAN-only items then use Wi-Fi, not the phone.

**"Home" target (call).** Built-in items with exit `lan`, and user rules with no `Exit` in `UserRules`, mean **home**. Home is the first answering of LAN, then Wi-Fi. It waits only when neither answers. So with only Wi-Fi chosen, these rules use Wi-Fi.

**Every profile includes the home items and the rules with non-metered targets (call).** Today `RuleSet.Build` loads the Phone profile's `UserRules` and the home items into the LAN profile so they stay off the phone during a heal. v6 does the same in **every** profile:
- **Main is LAN or Wi-Fi:** these rules ride the main.
- **During a heal onto the phone:** they wait, or use the other home connection.

**Built-in catalog.** It keeps its two exits: `lan` means "a home connection", `phone` means the phone. "wifi" stays invalid in `builtin.json` and `rules.user.json`, and the two existing rejection tests stay. Per profile:

| Built-in group | Catalog exit | Phone profile | LAN profile | Wi-Fi profile |
|---|---|---|---|---|
| updates, sync, video, games | lan (home) | → home: LAN, else Wi-Fi | main; home-only during a heal | main; home-only during a heal |
| ai-tools, isp | phone | main | → Phone (as today) | → Phone |
| ai-updates (carve-out) | lan (home) | → home: LAN, else Wi-Fi | → home, beats ai-tools (as today) | → home (Wi-Fi first, as the main), beats ai-tools |

"Home-only during a heal" means: when the profile's main is healed onto the phone, these items use the other home connection or wait. They never use the phone automatically.

**User rules.**
- Each profile has its own list:
  - `UserRules` is the Phone profile's list, as today.
  - `PhoneUserRules` is the LAN profile's list, as today.
  - New: `WifiUserRules`, the Wi-Fi profile's list.
- `UserRule` gains an optional **`Exit`** (`ConnectionId?`). Null means the list's implied target from today:
  - LAN for `UserRules`.
  - Phone for `PhoneUserRules`.
  - **Phone for `WifiUserRules` (call)**, so the add-rule default mirrors "LAN + exceptions".
- The rule editor and the Usage tab's "Send to…" offer every connection other than the profile's main.
- Rule ids keep today's scheme (`IdOf`, `phone-…`) and add `wifi-…` for the new list. Usage attribution strips every prefix, so an app stays one row.

**Waiting when a target is offline.** This generalises the LAN-outage machinery.
- When a rule's target connection is not answering, its traffic waits, as LAN-only traffic does today.
- The popup and footer name the target: "N apps waiting for Wi-Fi".
- The button reads "Use <main> until <target> is back". It adds "(uses mobile data)" only when the main is metered.
- Three healthy checks bring it back, and the "<target> back" toast fires, as today.

**Phase 1 behaviour (call).** Until Smart routing learns Wi-Fi (v6.2), Smart routing **pauses whenever Wi-Fi is the connection actually carrying internet**, as it does in Auto. That covers three cases:
- Wi-Fi mode.
- Healing onto Wi-Fi.
- The main connection unplugged, so Windows falls to Wi-Fi by metric.

Without this, sing-box would keep sending unmatched traffic to the phone (mobile data) or to a dead LAN. The card footer says "Smart routing paused while on Wi-Fi". Smart resumes when the path leaves Wi-Fi.

## Usage, live speed, speed test

**Per-connection cells.** Up and down per connection, plus "kept". `usage.json` stays **version 1** and gains a `"wifi": {up, down}` cell per row. v5.3 ignores unknown properties, so the file stays readable both ways.

**The phone figure stays exact.** It is reconciled against the phone adapter's Windows counter, as today. Bytes that are not on the phone and not attributed are split between LAN and Wi-Fi; how is **left to the builder**, under two constraints:
1. The phone number is exact.
2. Per-row and total figures add up.

**Kept off 4G.** Bytes that exception rules put on any **non-metered** connection while the Phone is the main.

**Classifier.** `TrafficClassifier` and `WindowsTrafficTracker` take one address set per connection. LAN and Wi-Fi have different local IPs even on the same subnet, so classification still works.

**Usage tab.** A column group per connection: up, down, total, now. The Wi-Fi group is shown when a Wi-Fi adapter is chosen or the period has Wi-Fi data. Sort columns are added to match.

**Speed test.** Runs once per connected connection. Only the Phone carries the mobile-data note.

**Live speed and totals.** The card shows them on every row, Wi-Fi included.

**Before v6.3 (call).** v6.0 to v6.2 ship with the Wi-Fi row's live speed and today's total shown as "—". Address and latency work from v6.0. Until v6.3, Wi-Fi bytes the counter sees are booked in the LAN share, which is today's behaviour for unknown non-phone traffic. The phone figure stays exact throughout.

## Path map

**Lanes.**
- One lane per chosen connection, in the order LAN, Wi-Fi, Phone.
- Canvas height grows with the lane count, so the fixed `LanY`/`PhoneY` geometry goes.
- The "This PC" box and the Internet box list one row per connection.
- The Wi-Fi lane header reads "WI-FI PATH (pzhome) · FROM <ip>".
- The first hop is titled "Router" for LAN and Wi-Fi.
- Node ids use the connection id as prefix.

**Diagnosis.** Diagnosis text is built per lane. "Both connections reach the internet" becomes a count, or "All connections reach the internet".

**Local network tab.** Unchanged, LAN only.

## Turning a connection off (v6.1)

**Why (what the user said, 2026-10-30).** "I want to do some tests on the Wi-Fi, and sometimes I need also a feature to enable/disable, let's say, the LAN." The user picked a quick switch on the card over disconnecting the adapter in Windows, knowing that Windows can still use the cable.

**What it is.** Each card row's coloured dot is a switch. Clicking it turns that connection **off**; clicking again turns it back **on**. The tooltip says "Turn LAN off" or "Turn LAN on" (with the connection's label).

**While a connection is off (call: it is "Not used" for everything except the card row):**
- NetRoute treats it like Not used: no metric changes, no latency probes, no path-map lane, never a heal or backup target, no route pin.
- Its adapter is reset to the automatic metric when it is turned off, as when a slot becomes Not used.
- If the saved mode points at it, the mode falls back exactly as for Not used (first still-used connection in `FallbackOrder`, else Auto), and the log says so.
- The card **keeps its row**: grey dot, the usual label, the address column reads "off", no latency, normal weight. Its mode button and tray item stay visible but disabled.
- A mode change to an off connection is refused: "LAN is turned off".
- The adapter choice in Choose adapters is untouched, so turning it on again needs no re-picking. A slot set to Not used has no row, so it cannot be turned off.
- The setting is saved (`TurnedOff`, a list of connection ids) and survives a restart. Without administrator rights it is saved and takes effect like an adapter choice does.

**What off does not do.** It does not disable the adapter in Windows. With the cable still in, Windows can still send local traffic over it, and if every NetRoute-managed connection fails Windows may fall to it by its automatic metric.


## Settings and migration

**New and changed fields.**
- `Mode` may be `"Wifi"`.
- New fields: `WifiOverrideMac`, `SmartRouting.WifiUserRules`, and an optional `Exit` on user rules.
- Existing fields keep their names, so `RestoreScriptTests` and the restore script keep working.

**Backup before the first v6 save.** v5.3 treats an unknown enum value as a corrupt file and overwrites it with defaults, so a downgrade would lose the user's rules. Before v6 first writes `settings.json`, it copies it once to `settings.v5-backup.json`, and only if no such backup exists. The README's downgrade note points to it.

**An existing LAN choice that is wireless.** The v5.3 picker allowed this, and the user may be set up this way right now. If `LanOverrideMac` (or `SmartRouting.LastLanInterface`) names a wireless adapter, v6 migrates on first load so that **traffic keeps going where it went (call)**:
- The MAC moves to `WifiOverrideMac`, and the LAN choice is cleared, so the LAN goes back to detection.
- `Mode = Lan` becomes `Mode = Wifi`.
- The "LAN + exceptions" list (`PhoneUserRules`) is **copied** to `WifiUserRules`. It is kept in place too, so nothing is lost.

**`Restore-Network.cmd`.** Already resets every adapter with a manual metric, Wi-Fi included. It gains the route-pin reset, and keeps setting `Mode='Auto'` and turning Smart off.

## Phases

Each phase ships on its own, and the plan may stop at any boundary.

1. **v6.0: generic model and Wi-Fi as a routing mode.**
   - `ConnectionId` everywhere in Core routing.
   - Metrics and ordered auto-heal.
   - The local-traffic pin, after its spike.
   - Choose adapters (screen 4).
   - The widget row and button (screen 5), with the name taken from the connection profile.
   - The tray item.
   - Settings migration and backup.
   - Smart routing pauses in Wi-Fi mode.
2. **v6.1: path map lanes and the on/off switch** (moved forward on 2026-10-30; see "Path map" and "Turning a connection off").
3. **v6.2: Smart routing over three connections.**
   - Outbounds and selectors.
   - The catalog table.
   - Per-profile rule lists with `Exit`.
   - Generic waiting.
   - The Config tab.
4. **v6.3: usage per connection.** Counter, store, reconciler, Usage tab, live speed, speed test.
5. **v6.4: network picker** (screen 6). WlanApi P/Invoke (scan, list, connect to a saved profile, disconnect, current SSID), the Location-permission handling, and the SSID label.

## Testing

**Identity test for every mapping (required).** A table-driven test sends **every `ConnectionId`** through each mapping and checks it comes back as itself (or as its documented value). These are exactly the places that compile today but would fall through:
- `SingBoxConfigBuilder` (rule outbound, default selector, `only-*` selectors)
- `SmartRoutingController` `Tag`, `ProfileOf`, `WantedExit`
- `UsageCounter` `Update`, `Add`, `AddMeasured`, `RebuildRates`, `RebuildLive`
- `NetworkStatus.ResolvePath` and `IsFallback`
- `App` `ExternalPathResolver` (move it into Core so it can be tested)
- `StatusPresenter` header, toast and tray colour
- `MetricPolicy`
- `UsageExceptions`
- `RuleSet.Build` for all three profiles against the catalog table

**Tests that change on purpose.**
- `AdapterDetectorTests.Wifi_is_not_auto_detected_as_lan_but_can_be_chosen_by_override` changes: Wi-Fi is detected, but **as Wi-Fi**.
- The "wifi exit rejected" catalog tests stay.

**New tests.**
- Ordered healing: target selection, target dies, none healthy.
- Graded metrics for every mode.
- The route pin: applied only on subnet overlap, re-applied after drift, removed on Auto and quit.
- Settings: migration of a wireless `LanOverrideMac`, the backup written once, a v5.3 file loads, and a v6 file without Wi-Fi fields loads.
- `usage.json` with and without `wifi` cells.

**On the machine.** The user's real setup (Ethernet and Wi-Fi both on pzhome, phone tethered):
- `Find-NetRoute` shows local traffic on Ethernet and internet on Wi-Fi in Wi-Fi mode, with Smart on and off.
- Pulling the cable heals to Wi-Fi, then to the Phone.

## Out of scope

- Two adapters of the same kind.
- Entering Wi-Fi passwords.
- Creating Wi-Fi profiles.
- Wi-Fi in the Local network tab.
- Choosing the fallback order in the UI. It is fixed to LAN → Wi-Fi → Phone.
- Per-connection data caps.

## Left to the builder

- Exact type shapes (`PerConnection<T>` or a dictionary), file and class splits, and whether `RouteExit` is renamed or aliased.
- How unattributed non-phone bytes split between LAN and Wi-Fi, under the constraints above.
- The route-pin mechanism, decided by the spike.
- The P/Invoke surface for WlanApi, following the existing `IpHelperNative` and `IcmpNative` pattern.
- The card's widths and spacing for four buttons, matching mockup screen 5.

## Risks and open points

- **The route pin is the riskiest piece.** It depends on Windows route selection with two adapters on one subnet. Hence the spike.
- **Location permission on 24H2 and later** may leave the picker empty for users who decline. The label still works through the connection profile name.
- **The change touches about 30 test files.** The identity tests are the safety net. The plan should migrate per phase, keeping the suite green at each step.
