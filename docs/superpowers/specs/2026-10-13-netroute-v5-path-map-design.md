# NetRoute Widget v5 — Network path map (design)

**Status:** Draft for review. Nothing is implemented yet.
**Builds on:** v4 (profiles) and v1's adapter detection and auto-heal.
**Mockup:** [`docs/superpowers/mockups/2026-10-13-path-map-mockup.html`](../mockups/2026-10-13-path-map-mockup.html) (open it in a browser; sample numbers, interactive).

## Why

On 2026-10-13 the LAN kept dropping for 5–8 seconds every 30–60 seconds while the router still answered pings. A throwaway monitor showed the break was somewhere past the router, but only after a lot of manual work: tracing the hops with `ping -i`, then pinging each one. The widget already knows when a connection has no internet (auto-heal), but not **where** on the way it breaks. v5 shows that.

## Goal

Click the card and see a drawn map of the route from this PC to the internet over **both** connections (LAN and phone). Each device on the way shows live health, and the first one that stops answering is obvious.

## Decisions made (from the conversation)

1. **Live health**, not a static map: each device is green, amber or red with latency and packet loss, refreshed every second while the window is open.
2. **Both connections side by side**: the PC splits into a LAN branch and a phone branch that meet at the internet.
3. **Two ways to open it:** the card's status line becomes a link (dotted underline), and a new icon sits next to ⏱ and ⚙.
4. **Drawn natively in WPF** with vector shapes and the existing light/dark theme. No web view, no new dependency.
5. **Windows' own ICMP API** for the probes (not `ping.exe`, not .NET's `Ping`).
6. **Measures only while the window is open.** No background load, no new settings.

## What the user sees

- **Card:** the header text opens the window; so does a new network icon in the footer row. Both raise the same single window (a second click brings it to the front).
- **Window:**
  - Headline: the card's own status line (`StatusPresenter`), e.g. "Internet via PHONE (LAN has no internet)", amber when it is a fallback.
  - A one- or two-line diagnosis under it (see "Diagnosis").
  - The map: **This PC** on the left; a LAN lane on top and a phone lane below, each a chain of devices; both end at one **Internet** card with a LAN row and a Phone row.
  - A detail panel for the selected device: latency (average, best, worst), packets lost, a 30-second sparkline, and a plain-language note about what that device is.
  - Footer: "Live" indicator, a legend, and a Pause button.
- **Links** between devices are drawn as pipes: healthy links animate flowing green dashes, links with loss are amber and slower, a broken link is red with a ✕ and a pulsing ring, and every link after a break is grey ("not reachable"). The first problem device also gets a soft pulsing halo.
- **Devices:** a card with a status dot, a name, an address and `12 ms · 0%`. A device that never answers the trace is drawn dashed and labelled "Hidden hop" (carriers and some routers do this; it is not a failure). Clicking or pressing Enter on a device or on the Internet rows selects it.
- A lane whose adapter is missing or has no IPv4 is drawn dim with "Not connected" and is not probed.
- The window follows the Windows light/dark theme, and animations stop when Windows' animation setting is off or the user pauses.

## How it works

### Tracing

For each connected lane, send ICMP echo requests from that adapter's IPv4 address to `1.1.1.1` with TTL 1, 2, 3, ... (all TTLs in parallel, 1 s timeout each, at most 12). A device that sends back "TTL expired" reveals its address; the trace ends at the first TTL that gets a real echo reply from the target. A TTL that gets no answer is a **hidden hop** (kept in the chain so hop numbers stay right).

- Sending from the adapter's own address is what makes the two lanes independent of which one Windows currently prefers.
- A lane shows at most four devices between the PC and the Internet card; any further hops collapse into one "+N more" device that takes the worst state of its members and lists them in the detail panel.
- A trace repeats every 30 s, and immediately when the detected adapters or their addresses change, or when a probe's reply comes from a different address than the trace recorded ("path changed").

### Monitoring

Once a second, for each lane, probe every hop with its own TTL (the same packets as the trace). Every 2 s, run the widget's existing end-to-end check for the lane (`ILatencyProbe`: a TCP connection to `1.1.1.1:443`, then `8.8.8.8:443`, from the adapter's address), which feeds the Internet row. The TCP check runs at half rate on purpose: the earlier incident showed Windows logging ephemeral-port exhaustion on this machine, so the new feature must not add churn.

Each device keeps the samples of the last 30 s: replied or not, round-trip time.

### Health rules (pure, in `PathMapPresenter`)

| State | When |
|---|---|
| **No reply (red)** | the last 3 samples were all lost |
| **Losing packets (amber)** | at least 5% of the window's samples were lost, and not red |
| **Healthy (green)** | otherwise |
| **Hidden** | the trace never got an address for this hop |

- **Rate-limit rule.** Routers often answer TTL probes slowly or not at all while forwarding traffic fine (the classic traceroute caveat). Loss at a hop therefore **counts only if it continues**: the end-to-end check for that lane also had a failure in the window, or a later hop is also losing. Otherwise the hop is shown healthy and the detail panel says "slow to answer probes".
- **Break detection.** The first device in a lane that is red marks the break: the link into it is drawn broken and every later link is "not reachable". A lane whose devices are all healthy but whose end-to-end check fails shows the break on the last link into the Internet.

### Diagnosis (templated, tested)

- All healthy: "Both connections reach the internet."
- First red device H after device P: "The LAN path stops after P. H and everything after it do not answer." (P is "this PC" for hop 1.)
- First amber device H: "The LAN loses N% of packets at H, device k on its path. The devices before it are fine."
- Hops healthy but the internet check fails: "The LAN reaches every device on the way, but the internet check fails."
- One sentence per lane that has a problem; "LAN" and "phone" named as the card names them.

## Components

**Core (`NetRoute.Core`, no UI)**

| File | Responsibility |
|---|---|
| `Path/PathModels.cs` | `EchoStatus`, `EchoResult`, `PathHop`, `PathTrace`, `NodeState`, snapshot and view-model records |
| `Path/IPathProbe.cs` | `Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken)` |
| `Path/PathTracer.cs` | builds a `PathTrace` for one adapter with an `IPathProbe` |
| `Path/PathSamples.cs` | a 30 s rolling window per device: loss %, consecutive loss, average / best / worst |
| `Path/PathMonitor.cs` | owns both lanes while started; the 1 s and 2 s loops, re-tracing, a `Snapshot` event; `Start` / `Stop` |
| `Path/PathMapPresenter.cs` | snapshot → states, links, headline, diagnosis, detail for the selected device (the rules above) |
| `Windows/IcmpNative.cs` | `IcmpCreateFile`, `IcmpSendEcho2Ex`, `IcmpCloseHandle` interop and the reply structs |
| `Windows/IcmpPathProbe.cs` | `IPathProbe` over `IcmpNative` |

**App (`NetRoute.App`)**

| File | Responsibility |
|---|---|
| `PathMapWindow.xaml(.cs)` | window chrome, headline, diagnosis, detail panel, footer |
| `PathMapCanvas.xaml(.cs)` | draws the nodes and links from the view model; dash-flow and pulse animations |
| `Theme.cs` | new palette keys: `DotRed`, `NodeBackground`, `Track` (light and dark) |
| `CardWindow.xaml(.cs)` | header text as a link and the footer icon raise `PathMapRequested` |
| `App.xaml.cs` | `OpenPathMap()` like `OpenSmartRouting()`: one window; starts the monitor on open and stops it on close |

The monitor takes its adapters from the controller's latest `NetworkStatus.Adapters` and the headline from the same status, so the window and the card never disagree.

## Error handling and edge cases

- Probe errors (a Win32 error, a missing route, `IcmpSendEcho2Ex` returning an unexpected status) count as "no reply" for that sample and never throw into the UI; an exception in a loop is logged once and the loop continues.
- ICMP needs no administrator rights, so the window works when the widget is read-only.
- IPv4 only. The adapters' IPv6 paths are out of scope.
- If a lane's adapter changes mid-session, its samples are cleared and it is re-traced.
- Closing the window cancels all probes within one timeout.
- Smart routing's sing-box tunnel may be running; the feasibility check below ran with it on.

## Testing

- **`PathTracerTests`** with a fake probe: stops at the target, marks silent TTLs hidden, respects the 12-hop cap, detects a changed address.
- **`PathSamplesTests`:** window expiry, loss percentage, consecutive-loss count, best / worst.
- **`PathMapPresenterTests`:** every state in the table, the rate-limit rule (loss at a hop with a healthy end-to-end check stays green), break detection and "not reachable" links, a missing lane, and the diagnosis text for the three mockup situations (healthy, losing packets at hop 3, broken after hop 2).
- **`IcmpLayoutTests`** (like `IpHelperLayoutTests`): `ICMP_ECHO_REPLY` is 40 bytes on x64 with the expected field offsets.
- **Integration test** (like `WindowsIntegrationTests`): an echo from `127.0.0.1` to `127.0.0.1` returns a reply.
- **On-machine checks** (listed in the plan, run by the user): the real LAN and phone traces match the mockup's shape; unplugging the LAN cable greys the LAN lane; a deliberately blocked hop turns red; the window uses no CPU after it is closed.

## Feasibility check (done)

A throwaway PowerShell P/Invoke of `IcmpSendEcho2Ex` with a source address and TTL, run on 2026-10-13 on the user's PC with Smart routing active:

| Lane (source) | TTL 1 | TTL 2 | TTL 3 | TTL 4 | TTL 64 |
|---|---|---|---|---|---|
| LAN (`192.168.86.42`) | `192.168.86.1` | `192.168.1.1` | `172.17.18.1` | `10.9.8.249` | `1.1.1.1` echo reply, 52 ms |
| Phone (`192.168.42.11`) | `192.168.42.129` | `172.26.220.1` | `172.26.242.30` | no reply | `1.1.1.1` echo reply, 26 ms |

Both lanes trace independently, with status 11013 (TTL expired) at each hop. One correction to the mockup: the phone lane's carrier hops 2 and 3 **do** answer here, so the real phone lane shows devices, with hidden hops only where a hop truly stays silent.

## Out of scope

- Logging path changes and drops to the widget log (a natural follow-up: one line when the first problem hop changes while the window is open).
- A drop history or timeline.
- Running in the background when the window is closed.
- IPv6, VPN adapters, and tracing to a destination other than `1.1.1.1`.
- Using the path result to drive auto-heal decisions (a separate change).

## Open questions to settle in review

1. Is 5% loss over 30 s the right amber threshold, and "last 3 samples lost" the right red? They are cheap to change in one place.
2. Should the footer icon be a plain glyph (like ⏱ and ⚙) or the drawn network icon in the mockup? The mockup's drawn icon is the default.
