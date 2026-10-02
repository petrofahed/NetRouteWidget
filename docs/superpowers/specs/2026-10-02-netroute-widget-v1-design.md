# NetRoute Widget v1 — Design

**Date:** 2026-10-02
**Status:** Approved in brainstorming, pending written-spec review

## Goal

A small Windows tray app + floating card that lets a PC connected to **two networks at once** — a wired LAN and a phone's USB tethering — send **internet traffic through the phone** (the faster link) while **local devices (printer, NAS, other PCs) stay reachable over the LAN**.

### Background

On the target machine both adapters have a default route with the same effective metric, so Windows picks the internet path unpredictably:

| Adapter | Type | Example gateway | Effective metric |
|---|---|---|---|
| Phone | USB tethering (Remote NDIS) | 192.168.42.129 | 25 |
| LAN | Physical Ethernet | 192.168.86.1 | 25 |

Local traffic already works regardless of the default route, because Windows has an on-link route for the LAN subnet. The problem to solve is making the internet path deterministic and switchable.

### Roadmap (scope of this spec is v1 only)

- **v1 (this spec):** choose which adapter carries internet; local access always preserved.
- **v2:** site/IP exceptions routed via the LAN (static routes; domains resolved to IPs).
- **v3:** per-app routing (e.g. WinDivert-based), likely moving the routing engine into a Windows service.

## Requirements

### Modes

| Mode | Phone metric | LAN metric | Behavior |
|---|---|---|---|
| **Phone** (default) | 5 | 50 | Internet via phone; LAN default route kept as automatic fallback |
| **LAN** | 50 | 5 | Internet via LAN; phone kept as fallback |
| **Auto** | automatic | automatic | Windows' automatic metrics restored |

- Metrics are set per IPv4 and IPv6 interface. Routes are **never deleted**, so unplugging the preferred adapter falls back to the other one automatically.
- LAN-subnet traffic (printer, NAS) always goes over the LAN in every mode.
- When the app exits, the current routing stays in place.
- When the phone reconnects (Windows recreates the adapter with a new name/index and automatic metric), the app reapplies the current mode automatically.

### Status shown

- The adapter **actually** carrying internet, read live from Windows' best-route lookup for a public IPv4 address (1.1.1.1) and a public IPv6 address, not assumed from the mode. A note is shown when IPv6 takes a different adapter than IPv4.
- Up/down state of each adapter, its IPv4 address, and its latency (TCP-connect probe sourced from that adapter's address).
- A fallback state: mode is Phone, but the phone is offline and internet is going via the LAN (or the reverse in LAN mode).

## Architecture

Approach: **one elevated WPF app** (.NET 10). The routing logic lives in a separate class library so it can later move into a Windows service (v3) without a rewrite.

```
NetRouteWidget.sln
├─ src/NetRoute.Core        (class library, no UI)
│   ├─ AdapterDetector
│   ├─ RoutingEngine
│   ├─ NetworkMonitor
│   ├─ Settings
│   └─ Interop/  (IP Helper P/Invoke behind interfaces)
├─ src/NetRoute.App         (WPF: tray icon + floating card)
└─ tests/NetRoute.Core.Tests (xUnit)
```

### NetRoute.Core

- **AdapterDetector**: picks the phone and LAN adapters from the system adapter list.
  - Phone: up adapter whose interface description contains "Remote NDIS" (any Android/iOS USB tether).
  - LAN: up physical Ethernet adapter (not Remote NDIS) that has a default gateway.
  - Always ignored: virtual adapters (VMware, Hyper-V vEthernet, VPN tunnels such as NordLynx/WireGuard/TAP), loopback.
  - User overrides from Settings (stored by interface description and, for the LAN, MAC address) win over auto-detection.
  - If there is no candidate or more than one, it reports this as "not found / ambiguous" instead of guessing.
- **RoutingEngine**: `Apply(Mode)` sets interface metrics through the IP Helper API (`GetIpInterfaceEntry` / `SetIpInterfaceEntry`; `UseAutomaticMetric` for Auto), then reads them back to verify. It returns a result with the metrics actually applied, or an error with the reason.
- **Network monitoring** (implemented as a `Debouncer` plus a `RouteController` in Core, driven by the app):
  - listens to .NET's `NetworkChange.NetworkAddressChanged` / `NetworkAvailabilityChanged` and debounces bursts (about 1.5 s quiet period) into a single refresh;
  - also runs a poll refresh every 5 s while the card is visible and every 30 s while it is hidden, so a missed event heals itself;
  - determines the active internet interface with `GetBestInterfaceEx` for an IPv4 target (1.1.1.1) **and** an IPv6 target (2606:4700:4700::1111). If IPv6 resolves to a different adapter than IPv4 (e.g. only the LAN has an IPv6 default route), the card shows a warning note, because metrics can only choose between routes that exist;
  - measures per-adapter latency with a TCP connect to 1.1.1.1:443 from a socket bound to each adapter's IPv4 address (2 s timeout). Windows' strong-host model sends it out of that adapter. TCP is used because some networks drop ICMP.
- **Settings**: JSON at `%AppData%\NetRouteWidget\settings.json` holding the mode, adapter overrides, card position, card visibility and start-with-Windows. A missing or corrupt file means defaults are used and the file is rewritten.
- All Windows calls sit behind small interfaces (`IAdapterSource`, `IInterfaceMetrics`, `IRouteQuery`, `ILatencyProbe`) so the logic is unit-testable without touching real networking.

### NetRoute.App

- **Tray icon**:
  - a globe colored green (Phone), blue (LAN) or gray (Auto), with an amber badge while in fallback;
  - left-click toggles the card;
  - right-click menu: Phone / LAN / Auto (checked), Show card, Start with Windows, Quit;
  - tooltip such as "Internet: Phone (38 ms) · LAN ready".
- **Floating card** (about 260×140, borderless, rounded, always-on-top, follows Windows light/dark theme):

  ```
  ┌──────────────────────────────────┐
  │ 🌐 Internet via  PHONE      ⋮  ✕ │
  │ ──────────────────────────────── │
  │ ● Phone   192.168.42.11   38 ms  │
  │ ● LAN     192.168.86.42   12 ms  │
  │ ──────────────────────────────── │
  │ [ Phone ]  [ LAN ]  [ Auto ]     │
  └──────────────────────────────────┘
  ```

  - Dots: green = up with internet, amber = up but the latency probe fails, gray = disconnected. The row carrying internet is bold.
  - The header shows "Internet via LAN (phone offline)" in amber while in fallback.
  - The card can be dragged and its position is remembered. ✕ hides it to the tray.
  - The ⋮ menu offers: Start with Windows, choose adapters manually, open Windows network settings, Quit.
- **Toasts** appear only for automatic changes ("Phone disconnected — internet via LAN", "Phone back — internet via Phone"), never for the user's own clicks.
- **Start with Windows** creates or removes a Task Scheduler task (at logon, "run with highest privileges"), so the elevated app starts without a UAC prompt.
- **Single instance**: a second launch brings the existing card to the front.

### Data flow

1. Startup: load Settings → detect adapters → `RoutingEngine.Apply(mode)` → start NetworkMonitor → show tray (and the card, if it was visible last time).
2. User clicks a mode: Apply → verify → save Settings → refresh UI.
3. Network change (debounced): re-detect adapters → if the target adapter's metrics differ from the mode's expectation, re-Apply → recompute active interface and fallback state → update UI → toast if the active internet adapter changed because of the event.

## Error handling

- **Not elevated**: status-only mode. Mode buttons are disabled and a "Restart as admin" link is shown.
- **Apply fails or the verification read-back mismatches**: the error is shown in the card header, and the UI reflects the metrics Windows actually has. A failed mode change keeps the previous mode (not saved), and the next refresh re-asserts it.
- **Adapter override changed**: the previously used adapter that is no longer phone/LAN is reset to automatic metric, so a stale preferred metric cannot keep winning.
- **Adapter not found / ambiguous**: the card shows "Phone: not connected" or "Choose LAN adapter" and links to the adapter picker.
- **Settings missing or corrupt**: defaults are used and the file is rewritten; the event is logged.
- **Logging**: a rolling daily log in `%AppData%\NetRouteWidget\logs\` (7 days kept) records mode changes, adapter events, applied metrics and errors.

## Testing

**Unit tests (xUnit, written test-first) for Core:**

- adapter detection: phone + LAN + VMware/Hyper-V/VPN noise; a renamed phone adapter (new index/name); no phone; two LAN candidates; overrides;
- metrics produced for each mode, including Auto;
- active-interface → fallback state mapping;
- debounce: a burst of events produces one `NetworkChanged`;
- settings: defaults, round-trip, corrupt-file recovery;
- the reconnect rule: re-Apply only when metrics drift from the mode.

**Manual verification on the target machine:**

1. Phone mode: `Find-NetRoute -RemoteIPAddress 1.1.1.1` reports the phone adapter, and the printer/NAS is still reachable (by IP **and by name**).
2. Unplug the phone: internet continues via the LAN, a toast appears and the tray shows the amber badge.
3. Replug the phone: the new adapter is detected, the mode is reapplied and internet returns to the phone.
4. LAN and Auto modes: `Get-NetIPInterface` shows the expected metrics.
5. Reboot with Start with Windows on: the app starts with no UAC prompt and keeps the saved mode.

**Known follow-up:** if the NAS is reached by hostname and name resolution breaks when the phone is preferred (router-provided DNS names), add an NRPT/DNS suffix rule. This is tracked, not in v1.

## Out of scope for v1

- Site/IP exception rules (v2) and per-app routing (v3).
- VPN-aware behavior. A VPN's own routes take precedence, and its tunnel rides whichever adapter wins.
- Windows 11 Widgets-board integration, installer/MSIX packaging, auto-update.
