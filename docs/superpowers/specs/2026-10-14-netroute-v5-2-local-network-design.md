# NetRoute Widget v5.2: Local network tab — Design

**Date:** 2026-10-14
**Status:** Approved; implemented by `docs/superpowers/plans/2026-10-14-netroute-v5-2-local-network.md`. The real-machine check is still to be run by the user.
**Builds on:** `2026-10-13-netroute-v5-path-map-design.md` (the network path window, `PathMonitor`, `IPathProbe`, `PathSamples`, `SampleStats`, `PathThresholds`).

## Goal

Add a second tab, **Local network**, to the network path window. It tests the wired local network (this PC's network card and cable, the switch, the router, the NAS and any other device the user adds) separately from the internet path, so the user can tell "my local cabling is fine, the fault is beyond my router" from "something on my local network is wrong".

### What the user said

- "Add a tab for testing the internal network. It could be the NAS or any other device." The NAS is at 192.168.86.44.
- The path map keeps showing "the break is between 192.168.86.1 and 192.168.1.1" (router → the next device). The user wants to tell whether the cables and the switch on their side are healthy.
- Approved this design: a device list (router first, devices the user adds, a scan to pick from), one ping per device per second, a plain-words verdict, a card for this PC's network card, and no file-copy speed test in the first version.

### Understanding and assumptions

- Said: test local devices from the PC, in a new tab of the path window; the NAS and any device.
- Assumed: the user's LAN is a normal IPv4 home network of at most a /24 (254 hosts); the interesting question is loss and latency on the local segment, which pings answer well.
- Accepted limits: no throughput test (a cable can show good pings but low speed; the card's link speed and error counters are the cheap hint), IPv4 only, devices that never answer pings are shown as such (no TCP fallback).

## The tab

Top to bottom:

1. **Verdict** (plain words, one to three lines, see Verdict).
2. **This PC's network card:** the LAN adapter's name, its link speed (`NetworkInterface.Speed`), and the error and discard counters (`GetIPStatistics`: incoming/outgoing errors and discards) as the **change since the tab was opened**. Link speed below 1 Gbps on an Ethernet adapter is amber with the note "Gigabit is normal on modern cables; 100 Mbps often means a damaged cable or an old device in the path". Counters that grow while the tab is open are amber.
3. **Device list:** one row per device: status dot (the path map's colours), name, address, average latency and loss over the 30 s window (the latency graph is in the detail panel below the list). The first row is always **Router** (the LAN adapter's default gateway, read from `IPInterfaceProperties.GatewayAddresses`). The rest are the user's devices. Clicking a row opens the same detail panel (stats and latency graph) the path map uses.
4. **Buttons:** **Add device…** (name plus IPv4 address or hostname), **Remove** (the selected user device; the router row cannot be removed), **Scan network…**.

The existing content of the window (headline, diagnosis, map, detail, legend) becomes the first tab, **Internet path**, unchanged. The **Pause display** button stays on the Internet path tab and freezes that tab only; the Local network tab updates twice a second and needs no pause.

## Measuring

- A new `LocalNetworkMonitor` (Core/PathMap) sends one ICMP echo per device per second, from the LAN adapter's IPv4 address, TTL 64, timeout 1 s, through the existing `IPathProbe`. It keeps the last 30 s per device in `PathSamples` and hands out immutable snapshots; it never touches the UI. Rounds start on a fixed rate with at most 3 outstanding, like `PathMonitor`.
- It runs on its own `CancellationToken` from the moment the tab is first opened until the window closes. Nothing runs while the window is closed.
- **Resolution:** a hostname is resolved the first time it is probed and again after a minute (a name that failed is asked again after 10 s). A name that does not resolve gives state "Name not found" (Unavailable samples), never "no reply".
- **States** reuse `HopDisplay` and `SampleStats`/`PathThresholds`: Measuring until `HopMinSamples` valid samples; Answering; Suspect (loss at or above `LossyPercent`); Broken (`SilentConsecutive` lost in a row after it answered); Silent (never answered in a full window: "doesn't answer ping").
- **No LAN adapter** (cable out): the monitor idles and the tab says "No LAN connection".

## Verdict

A pure function (`LocalVerdict`) of: the router's and each on-subnet device's state and loss, and the LAN lane's internet state from the path map (`LaneView.Internet.State`: Broken/Suspect/Silent = failing, Answering = fine, Measuring = unknown). Devices outside the LAN subnet are listed but ignored by the verdict. Rules, first match wins:

1. Fewer than `HopMinSamples` valid samples for the router: "Measuring… N of 10 s".
2. The router and at least one other on-subnet device lossy or silent-after-answering: "Several devices on your local network are losing packets. Check this PC's cable and its switch port, and the switch."
3. The router fine but two or more other on-subnet devices lossy: "Several devices are losing packets but the router is fine: check the switch they share and their cables."
4. Exactly one on-subnet device (not the router) lossy: "<name> is losing packets while the router and the others are fine: check that device's cable and switch port."
5. The router lossy but every other measured on-subnet device fine: "The router answers pings badly but your other devices are fine: the router may be de-prioritising pings. This does not point to a cable."
6. The router lossy and no other device measured: "The router is losing packets. Add another device (like your NAS) to tell a PC-side cable problem from a router problem."
7. Everything local healthy and the internet failing: "Your local network is healthy: <list> answer(s) normally. The fault is beyond your router (its link to the modem, the modem, or the line)."
8. Everything local healthy and the internet fine or unknown: "Your local network is healthy."

The same machine listed twice (the router added by hand after a scan, two names with one address) is judged once: the router wins, then the first device with that address; the duplicates stay in the list. A device that never answers pings (grey, "doesn't answer ping") and a name that was not found count as neither healthy nor bad. With nothing healthy and nothing bad, the verdict says "No device on your local network can be measured yet. Add your NAS or another device that answers pings."

## Adding devices

- **Add device…** opens a small dialog (like the existing add-rule dialog): name (optional, defaults to the address) and an IPv4 address or hostname. Validation: address must parse as IPv4 or be a valid hostname (at most 253 characters, letters/digits/dots/hyphens); name at most 40 characters; no duplicate address; at most 20 devices. Errors show in the dialog.
- **Scan network…** opens a dialog that scans the LAN adapter's subnet and lists the devices found (address, name, MAC); selecting one fills the add dialog. Details under Scan.
- The list is saved in `settings.json` as `LocalDevices` (name and address, in order) and restored. On load, an entry that fails validation is dropped; the rest of the settings load normally.

## Scan

- Subnet: the LAN adapter's address and prefix length; a prefix shorter than /24 scans the /24 around the address; a longer prefix scans its own subnet. Never more than 254 addresses.
- Method: an ICMP echo to every address in the subnet (64 at a time, 500 ms timeout), merged with Windows' neighbour table (`GetIpNetTable`, the IPv4 table, which also lists devices that do not answer pings but have talked to this PC). Names by reverse DNS with a 1 s timeout (8 at a time); the MAC comes from the neighbour table.
- User-triggered only; takes a few seconds; shows progress; can be cancelled; results are not saved. Only the user's own subnet is touched.

## Architecture

- **NetRoute.Core/PathMap:** `LocalDevice` (record: Name, Address), `LocalNetworkMonitor`, `LocalNetworkPresenter` (monitor snapshot + NIC stats + internet state → view model, calls `LocalVerdict`), `LocalVerdict`, `NetworkScanner` (uses `IPathProbe` and an `INeighborTable`), `LocalDeviceValidator` (name/address rules, shared by the dialog and the settings loader).
- **NetRoute.Core/Windows:** `NeighborTable` (`GetIpNetTable`), `WindowsNetworkInfo` (adapter speed, gateway, prefix length and counters by name) and `HostResolver` (name lookups with short timeouts); the first two sit behind small interfaces so tests use fakes.
- **NetRoute.Core:** `AppSettings.LocalDevices` (default empty) and its sanitising on load in `SettingsStore`.
- **NetRoute.App:** `PathMapWindow` gains a `TabControl` (its current content moves under "Internet path"); a `LocalNetworkTab` user control (same refresh timer as the window); `AddDeviceWindow` and `ScanWindow` dialogs; `App` starts the local monitor with the window and passes settings read/write.

## Error handling

- Probe failures are Unavailable samples, never exceptions (the `IPathProbe` contract). A round that throws is logged once per message and the next round runs; if the monitor task itself faults it is logged.
- A failing neighbour-table or statistics read leaves that part blank ("not available"); the rest of the tab works.
- The scan never throws into the UI: a failure shows "Scan failed: <reason>" in its dialog.
- Saving settings goes through the existing `UpdateSettings` and its unreadable-file protection.

## Testing

- **Unit tests (Core, fakes only):** `LocalVerdict` for every rule and the minimum-samples gate; `LocalNetworkPresenter` rows, states and NIC-card flags (speed below 1 Gbps, counters growing); `LocalNetworkMonitor` rounds with a fake probe (loss, latency, outstanding cap, adapter change, name not found, no adapter); `NetworkScanner` (subnet rules for /16, /24, /28, merging pings with neighbours, names, cancellation, the 254 cap); `LocalDeviceValidator` (every rule, duplicates, the cap); settings round trip and dropping of invalid saved devices.
- **Real machine (read-only, by the user):** the Router row and the NAS (192.168.86.44) show 0% loss and sub-millisecond latency, matching a manual `ping`; the card shows 1 Gbps and zero errors; unplugging the NAS shows it red within a few seconds; Scan lists the NAS.

## Out of scope

- A file-copy throughput test to a share (the natural next version).
- IPv6 devices, TCP fallback probes for devices that block pings, alerts, history beyond 30 s.
- Scanning anything but the LAN adapter's own subnet; vendor names from MAC addresses.

## Open risks

- Some NAS or IoT devices rate-limit or ignore pings and will show as lossy or silent without being faulty; the verdict wording for a single lossy device says "check", not "faulty".
- Reverse DNS on a home router can be slow or empty; names are best effort.
- A PC with a VPN or several Ethernet adapters: only the adapter the app treats as LAN is used.
