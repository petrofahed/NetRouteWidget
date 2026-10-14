# NetRoute Widget v5.1: Usage tracking while Smart routing is off — Design

**Date:** 2026-10-14
**Status:** Approved; implemented by `docs/superpowers/plans/2026-10-14-netroute-v5-1-usage-while-smart-off.md`. The real-machine probe and accuracy check are still to be run by the user.
**Builds on:** `2026-10-02-netroute-v3-usage-and-routing-design.md` (usage counter, Usage tab). That spec listed "Windows-level tracing (option B), if ever needed" as out of scope; this is that case.

## Goal

Keep the per-application Phone / LAN usage recording when Smart routing is off, so the Usage tab always answers "who is using my 4G package?", whatever mode the widget is in.

### What the user said

- "The tracking is still not working during Smart is off."
- Chose **app-level accuracy through Windows tracing** over site rows or an inexact connection poll.

### Understanding and assumptions

- Said: usage must keep recording with Smart off. Smart on keeps working as today.
- Assumed: the main use is watching which apps use the phone connection when the mode is Phone, and which use the LAN when the mode is LAN.
- Accepted limit: with Smart off there are no site rows (YouTube, Facebook, Windows Update items). Browser traffic counts under the browser's row (`app:chrome.exe`). Those rows exist only while Smart is on, because only sing-box sees host names.

## Why a new source is needed

Usage is counted from sing-box's Clash API. With Smart off, sing-box is not running, so nothing sees per-application traffic. Polling Windows' connection tables is not an option: the v3 spec measured that polling misses short connections (10 % accuracy), and the tables show no UDP/QUIC traffic at all. Windows' own per-app history (SRUM) is hourly at best and does not separate adapters.

## Design

### `WindowsTrafficTracker` (NetRoute.Core/Windows)

- Starts an ETW session on the provider `Microsoft-Windows-Kernel-Network` using the TraceEvent library (MIT). It is a **named user-mode session**, so it never fights over Windows' single "NT Kernel Logger". It needs administrator rights, which the widget already has when it can change routing.
- Subscribes to the TCP and UDP, IPv4 and IPv6 send and receive events. Each event carries the process ID, the byte count and two addresses.
- **Exit classification** (pure function, unit-tested): a packet counts for the **Phone** when one of its two addresses is **any IPv4 or IPv6 address of the Phone adapter**, and for the **LAN** when one is any address of the LAN adapter (the addresses are looked up by adapter name on every apply, because `AdapterInfo` only knows the IPv4 address and IPv6 is common on mobile and home lines). Both addresses are checked instead of trusting a field, because the local/remote order differs between event types. The *other* address is the remote one.
- **Ignored:** loopback, and remote addresses that are private, link-local, multicast or broadcast (the home network and the phone's own network are not counted, matching v3's "traffic to the home network is not counted"); the TUN and any other adapter (no match).
- **Direction:** a send event is upload, a receive event is download.
- **Process names:** resolved once per process ID and cached. A process that exits before it can be resolved counts under "Other".
- **Aggregation:** the trace thread adds into a table keyed by (process ID, exit) under a short lock (one dictionary update); `Drain` swaps the table out. It never touches the UI or the counter.
- `Drain()` returns and clears the table; `IsRunning` and the last start error are exposed for the UI and the log.

The adapter addresses come from the controller's current detection result (`Phone`/`Lan` `AdapterInfo.IPv4`), passed to the tracker as a resolver function so the tracker follows a replugged phone.

### Integration in `SmartRoutingController`

- It already owns the `UsageCounter`, the gate and the 1 s usage poll (`PollStatsAsync`). New: when sing-box is **not** running, the poll drains the tracker and calls a new `UsageCounter.AddMeasured(day, key, exit, traffic, now)`, which uses the same cell storage and live-rate queues as `Update`. The key comes from `UsageAttribution.Resolve(processName, host: null)`, so history stays continuous with Smart-on data (a built-in app item such as OneDrive keeps its row).
- When sing-box **is** running, the drained samples are **discarded**. sing-box's own traffic would otherwise appear twice, once on the TUN and once on the physical adapter.
- **Live speeds** (card and "Now phone/Now LAN" columns) come from the same rate queues, so they work with Smart off too. The card's live speed is no longer cleared when Smart is not running, as long as the tracker is running.
- **Phone adapter total:** while Smart is off, the poll also keeps adding the Phone adapter's Windows byte-counter growth to the day's `PhoneAdapterBytes`, so the footer line "Phone adapter total (exact, from Windows)" stays comparable with the Phone column.
- **Gaps:** when the tracker stops unexpectedly, `NoteGap` marks the day, like a sing-box stop does.
- `kept` (the "kept off 4G" figure) is not changed by tracked traffic: it is defined by Smart's LAN-only rules and stays 0 for it.

### Settings

None. The tracker runs whenever the widget is elevated and Smart is off; there is no extra switch (YAGNI).

### UI

- `UsageTab` banner: when the tracker is running and Smart is off, **"Smart routing is off: usage is counted per application by Windows (no website rows)."** replaces the old "recorded only while Smart routing is running".
- No admin rights, or the trace could not start: the old banner stays, and the log says why.

## Error handling

- A failed start (access denied, session name in use, provider missing) is logged once; the retry happens on a later poll, at most every 30 s. It never affects Smart-on tracking or routing.
- If the session is stopped by something else, the tracker notes the gap and restarts as above.
- A very high event rate stays cheap: one dictionary add per event, no allocation per packet beyond the event itself.
- The tracker is disposed at exit, which ends the trace session. A leftover session from a crashed run is stopped by name before starting a new one.

## Testing

- **Unit tests (Core):**
  - exit classification: Phone, LAN, neither, both addresses on one adapter, IPv6, loopback, every private/link-local/multicast range ignored;
  - `UsageCounter.AddMeasured`: cells, day rollover, row cap folding into "Other", live rates, dirty flag;
  - controller: drains and records while sing-box is off, discards while it runs, switching on and off mid-session counts nothing twice, no tracker (not elevated) leaves behaviour unchanged;
  - banner choice (pure presenter).
- **Throwaway probe (first plan step, run once by the user as admin):** a console tool that prints per-process bytes by adapter for 20 s and the Windows adapter counter growth, to confirm the real event fields (address order, UDP events, sizes) before the classifier is fixed. Anything built for it stays labelled throwaway.
- **Accuracy check on the real machine:** an admin script like `tools\Check-UsageAccuracy.ps1`: with Smart off, download a known amount through the active adapter and require the recorded growth for the downloading app to be within 10 % of it, and the Phone column to follow the adapter total within 10 % over a quiet hour.

## Out of scope

- Site/domain rows with Smart off (needs IP-to-name resolution).
- History from before this version.
- Per-connection detail, alerts, charts.
- Counting traffic to home devices.

## Open risks

- ETW field order and UDP coverage on this machine are verified by the probe before the classifier is finalised.
- The TraceEvent package brings native files (roughly 10 MB in the install folder) and needs an entry in THIRD-PARTY-NOTICES.md.
- Very short-lived processes count under "Other".
- Packets NATed for VMs/WSL carry no owning process and may be missing, so the per-app total can be below the adapter total.

## Probe results (2026-10-14, run by the user as administrator, 20 s, Smart routing ON)

- **Data event ids** (provider `Microsoft-Windows-Kernel-Network`, keywords 0x30): TCP IPv4 send 10 / receive 11; TCP IPv6 send 26 / receive 27; UDP IPv4 send 42 / receive 43; UDP IPv6 send 58 / receive 59. This matches `KernelNetworkEvents`.
- **Not data:** 12 (connect attempted), 13 (disconnect), 14 (retransmit), 15 (accepted), 16 (reconnect), and 18/34 ("protocol copied data on behalf of user"), which repeats the bytes of 11/27 and must stay ignored.
- **Payload names:** `PID`, `size`, `saddr`, `daddr`, `sport`, `dport` (plus others). Types: `PID` and `size` are `Int32`; IPv4 addresses are `Int32` read as little-endian (`16777343` is 127.0.0.1); IPv6 addresses are `Byte[]` (16 bytes). On receive events `saddr` is the local address and `daddr` the remote one (192.168.42.11 appeared as `saddr` of a receive), which is why the classifier checks both.
- **Seen on the wire:** sing-box's own traffic (discarded while it runs, as designed), loopback traffic of the widget to sing-box's API (ignored as local), UDP/QUIC traffic of browsers and other apps (events 43/59), and kernel traffic as `System` (pid 4).
- **Short-lived processes:** one process id (about 420 KB in 20 s) had already exited when the probe resolved its name. Such bytes count under "Other", as the open risks say.
