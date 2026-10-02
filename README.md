# NetRoute Widget

A small Windows tray app + floating card for PCs that are connected to two networks at once, such as a wired LAN and a phone's USB tethering.

- Send **internet traffic through the phone** (or the LAN) with one click.
- Keep **local devices (printer, NAS, other PCs) reachable over the LAN** the whole time.
- See which connection is carrying internet right now and the latency through each one.
- If the preferred connection drops, internet falls back to the other one automatically.

> Status: in design. See [the v1 design spec](docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md).

## Roadmap

- **v1:** Phone / LAN / Auto switch for the internet path, local access preserved
- **v2:** site/IP exceptions routed via the LAN
- **v3:** per-app routing

## Requirements

- Windows 10/11
- .NET 10
- Administrator rights, because changing interface metrics needs elevation
