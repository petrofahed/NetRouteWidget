# NetRoute Widget v5.3: Slow is not "no reply" — Design

**Date:** 2026-10-20
**Status:** Approved; implemented test-first. The real-machine check on the congested LAN is still to be run by the user.
**Builds on:** `2026-10-02-netroute-widget-v1-design.md` (card, latency probe, auto-heal) and `2026-10-02-netroute-v2-smart-routing-design.md` (LAN offline, waiting list and popup).

## Goal

A congested but working connection must read as **slow**, not as **dead**. The card keeps "no reply" for a connection that really does not answer.

## The problem

The user's LAN currently has TCP handshake times of 0.3 to 2 s because of upstream congestion, while traffic still flows. `TcpLatencyProbe` tried 1.1.1.1:443 and then 8.8.8.8:443 one after another, with 2 s for each. A handshake that took just over 2 s was a miss. After two misses in a row (`RouteController.NoReplyAfterMissedChecks`) the card showed **"no reply"**, and that reads like "the LAN is down".

There was a second risk. With both targets slow, one check could take 2 s × 2 targets. The card polls every 5 s and does not wait for the previous refresh, so a refresh that runs for 5 s or more lets waiters pile up on `RouteController`'s gate.

## Design

### The race (`TcpLatencyProbe`)

- All targets are tried **at once**. The first target that connects wins, and its own handshake time is returned. The losers are cancelled, and the probe waits for them so their sockets are closed and their outcomes observed (no leaked sockets, no unobserved task exceptions).
- A target that fails fast (refused, unreachable, any `SocketException`) does **not** end the race. The others keep going.
- **One deadline** equal to the timeout covers the whole check. A check never takes longer than about one timeout, however many targets there are. The result is null when no target connects before the deadline.
- Caller cancellation still throws `OperationCanceledException`, as before. Only the probe's own deadline (or losing the race) maps to null.

### The 4 s deadline

`TcpLatencyProbe.Default()` uses **4 s** (was 2 s), with the same two targets.

- A congested but working link answers in 1 to 2 s, so 4 s leaves room for it.
- It must stay **under the 5 s visible poll**. The race makes the whole check last at most about 4 s, so a refresh ends before the next tick and refreshes never queue on the gate. A deadline of 5 s or more would bring the pile-up back.

### The slow threshold (`StatusPresenter.SlowLatencyMs = 500`)

- A reply **at or above 500 ms** shows its real figure ("1250 ms") with an **amber** dot: the connection answers, but slowly.
- A reply below 500 ms stays green.
- "no reply" is unchanged: amber dot, text "no reply".
- The row text format is unchanged. The card column is narrow: the latency sits between the live speed and today's total.

### What still keys on "no reply" only

Only a missing reply counts as a failure, as before. A slow reply is a good check.

- **Smart routing:** keys on the card's "no reply" (`LanLatencyMs == null`, two misses in a row). A slow but answering LAN is online. It builds no waiting list and raises no "waiting for the LAN" popup. `SmartRoutingController.LanOfflinePopupDelay` is unchanged.
- **Auto-heal:** uses each check's raw result. A check whose probe got no answer at all is a failed check. A slow but answering preferred adapter is a good check. `RouteController`'s `Smooth` / `NoReplyAfterMissedChecks` and the heal thresholds are unchanged.

## Testing

- **`TcpLatencyProbeTests`:**
  - a fast second target wins while the first hangs, and the check ends well inside the timeout;
  - a target refused at once does not end the race;
  - with every target hanging, the result is null within about one timeout (two targets, 500 ms timeout, under 900 ms);
  - caller cancellation still throws;
  - the losers are cancelled and their sockets closed before the result returns.
  - The existing tests stay unchanged.
- **Test seam:** no loopback endpoint hangs on Windows. A closed port, and a listener whose accept backlog is full, are both refused after about 2 s, because Windows retries the SYN after a reset. Measured: about 2040 ms for both. So the probe has a minimal internal `Connect` seam. The probe still creates, binds and disposes the real socket. Tests swap in a connect that never answers (until cancelled), or one that is refused at once, for chosen endpoints. Every other target connects for real to a loopback listener. No test needs internet access.
- **`StatusPresenterTests`:** 499 ms is green, 500 ms and 1250 ms are amber with their figures, and "no reply" is unchanged.
- **`RouteControllerTests`:** a LAN-mode preferred LAN at 1500 ms for twice the heal window: no heal, no toast, the metric unchanged, and the figure shown.
- **`SmartRoutingControllerTests`:** a LAN at 1500 ms past the popup delay, with a LAN-only dial failure: still online, no waiting list, no popup.

## Out of scope

- Separate thresholds per adapter, or a user setting for the threshold.
- A "slow" verdict for Smart routing or auto-heal: they act on "no reply" only.
- Other probes (`TcpEndpointProbe` for the path map keeps its own targets and timeout).
