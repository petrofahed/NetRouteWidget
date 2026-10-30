# Codex PR-team adapter

Use this adapter only when PR-team was invoked in Codex. Read [the shared workflow](../pr-team.md).

## Assignments and launch

Run `python scripts/pr-team.py --host codex --describe` from the launch checkout.
Read the native `.codex/agents/pr-*.toml` profiles it names. These are the
authoritative Codex role assignments; model and effort per role come from that output.
The external reviewer is the other provider, outside this internal team.

Use the registered `pr-lead`, `pr-architect`, `pr-developer`, `pr-executor`
and `pr-auditor` roles with the host's spawn capability. If named profiles are
unavailable but explicit model/effort spawn parameters exist, pass the profile's
model, effort and developer instructions explicitly. For a `collaboration.spawn_agent`
host, use `model`, `reasoning_effort` and `fork_turns: "none"`, with a
self-contained task packet. For a native `spawn_agent` host, use the registered
agent type. Never send parameters not supported by the current tool schema.

**Lead:** the `pr-lead` role in [Who the lead is](../pr-team.md#who-the-lead-is). When
the lead cannot spawn agents, the outer session spawns the specialists it asks for,
using the forms above.

Start architect, developer and executor concurrently, reactivating any role already recorded in the `PLAN.md` roster ([Reuse before spawning](../pr-team.md#4-spawn-the-three-in-one-message-then-connect-them)) with `followup_task` or the host's resume tool instead of spawning a duplicate. Give each new one the self-contained
launch packet required by the shared workflow; no implementation starts before the
architect supplies its plan. Use `send_message` / `send_input` for messages,
`followup_task` / the host's resume tool to reactivate stopped teammates, and
`wait_agent` / `wait` for completion. Discover the actual tool schema first.
Where peer messaging is unavailable, relay through the lead.

Spawn the internal auditor with a **fresh context**, never a fork of implementation
history. Pass the goal, files, reviewed SHA and evidence as inputs to inspect.
Its profile forbids edits to source and GitHub writes; test/log output is permitted
only under [the shared executor policy](../executor.md).

Do not silently fall back when a model, effort or independent agent capability is
unavailable. Report the missing capability and preserve the next action. If the host
does not expose resolved settings, label them "requested; runtime confirmation
unavailable" rather than claiming verification.

For a concrete hard issue the lead may raise architect/developer effort to High
through supported configuration or a fresh agent with the same task/evidence,
recording the change and returning to Medium afterward. No automatic Max setting.
Do not alter the repository's global model or the user's global settings.

## External audit: Claude Opus High

After the audited push and separate GitHub-head confirmation, the lead runs:

```text
python scripts/pr-team.py --host codex --repo <worktree> --sha <pushed SHA> --base <PR baseRefOid> --prompt <scratchpad/prompt.txt> --out <scratchpad/audit-1>
```

The helper reads the actual diff from the PR base's merge-base to the audited SHA
and includes it in the review input, because Claude has no shell. On a change pass
it includes the diff from the previous reviewed SHA instead.
The dispatcher invokes `scripts/claude-audit.py` with the pinned Opus model from
the existing Claude auditor profile and `--effort high`. It gives Claude only
Read/Glob/Grep (plus the schema-output tool if supplied by Claude), no shell,
write, browser or connector tools. The canary must confirm that tool inventory
before the PR prompt is sent. This external audit is **source inspection**;
executor/internal-auditor receipts supply executed-test evidence.
For the one change pass add `--resume <session_id> --since <previous SHA>`.
Missing CLI/auth, unsupported settings, unexpected tools/model, incomplete or
malformed answers are pending, never approval. Follow the shared retry/fallback rule.

Run under the same OS user and `CLAUDE_CONFIG_DIR` as the owner's signed-in Claude
CLI. The helper uses `--safe-mode` to disable customizations while preserving normal
authentication, plus `--restricted` and the explicit tool restrictions. Do not use
`--bare`: that mode does not read subscription OAuth/keychain credentials and would
require separate API authentication. No credentials need to be copied into the repo.
Check `claude auth status --text` on the executing machine before the first live run.

Native format references (checked 2026-10-06):
[Codex subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents),
[skill discovery](https://learn.chatgpt.com/docs/build-skills),
[Claude CLI](https://code.claude.com/docs/en/cli-reference).
