# NetRoute Widget

**v6.2 local source package:** implementation and internal phase checks are complete; final exact-commit checks/review are recorded in the [implementation plan](docs/superpowers/plans/2026-10-30-netroute-v6-2-three-connection-smart-routing.md). This is not a release or deployment. Real WPF interaction, TUN/network routing and the owner runtime matrix remain unverified. Prior PR #5 G1/G2 runtime results remain unreported; its merge did not establish a pass.

A small Windows tray widget for PCs that are connected to **multiple networks at once**, such as a wired LAN, a phone's USB tethering, and (from v6.0) Wi-Fi as a third path.

- Sends **internet traffic through the phone**, through the LAN, through Wi-Fi, or lets Windows decide, with one click.
- Keeps **local devices (printer, NAS, TV, other PCs) on the LAN** in Phone, LAN and Wi-Fi mode, even when Wi-Fi joins the same home network as the LAN cable (Windows default hands routing back to Windows entirely, so this does not apply there).
- Shows which connection is carrying internet right now, and the latency through each. The latency has two amber states:
  - **"1250 ms" in amber:** the connection answers, but slowly (500 ms or more, for example a congested line). Traffic still flows.
  - **"no reply" in amber:** two checks in a row got no answer within 4 seconds. This is also what Smart routing treats as the LAN being offline. A slow answer never counts as a failure, for Smart routing or for auto-heal.
- When the preferred connection **disconnects**, internet falls back to the next one automatically, in order **LAN → Wi-Fi → Phone**, and you get a notification.
- **Auto-heal:** if the preferred connection is up but has **no internet** (for example, phone mobile data off, or the carrier blocking tethering), the widget switches to the next connection in that same order until it recovers, and steps back up as higher-priority connections come back. It waits until the connection has failed for a full minute, so short blips never move your traffic onto a worse (maybe metered) connection. Turn it off entirely in the card's ⋮ menu or the tray menu.
  - Repeated failures make it wait longer before switching back.
  - If the preferred connection keeps failing, it stays on the working one and says so ("click Phone to retry").

## Features at a glance

| | |
|---|---|
| **Routing modes** (v1) | One-click **Phone / LAN** plus a **⚡ Smart** switch on the card, local devices stay on the LAN outside Windows default mode, auto-heal when a connection has no internet, break-glass restore. |
| **Smart routing** (v6.2 source) | Routes exceptions over Phone, LAN or Wi-Fi; Home/nonmetered rules wait before using mobile data, with target-specific explicit overrides. |
| **Usage tab** (v3) | Who used how much data: Phone and LAN (upload, download and total) per application or site for Today / 3 / 7 / 15 / 30 days, live **Now phone** and **Now LAN** speeds, filter box, accurate counting reconciled with Windows' own phone-adapter counter. |
| **Profiles** (v6.2 source) | **Phone + exceptions**, **LAN + exceptions** and **Wi-Fi + exceptions**; built-in AI/ISP items target Phone, and update carve-outs target Home ahead of app rules. |
| **Network path** (v5) | Click the card's status line, or the network icon next to ⏱ and ⚙, for a live map of the route from this PC to the internet over the LAN and over the phone: your router, the devices behind it, the internet. It shows where a connection stops answering, and says so only when the evidence supports it. |
| **Wi-Fi as a third connection** (v6.0) | Choose a Wi-Fi adapter in **Choose adapters** and route through it with its own card button and tray item, alongside Phone and LAN. Auto-heal now falls back in order (LAN → Wi-Fi → Phone), and local devices stay on the LAN cable outside Windows default mode, even when Wi-Fi joins the same home network. Smart routing supports all three paths in the v6.2 source package. |
| **Wi-Fi on the map, on/off switch** (v6.1) | The network path map draws one lane per connection you use, Wi-Fi included (LAN, Wi-Fi, Phone). Use the **Enabled** toggle beside each adapter in **Choose adapters**, then **OK**, to turn connections off or on. An off connection's row and mode button disappear from the widget; its toggle stays in Choose adapters. The card's coloured dots are shortcuts to turn a shown connection off. |
| **On the card** | Live speed and today's totals keep their existing Phone/LAN measurement display until v6.3; Wi-Fi routes correctly but has no separate usage bucket yet. |
| **Three Smart profiles** (v6.2 source) | Phone, LAN and Wi-Fi each have a Config list; user app/site rules choose another connection. Home rules use nonmetered paths and wait before using mobile data. |

## How it works

The widget mainly changes **interface metrics**, Windows' priority numbers for each connection. The chosen connection gets metric 5, and the others become graded backups in a fixed order (LAN, then Wi-Fi, then Phone, skipping whichever is the chosen one):

| Mode | What it does |
|---|---|
| **Phone** | phone metric 5, LAN 50, Wi-Fi 60 |
| **LAN** | LAN metric 5, Wi-Fi 50, phone 60 |
| **Wi-Fi** (v6.0) | Wi-Fi metric 5, LAN 50, phone 60 |
| **Windows default** (tray menu only) | restores Windows' own automatic metrics (what Restore-Network.cmd does); the card has no Auto button, and Smart routing pauses while it is active |

It never deletes routes, so the other connections always remain a working fallback. Local-subnet traffic (your cameras, NAS, printer and so on) stays on the LAN cable in Phone, LAN and Wi-Fi mode, and through healing between them — even when Wi-Fi joins the same home network as the LAN, which would otherwise let Wi-Fi's lower metric win that traffic. NetRoute checks for that overlap and, only when it is present, pins the LAN's own local-subnet route so it keeps winning; it un-pins the same route the moment the overlap goes away, on quit, or when you switch to Windows default, which hands routing back to Windows entirely and is excluded from this guarantee.

## Wi-Fi as a third connection (v6.0)

Wi-Fi now routes exactly like Phone and LAN do, with the same one-click card button and tray item.

- **Choosing it:** open **Choose adapters** (the card's ⋮ menu) to pick a Wi-Fi adapter, alongside the Phone and LAN adapters. Each box shows what is actually selected, never a hidden "Automatic": pick **Not used** to leave Wi-Fi out of routing entirely, or click **Detect automatically** to have NetRoute fill in a best guess for every box (the result is always shown, so you can change it). A Wi-Fi row that was previously chosen but is not currently connected is told apart from one you set to "Not used".
- **The Wi-Fi button:** once a Wi-Fi adapter is chosen, the card and tray menu gain a **Wi-Fi** button next to Phone and LAN. Click it to send internet traffic through Wi-Fi, the same way Phone and LAN already work.
- **Auto-heal's fallback order is now LAN → Wi-Fi → Phone:** mobile data is always the last resort. If your chosen connection loses internet, NetRoute heals to the next connection in that order that is actually answering, and steps back up as higher-priority connections recover, exactly as it already did for the two-connection case.
- **Local devices stay on the cable:** adding Wi-Fi does not risk your cameras, NAS or printer, in Phone, LAN or Wi-Fi mode and through healing between them. When Wi-Fi joins the same home network as the LAN (for example both on `pzhome`), NetRoute pins the LAN's local-subnet route so it keeps winning that traffic — see the metric table above. Windows default hands routing back to Windows entirely and is excluded from this guarantee.
- **Smart routing and Wi-Fi (v6.2 source):** Wi-Fi mode and healing onto Wi-Fi keep Smart running. It can run with only LAN or Wi-Fi and no Phone; Auto/Windows default still pauses it. Usage and live speed retain their existing two buckets until v6.3.

## Smart routing over three connections (v6.2 source)

The card's **Phone**, **LAN** and **Wi-Fi** buttons choose the main profile; **⚡ Smart** turns rule routing on or off. Config has a separate **Editing** button for each profile, with an active indicator. Editing another profile does not switch the main connection.

- **Phone + exceptions:** unmatched traffic uses the Phone. Built-in home items (updates, sync, video and games) use LAN, then Wi-Fi, and wait when neither answers.
- **LAN + exceptions / Wi-Fi + exceptions:** unmatched traffic uses that connection or its current healed physical path. Built-in AI tools and ISP items use Phone. Home and nonmetered rules stay protected through healing onto Phone.
- **Home is distinct from literal LAN:** Home prefers the profile's nonmetered main, then LAN/Wi-Fi in fallback order. A literal LAN or Wi-Fi rule tries its named connection first, then another answering nonmetered connection. Neither automatically spills onto Phone. A Phone-targeted rule may fall back to the current main.
- **Built-in updates:** VS Code, Claude and Codex update carve-outs remain Home and match before their apps' Phone rules. Built-in catalog exits remain `lan` (Home) and `phone`; a catalog `wifi` exit is invalid.
- **Your app/site rules:** each Config profile has its own list. Add an app (`.exe`) or website (subdomains included), then choose any other connection, including a configured but disconnected one. New defaults are LAN for the Phone profile and Phone for LAN/Wi-Fi profiles. Changing a destination preserves the rule's ID. Untouched old Phone-list rules retain null `Exit`, which means Home; explicitly choosing LAN means literal LAN.
- **Usage actions:** a user app/site row offers **Send to LAN / Wi-Fi / Phone**, excluding the rendered main, plus **Exclude from exception**. The menu captures its row, profile and destination when opened. Built-in Usage aggregate items retain their catalog Home/Phone target and offer enable/exclude. Rule edits apply to the captured profile against its latest settings.
- **Waiting:** the footer, Config and popup name the actual target. **Use <main> until <target> is back** applies only to that target/outage and warns when the main is Phone. A qualified but unanswered main may have a pending explicit grant; the target still waits and is not reported as using main until applied. Old popup actions cannot authorize a later outage/profile/binding context. Recovery requires three consecutive healthy checks; a miss resets the count, and a failed application is retried without announcing success.
- **Off / Not used:** the Smart config excludes that physical binding, including cached LAN names. A genuinely disconnected chosen adapter can retain its own observed binding; a different missing explicit choice cannot borrow a saved name from another adapter.

Smart routing needs administrator rights and the bundled sing-box helper. It pauses in Auto/Windows default or when no usable main path exists. Switching rules or selector membership can restart the helper and reset open connections. Turning Smart off, normally quitting the widget, or the restore tool stops it.

**Current measurement limits:** Usage history remains the existing Phone/LAN schema; Wi-Fi traffic is booked in the LAN/nonmetered bucket. Unknown physical chains are not guessed as Phone; aggregate totals and exact Phone-adapter reconciliation handle unattributed bytes without double counting. Card speeds/totals and the speed test retain their existing two-connection behavior. Three buckets, Wi-Fi speed test and network picker are later phases (v6.3/v6.4).

Built-in names/domains are best guesses. Use **Edit rules file…** in Config for `rules.user.json`; that file customizes the catalog, while per-profile app/site lists live in `settings.json`.

## Turning a connection off (v6.1)

Open **Choose adapters** and use the **Enabled** on/off toggle beside each adapter. **OK** applies the adapter choices and toggles together; **Cancel** or closing the dialog discards them. **Not used** has a disabled, off toggle; choosing an adapter after that starts it on. Changing between chosen adapters, or using **Detect automatically**, preserves an existing off toggle unless the slot becomes Not used.

Turning a connection **off** hides its row and mode button from the compact widget; its tray item remains disabled. NetRoute stops probing it, puts its metric back to automatic, never falls back to it and draws no path-map lane for it. If the card was in that connection's mode, it moves to the next connection (LAN → Wi-Fi → Phone). Turn it back on with its toggle in **Choose adapters**; your adapter choice is kept and its row and mode button return. The card dot is a shortcut to turn a shown connection off. A chosen connection that is merely disconnected remains visible. The setting survives a restart.

"Off" means NetRoute leaves the connection alone; it does **not** disable the adapter in Windows. With the cable still plugged in, Windows can still send local traffic over it, and if every other connection fails Windows may use it by its automatic metric (the card then says "Internet via LAN"). In v6.2 Smart routing excludes an off or Not-used LAN binding entirely; it cannot route through the old cached interface. See [the v6 spec](docs/superpowers/specs/2026-10-22-netroute-v6-wifi-connection-design.md).

## Network path (v5)

Click the card's status line (it has a dotted underline) or the network icon next to ⏱ and ⚙ to open a map of the route from this PC to the internet, one lane per connection you use: **LAN**, **Wi-Fi** (v6.1, headed with its network name) and **phone**, top to bottom. A connection set to *Not used* or turned off has no lane.

- **What it shows:** each device on the way (your router, the box behind it, your provider's gateway, and so on) with its latency and packet loss, and the internet at the end. Healthy links flow green; a link with loss turns amber; a located break is red with a ✕.
- **What it will and will not claim:** the connection's own internet check decides whether it is failing. A device is blamed only if its route probes **and** a direct ping to it were both lost in the same moments as the failed internet checks; missing route probes alone name nobody. A device that ignores probes while traffic still gets through is shown as "Ignores probes", never as broken, and a device that answers a direct ping is shown as reachable (that is not proof it passes traffic on). When the evidence is partial the window says the fault is "at or beyond" a device without naming one, and when it conflicts or is missing it says the "location is uncertain". The headline is the **routing** (which connection Windows uses); the lines under "Reachability" say whether each connection can reach the internet on its own. A route seen before a worse trace replaced it is kept and labelled "last known route (stale …)".
- **Calm states:** "Measuring…" for the first ten seconds, "Measurement unavailable" when this PC could not run a check (this is never counted as packet loss), and "Trace incomplete" when the route is longer than 12 hops or ICMP to the target is filtered (this is never a fault by itself).
- **How it measures:** ICMP echo with a chosen TTL (and a direct ping to the device's own address), sent from each connection's own address, once a second per device; a TCP connection to `1.1.1.1` (then `8.8.8.8`) every two seconds as the internet check. It runs only while the window is open and needs no administrator rights. IPv4 only.
- **Details:** click any device for its latency, loss, a 30-second sparkline and a plain-language note. See [the v5 spec](docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md).
- **Local network tab:** a second tab tests your own wired network apart from the internet path. It pings your router and any devices you add (a NAS, a printer, anything with an address or name; **Scan network…** lists what is on your subnet) once a second, shows this PC's network card (link speed, errors) and says in plain words whether the local network is healthy or whether the fault is beyond your router. Devices are saved in `settings.json`. See [the v5.2 spec](docs/superpowers/specs/2026-10-14-netroute-v5-2-local-network-design.md).

## Editing the lists

The built-in lists are in `rules\builtin.json` next to the app; every install overwrites that file, so do not edit it. Your own changes go in `%AppData%\NetRouteWidget\rules.user.json`, which uses the same format and is merged over the built-in lists by item `id`:

- an item with a built-in `id` **replaces** that item (and keeps its place in the list);
- an item with a new `id` is **added**; it joins an existing built-in group only when BOTH the group `id` and `name` match (otherwise a second header appears), and a new group `id` makes a new group after the built-in ones;
- `{ "id": "games", "name": "Game launchers", "items": [ { "id": "xbox", "remove": true } ] }` **removes** the built-in item `xbox` (the item sits inside its group, as in the built-in file; naming an unknown `id` does nothing).

A missing file changes nothing. A broken file (bad JSON, a bad `exit`, a duplicate `id`, a phone carve-out) is ignored as a whole, with a line in the log, and the built-in lists are used, so it can never stop Smart routing from starting. `rules.user.json` must be plain JSON (no comments), or the whole file is ignored. Do not mix LAN and phone items in one group: a new item joins a built-in group only when the group `id` and `name` match AND it should use the same `exit` as that group (put items with another `exit` in a group of their own). Changes apply after you restart the widget. On the Config tab, **Edit rules file…** creates a small template if the file does not exist yet and opens it in your editor.

## Usage tab (v3)

The ⚙ window has two tabs, **Usage** and **Config**. Config is the Smart routing page described above; Usage shows who is using your data.

- **Recorded per application whether Smart routing is on or off.** With Smart on, sing-box counts it (including site rows such as YouTube). With Smart off, Windows network tracing counts it per application (no site rows; the widget needs administrator rights for this, and the tab says which source is counting). That tracing runs only while the Smart routing window is open, so it costs no CPU otherwise; the usage recorded while the window is closed is missing, and the day shows a gap. Traffic to your home devices is never counted.
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
- restores any local-subnet route NetRoute pinned (v6.0's Wi-Fi route pin, see above) back to its normal metric;
- sets the widget to Auto mode and switches Smart routing off in its saved settings;
- offers to remove the startup task.

Run `Restore-Network.cmd /check` first to see what it would do without changing anything (it also reports whether a leftover `NetRoute` adapter is present, or a pinned local route that was never released).

> Note: the restore script resets **all** adapters that have a manual metric, including any you set yourself.

> **Downgrading from v6.0:** installing v6.0 keeps a one-time backup of your previous settings at `%AppData%\NetRouteWidget\settings.v5-backup.json`. If you go back to an older version, restore that file over `settings.json` first.

Manual equivalent (administrator PowerShell):

```powershell
Get-NetIPInterface | Where-Object AutomaticMetric -eq Disabled | Set-NetIPInterface -AutomaticMetric Enabled
Get-NetRoute -PolicyStore ActiveStore -AddressFamily IPv4 | Where-Object { $_.NextHop -eq "0.0.0.0" -and $_.RouteMetric -eq 0 } |
    Set-NetRoute -RouteMetric 256 -Confirm:$false
schtasks /Delete /TN NetRouteWidget /F
Remove-Item -Recurse "$env:APPDATA\NetRouteWidget"
```

## Build and test

Requires the .NET 10 SDK; the version is pinned in `global.json`.

```powershell
dotnet test --filter "Category!=Integration"   # unit tests
# Owner-only elevated/network run after reviewing the route baseline and isolation prerequisites:
dotnet test --filter "Category=Integration"    # mutates real routes, starts test proxies, accesses the internet
dotnet run --project src/NetRoute.App
```

## Versions

- **v1:** Phone / LAN / Auto switch for the internet path, with local access preserved, auto-heal and a break-glass restore ✅
- **v2:** Smart routing. Keep data-hungry traffic (updates, cloud sync, game downloads, YouTube/social, and your own apps and sites) off 4G by sending it over the LAN, plus an on-demand speed test. See [the v2 spec](docs/superpowers/specs/2026-10-02-netroute-v2-smart-routing-design.md). ✅
- **v3:** A Usage tab: Phone and LAN use per application, date ranges, a filter box and live "Now phone" and "Now LAN" speed columns; accurate counting; the card shows the live speed through each connection. See [the v3 spec](docs/superpowers/specs/2026-10-02-netroute-v3-usage-and-routing-design.md). ✅
- **v4:** Smart routing profiles: the Phone / LAN buttons choose which connection carries everything else; each profile has its own exception list (AI tools and Omantel go through the phone in LAN + exceptions; update downloads always use the LAN), a Usage-tab right-click menu to move rows in and out of the exceptions, a user rules file merged over the built-in lists, an application icon, a roomier Usage grid, and today's total per connection on the card. See [the v4 spec](docs/superpowers/specs/2026-10-03-netroute-v4-profiles-design.md). ✅
- **v5:** A network path map: a live drawing of the route from this PC to the internet over the LAN and the phone, opened from the card, with per-device health and an evidence-based diagnosis of where a connection stops answering. See [the v5 spec](docs/superpowers/specs/2026-10-13-netroute-v5-path-map-design.md). ✅
- **v5.2:** A Local network tab in the path window: ping your router and chosen devices, scan the subnet, and tell a local cable or switch problem from a fault beyond the router. See [the v5.2 spec](docs/superpowers/specs/2026-10-14-netroute-v5-2-local-network-design.md). ✅
- **v5.3:** A slow connection is no longer shown as dead: the latency check races both targets within one 4 s deadline, and a reply of 500 ms or more shows its figure in amber instead of "no reply". See [the v5.3 spec](docs/superpowers/specs/2026-10-20-netroute-v5-3-slow-vs-no-reply-design.md). ✅
- **v6.0:** Wi-Fi as a third, equal connection alongside Phone and LAN, with its own row, card button and tray item; auto-heal now falls back in order (LAN → Wi-Fi → Phone); local traffic stays on the LAN cable in Phone, LAN and Wi-Fi mode, even when Wi-Fi shares the LAN's home network, via a route pin. See [the v6.0 spec](docs/superpowers/specs/2026-10-22-netroute-v6-wifi-connection-design.md). ✅
- **v6.1:** Wi-Fi map lanes and Enabled toggles beside adapter selectors, with card dots as shortcuts. Source merged; G1/G2 owner runtime checks remain unreported.
- **v6.2 local source:** three Smart profiles, explicit user destinations, safe Home/nonmetered selectors and target-specific waiting/recovery. Internal source checks passed; final exact-SHA evidence is in the implementation plan. Not released/deployed; owner WPF/TUN/network checks remain pending.
- **Later:** v6.3 usage per connection; v6.4 Wi-Fi network picker.

Design: [docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md](docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md)
