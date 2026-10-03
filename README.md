# NetRoute Widget

A small Windows tray widget for PCs that are connected to **two networks at once**, such as a wired LAN and a phone's USB tethering.

- Sends **internet traffic through the phone**, through the LAN, or lets Windows decide, with one click.
- Keeps **local devices (printer, NAS, TV, other PCs) on the LAN** the whole time.
- Shows which connection is carrying internet right now, and the latency through each.
- When the preferred connection **disconnects**, internet falls back to the other one automatically, and you get a notification.
- **Auto-heal:** if the preferred connection is up but has **no internet** (for example, phone mobile data off, or the carrier blocking tethering), the widget switches to the other connection until it recovers.
  - Repeated failures make it wait longer before switching back.
  - If the preferred connection keeps failing, it stays on the working one and says so ("click Phone to retry").

## How it works

The widget only changes **interface metrics**, Windows' priority numbers for each connection:

| Mode | What it does |
|---|---|
| **Phone** | phone metric 5, LAN 50 |
| **LAN** | phone 50, LAN 5 |
| **Auto** | restores Windows' own automatic metrics |

It never deletes routes, so the other connection always remains a working fallback. LAN-subnet traffic always uses the LAN, because Windows has a direct route for it.

## Smart routing (v2)

Keep data-hungry traffic off 4G while the PC uses the phone:

- **Built-in items, all on by default** (each has its own toggle):
  - System updates: Windows Update, Microsoft Store
  - Cloud sync: OneDrive, Google Drive, Dropbox, iCloud
  - Video & social: YouTube, Facebook, Instagram
  - Game launchers: Steam, Epic, Battle.net, Xbox
- **Your own rules:** add any app (`.exe`) or website (subdomains are included).
- **How it works:** matching traffic goes through the LAN. If the LAN is down it **waits**, and a popup offers "Use phone until LAN is back".
- **What it saves:** the card and the ⚙ page show how much was kept off 4G today.
- **Speed test:** ⏱ on the card measures both connections (about 5 MB of mobile data).

Smart routing runs the official [sing-box](https://github.com/SagerNet/sing-box) 1.14.2 as a helper process (see THIRD-PARTY-NOTICES.md). It needs administrator rights and runs in Phone and LAN modes and pauses in Auto mode. LAN-only items wait while the LAN is down; they use the phone only if you click "Use phone until LAN is back". Turning it off, quitting the widget, or running `Restore-Network.cmd` stops it.

## Usage tab (v3)

The ⚙ window has two tabs, **Usage** and **Config**. Config is the Smart routing page described above; Usage shows who is using your data.

- **Measured through Smart routing,** so history only grows while it is running. Traffic to your home devices and anything while Smart routing is off is not counted.
- **Phone and LAN columns** per application or site, for **Today, 3, 7, 15 or 30 days** (click a column header to sort; the default is Phone, largest first). Two live columns, **Now phone** and **Now LAN**, show the current speed on each connection over the last ~3 seconds (shown only above 1 KB/s, otherwise a dash).
- **Filter box:** type part of a name (for example `youtube`) to narrow the list and its totals to the matching applications and sites; Esc clears it.
- **The Usage tab only shows the numbers.** To keep an application or site off 4G (or let it use the phone again), change its rule on the Config tab.
- **Unattributed:** connections that open and close between two one-second checks cannot be tied to an application, so they appear in an **Unattributed** row and the column totals stay right. The footer also shows the **exact phone adapter total** from Windows, to compare with the Phone column.
- **Stored in `%AppData%\NetRouteWidget\usage.json`** for 35 days: only application names and byte totals, never URLs. **Clear usage history…** (Config tab) deletes it. The "kept off 4G" figure comes from this file too.
- **Check the accuracy on your PC:** with Smart routing on and a quiet connection, run `tools\Check-UsageAccuracy.ps1`. It downloads a known amount and passes if the recorded growth is within 10 %. It only reads `usage.json`.

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

## Roadmap

- **v1:** Phone / LAN / Auto switch for the internet path, with local access preserved, auto-heal and a break-glass restore ✅
- **v2:** Smart routing. Keep data-hungry traffic (updates, cloud sync, game downloads, YouTube/social, and your own apps and sites) off 4G by sending it over the LAN, plus an on-demand speed test. See [the v2 spec](docs/superpowers/specs/2026-10-02-netroute-v2-smart-routing-design.md).
- **v3:** A Usage tab: Phone and LAN use per application, date ranges, and live "Now phone" and "Now LAN" speed columns. It is a read-only view; rules are changed on the Config tab. See [the v3 spec](docs/superpowers/specs/2026-10-02-netroute-v3-usage-and-routing-design.md).

Design: [docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md](docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md)
