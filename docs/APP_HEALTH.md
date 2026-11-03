# App health and TODO

Living document: update it at the end of every work session. Status marks: **[x]** done and verified, **[~]** done but only partly verified, **[ ]** not done.

## MCP feature — 2026-11-03

- [x] Repository behavior inspected and [proposed MCP design](superpowers/specs/2026-11-03-netroute-mcp-design.md) independently reviewed; the builder review found no material unanswered planning questions.
- [ ] Implementation: authenticated ChatGPT control, status, calendar-month usage queries, and command communication that reconnects through a working configured connection without changing the traffic profile for recovery.
- [ ] First feasibility gate: prove adapter-bound HTTPS and DNS work with Smart routing's TUN active, including a dead LAN and working phone. No runtime tests or network changes were performed for the design.
- [ ] Before deployment: verify target ChatGPT access and select hosting/authentication, or validate an equivalent managed tunnel. Existing 35-day usage history has coverage limitations; MCP-enabled recording demand and explicit coverage are part of the design.
