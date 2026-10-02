# NetRoute Widget v2: Smart routing — Design

**Date:** 2026-10-02
**Status:** Approved (user: "start phase 2"); feasibility spike next
**Builds on:** `2026-10-02-netroute-widget-v1-design.md` (v1 modes, auto-heal, break-glass)

## Goal

While the PC uses the **phone** for internet, keep data-hungry traffic **off 4G** by sending it through the LAN. That means system updates, cloud sync, game downloads, video/social sites, and anything the user adds. Normal interactive traffic stays on the fast phone link. Every item is visible and can be switched on and off from a management page.

### What the user asked for

- Route through the LAN: Windows/Store updates, cloud sync (OneDrive and similar), game launchers, YouTube, Facebook and Instagram, plus apps and sites the user picks.
- A set of items is **on by default**, and each can be switched off individually (e.g. "Facebook back on the phone").
- Clicking the option in the widget opens a **big management page**.
- When LAN-routed traffic can't flow because the LAN is down, it should **wait**, not fall back to 4G. The user gets a **notification** that offers to "use the phone until the LAN is back" or "keep waiting".
- Engine: **sing-box**, embedded and fully managed by the widget.

### Success criteria

- With Smart routing on in Phone mode, enabled items produce **zero bytes on the phone adapter**. Unmatched traffic still goes through the phone.
- Home-network devices (NAS, printer, TV) stay reachable in every state.
- If sing-box is off, crashed or missing, internet keeps working through v1 routing.
- The user can see what was kept off 4G today, per item.

## Scope

**In v2:**
- the Smart routing on/off switch;
- built-in rule groups and items, plus user rules (app or website);
- the management page;
- the "waiting" notification with its two actions;
- the data-kept-off-4G counter;
- sing-box lifecycle management;
- break-glass support;
- an on-demand **speed test** button on the card (a 5 MB download through each adapter, showing both speeds; about 5 MB of mobile data per run).

**Out of v2:**
- the reverse direction ("send X via the phone while in LAN mode");
- data caps and alerts;
- per-URL rules (rules work per domain);
- IPv6-specific rules beyond what sing-box does by default;
- editing the built-in domain lists in the UI (they ship as a data file).

## Behaviour

### When rules apply

Rules are active only while Smart routing is ON **and** the mode is Phone or Auto. In LAN mode everything goes through the LAN anyway, so sing-box is not needed and is stopped.

In Auto, non-matching traffic goes through the phone. sing-box needs a fixed default exit, and the user's stated intent is "phone unless saving data".

### Matching precedence (first match wins)

1. **Private and home-network ranges** (RFC1918, link-local, multicast, the LAN subnet) bypass sing-box completely. They are excluded from the virtual adapter's routes and go straight out through the LAN, as in v1.
2. **User rules** (enabled ones): an app (`.exe` name) or a website (the domain and its subdomains). These go through the LAN only.
3. **Built-in items** (enabled ones). These go through the LAN only.
4. **Everything else** goes to the current default exit: the phone, or the LAN while auto-heal has moved traffic there.

### Built-in groups and items (all ON by default)

| Group | Item | Matched by |
|---|---|---|
| System updates | Windows Update & Delivery Optimization | domains: `windowsupdate.com`, `update.microsoft.com`, `delivery.mp.microsoft.com`, `dl.delivery.mp.microsoft.com`, `download.windowsupdate.com`, `tlu.dl.delivery.mp.microsoft.com` |
| | Microsoft Store | domains: `dl.delivery.mp.microsoft.com`, `storeedgefd.dsx.mp.microsoft.com`, `displaycatalog.mp.microsoft.com` |
| Cloud sync | OneDrive | process `OneDrive.exe`; domains `storage.live.com`, `onedrive.live.com`, `sharepoint.com` |
| | Google Drive | process `GoogleDriveFS.exe` |
| | Dropbox | process `Dropbox.exe` |
| | iCloud | processes `iCloudDrive.exe`, `iCloudServices.exe`, `iCloudPhotos.exe` |
| Video & social | YouTube | domains `youtube.com`, `googlevideo.com`, `ytimg.com`, `youtu.be`, `youtube-nocookie.com` |
| | Facebook | domains `facebook.com`, `fbcdn.net`, `facebook.net`, `fb.com`, `fbsbx.com` |
| | Instagram | domains `instagram.com`, `cdninstagram.com` |
| Game launchers | Steam | process `steam.exe`; domains `steamcontent.com`, `steamstatic.com` |
| | Epic Games | process `EpicGamesLauncher.exe`; domain `epicgames-download1.akamaized.net` |
| | Battle.net | processes `Battle.net.exe`, `Agent.exe`; domains `blzddist1-a.akamaihd.net`, `blizzard.com` |
| | Xbox / Microsoft gaming | processes `XboxPcApp.exe`, `gamingservices.exe`; domain `assets1.xboxlive.com` |

Notes on the list:
- The lists live in a shipped data file, `rules/builtin.json`, so they can be updated without code changes.
- A group has a master toggle. Each item also has its own toggle.
- Windows Update is matched by **domain only**, because it runs inside the shared `svchost.exe`.
- Domain matching uses sing-box **sniffing** (TLS SNI, HTTP Host, QUIC), so it covers both browsers and desktop apps.
- Facebook's domains include its connectivity-check domains, so switching Facebook off fully returns it to the phone.

### When LAN-routed traffic is waiting

- **Detection:** the LAN probe reports the LAN as offline (v1 status) **and** sing-box reports at least one failed connection for a LAN-only rule within the last 30 s.
- **Popup:** a custom WPF popup near the tray, not a balloon, because balloons can't have buttons. It lists the waiting items and offers two buttons:
  - **Use phone until LAN is back.** All LAN-only rules are temporarily routed through the phone.
    - The card shows "⚠ Using phone for all traffic — LAN offline".
    - When the LAN probe reports healthy again (3 good checks), the rules revert automatically and a toast says "LAN back — <items> back on LAN".
  - **Keep waiting.** Nothing changes. No further popup appears for the same LAN outage; a new outage can ask again.
- The same choice is available on the card and in the management page for as long as the LAN is offline.

### Interaction with v1

- **Auto-heal still runs.**
  - Phone dies: the default exit becomes the LAN. LAN-only items are unaffected.
  - LAN dies: the "waiting" flow above applies.
- **v1 interface metrics stay applied underneath** as the safety net. If sing-box stops, its virtual adapter disappears and routing falls back to v1 automatically.
- **Auto-heal moving the default exit** (phone ↔ LAN) switches sing-box's `default` selector at runtime through its local control API. No restart is needed.
- **Switching to LAN mode** stops sing-box. Switching back to Phone or Auto starts it again.
- **Break-glass:** `Restore-Network.cmd` also stops `sing-box.exe`, and the virtual adapter is removed with it.

## Architecture

### NetRoute.Core (pure, unit-tested)

- **`RuleCatalog`**: loads `builtin.json`, merges in the user's rule state and toggles, and produces the effective rule set (process names and domain suffixes per item).
- **`SingBoxConfigBuilder`**: turns the effective rules, the detected phone/LAN adapters, the LAN subnet and the "use phone until LAN back" flag into a sing-box JSON config. Key parts:
  - a TUN inbound with `auto_route`, and the private and LAN ranges excluded;
  - sniffing enabled;
  - outbounds `phone` (direct, `bind_interface` = phone adapter name) and `lan` (direct, `bind_interface` = LAN adapter name);
  - a `selector` named `default` over `phone` and `lan`;
  - route rules: process-name and domain-suffix rules go to `lan`, and the final rule goes to `default`;
  - the local control API on `127.0.0.1` with a random secret.
- **`SmartRoutingController`**: owns the Smart routing state machine:
  - on/off;
  - running or degraded;
  - "use phone until LAN back";
  - the waiting-popup decision;
  - the crash-restart policy;
  - the per-item daily counters.

  It works through interfaces (`ISingBoxHost`, `ISingBoxApi`, a clock), so all of it is testable with fakes. It subscribes to `RouteController` status (mode, adapters, LAN health, heal state).
- **`DataSavedCounter`**: sums the download and upload bytes of connections that matched LAN-only rules while the phone was the default exit, keeps per-item totals for each day, and persists them to `%AppData%\NetRouteWidget\stats.json`.

### NetRoute.Core/Windows

- **`SingBoxHost`**: starts `sing-box.exe run -c <generated config>` from the app folder, with stdout/stderr going to the log, and stops it (kills the process tree). It reports when the process exits.
- **`SingBoxApi`**: an HTTP client for the local control API:
  - switch the `default` selector;
  - read `/connections` (for counters and for detecting waiting traffic);
  - a health ping.

### NetRoute.App

- **Card:** one new row: "⚡ Smart routing ON · N rules · X kept off 4G today ⚙". It turns amber with "LAN-only traffic waiting (LAN offline)" when traffic is waiting. Clicking it opens the management page.
- **`SmartRoutingWindow`**: a full-size, resizable management page:
  - the master switch;
  - a LAN status line;
  - "kept off 4G today";
  - expandable groups with a master toggle per group, item toggles, and today's bytes per item;
  - "My rules" with "+ App…" (pick from running processes or browse for an exe) and "+ Website…" (enter a domain), each rule with a toggle and a delete button.
- **`WaitingPopup`**: the custom tray-anchored popup with the two buttons.

### Packaging

- A pinned sing-box release (Windows amd64) is downloaded at build/publish time from its official GitHub release, with a SHA-256 check, and placed next to `NetRouteWidget.exe`.
- `rules/builtin.json` ships next to the exe.
- The repo gets `THIRD-PARTY-NOTICES.md` with the sing-box licence (GPL-3.0) and a link to its source.
- `sing-box.exe` runs as a separate process and is not linked into the app.

### Settings (added to `settings.json`)

```json
"SmartRouting": {
  "Enabled": false,
  "Items": { "windows-update": true, "youtube": true, "facebook": false },
  "UserRules": [ { "Type": "App", "Value": "qbittorrent.exe", "Enabled": true },
                 { "Type": "Website", "Value": "dropbox.com", "Enabled": true } ]
}
```

- Items missing from the map default to the item's built-in default, which is on.
- Smart routing as a whole starts **off** after upgrading, until the user turns it on.

## Error handling

- **sing-box fails to start**, e.g. its virtual adapter is blocked by antivirus or the exe is missing: the switch reverts to off and the card or page shows the reason, taken from sing-box's stderr. v1 routing is unaffected.
- **sing-box crashes:** it is restarted automatically. After 3 crashes within 5 minutes, Smart routing turns off with the message "Smart routing stopped — sing-box keeps crashing (see log)".
- **Phone or LAN adapter renamed or recreated** (replug): the config is regenerated and sing-box restarted (about 1 s). Restarts are debounced together with v1's network-change debounce.
- **The control API doesn't answer:** a health ping fails 3 times in a row, which is treated as a crash.
- **Not elevated:** Smart routing needs admin rights for the virtual adapter. The switch is disabled with the same "Read-only — restart as admin" hint as v1.
- **Quitting the widget** stops sing-box. Routing is left as v1 left it.

## Testing

- **Unit tests (TDD):**
  - the config builder: each toggle combination, user rules, adapter names, LAN-subnet exclusion, the "use phone until LAN back" flag;
  - `RuleCatalog` merge and defaults;
  - the `SmartRoutingController` state machine: start, crash and restart policy, mode → selector, waiting detection, popup once per outage, automatic revert after 3 good LAN checks;
  - `DataSavedCounter` accounting and day rollover;
  - settings round-trip and defaults.
- **Feasibility spike first, on the user's machine.** Throwaway; it decides go/no-go before the full build. It checks that:
  1. sing-box TUN starts alongside Kaspersky;
  2. OneDrive (by process) and YouTube (by domain) leave through the LAN while other Chrome traffic leaves through the phone, verified with per-adapter byte counters;
  3. NAS, printer and TV are still reachable;
  4. with the LAN unplugged, YouTube waits, and switching the selector or rule makes it play over the phone;
  5. killing sing-box restores normal v1 internet within seconds.
- **Manual checklist** with the user after the build, like v1 Task 11.

## Open risks

- **Antivirus:** Kaspersky may block or warn about sing-box's virtual adapter. The spike checks this.
- **Domain lists drift:** CDNs change. The data file makes updates cheap, and the per-item byte counters show when an item stops matching.
- **Slow LAN:** routing video through a slow or flaky LAN can mean slower starts or buffering. That is the trade-off for saving data, and each item can be switched off.
