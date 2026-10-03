# Spike: sing-box feasibility for v2 Smart routing

**Date:** 2026-10-02 · **Verdict: GO** · Throwaway; only these findings are kept.

## Setup
- **sing-box v1.14.2**, `sing-box-1.14.2-windows-amd64.zip` from the official GitHub release.
  - SHA-256 `c2d8bfff918755808781dfdeeb8581b6c91eb3a243d9a7b55483cfc0c0684d32`, matching the release asset digest.
  - Build tags include `with_clash_api`.
- **Target machine:**
  - Windows 11 Pro 22631, with Kaspersky (`avp.exe`) and NordVPN (disconnected) installed.
  - Phone: Samsung Note9 USB tethering, adapter "Ethernet 5", 192.168.42.11.
  - LAN: Realtek 2.5GbE, adapter "Ethernet", 192.168.86.42/24.
  - The v1 widget was running in Phone mode.
- **Config shape that worked.** `sing-box check` passed and the config ran elevated:
  - **TUN inbound:**
    - `address 172.19.0.1/30`, `auto_route: true`, `strict_route: false`, default `dns_mode` (hijack);
    - `route_exclude_address` covering the LAN and phone subnets, the RFC1918 ranges, link-local, multicast, `fc00::/7` and `fe80::/10`.
  - **Outbounds:** `direct` with `bind_interface` "Ethernet 5" (tag `phone`) and "Ethernet" (tag `lan`).
  - **DNS:** a single UDP server, `192.168.42.129`, with `detour: phone`.
  - **Route rules:**
    1. `{action: sniff}`
    2. `{protocol: dns, action: hijack-dns}`
    3. `{ip_is_private → lan}`
    4. `{process_name → lan}`
    5. `{domain_suffix → lan}`
    6. `final: phone`
  - **Clash API:** on `127.0.0.1:9090` with a secret.

## Results

| Check | Result |
|---|---|
| Starts alongside Kaspersky | ✅ No prompts or blocks. Kaspersky's own traffic flowed through the TUN. |
| Process rule: `OneDrive.exe` → LAN | ✅ |
| Domain rule: YouTube → LAN, including the `googlevideo.com` video stream (sniffed SNI) | ✅ 8.6 MB of video went over the LAN while other traffic stayed on the phone. Domain matching also caught a download manager (`IDMan.exe`) fetching YouTube. |
| Unmatched traffic → phone | ✅ |
| Home network reachable (router .1, .245, .197) | ✅ The routes were excluded from the TUN. |
| LAN unplugged: LAN-only traffic **waits**, no 4G fallback | ✅ There were 21 `dial tcp … i/o timeout` errors on `direct[lan]` and **0 YouTube connections went via the phone**. Other sites kept loading via the phone. |
| LAN replugged: traffic resumes | ✅ Confirmed by the user. |
| Clean stop: closing the window removes the TUN, and v1 routing is back | ✅ |

## Learnings that change the v2 plan
1. **OneDrive uses a second process**, `OneDrive.Sync.Service.exe`, which went via the phone. Add it to the OneDrive item along with the OneDrive domains.
2. **Waiting detection is easy.** A LAN-only dial fails with `ERROR … outbound/direct[lan]: dial tcp …: i/o timeout` after 5 s. Use those failures, together with the v1 LAN health probe, as the "waiting" signal. It is also visible through the Clash API connection list.
3. **The v1 card shows "Internet via other adapter"** while the TUN is active, because the best route is now the TUN. v2 must recognise its own TUN and show "Smart routing" instead.
4. **Processes seen on this machine**, which are candidate items or rules: `svchost.exe` (by far the most, as expected; matched by domain only), `GoogleDriveFS.exe` (Google Drive is in use), `ms-teams.exe`, `BackgroundDownload.exe`, `nordvpn-service.exe`, `avp.exe`, `IDMan.exe`.
5. **`strict_route: false` keeps router-provided local DNS names working.** With it, DNS for everything goes via sing-box to the phone's resolver, which is negligible data. Revisit only if DNS leaks matter.
6. **Interface names are bound by name.** A phone replug renames the adapter ("Ethernet 6"), so the config must be regenerated and sing-box restarted, as the spec already says.
