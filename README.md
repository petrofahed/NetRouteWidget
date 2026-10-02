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

- stops the widget;
- gives every adapter back to Windows' automatic priority;
- sets the widget to Auto mode;
- offers to remove the startup task.

Run `Restore-Network.cmd /check` first to see what it would do without changing anything.

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

Design: [docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md](docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md)
