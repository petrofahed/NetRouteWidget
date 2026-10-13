# NetRoute Widget v5 — Network path map (design)

**Status:** Implemented on branch feat/v5-path-map (PR #3); waiting for the user's on-machine verification.
**Builds on:** v4 (profiles) and v1's adapter detection and auto-heal.
**Mockup:** [`docs/superpowers/mockups/2026-10-13-path-map-mockup.html`](../mockups/2026-10-13-path-map-mockup.html), interactive, sample numbers. It shows the layout, the look and the two failure situations (loss, break). The calmer states in this spec (measuring, quiet, unavailable, trace incomplete) are specified in the tables below and get their visual treatment in the plan's UI task.

## Why

On 2026-10-13 the LAN kept dropping for 5–8 seconds every 30–60 seconds while the router still answered pings. A throwaway monitor showed the break was past the router, but only after a lot of manual work. The widget already knows *that* a connection has no internet (auto-heal), not *where* it breaks. v5 shows that, **and is careful to say so only when the evidence supports it.**

## Goal

Click the card and see a drawn map of the route from this PC to the internet over **both** connections (LAN and phone). Each device on the way shows live health. When a connection is failing, the map says where the break is when the evidence supports it, narrows it when it only partly does, and says "not enough data" when it does not.

## Decisions made

From the conversation:

1. **Live health**, not a static map. Both connections side by side.
2. **Two ways to open it:** the card's status line becomes a link, and a new **drawn network icon** (the one in the mockup) sits next to ⏱ and ⚙.
3. **Drawn natively in WPF**, existing light/dark theme, no new dependency.
4. **Windows' own ICMP API** for the hop probes.
5. **Measures only while the window is open.** No background load, no new settings.

From the review of draft 1:

- **An intermediate device that ignores probes is never called broken while the destination still responds**, and nothing that answers is greyed out.
- **A hop is blamed only with evidence from the same observation period as the endpoint failure**, and the wording says exactly how far the evidence goes (located / narrowed / not enough data).
- **A local measurement problem is "measurement unavailable", never packet loss.** A trace that hits the hop limit is "trace incomplete", never a broken connection.
- Thresholds below are **initial defaults**, all in one record (`PathThresholds`) so they can be tuned after real use.

## What the user sees

- **Card:** the header text opens the window; so does the new footer icon. Both raise the same single window (a second click brings it forward).
- **Window:** the card's own status line as the headline (`StatusPresenter`), one diagnosis line per connection, the map, a detail panel for the selected device, and a footer with a live indicator, a legend and a Pause display button.
- **Map:** **This PC** on the left; a LAN lane on top and a phone lane below, each a chain of devices; both end at one **Internet** card with a LAN row and a Phone row. Links are drawn as pipes with flowing dashes when healthy. A lane whose adapter is missing or has no IPv4 is drawn dim with "Not connected" and is not probed.
- **Device and link states** (every device and link is in exactly one):

| State | Looks like | Means |
|---|---|---|
| **Measuring** | blue-grey, gentle pulse, "Measuring…" | not enough samples yet to judge (startup, new trace) |
| **Answering** | green dot, flowing green link | answering its probes normally |
| **Quiet** | grey dashed outline, "Ignores probes", link stays green when the destination answers | not answering probes well, but there is no evidence against it (the destination still responds, or the loss does not line up with the failure) |
| **Hidden** | grey dashed outline, "Hidden hop" | never answered the trace (many carriers and some routers do this) |
| **Suspect** | amber outline, amber link | loses probes during the failure period, but not clearly enough to call it the break |
| **Broken** | red outline, red ✕ on the link into it, pulsing ring | the located break (see the fault rule) |
| **After the break** | grey dashed, "After the break" | silent devices after a located break. A device that answers is never in this state |
| **Measurement unavailable** | grey hatched, "Measurement unavailable" | the checks could not run on this PC (see outcomes). Says nothing about the network |
| **Trace incomplete** | a dashed ghost box "Not traced", labelled "Trace incomplete", after the last known device | the trace did not reach `1.1.1.1` within 12 hops. Says nothing about the network |

- A lane shows at most four boxes between the PC and the Internet card. Consecutive hidden hops share one box titled "Hops 2–3" (or "Hop 2"), labelled "Hidden hop", and anything beyond the fourth box collapses into "+N more", whose detail panel lists every member with its own state. The collapsed box never presents responding devices as unavailable, silent or quiet: if any member is Broken or Suspect the box is too (a fault claim about a specific member); otherwise, ignoring hidden members, a box whose members all answer is Answering, a mixed box (some answering, some not) is Answering and labelled "k of n answering", and a box in which no member answers takes the worst member state. The "Not traced" ghost box of an incomplete trace is drawn in addition to the four.
- The detail panel for a device shows latency (average, best, worst), packets lost, checks that could not be measured, a 30-second sparkline, and a plain-language note.
- The window follows the Windows light/dark theme. Animations stop when Windows' animation setting is off or the user pauses.

## How it works

### The outcome of one check

Every check ends in exactly one of three outcomes. They are never merged.

| Outcome | Meaning | Counts as loss? |
|---|---|---|
| **Reply** | an echo reply, a "TTL expired" reply from the device, or a TCP connect that succeeded | no |
| **NoReply** | the check ran and nothing came back within the timeout, or the network answered "unreachable" | **yes** |
| **Unavailable** | the check itself could not run or finished early: a Win32 or socket error on this PC (the adapter's address is gone, ephemeral ports or buffers exhausted, access denied, an unexpected ICMP status), or the check was cancelled or superseded | **no**: left out of the loss percentage, out of the consecutive-loss count, and out of the evidence counts |

Classification:

- **ICMP** (`IcmpSendEcho2Ex`): reply status 0 or 11013 (TTL expired) → Reply; send result 0 with Win32 error 11010, or reply status 11010 / 11002–11005 / 11018 (timed out, destination or host unreachable) → NoReply; everything else (resource, general-failure, buffer, option and parameter errors, any other Win32 error) → Unavailable.
- **TCP** (the internet check): connect succeeded → Reply; own timeout, `TimedOut`, `HostUnreachable`, `NetworkUnreachable`, `ConnectionRefused`, `ConnectionReset`, `ConnectionAborted`, `HostDown` → NoReply; `AddressAlreadyInUse`, `NoBufferSpaceAvailable`, `TooManyOpenSockets`, `AddressNotAvailable`, `AccessDenied`, `NetworkDown`, any other error, or caller cancellation → Unavailable. The internet check tries `1.1.1.1:443`, then `8.8.8.8:443`: any Reply → Reply; otherwise any Unavailable → Unavailable; otherwise NoReply. (The existing auto-heal probe treats every socket error as "no internet"; it is **not** changed, so auto-heal behaves exactly as today. The new probe is separate.)
- If the monitor is stopping when a check is cancelled, the sample is discarded rather than stored.
- A device or lane is shown **Measurement unavailable** when its last 3 samples are all Unavailable, or at least half of the window's samples are.

### Tracing and trace status

For each connected lane, send ICMP echo requests from the adapter's IPv4 address to `1.1.1.1` with TTL 1…12 in parallel (1 s timeout each). A device that sends "TTL expired" reveals its address; the first TTL answered by the target itself ends the path. A TTL with no answer is a **hidden hop** (kept so hop numbers stay right).

| Trace status | When | Shown as |
|---|---|---|
| **Complete** | the target answered at some TTL ≤ 12 | the devices before the target, then the Internet card |
| **Incomplete** | no TTL ≤ 12 got an answer from the target (the path is longer than 12 hops, or ICMP to the target is filtered) | the devices found (trailing hidden hops trimmed), then the "Trace incomplete" ghost, then the Internet card. **Never a fault by itself**: the lane's health is decided by the internet check |
| **Unavailable** | TTL 1 could not be measured, or at least half the TTLs were Unavailable | no devices are listed and the lane's health comes from the internet check alone (a failing check is "not enough data", an answering one "reaches the internet. The devices on the way could not be measured."); "Measurement unavailable" only when the internet check itself is Unavailable |

A trace repeats every 30 s, and sooner (at most once per 10 s) when a probe's reply comes from a different address than the trace recorded, or when the target answers at a lower TTL than expected. A hidden hop that starts answering is picked up by the periodic 30 s trace (hidden hops are not probed between traces, so silent devices cost nothing). A different address must repeat on 3 consecutive probes of the same device before it triggers a re-trace (a load-balanced device that answers from varying addresses must not keep the lane measuring); the target answering at a device's TTL triggers one at once. A change of the detected adapter or its address re-traces at once and clears that lane's samples.

**A degraded re-trace is not adopted on first sight.** If a re-trace finds fewer visible devices than the current one, or comes back Incomplete or Unavailable where the current one was Complete (typically because it ran during a drop and the devices past the break did not answer), the current trace and its samples are kept and the trace is retried after the 10 s gap; it is adopted only when the next trace is degraded too (a real path change is adopted about 10 s later). When a trace is adopted, each device keeps its samples if its position and address are unchanged; only changed or new devices start empty.

### Frequencies: probes versus the screen

| What | How often | Notes |
|---|---|---|
| Hop probes | **every 1 s** per device, all devices of a lane in parallel | ICMP, TTL = the device's hop number |
| Internet check | **every 2 s** per lane | TCP; half the hop rate on purpose, so it adds little ephemeral-port churn (the incident logged port exhaustion) |
| Trace | every 30 s, and on change | see above |
| Screen refresh | **at most every 500 ms** | a UI timer takes the monitor's latest immutable snapshot and re-renders. Probe results never trigger a redraw themselves |
| Animations | continuous, independent of data | drawn from the render clock, not from probe results |
| Pause display | freezes the screen and animations; the button says "Pause display" / "Resume display" and the footer says "Display paused. Checks continue in the background." | probing continues, so Resume display shows current data |

If no sample has arrived for 5 s, the footer says "No recent checks" instead of "Live".

### Per-device observation

Samples older than the **30 s window** are dropped. For each device (and for the internet check), from its samples in the window:

- **Valid** samples = Reply + NoReply. Unavailable samples are counted separately and never as loss.
- **Loss %** = NoReply ÷ Valid.
- **Consecutive lost** = the run of NoReply from the newest sample backwards, ignoring Unavailable samples.
- **Consecutive replies** = the same for Reply.

| Observation | Rule (first match wins) |
|---|---|
| **Hidden** | the trace never got an address for this hop |
| **Unavailable** | last 3 samples Unavailable, or ≥ 50% of the window Unavailable |
| **Silent** | consecutive lost ≥ **3** |
| **Measuring** | fewer than **10** valid samples (hops) or **5** (internet check) |
| **Lossy** | loss % ≥ **5** and consecutive replies < the recovery count |
| **Answering** | otherwise |

**Minimum sample count:** no judgement of loss before 10 valid samples for a hop (10 s) or 5 for the internet check (10 s). Three consecutive lost checks are enough to observe "Silent" earlier, but a Silent observation alone never produces a fault claim (next section).

**Startup "measuring":** when the window opens, or a lane is re-traced after an adapter change, the map is drawn from the trace immediately (addresses known) with every device **Measuring**, and the diagnosis line reads "Measuring… N of 10 s". A lane leaves Measuring when its internet check has 5 valid samples, or sooner when the internet check has 3 consecutive lost checks.

**Recovery rule:** a device or lane returns to **Answering** once it has had **10 consecutive replies** (hops, 10 s) or **5 consecutive replies** (internet check, 10 s) since its last lost check, even if older losses are still inside the 30 s window. This keeps the display from sticking on an old loss and from flapping on a single lucky reply. A fault clears when the internet check is Answering again.

### The fault rule

Raw observations are not conclusions. A lane gets one of these statuses from the **internet check** first:

| Internet check observation | Lane status |
|---|---|
| Unavailable | **Measurement unavailable.** No fault claim |
| Measuring | **Measuring** |
| Answering | **Healthy.** Devices that are Silent or Lossy are shown **Quiet**; nothing is called broken and nothing is greyed |
| Lossy or Silent with fewer than **3** failed checks in the window | **Blip** ("N internet check(s) failed; not enough to call a fault"). Devices shown as in Healthy |
| Lossy or Silent with ≥ **3** failed checks in the window | **Fault.** Run the localisation below |

**Localisation (only in a Fault).** All evidence comes from the same trailing window as the failure:

1. Cut the window into **2 s buckets**. A bucket is *failing* if the internet check lost in it, *good* if it replied. Buckets with only Unavailable checks are ignored.
2. For each non-hidden device, count its valid probes in failing buckets (`nF`, `lostF`) and in good buckets (`nG`, `lostG`). Its loss during failures is `lostF / nF`. Its loss during good periods is `lostG / nG`. A device can be Implicated only when `nG` ≥ **5**: without a real good-period baseline (for example when the window is opened during an outage) a device that ignores or rate-limits probes cannot be told from a broken one, so it is never implicated; it can still be Cleared.
3. A device is **Cleared** when `nF` ≥ **5** and its loss during failures ≤ **10%**: it answered while the internet check was failing, so traffic reaches at least that far.
4. A device is **Implicated** when `nF` ≥ **5**, its loss during failures ≥ **50%**, and it is at least **30 points** worse during failures than during good periods. A device that is silent during good periods too is just quiet; it is not implicated.
5. Everything else is **Unknown** (too little evidence, or not correlated).
6. Let **P** be the furthest Cleared device (or "this PC" when none is). The fault is after P. Let the **candidates** be the non-hidden devices after P.
   - **Located:** there is at least one candidate and **all** candidates are Implicated. The break is between P and the first candidate N.
   - **Narrowed:** P exists, but the candidates are mixed or Unknown, or there are no candidates (P is the last visible device). The fault is after P; no device is named as the break.
   - **Not enough data:** no device is Cleared and the candidates are not all Implicated.

**What each result draws:**

- **Located:** N is **Broken** (red, with the ✕ link) if it is Silent, otherwise **Suspect** (amber). Later candidates are **After the break** (grey dashed). Devices up to P are Answering, or Quiet when they ignore probes. A device that answers is never greyed.
- **Narrowed:** Implicated devices are Suspect; Cleared devices Answering; the rest Quiet. The link leaving P is amber dashed ("fault somewhere after here").
- **Not enough data:** every device keeps its calm display (Silent or Lossy shown as Quiet). No device is blamed.
- The Internet card's row for the lane shows the internet check's own state (no reply, losing, answering, measuring, unavailable).

### Diagnosis text (templated and unit-tested)

- Healthy, both lanes: "Both connections reach the internet." A missing lane: "LAN: not connected."
- Measuring: "LAN: measuring… 6 of 10 s." (one line per connection, lane-prefixed).
- Unavailable: "LAN: measurement unavailable on this PC. That says nothing about the connection."
- Blip: "LAN: 1 internet check failed in the last 30 s. That is not enough to call a fault."
- Located: "LAN: the break is between 192.168.1.1 and 172.17.18.1. While the internet check failed, 192.168.1.1 answered and 172.17.18.1 lost 38% of its probes." (P is "this PC" when none is cleared; "did not answer" replaces the percentage when it lost every probe.)
- Narrowed: "LAN: the internet check is failing. The last device that answered during the failures is 192.168.1.1, so the fault is after it, but the evidence does not say exactly where." When P is the last visible device: "…so the fault is beyond it or on the internet side."
- Not enough data: "LAN: the internet check is failing, but there is not enough data yet to say where."
- Incomplete trace adds one sentence to the detail panel and the ghost device's note, never to a fault claim: "The trace did not reach 1.1.1.1 within 12 hops, so devices further on are not listed."

## Components

**Core (`NetRoute.Core`, namespace `NetRoute.Core.PathMap`, no UI)**

| File | Responsibility |
|---|---|
| `PathMap/PathModels.cs` | `ProbeOutcome`, `Sample`, `EchoResult`, `EndpointResult`, `IPathProbe`, `IEndpointProbe`, `PathHop`, `PathTrace`, `TraceStatus` |
| `PathMap/PathThresholds.cs` | every default above in one record |
| `PathMap/SampleStats.cs`, `PathSamples.cs` | the rolling window and the statistics (loss, consecutive runs, latency) |
| `PathMap/PathObserver.cs` | sample statistics → `Observation` |
| `PathMap/PathTracer.cs` | one trace for one adapter |
| `PathMap/PathDiagnoser.cs` | lane status, evidence classification, localisation, per-device display state |
| `PathMap/PathMapPresenter.cs` | snapshot + `NetworkStatus` → the view model (nodes, links, texts, detail) |
| `PathMap/PathMonitor.cs` | the two probe loops, retracing, immutable snapshots, start/stop |
| `Windows/IcmpNative.cs`, `Windows/IcmpPathProbe.cs` | the ICMP interop and the `IPathProbe` over it, including the classification above |
| `Windows/TcpEndpointProbe.cs` | the internet check with the three-way outcome |

The namespace is `PathMap`, not `Path`, so `System.IO.Path` keeps resolving inside `NetRoute.Core`.

**App (`NetRoute.App`)**

| File | Responsibility |
|---|---|
| `PathMapWindow.xaml(.cs)` | window chrome, headline, diagnosis lines, detail panel, footer; owns the 500 ms refresh timer |
| `PathMapCanvas.cs` | draws nodes and links from the view model; animations from the render clock |
| `Theme.cs` | new palette keys: `DotRed`, `NodeBackground`, `Track`, `DotBlue` |
| `CardWindow.xaml(.cs)` | header text as a link and the footer icon raise `PathMapRequested` |
| `App.xaml.cs` | `OpenPathMap()` like `OpenSmartRouting()`: one window; starts the monitor on open and stops it on close |

The monitor takes its adapters from the controller's latest `NetworkStatus.Adapters`, and the headline comes from the same status, so the window and the card never disagree.

## Error handling and edge cases

- A probe that fails to run is an **Unavailable** sample, never loss (see outcomes). An exception in a monitor loop is logged once per distinct message and the loop continues.
- ICMP needs no administrator rights, so the window works when the widget is read-only.
- IPv4 only.
- Closing the window cancels all probes within one timeout; cancelled samples are discarded.
- A sample whose lane was re-traced while the probe was in flight is dropped (stale generation).
- Smart routing's sing-box tunnel may be running; the feasibility check below ran with it on.

## Testing

- **Core, pure and fake-probe tests:** outcome statistics (Unavailable never counts as loss), the observation table, the recovery rule, the tracer (complete, incomplete at the hop limit, unavailable, hidden hops), the diagnoser (healthy with a quiet hop, blip, located, narrowed, not enough data, a downstream device that answers is not greyed, correlation uses the same window, insufficient evidence refuses to name a hop, measuring, unavailable), the presenter (collapsing, link states, every diagnosis sentence), and the monitor (rounds with a `FakeTimeProvider`, stale generation, cancellation discards).
- **Windows layer:** `IcmpLayoutTests` (`ICMP_ECHO_REPLY` is 40 bytes on x64), the ICMP classifier table, the TCP classifier table, and one integration test (an echo from `127.0.0.1` to `127.0.0.1` returns a Reply).
- **App:** builds clean; on-machine checks listed in the plan (real traces match the feasibility table's shape; unplugging the LAN cable shows "Not connected"; the window uses no CPU after it closes).

## Feasibility check (done)

A throwaway PowerShell P/Invoke of `IcmpSendEcho2Ex` with a source address and TTL, run on 2026-10-13 on the user's PC with Smart routing active:

| Lane (source) | TTL 1 | TTL 2 | TTL 3 | TTL 4 | TTL 64 |
|---|---|---|---|---|---|
| LAN (`192.168.86.42`) | `192.168.86.1` | `192.168.1.1` | `172.17.18.1` | `10.9.8.249` | `1.1.1.1` echo reply, 52 ms |
| Phone (`192.168.42.11`) | `192.168.42.129` | `172.26.220.1` | `172.26.242.30` | no reply | `1.1.1.1` echo reply, 26 ms |

Both lanes trace independently, with status 11013 (TTL expired) at each hop. The phone lane's carrier hops 2 and 3 answer here, so the real phone lane shows devices; hidden hops appear only where a hop truly stays silent.

## Out of scope

- Logging path changes and drops to the widget log (a natural follow-up: one line when the first problem hop changes while the window is open).
- A drop history or timeline; running with the window closed; IPv6; VPN adapters; tracing to a destination other than `1.1.1.1`.
- Changing auto-heal or using the path result to drive it.
- The existing LAN monitor script is untouched and unrelated to the product code.

## Defaults to tune after real use

All in `PathThresholds`: 30 s window, 10 and 5 minimum samples, 5% loss, 3 consecutive lost checks, 10 and 5 recovery replies, 3 failed checks for a fault, 5 evidence probes, 10% / 50% / 30-point evidence cut-offs, 2 s buckets, 12 hops, 4 drawn boxes.
