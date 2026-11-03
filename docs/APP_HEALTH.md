# App health and TODO

Living document: update it at the end of every work session. Status marks: **[x]** done and verified, **[~]** done but only partly verified, **[ ]** not done.

## MCP feature — 2026-11-03

- [x] Repository behavior inspected and [proposed MCP design](superpowers/specs/2026-11-03-netroute-mcp-design.md) independently reviewed; the builder review found no material unanswered planning questions.
- [x] [Implementation plan](superpowers/plans/2026-11-03-netroute-mcp.md) checked against the design, with an explicit first gate for phone HTTPS/DNS while LAN fails and Smart remains active. Planning preflight found .NET 10.0.403, packet-capture tooling, and running NetRoute/sing-box processes; adapter inspection was denied by the sandbox. This is not live failover evidence.
- [ ] Implementation: authenticated ChatGPT control, status, calendar-month usage queries, and command communication that reconnects through a working configured connection without changing the traffic profile for recovery.
- [ ] First feasibility gate: prove adapter-bound HTTPS and DNS work with Smart routing's TUN active, including a dead LAN and working phone. No runtime tests or network changes were performed for the design or implementation plan.
- [ ] Before deployment: verify target ChatGPT access and select hosting/authentication, or validate an equivalent managed tunnel. Existing 35-day usage history has coverage limitations; MCP-enabled recording demand and explicit coverage are part of the design.
