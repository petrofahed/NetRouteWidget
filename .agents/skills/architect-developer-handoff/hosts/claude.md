# Claude PR-team adapter

Use this adapter only when PR-team was invoked in Claude. Read [the shared workflow](../pr-team.md).

## Assignments and launch

Run `python scripts/pr-team.py --host claude --describe` from the launch checkout.
The native `.claude/agents/tasks/pr-*.md` files are authoritative for the Claude
roles: lead, architect, developer, executor and auditor.

**Lead:** the `pr-lead` role in [Who the lead is](../pr-team.md#who-the-lead-is). A
Claude background agent normally cannot start agents, so the lead asks the outer
session for its dispatches with `SendMessage`.

First reactivate any role already recorded in the `PLAN.md` roster with `SendMessage` to its agent ID ([Reuse before spawning](../pr-team.md#4-spawn-the-three-in-one-message-then-connect-them)). Launch only the missing ones among architect, developer and executor with the Agent tool, the corresponding
`subagent_type: pr-architect / pr-developer / pr-executor`, and `run_in_background: true`.
Launch `pr-auditor` only at the shared workflow's internal-audit step, in a fresh
context. Use `SendMessage` for peers and the lead, and the host's agent completion
notifications/resume capability for continuation. Supply IDs explicitly.

If a role is not registered, do not silently replace it with a general-purpose agent
whose effort differs. Report the missing profile and reload the session after the
repository's profiles are available. Keep the PR's accepted evidence and next step.

The executor reads [the shared executor policy](../executor.md). Its native tools
and default effort remain as configured; do not invent an effort setting.

## External audit: Codex Astra

After the shared workflow's audited push and separate GitHub-head confirmation,
the lead runs this as its own shell call from the launch checkout:

```text
python scripts/pr-team.py --host claude --repo <worktree> --sha <pushed SHA> --prompt <scratchpad/prompt.txt> --out <scratchpad/audit-1>
```

The dispatcher uses the existing `scripts/codex-audit.py` with `gpt-6-astra`,
preserving its current effort behavior and canary protections. For the one change
pass add `--resume <session_id> --since <previous SHA>`. Follow the shared
pending/retry/manual-fallback rule. Only the lead calls the helper.

This adapter selects tools and models; it never changes authorization or review limits.
