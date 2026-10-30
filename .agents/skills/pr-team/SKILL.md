---
name: pr-team
description: Lead the existing PR team in Codex. Use when the owner invokes $pr-team with a PR number, says /pr-team in Codex, or explicitly asks to run the PR team on an existing PR. Coordinate the lead, architect, developer, executor and independent auditor through the shared workflow.
---

# PR team in Codex

This is the **Codex** entry point. Set the host to `codex`; do not infer it from
installed CLIs or the presence of a `.claude` folder.

Read and follow these repository-root paths from the launch checkout:

1. `.claude/skills/architect-developer-handoff/hosts/codex.md`
2. `.claude/skills/architect-developer-handoff/pr-team.md`

Use the owner's PR number as `$ARGUMENTS` in the shared workflow.
Keep all team rules in that workflow. This skill only selects the adapter.
Do not load or execute the Claude slash-command entry point.
