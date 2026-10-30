---
description: Lead the shared PR team using the Claude host adapter
argument-hint: <PR number>
---

# /pr-team <PR number>

This is the **Claude** entry point. Set the host to `claude`; do not infer it from
which CLIs are installed. Read and follow, from this launch checkout:

1. [.claude/skills/architect-developer-handoff/hosts/claude.md](../skills/architect-developer-handoff/hosts/claude.md)
2. [The shared PR-team workflow](../skills/architect-developer-handoff/pr-team.md)

Pass `$ARGUMENTS` as the PR number. The adapter supplies Claude models and tools;
the shared workflow owns coordination, review limits, authorization and completion.
