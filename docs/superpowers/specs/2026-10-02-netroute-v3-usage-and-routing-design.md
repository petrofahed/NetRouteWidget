# NetRoute Widget v3: Usage & routing page — Design

**Date:** 2026-10-02
**Status:** Draft for the user's review. Not built yet.
**Builds on:** `2026-10-02-netroute-v2-smart-routing-design.md` (Smart routing, sing-box, the management window). Implementation starts after v2's live verification and its fix rounds.

## Goal

One window, two tabs, that answers two questions together:

1. **Who is using my data, through the phone and through the LAN, now and over the last days?**
2. **Where should each app's traffic go?**

### What the user asked for

- Check which application consumes the most data, with **Phone** and **LAN** shown separately for each application.
- A runtime view: is YouTube (or any app) consuming **right now**, and through which connection?
- Filter by **date ranges** (1 day, 3 days, 7 days, 15 days, 1 month), not by an All/Phone/LAN toggle, because Phone and LAN are columns.
- It is part of the **management of assigning traffic to 4G or LAN**, not a separate page. Usage and assignment sit side by side.
- **Two tabs. The second tab is the config.**
- The window must follow the Windows light/dark theme (tracked as a v2 live fix).

### Decisions already taken

- Measured through **sing-box** (not Windows tracing), so usage is recorded only while Smart routing is running.
- Any application can be assigned, not only the built-in items.

## The window

Tabs: **Usage** (first, the default) and **Config** (second).

### Tab 1: Usage

```
Show usage for: [ Today ] [ 3 days ] [ 7 days ] [ 15 days ] [ 30 days ]      Smart routing: ON · LAN ● online

 Application / site      Goes via      📱 Phone     🖧 LAN       Now
 ─────────────────────────────────────────────────────────────────────────────
 Chrome                  [ Phone ▾ ]    1.2 GB      340 MB      ↕ 1.4 MB/s · phone
 YouTube                 [ LAN   ▾ ]    0 MB        2.3 GB      ↕ 3.0 MB/s · LAN
 Windows Update          [ LAN   ▾ ]    0 MB        860 MB      idle
 Steam                   [ LAN   ▾ ]    0 MB        2.1 GB      idle
 Facebook                [ Phone ▾ ]    120 MB      0 MB        idle
 ─────────────────────────────────────────────────────────────────────────────
 Total                                  1.3 GB      5.6 GB
 Kept off 4G in this period: 5.3 GB
```

- **Rows** are the things that can be assigned. A connection is attributed to exactly one row, so the totals never double-count:
  1. if the connection's application matches a rule that lists that application (OneDrive, Steam, a user "App" rule), the row is that application;
  2. otherwise, if its host matches a domain rule (YouTube, Facebook, Windows Update…), the row is that rule's item;
  3. otherwise the row is the application (its file name, e.g. `chrome`).
  - So YouTube traffic from Chrome counts under **YouTube**; other Chrome traffic under **Chrome**.
  - A connection with no known application and no matching host goes to a row named **"Other"**.
- **Application names:** the file name without `.exe`, except where a built-in item lists that file (OneDrive.exe → "OneDrive", steam.exe → "Steam").
- **Goes via** is the assignment, a drop-down with **Phone** and **LAN**:
  - built-in item row: LAN = the item is switched on, Phone = switched off (the same switch as in the Config tab);
  - application row with a user App rule: LAN = the rule is enabled, Phone = the rule is disabled (it stays listed in the Config tab);
  - application row with no rule: choosing **LAN** creates a user App rule; choosing Phone later disables it;
  - the "Other" row and any row while Smart routing is unavailable: the drop-down is disabled.
- **Phone** and **LAN** columns show bytes (download + upload) for the selected date range. The exit is the connection's actual exit in sing-box: the phone or the LAN adapter.
- **Now** shows the current rate and exit for rows that moved data in the last few seconds ("↕ 1.4 MB/s · phone"), otherwise "idle".
- **Sorting:** click any column header. The default sort is Phone, largest first, so the biggest consumers of mobile data are on top.
- **Date ranges:** Today (since local midnight), 3, 7, 15 and 30 days (today plus the previous days). The choice is remembered.
- **Footer:** totals per column and "kept off 4G in this period".
- **Banners:**
  - Smart routing off or paused: "Usage is recorded only while Smart routing is running" (history stays visible).
  - Fewer days recorded than the range: "Recording since <date>".

### Tab 2: Config

Today's management page, unchanged in behaviour:

- the master switch, the status line, the LAN status and "Use phone until LAN is back";
- built-in groups and items with their switches (the same switches the "Goes via" drop-down changes);
- "My rules" with + App and + Website;
- the note that a rule change restarts Smart routing for about a second;
- new: **Clear usage history** (with a confirmation) and "History is kept for 35 days".

Both tabs update in place, without rebuilding on every refresh. Both follow the Windows theme.

## Data

### What is counted

- Every connection that passes through the sing-box TUN, with its application, host and exit (`chains[0]` is `phone` or `lan`) and its upload/download byte counters from the Clash API.
- **Not counted:** traffic to the home network (excluded from the TUN), traffic while sing-box is not running, and the bytes of connections that opened and closed between two polls. The page labels the numbers **approximate**.

### How

- The existing Smart routing poll runs every **3 s** (instead of 5/10 s) while sing-box is running. Byte **deltas** since the previous poll are added to the row's counter for the current local day and the connection's exit. The first time a connection is seen, its current totals count (it is live traffic that began before the poll).
- **Now** is computed from the same deltas over the last poll interval. A row counts as active when it moved at least 1 KB/s.

### Storage

- `%AppData%\NetRouteWidget\usage.json`, written atomically (temp file, then move), at most every 30 s while it changes and at exit.
- Structure: `{ "version": 1, "days": { "2026-10-02": { "<row key>": { "phone": { "up": n, "down": n }, "lan": { "up": n, "down": n } } } } }`. The day key is culture-invariant `yyyy-MM-dd` (local date).
- **Row key:** `app:<exe lower case>` for applications, the item id for domain rules, `other` for unknown. A row's key does not change when the user assigns or unassigns it, so history stays continuous.
- **Retention:** 35 days, pruned at load and at day rollover. At most 500 rows per day; beyond that, the smallest rows fold into "Other".
- A missing, locked or corrupt file starts empty. A corrupt file is renamed to `usage.json.bad` once, and an unreadable (locked) file is left untouched and not overwritten for the session (same rule as `settings.json`).
- Privacy: only application names, item names and byte totals are stored, never URLs or full host lists.

## Architecture

### NetRoute.Core (pure, unit-tested)

- **`UsageKeyResolver`**: `Resolve(SingBoxConnection, RuleSet) → (key, display name)`, with the three attribution rules above.
- **`UsageCounter`**: per-day, per-key, per-exit up/down totals. `Update(connections, rules, now)` computes deltas per connection id, rolls the day over, and records the **live rate** per key and exit. Not thread-safe: called only under the controller's gate, like `DataSavedCounter`. `Snapshot()` returns an immutable copy.
- **`UsageStore`**: load/save/prune of `usage.json` (atomic, invariant culture, corrupt/locked handling).
- **`UsageReport`**: `Build(snapshot, today, rangeDays, rules, settings, sort) → rows + totals`. It merges the assignment state and display names, and sorts.
- **`SmartRoutingController`**: gains the faster poll, owns the `UsageCounter`, and exposes `UsageSnapshot GetUsage()` (copy, taken under the gate). No new threads.
- **Settings**: `SmartRouting.UsageRangeDays` (remembered range, default 7).
- **Assignment helper** `UsageAssignment.Set(settings, row, goesViaLan) → settings`, implementing the per-row rules above (item switch, user App rule create/enable/disable). Pure, with tests.

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
  - `UsageStore`: round trip, culture safety (ar-SA), corrupt and locked files, retention, the 500-row cap.
  - `UsageReport`: ranges (today, 3, 7, 15, 30), sorting, totals, assignment state per row type.
  - `UsageAssignment`: every row type, both directions.
- Manual checklist with the user: play YouTube through the LAN and watch the **LAN** column and **Now**; switch Chrome to LAN and watch it move; unplug the LAN; check ranges after two days; restart the widget and see the history survive.

## Out of scope

- History from before the feature or while Smart routing is off.
- Traffic to home devices.
- Per-site breakdown inside an application (a possible expandable row later).
- Export, charts and data-cap alerts.
- Windows-level tracing (option B), if ever needed.

## Open risks

- **Approximate numbers:** short connections between polls are missed, so totals are a lower bound.
- **Row churn:** an application that sits in a built-in item's domain list (Chrome on youtube.com) appears under the item, as intended, but users may expect to see it under Chrome as well. The page explains this in a tooltip.
