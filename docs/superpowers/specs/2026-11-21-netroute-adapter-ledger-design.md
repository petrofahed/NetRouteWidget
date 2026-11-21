# NetRoute Widget: Adapter ledger (exact totals on the card) — Design

**Date:** 2026-11-21
**Status:** Approved; implemented by `docs/superpowers/plans/2026-11-21-netroute-adapter-ledger.md`. Real-machine comparison with BitMeter still to be run by the owner.
**Builds on:** `2026-10-14-netroute-v5-1-usage-while-smart-off-design.md` (per-app tracing, gated by the Usage tab) and the v6.2 three-connection work (`ConnectionId { Lan, Wifi, Phone }`, branch `codex/v6-2-smart-routing`, not yet merged to `main`). This design assumes the v6.2 types exist; build it on top of v6.2 or after its merge.

## Why

The owner runs BitMeter 2 next to the widget. On 2026-11-21 BitMeter counted **5.23 GB** for the day (3.84 GB down, 1.39 GB up). The widget's card showed **135 MB** (98 MB phone, 37 MB LAN). The widget is the one that is wrong. Findings behind that, from the log, `usage.json`, BitMeter's `C:\ProgramData\Bitmeter2\BitmeterHistory.xml` and live adapter counters:

1. **Nothing is counted while Smart routing is off and the Usage tab is closed.** The widget started 08:13 in LAN mode; sing-box only started 17:26. BitMeter saw about 4.9 GB before 17:26. `MeasureWithoutSingBox` (`SmartRoutingController.cs`) stops tracing and records a gap when `_usageWatched` is false. v5.1 chose this on purpose (ETW tracing costs CPU), but the card then shows a total that is silently far too low.
2. **While sing-box runs, only the phone is checked against Windows.** LAN bytes come from sing-box's Clash API only. Traffic on the home-line adapter that does not enter the tunnel (seen live: home line receiving ~1.25 MB/s while the tunnel carried ~21 KB/s) is never counted.
3. **Per-app counting ignores private/local addresses** by design, so traffic to the home network never appears.
4. **The card's live speed has the same blind spot** (it showed ↕ 10 KB/s next to a BitMeter graph near 800 KB/s).

Windows keeps an exact, always-on byte counter per adapter. It is cheap, needs no admin rights and does not depend on sing-box. The phone already uses it for a footer figure ("Phone adapter total"); the card and the LAN/Wi-Fi sides do not.

## What the owner decided

- Fix the mismatch with an **always-on adapter ledger** (approach A, chosen over "trace all the time" and over doing both).
- **The card shows exact adapter totals.**
- Per-app rows stay as the detail view.

## Goals

- The card's per-connection total for today equals what Windows counted on that connection's adapter today (up + down), whatever the mode, whether sing-box runs, whether the Usage tab is open.
- Counting runs for as long as the widget runs. No new setting, no admin rights.
- The Usage tab still adds up: per-app rows plus one derived remainder row equal the ledger total.
- History survives restarts and is kept like the existing 35 days.

## Non-goals

- Counting while the widget is not running (a restart loses the bytes since the last reading; see Open points).
- Per-app attribution when sing-box is off and the Usage tab is closed (still needs tracing; approach C, a possible later step).
- Changing routing, "kept off 4G", or how per-app rows are computed.
- Counting the NetRoute TUN, VMware, VPN, TAP or any adapter that is not the Phone, LAN or Wi-Fi connection.

## Anti-goals

- A card total that again disagrees with Windows' own adapter counters (the failure this design exists to remove).
- Double counting: never add the TUN adapter, and never add the ledger on top of per-app rows.
- A save per second (see Storage).
- Breaking an existing `usage.json`, or making an older build discard a newer file.

## Approaches considered

| | What | Trade-off |
|---|---|---|
| **A. Adapter ledger (chosen)** | Read the Windows byte counter of each connection's physical adapter every second, always; store daily up/down per connection; card shows it. | Exact, cheap, no admin. Per-app detail still only while sing-box runs or the Usage tab is open. |
| B. Trace all the time | Drop the `_usageWatched` gate so ETW per-app tracing never stops. | Needs admin, costs CPU all day, still misses tunnel-bypass traffic while sing-box runs, no exact totals. |
| C. A + B | Exact totals plus full attribution. | Best result, CPU cost of B. Leave as a follow-up once A shows what is still missing. |

## Design

### `AdapterLedger` (NetRoute.Core)

A new class beside `UsageCounter`, with its own state and tests. It is **not** part of `UsageCounter.Update` and not under the Smart gate: it must keep counting when sing-box is off, crashed, restarting, or when `_api is null`.

- **Input per tick (about 1 s):** for each `ConnectionId` (Lan, Wifi, Phone) whose adapter is currently detected, the adapter name and its Windows byte counters **split into up (`BytesSent`) and down (`BytesReceived`)**. This extends `IAdapterCounters` (today it returns only a total). Use one enumeration of the network interfaces per tick for all three slots, not one per slot.
- **Which adapters:** the adapters detection assigned to each slot, including a slot that is **turned off** in the widget (turning a connection off stops routing through it, not the bytes it still carries). Not the TUN, not other adapters.
- **Baselines, per connection:** keep the last raw reading and the adapter name it came from.
  - First reading, or the adapter name for that slot changed: set the baseline, count nothing (the counter is "since the adapter came up", so counting it would add the whole since-boot total, e.g. 3.4 GB).
  - Reading ≥ last reading: add the difference.
  - Reading < last reading with the same name (adapter reset or replug): treat as a reset and add the **new reading** as growth, same rule as `UsageCounter.Grow`.
  - Adapter not present this tick: keep the baseline, count nothing.
- **Day:** the local date of the reading (same as `UsageCounter`); old days are pruned with the existing retention.
- **Output:** today's total per connection (up, down), a snapshot for saving, and live rates per connection over the same 3-second window as the card's current rates.

### Card

`CardWindow` row totals (`PhoneTotal`, `LanTotal`, `WifiTotal`) show the ledger's today total for that connection, formatted as now. These replace `TodayTotals` from `UsageCounter` on the card. The card shows a total whenever the ledger has one for today, including when Smart is off.

**Live speed on the card also comes from the ledger** (owner did not ask for this explicitly; it is my call because the first screenshot compared speed with BitMeter too, and the ledger already has the deltas). Speed is shown for any connection that is carrying traffic, whatever the mode. `LiveSpeedDisplay.IsShown` and `RenderLiveSpeeds` change accordingly. Easy to drop if the owner wants totals only.

### Storage

`usage.json` gets one optional per-day field, `adapters`, e.g. `"adapters": { "phone": {"up":…, "down":…}, "lan": {…}, "wifi": {…} }`. **`Version` stays 1.** Reason: `UsageStore.Load` moves aside any file whose version differs, so a bump would make a rollback to an older build discard the whole history; an optional extra property is ignored by the older reader (it will drop the ledger numbers on its next save, which is acceptable). A missing `adapters` field loads as zero. `PhoneAdapterBytes` stays for compatibility; the footer reads the ledger when present.

Saving follows the existing rule (save when dirty, plus periodically), not once per second. `UsageStore.Save` currently skips a day with no rows and `PhoneAdapterBytes == 0`; it must also keep a day that has ledger data. Each day's `adapters` also stores `since`, the local time of the day's first ledger reading (see "Calls made in review", item 4).

### Usage tab

- The "Phone adapter total (exact, from Windows)" footer becomes **per-connection exact totals for the chosen range**, from the ledger.
- A derived row **"Not counted per app"** is added to the list and the totals. It is **computed at report time, never stored**: for the Phone bucket, ledger Phone − sum of Phone bytes in the rows; for the **LAN bucket, ledger Lan + ledger Wifi − sum of LAN bytes in the rows** (v6.2 keeps Lan and Wi-Fi in one bucket, "two-bucket history until v6.3"). Clamp at 0. Per-app rows therefore keep their meaning, and the tab sums to the ledger.
- A short note: totals are exact while the widget is running; per-app rows are counted only while Smart routing runs or this tab is open.

### Error handling

- A counter read that throws (`NetworkInformationException`) counts as "adapter not present this tick" and is logged once per failure streak, like `PhoneAdapterBytes` does today.
- Clock steps and day changes follow `UsageCounter`.
- The ledger never throws into the poll loop and never affects routing.

### Testing

- Unit (Core, fake `IAdapterCounters`): first reading counts nothing; growth adds up/down separately; same-name backwards reading counts the new value; name change re-baselines without counting; absent adapter keeps baseline; turned-off slot still counts; day rollover; snapshot/restore round trip; an old `usage.json` (no `adapters`) loads; a file with `adapters` loads in the current reader.
- Report: "Not counted per app" for the Phone bucket and the merged LAN+Wi-Fi bucket, clamp at 0, range sums, filter behaviour (the remainder row is subject to the name filter like any row).
- Card: totals come from the ledger with Smart off and with Smart running; no value from `UsageCounter` leaks onto the card.
- **Acceptance on the real machine:** over a busy hour and a quiet hour the card total per connection equals the Windows adapter counter growth for that adapter (exact, since it is the same counter). Then compare with BitMeter over a day. BitMeter keeps hourly/daily totals in `C:\ProgramData\Bitmeter2\BitmeterHistory.xml`; a small read-only script can print them. BitMeter may also count the NetRoute tunnel (double counting) — **find out which adapters BitMeter monitors before treating a difference as a widget bug.**

## Open points

- **Restarts lose bytes** between the last reading and the next start (the counter is since-boot, not since-last-reading). The widget starts with Windows, so the loss is small, but BitMeter does not have it. A follow-up could recover the delta when the saved reading is clearly the same boot and ≤ the current counter. Not in scope here.
- **Unexplained observation, not part of this design:** at 17:32:33 on 2026-11-21 the card showed 5.6 MB phone / 7.4 MB LAN while `usage.json` held 28.66 MB / 22.5 MB, although both come from the same `UsageCounter` in the v6.2 source.
  - **Result of plan Task 0 (2026-11-21): no bug found, mismatch not reproduced.**
    - *Running build:* the startup task `NetRouteWidget` points at `C:\Program Files\NetRouteWidget\NetRouteWidget.exe`, whose `ProductVersion` is `6.2.0+eec7ab4021dd6f533378c025102675c01cf804f4` (built 2026-10-31). The process path itself is unreadable without elevation, so the match is by the installed exe (the one the task launches), not by the process. `git diff eec7ab4 HEAD -- src tests` is empty: the running build has the same source as the branch tip this work starts from, so the v6.2 source read here is the code that ran.
    - *Card path:* the card total is `SmartRoutingController._todayTotals`. It is set in the constructor and after `UsageCounter.Update`, `AddMeasured` and `Clear`, i.e. after every code path that changes the cells, and `App.RenderLiveSpeeds` re-reads it every second. `TodayTotals` and `Snapshot` sum the same `Cell` rows. No path was found where the card can diverge from the file.
    - *Test:* `Today_totals_equal_the_sum_of_the_snapshot_rows` (UsageCounterTests) drives Update (with a phone take-back), AddMeasured, NoteGap, ResetBaselines (sing-box restart), a day rollover, a snapshot restore and Clear, and asserts after each step that `TodayTotals(day)` equals the sum of the `Snapshot()` rows for phone and LAN. It passes on the current code.
    - *Explanations left, not provable now:* (a) the two numbers were read at different moments (the file is written only every 30 s and only when dirty; it has since moved on and now holds about 1.1 GB phone for the day), or (b) \"Clear usage history\" was used between the two readings. Clear zeroes the card at once and also rewrites the file, so (b) explains the numbers only if usage.json was read before the Clear (and the card after it). Clear does not log, so (b) cannot be excluded from the log. Neither is a defect in `UsageCounter`. If it recurs, note the exact time of both readings and whether Clear was used.
- BitMeter 2's adapter selection (see Testing).

## Calls made in review

These were open after a fresh reader went through the draft. They are my calls, not the owner's decisions; the owner can overturn any of them.

1. **"Clear usage history…" clears the ledger too.** Today's button zeroes the per-app counter and the card totals. It now also zeroes the stored ledger days and **keeps the last raw readings as the baseline**, so counting continues from zero without re-adding the since-boot counter. After a Clear the card legitimately reads "since cleared". The confirmation text says that the exact adapter totals are cleared as well.
2. **The card-versus-`usage.json` mismatch is the first, time-boxed plan task.** The remainder row is "ledger minus per-app rows", so a real bug in `UsageCounter`/`TodayTotals` would make it wrong. Spend a short, fixed amount of effort identifying the running build and reproducing the mismatch before the ledger work. If it is a bug in `UsageCounter`, fix it as its own change first; if it cannot be reproduced in the time box, record that and continue.
3. **The live-speed change is a separate, droppable phase after the totals work.** The Usage tab's "Now" columns keep their sing-box per-app source (they are per app, which the ledger cannot give); only the card's per-connection speed moves to the ledger, so the two can legitimately differ and the Usage tab says so in its note.
4. **Days before the ledger existed are marked, not hidden.** Pre-ledger days load with no ledger data. The footer shows the ledger figure only where it exists, and on the first ledger day it reads "counted since HH:mm" (from the stored `since`). Because the baseline is not persisted across restarts, the ledger cannot match BitMeter for that first day; the BitMeter comparison starts on the first full day. This is expected, not a failure.
5. **The ledger is owned by the app (not by the Smart controller), so it exists when Smart is unavailable.** The slot-to-adapter mapping comes from detection with the "turned off" flags ignored (re-run or share `RouteController`'s detection), and falls back to the last known adapter name per slot when detection is empty.
6. **Base branch:** branch from the tip of `codex/v6-2-smart-routing` and target the PR at that branch; retarget to `main` once v6.2 merges. Expect conflicts in `SmartRoutingController`, `App.xaml.cs` and `CardWindow` if v6.2 keeps changing.

## Left to the builder

Class and file names, where the ledger is polled from (it must run independent of the Smart gate and of `_usageWatched`; the controller's one-second poll or the app timer are both acceptable), the exact interface change on `IAdapterCounters`, JSON property naming, and how the Usage tab lays out the new row.
