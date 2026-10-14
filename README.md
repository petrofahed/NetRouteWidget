# NetRoute Widget

A small Windows tray widget for PCs that are connected to **two networks at once**, such as a wired LAN and a phone's USB tethering.

- Sends **internet traffic through the phone**, through the LAN, or lets Windows decide, with one click.
- Keeps **local devices (printer, NAS, TV, other PCs) on the LAN** the whole time.
- Shows which connection is carrying internet right now, and the latency through each.
- When the preferred connection **disconnects**, internet falls back to the other one automatically, and you get a notification.
- **Auto-heal:** if the preferred connection is up but has **no internet** (for example, phone mobile data off, or the carrier blocking tethering), the widget switches to the other connection until it recovers. It waits until the connection has failed for a full minute, so short blips never move your traffic onto the other (maybe metered) connection. Turn it off entirely in the card's ⋮ menu or the tray menu.
  - Repeated failures make it wait longer before switching back.
  - If the preferred connection keeps failing, it stays on the working one and says so ("click Phone to retry").

## Features at a glance

| | |
|---|---|
| **Routing modes** (v1) | One-click **Phone / LAN** plus a **⚡ Smart** switch on the card, local devices always stay on the LAN, auto-heal when a connection has no internet, break-glass restore. |
| **Smart routing** (v2) | Keeps data-hungry traffic (Windows Update, cloud sync, game downloads, YouTube/social, your own apps and sites) off 4G by sending it over the LAN; waits for the LAN instead of falling onto mobile data; on-demand speed test. |
| **Usage tab** (v3) | Who used how much data: Phone and LAN (upload, download and total) per application or site for Today / 3 / 7 / 15 / 30 days, live **Now phone** and **Now LAN** speeds, filter box, accurate counting reconciled with Windows' own phone-adapter counter. |
| **Profiles** (v4) | **Phone + exceptions** and **LAN + exceptions**: choose which connection carries everything else; AI tools (Claude, ChatGPT, Codex, VS Code, Copilot, Gemini, Cursor, Perplexity) and `*.omantel.om` can use the phone while the rest uses the LAN; update downloads always use the LAN. |
| **Network path** (v5) | Click the card's status line, or the network icon next to ⏱ and ⚙, for a live map of the route from this PC to the internet over the LAN and over the phone: your router, the devices behind it, the internet. It shows where a connection stops answering, and says so only when the evidence supports it. |
| **On the card** | The live speed through each connection next to its latency, and today's total data through each connection at the right edge. |
| **Your own lists** | Right-click a row in the Usage tab to *Send to exception* / *Exclude from exception* (a tag marks exceptions), edit either profile's list on the Config tab, or manage the lists from JSON in `%AppData%\NetRouteWidget\rules.user.json`. |

## How it works

The widget only changes **interface metrics**, Windows' priority numbers for each connection:

| Mode | What it does |
|---|---|
| **Phone** | phone metric 5, LAN 50 |
| **LAN** | phone 50, LAN 5 |
| **Windows default** (tray menu only) | restores Windows' own automatic metrics (what Restore-Network.cmd does); the card has no Auto button, and Smart routing pauses while it is active |

It never deletes routes, so the other connection always remains a working fallback. LAN-subnet traffic always uses the LAN, because Windows has a direct route for it.

## Smart routing (v2)

Keep data-hungry traffic off 4G while the PC uses the phone:

- **Built-in items, all on by default** (each has its own toggle):
  - System updates: Windows Update, Microsoft Store
  - Cloud sync: OneDrive, Google Drive, Dropbox, iCloud
  - Video & social: YouTube, Facebook, Instagram
  - Game launchers: Steam, Epic, Battle.net, Xbox
  - AI & dev tool updates (always LAN): VS Code, Claude and Codex updates (see Profiles below)
- **Your own rules:** add any app (`.exe`) or website (subdomains are included).
- **How it works:** in the default **Phone + exceptions** profile, matching traffic goes through the LAN. If the LAN is down it **waits**, and a popup offers "Use phone until LAN is back". The other profile does the opposite, see Profiles below.
- **What it saves:** the card and the ⚙ page show how much was kept off 4G today.
- **Speed test:** ⏱ on the card measures both connections (about 5 MB of mobile data).
- **Live speed on the card:** while Smart routing runs, the card shows the current total speed through the phone and through the LAN (all applications together, over the last ~3 seconds, shown only above 1 KB/s) next to each latency. It measures what passes through Smart routing, so nothing is shown while Smart routing is off.
- **Today's total on the card:** to the right of each latency the card shows how much data went through that connection today (the same numbers as the Usage tab's Today total), while there is any history.

Smart routing runs the official [sing-box](https://github.com/SagerNet/sing-box) 1.14.2 as a helper process (see THIRD-PARTY-NOTICES.md). It needs administrator rights, runs in Phone and LAN modes and pauses in Auto mode. Items that go through the LAN wait while the LAN is down; they use the phone only if you click "Use phone until LAN is back". Turning it off, quitting the widget, or running `Restore-Network.cmd` stops it.

## Profiles (v4)

Smart routing has two **profiles**, and the card's **Phone** and **LAN** buttons switch between them (the tooltips say "Phone + exceptions" and "LAN + exceptions"). The card's **⚡ Smart** button is the on/off switch: on, the selected side takes everything except the exceptions list; off, everything goes through the selected side only.

- **Phone + exceptions** (Phone button): everything uses the phone, except the items on the first list (updates, cloud sync, video, game launchers, your own LAN rules), which go through the LAN. This is the Smart routing described above.
- **LAN + exceptions** (LAN button): everything uses the LAN, except the items on the second list, which go through the **phone**. If the LAN is unplugged, other traffic falls back to the phone, as in plain LAN mode. Your LAN list (the first list) stays active here too: it is not an exception of this profile, but it makes those downloads wait for the LAN instead of falling onto 4G. Explicit phone rules win over it for the same connection.
- **Starting phone list**, each item with its own switch and all on by default: Claude / Claude Code, OpenAI / ChatGPT, Codex, Visual Studio Code, GitHub Copilot, Gemini, Cursor, Perplexity, and Omantel (`omantel.om`, so every `*.omantel.om` site).
- **Update items always use the LAN:** VS Code updates, Claude updates and Codex updates are separate items, and they stay on the LAN in both profiles so big downloads never use mobile data. They are matched before the phone list, so a VS Code update is not caught by the Visual Studio Code item. In LAN + exceptions the other LAN-list items (YouTube, OneDrive, Steam and so on) and your LAN user rules stay active as well, exactly as in LAN mode before profiles: like any LAN item they wait while the LAN is down (the "waiting" popup can appear for them), while plain traffic and the phone exceptions never wait.
- **Config tab:** an **Editing:** selector (Phone + exceptions | LAN + exceptions) chooses which list you are looking at; the active profile is marked, and you can edit the other list without switching. Adding an app or website adds it to the list you are editing.
- **Usage tab:** right-click a row and choose **Send to exception** or **Exclude from exception**. It edits the active profile's list, the same one the Config tab shows, and an exception row carries a tag showing where it goes (`→ LAN` or `→ phone`). The menu is available while Smart routing is running.
- **Switching restarts Smart routing for about a second**, because the rules change; connections that are open at that moment are reset.
- **Best guesses:** the built-in process names and update domains are best guesses, not a guarantee. Check the Usage tab to see what really goes where; if one is wrong, fix it with **Edit rules file…** on the Config tab (`rules.user.json`, below) or in `rules\builtin.json`.

## Network path (v5)

Click the card's status line (it has a dotted underline) or the network icon next to ⏱ and ⚙ to open a map of the route from this PC to the internet, over the **LAN** and over the **phone**, side by side.

- **What it shows:** each device on the way (your router, the box behind it, your provider's gateway, and so on) with its latency and packet loss, and the internet at the end. Healthy links flow green; a link with loss turns amber; a located break is red with a ✕.
- **What it will and will not claim:** the connection's own internet check decides whether it is failing. A device is blamed only if its route probes **and** a direct ping to it were both lost in the same moments as the failed internet checks; missing route probes alone name nobody. A device that ignores probes while traffic still gets through is shown as "Ignores probes", never as broken, and a device that answers a direct ping is shown as reachable (that is not proof it passes traffic on). When the evidence is partial the window says the fault is "at or beyond" a device without naming one, and when it conflicts or is missing it says the "location is uncertain". The headline is the **routing** (which connection Windows uses); the lines under "Reachability" say whether each connection can reach the internet on its own. A route seen before a worse trace replaced it is kept and labelled "last known route (stale …)".
- **Calm states:** "Measuring…" for the first ten seconds, "Measurement unavailable" when this PC could not run a check (this is never counted as packet loss), and "Trace incomplete" when the route is longer than 12 hops or ICMP to the target is filtered (this is never a fault by itself).
- **How it measures:** ICMP echo with a chosen TTL (and a direct ping to the device's own address), sent from each connection's own address, once a second per device; a TCP connection to `1.1.1.1` (then `8.8.8.8`) every two seconds as the internet check. It runs only while the window is open and needs no administrator rights. IPv4 only.
- **Details:** click any device for its latency, loss, a 30-second sparkline and a plain-language note. See [the v5 spec](docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md).

## Editing the lists

The built-in lists are in `rules\builtin.json` next to the app; every install overwrites that file, so do not edit it. Your own changes go in `%AppData%\NetRouteWidget\rules.user.json`, which uses the same format and is merged over the built-in lists by item `id`:

- an item with a built-in `id` **replaces** that item (and keeps its place in the list);
- an item with a new `id` is **added**; it joins an existing built-in group only when BOTH the group `id` and `name` match (otherwise a second header appears), and a new group `id` makes a new group after the built-in ones;
- `{ "id": "games", "name": "Game launchers", "items": [ { "id": "xbox", "remove": true } ] }` **removes** the built-in item `xbox` (the item sits inside its group, as in the built-in file; naming an unknown `id` does nothing).

A missing file changes nothing. A broken file (bad JSON, a bad `exit`, a duplicate `id`, a phone carve-out) is ignored as a whole, with a line in the log, and the built-in lists are used, so it can never stop Smart routing from starting. `rules.user.json` must be plain JSON (no comments), or the whole file is ignored. Do not mix LAN and phone items in one group: a new item joins a built-in group only when the group `id` and `name` match AND it should use the same `exit` as that group (put items with another `exit` in a group of their own). Changes apply after you restart the widget. On the Config tab, **Edit rules file…** creates a small template if the file does not exist yet and opens it in your editor.

## Usage tab (v3)

The ⚙ window has two tabs, **Usage** and **Config**. Config is the Smart routing page described above; Usage shows who is using your data.

- **Recorded per application whether Smart routing is on or off.** With Smart on, sing-box counts it (including site rows such as YouTube). With Smart off, Windows network tracing counts it per application (no site rows; the widget needs administrator rights for this, and the tab says which source is counting). Traffic to your home devices is never counted.
- **Phone and LAN, each split into ↑ Up, ↓ Down and Total**, per application or site, for **Today, 3, 7, 15 or 30 days** (click a column header to sort; the default is Phone ↑, largest first, so the heaviest uploaders over 4G are on top). Upload is data your PC sent; download is data it received. Two live columns, **Now phone** and **Now LAN**, show the current upload and download speed on each connection over the last ~3 seconds (shown only above 1 KB/s, otherwise a dash). Each connection is counted under exactly one row, so the Total row is a plain sum with nothing counted twice. The window opens wide (about 1250 px) and scrolls sideways when it is narrower.
- **Filter box:** type part of a name (for example `youtube`) to narrow the list and its totals to the matching applications and sites; Esc clears it.
- **Right-click a row** to **Send to exception** or **Exclude from exception** for the active profile (in Phone + exceptions that keeps it off 4G, in LAN + exceptions it makes it use the phone). Rows that are exceptions show a tag (`→ LAN` or `→ phone`). Rules can also be changed on the Config tab, and the numbers themselves are never changed.
- **Unattributed:** connections that open and close between two one-second checks cannot be tied to an application, so they appear in an **Unattributed** row and the column totals stay right. The footer also shows the **exact phone adapter total** from Windows, to compare with the Phone column.
- **Stored in `%AppData%\NetRouteWidget\usage.json`** for 35 days: only application names and byte totals, never URLs. **Clear usage history…** (Config tab) deletes it. The "kept off 4G" figure comes from this file too.
- **Check the accuracy on your PC:** with Smart routing on and a quiet connection, run `tools\Check-UsageAccuracy.ps1`. It downloads a known amount and passes if the recorded growth is within 10 %. It only reads `usage.json`. With Smart routing **off**, run `tools\Check-UsageWhileSmartOff.ps1` from an administrator PowerShell instead.

## Install

Requires the .NET 10 Desktop Runtime. Run from an **administrator** PowerShell in the repo folder:

```powershell
dotnet publish src/NetRoute.App -c Release -r win-x64 --self-contained false -o "$env:ProgramFiles\NetRouteWidget"
& "$env:ProgramFiles\NetRouteWidget\NetRouteWidget.exe"
```

Then, in the card's ⋮ menu, turn on **Start with Windows**. This registers an elevated logon task, so later starts need no UAC prompt.

The install goes under *Program Files* on purpose. That folder is writable only by administrators, which matters because the widget starts elevated at logon.

## Undo everything

**Easiest:** double-click `Restore-Network.cmd` next to the exe (it is also in `tools/`). It:

- stops the widget and the Smart routing helper (sing-box);
- gives every adapter back to Windows' automatic priority;
- sets the widget to Auto mode and switches Smart routing off in its saved settings;
- offers to remove the startup task.

Run `Restore-Network.cmd /check` first to see what it would do without changing anything (it also reports whether a leftover `NetRoute` adapter is present).

> Note: the restore script resets **all** adapters that have a manual metric, including any you set yourself.

Manual equivalent (administrator PowerShell):

```powershell
Get-NetIPInterface | Where-Object AutomaticMetric -eq Disabled | Set-NetIPInterface -AutomaticMetric Enabled
schtasks /Delete /TN NetRouteWidget /F
Remove-Item -Recurse "$env:APPDATA\NetRouteWidget"
```

## Build and test

Requires the .NET 10 SDK; the version is pinned in `global.json`.

```powershell
dotnet test --filter "Category!=Integration"   # unit tests
dotnet test --filter "Category=Integration"    # read-only checks against this machine
dotnet run --project src/NetRoute.App
```

## Versions

- **v1:** Phone / LAN / Auto switch for the internet path, with local access preserved, auto-heal and a break-glass restore ✅
- **v2:** Smart routing. Keep data-hungry traffic (updates, cloud sync, game downloads, YouTube/social, and your own apps and sites) off 4G by sending it over the LAN, plus an on-demand speed test. See [the v2 spec](docs/superpowers/specs/2026-10-02-netroute-v2-smart-routing-design.md). ✅
- **v3:** A Usage tab: Phone and LAN use per application, date ranges, a filter box and live "Now phone" and "Now LAN" speed columns; accurate counting; the card shows the live speed through each connection. See [the v3 spec](docs/superpowers/specs/2026-10-02-netroute-v3-usage-and-routing-design.md). ✅
- **v4:** Smart routing profiles: the Phone / LAN buttons choose which connection carries everything else; each profile has its own exception list (AI tools and Omantel go through the phone in LAN + exceptions; update downloads always use the LAN), a Usage-tab right-click menu to move rows in and out of the exceptions, a user rules file merged over the built-in lists, an application icon, a roomier Usage grid, and today's total per connection on the card. See [the v4 spec](docs/superpowers/specs/2026-10-03-netroute-v4-profiles-design.md). ✅
- **v5:** A network path map: a live drawing of the route from this PC to the internet over the LAN and the phone, opened from the card, with per-device health and an evidence-based diagnosis of where a connection stops answering. See [the v5 spec](docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md). ✅

Design: [docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md](docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md)
