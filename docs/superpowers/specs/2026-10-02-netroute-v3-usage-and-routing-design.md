# NetRoute Widget v3: Usage & routing page — Design

**Date:** 2026-10-02
**Status:** Implemented on branch feat/v2-smart-routing; waiting for the user's on-machine verification.
**Builds on:** `2026-10-02-netroute-v2-smart-routing-design.md` (Smart routing, sing-box, the management window). Implementation starts after v2's live verification and its fix rounds.

## Goal

One window, two tabs, that answers two questions together:

1. **Who is using my data, through the phone and through the LAN, now and over the last days?**
2. **Where should each app's traffic go?**

### What the user asked for

- Check which application consumes the most data, with **Phone** and **LAN** shown separately for each application.
- A runtime view: is YouTube (or any app) consuming **right now**, and through which connection?
- Filter by **date ranges** (1 day, 3 days, 7 days, 15 days, 1 month), not by an All/Phone/LAN toggle, because Phone and LAN are columns.
- It is part of the **management of assigning traffic to 4G or LAN**, not a separate page: the Usage tab shows the numbers, the Config tab (next to it) changes the rules.
- **Two tabs. The second tab is the config.**
- The window must follow the Windows light/dark theme (tracked as a v2 live fix).

### Decisions already taken

- Measured through **sing-box** (not Windows tracing), so usage is recorded only while Smart routing is running.
- Any application can be assigned (with a user App rule on the Config tab), not only the built-in items.

## The window

Tabs: **Usage** (first, the default) and **Config** (second).

### Tab 1: Usage

```
Show usage for: [ Today ] [ 3 days ] [ 7 days ] [ 15 days ] [ 30 days ]      Smart routing: ON · LAN ● online

 Application / site                    📱 Phone     🖧 LAN       Now phone      Now LAN
 ─────────────────────────────────────────────────────────────────────────────────────────
 Chrome                                 1.2 GB      340 MB      ↕ 1.4 MB/s     ↕ 5 KB/s
 YouTube                                0 MB        2.3 GB      –              ↕ 3 MB/s
 Windows Update                         0 MB        860 MB      –              –
 Steam                                  0 MB        2.1 GB      –              –
 Facebook                               120 MB      0 MB        –              –
 ─────────────────────────────────────────────────────────────────────────────────────────
 Total                                  1.3 GB      5.6 GB
 Kept off 4G in this period: 5.3 GB
```

- **Rows** are the things that can be assigned. A connection is attributed to exactly one row, so the totals never double-count:
  1. if the connection's application matches a rule that lists that application (OneDrive, Steam, a user "App" rule), the row is that application;
  2. otherwise, if its host matches a domain rule (YouTube, Facebook, Windows Update…), the row is that rule's item;
  3. otherwise the row is the application (its file name, e.g. `chrome`).
  - So YouTube traffic from Chrome counts under **YouTube**; other Chrome traffic under **Chrome**.
  - A connection with no known application and no matching host goes to a row named **"Other"**. Traffic of connections that were missed between polls is shown in a separate row, **"Unattributed (short connections)"** (see Data).
- **Application names:** the file name without `.exe`, except where a built-in item lists that file (OneDrive.exe → "OneDrive", steam.exe → "Steam").
- **No assignment on this tab.** The Usage tab is a read-only view of Phone and LAN use (there is no "Goes via" column). To move an application or a site, use the Config tab:
  - built-in item: switch it on (LAN) or off (Phone);
  - application: add a user App rule (**+ App**) and enable it for the LAN, or disable it to go back to the phone.
- **Phone** and **LAN** columns show bytes (download + upload) for the selected date range. The exit is the connection's actual exit in sing-box: the phone or the LAN adapter.
- **Now phone** and **Now LAN** are two live columns: the current speed on each exit over the last ~3 seconds ("↕ 1.4 MB/s"). A side is shown only above 1 KB/s, otherwise a dash. Each column is sortable (idle rows last when sorting largest-first).
- **Filter:** a "Filter:" box under the range buttons narrows the list by name (case-insensitive "contains" on the display name or the row key); the Total row then sums only the shown rows and reads "Total (filtered)", while "Kept off 4G" and the adapter total stay whole-period. It is per-session, not saved.
- **Sorting:** click any column header. The default sort is Phone, largest first, so the biggest consumers of mobile data are on top.
- **Date ranges:** Today (since local midnight), 3, 7, 15 and 30 days (today plus the previous days). The choice is remembered.
- **Footer:** totals per column and "kept off 4G in this period".
- **Banners:**
  - Smart routing off or paused: "Usage is recorded only while Smart routing is running" (history stays visible).
  - Fewer days recorded than the range: "Recording since <date>".

### Tab 2: Config

Today's management page, unchanged in behaviour:

- the master switch, the status line, the LAN status and "Use phone until LAN is back";
- built-in groups and items with their switches (rules are changed only here, not on the Usage tab);
- "My rules" with + App and + Website;
- the note that a rule change restarts Smart routing for about a second;
- new: **Clear usage history** (with a confirmation) and "History is kept for 35 days".

Both tabs update in place, without rebuilding on every refresh. Both follow the Windows theme.

## Data

### What is counted

- Every connection that passes through the sing-box TUN, with its application, host and exit (`chains[0]` is `phone` or `lan`) and its upload/download byte counters from the Clash API.
- **Not counted:** traffic to the home network (excluded from the TUN) and traffic while sing-box is not running.

### Why a plain connection poll is not enough (measured on the target machine)

sing-box's Clash API lists only the connections that are **open right now**; it keeps no list of closed ones (verified on 1.14.2: `/connections` has no closed list, and `connections?closed=true` and similar endpoints do nothing). The v2 counter polled every 5-10 s and counted per-connection byte deltas. A controlled test downloaded **5.4 MB** from youtube.com in six short requests through the widget, and the counter recorded **0.5 MB (10 %)**. Connections that open and close between two polls are missed, and only the bytes seen by a poll are counted.

### How (accurate counting)

1. **Poll every 1 s** while sing-box is running (a dedicated timer, independent of the 5-10 s v1 refresh). Per-connection byte deltas are added to the row for the current local day and the connection's exit. Long transfers (video, downloads) are caught almost fully; the first sighting of a connection counts its current totals.
2. **Reconcile against exact totals every poll.** The Clash API's `downloadTotal` and `uploadTotal` include closed connections. For each poll:
   - `unseen = totalDelta - sum(per-connection deltas seen)` is the traffic of connections that were missed.
   - The **phone** share is taken from Windows' own byte counters for the phone adapter (exact, per exit): `unseenPhone = max(0, phoneAdapterDelta - attributedPhoneDelta)`; the rest of `unseen` is `unseenLan`. Protocol framing makes the adapter counters a few percent larger than sing-box's payload counts, and that difference lands in the same row. The unattributed phone share is a **running balance** against the Windows adapter counter, not a per-poll clamp: the adapter is read a few hundred ms after the HTTP snapshot, so at the start of a burst it is ahead of the connections and at the end behind. A poll where the adapter is behind takes bytes back from the unattributed phone cell (or carries the deficit forward, capped at 8 MiB), so the timing skew cancels out and the Phone column follows the exact adapter total.
   - Unseen bytes go to a visible row **"Unattributed (short connections)"** with its own Phone and LAN values, so the column totals are correct instead of silently low. They are never assigned to a guessed application.
3. **Exact footer.** The page also shows **"Phone adapter total in this period (exact, from Windows)"** (it follows the selected range, so it can be compared with the Phone column).
4. **Kept off 4G** (card row and page) uses the same data: LAN bytes of rows assigned to the LAN while the default exit was the phone, plus the LAN part of the unattributed bytes that arrived while the phone was the default exit. It is labelled "at least" when a day in the range has `gaps > 0`.
5. **Now phone** and **Now LAN** (the live speeds) are computed from the 1 s deltas, per exit, over a 3-second window.
6. **Live speed on the widget card:** next to each adapter's latency the card shows the total live speed through that exit, all applications together. `UsageCounter.Live` sums, over the same 3-second window, the seen connection bytes plus the unattributed shares booked in each poll (a phone take-back counts as negative, the sums are clamped at 0, a side below 1 KB/s reads 0). It is not part of the snapshot and is never saved; the controller exposes it as `LiveSpeed` and the card clears it whenever Smart routing is not running.

The remaining limitation: an application's own short connections that were missed are counted under "Unattributed", not under the application. The page says so, and the totals stay right.

### Storage

- `%AppData%\NetRouteWidget\usage.json`, written atomically (temp file, then move), at most every 30 s while it changes and at exit.
- Structure: `{ "version": 1, "days": { "2026-10-02": { "rows": { "<row key>": { "phone": { "up": n, "down": n }, "lan": { "up": n, "down": n }, "kept": n } }, "phoneAdapter": n, "gaps": n } } }`. `kept` is the part of a row's LAN bytes that LAN-only rules kept off 4G; `phoneAdapter` is the day's bytes on the phone adapter as counted by Windows; `gaps` counts the times sing-box stopped or restarted that day (the bytes just before each stop were not recorded). The day key is culture-invariant `yyyy-MM-dd` (local date).
- **Row key:** `app:<exe lower case>` for applications, the item id for domain rules, `other` for unknown. A row's key does not change when the user assigns or unassigns it, so history stays continuous.
- **Retention:** 35 days, pruned at load and at day rollover. At most 500 application/item rows per day plus the two special rows (`other`, `unattributed`); while counting, a new row beyond the cap goes into `other`; on load, the smallest rows beyond the cap fold into `other`.
- A missing, locked or corrupt file starts empty. A corrupt file is renamed to `usage.json.bad` once, and an unreadable (locked) file is left untouched and not overwritten for the session (same rule as `settings.json`).
- Privacy: only application names, item names and byte totals are stored, never URLs or full host lists.

## Architecture

### NetRoute.Core (pure, unit-tested)

- **`UsageKeyResolver`**: `Resolve(SingBoxConnection, RuleSet) → (key, display name)`, with the three attribution rules above.
- **`UsageCounter`**: per-day, per-key, per-exit up/down totals. `Update(connections, rules, now)` computes deltas per connection id, rolls the day over, and records the **live rate** per key and exit. Not thread-safe: called only under the controller's gate, like `DataSavedCounter`. `Snapshot()` returns an immutable copy.
- **`UsageReconciler`**: given the Clash API totals, the per-connection deltas seen, and the phone adapter's byte-counter delta, returns the unseen Phone and LAN bytes for the poll (pure, with tests for clamping, framing overhead, counter resets and a restarted sing-box).
- **`IAdapterCounters`** (Windows implementation reads `NetworkInterface` statistics for the phone adapter by index; a fake in tests).
- **`UsageStore`**: load/save/prune of `usage.json` (atomic, invariant culture, corrupt/locked handling).
- **`UsageReport`**: `Build(snapshot, today, rangeDays, rules, settings, sort) → rows + totals`. It merges the assignment state (kept in the report model) and display names, and sorts.
- **`SmartRoutingController`**: gains the faster poll, owns the `UsageCounter`, and exposes `UsageSnapshot GetUsage()` (copy, taken under the gate). No new threads. `DataSavedCounter` and `stats.json` are replaced (the old file is ignored, not migrated). `ApiFailureLimit` became 10 because the poll now runs once a second.
- **Settings**: `SmartRouting.UsageRangeDays` (remembered range, default 7).
- **Assignment helper** `UsageAssignment.Set(settings, row, goesViaLan) → settings` (item switch, user App rule create/enable/disable) and `Describe`. Pure, with tests. The Usage tab no longer has a control that calls `Set`; `Describe` still feeds the report model.

### NetRoute.App

- `SmartRoutingWindow` becomes a `TabControl` with the Usage and Config tabs. The Usage tab updates in place (same technique as the current page), keyed by row key.
- `App` persists the usage file (timer, at exit) and passes the controller's snapshot to the window.

## Error handling

- sing-box off or unreachable: no new data, history stays visible, the banner explains.
- Usage file problems never block the widget or Smart routing (logged, session continues in memory).
- A very large number of connections (thousands): the poll stays bounded by the existing API timeout, and counters are dictionary updates (cheap).

## Testing

- Unit tests:
  - `UsageKeyResolver`: all three attribution rules, process-wins-over-domain, the "Other" row.
  - `UsageCounter`: deltas, first sighting, negative deltas, a reappearing connection id, day rollover, exit attribution, live rate.
  - `UsageReconciler`: no missed traffic, all traffic missed, adapter counter smaller than the attributed total, counter wrap or reset, sing-box restarted (totals drop), phone adapter absent.
  - `UsageStore`: round trip, culture safety (ar-SA), corrupt and locked files, retention, the 500-row cap.
  - `UsageReport`: ranges (today, 3, 7, 15, 30), sorting, totals, assignment state per row type.
  - `UsageAssignment`: every row type, both directions.
- Accuracy check on the real machine (scripted, read-only): download a known amount (for example 6 x youtube.com, about 5 MB) through the widget and require the page's YouTube + Unattributed rows to add up to within 10 % of the bytes downloaded; compare the phone column with Windows' phone adapter counter over an hour.
  - Manual checklist with the user: play YouTube through the LAN and watch the **LAN** column and **Now LAN**; switch Chrome to LAN and watch it move; unplug the LAN; check ranges after two days; restart the widget and see the history survive.

## Out of scope

- History from before the feature or while Smart routing is off.
- Traffic to home devices.
- Per-site breakdown inside an application (a possible expandable row later).
- Export, charts and data-cap alerts.
- Windows-level tracing (option B), if ever needed.

## Open risks

- **Approximate numbers:** short connections between polls are missed, so totals are a lower bound.
- **Row churn:** an application that sits in a built-in item's domain list (Chrome on youtube.com) appears under the item, as intended, but users may expect to see it under Chrome as well. The page explains this in a tooltip.
