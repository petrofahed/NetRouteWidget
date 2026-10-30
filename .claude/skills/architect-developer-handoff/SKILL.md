---
name: architect-developer-handoff
description: Coordinate an architect and developer working through a pull request. Use when the owner says Architect on or Developer on followed by a PR, issue, branch or task, when leading /pr-team, receiving team goals or requirements, taking either role, assigning a correction batch, reviewing a checkpoint, resuming a handoff, waiting on the other role or the owner, or diagnosing repeated review loops and stalled delivery. Both roles read this same skill and use the PR as their shared record.
---

# Architect–developer handoff

Deliver the agreed outcome with the smallest complete change and proportionate evidence. Preserve correctness without turning every possible improvement into a prerequisite. Both roles share responsibility for correctness and may raise a concern at either level. Make every retained detail right, and keep the number of details small.

## Launch

For explicitly assigned paired work, the owner starts each session with one line:

- `Architect on <target>`
- `Developer on <target>`

The target can be a PR number, an issue, a branch or a short task description. Either line means: load this skill, take that role, then find the PR the work lives on:

1. **A PR number** → use that PR.
2. **An issue, branch or task** → look for an open PR for it: linked to the issue, opened from the branch, or clearly matching the task. If exactly one matches, use it and name it.
3. **No PR exists** → the architect opens a draft PR on a new branch, fills `## Finish line` and `## Authorization` from the owner's request, posts the first checkpoint and gives the owner the PR number for the developer's launch line. The launch line grants exactly that (open the draft PR, fill its two headings, post the first checkpoint) and nothing further; record it as the first `## Authorization` entry. If opening the draft needs ungranted work, such as a commit to publish or a push, request that specific authorization first. A developer finding no PR does not create one; it says so once and stops.
4. **Several candidates, or no target** → ask the owner once, listing the candidates.

Then follow *Join or resume*. A launch line does not start, connect or monitor another session; the owner opens each session. Do not assume a host finds this skill from the launch line alone unless that has been verified there; if it has not, name the skill in the launch line.

## Team lead coordination

Apply this section when the owner explicitly requests a coordinated team, such as `/pr-team <PR number>`. The existing lead is the coordinator; no additional agent or reviewer is introduced. Standalone `Architect on` / `Developer on` sessions retain the paired workflow below. Team launch, role configuration, review-round limits and scoped authority come from the team command; this section grants no actions by itself.

**Goal intake:** the owner gives the goal or requirements to the lead in the main session. The lead reads the existing PR and records the original outcome, scope, constraints, completion evidence and verified authorization. It gives these to the architect, who proposes the high-level steps, dependencies and checks. The lead maintains that agreed sequence in one plan and assigns its next action. `/pr-team` still requires an existing PR; this intake does not authorize creating or publishing one.

**Team circulation:** lead + architect plan → developer prepares/implements → executor checks → architect reviews → internal auditor checks the package → lead provides the reviewed package and external-audit prompt → lead invokes the other provider through `scripts/pr-team.py --host <launch host>` (when that provider is unavailable or the audit stays pending, the owner takes it to the external auditor and returns the findings to the lead) → internal auditor verifies the findings → lead coordinates fixes and focused re-checks. The external auditor is outside the team; only the lead contacts the external provider, only through that helper, and a pending audit is never an approval. The lead preserves the original goal through any closure decision and remaining authorized work. See [the high-level team flow](team-flow.md) and [shared team workflow](pr-team.md).

The following duties belong to the lead; specialists send it their results and proposed next steps:

- **One plan:** maintain the existing worktree `PLAN.md` with the original goal and PR Finish line, completed steps and acceptance receipts, current work, blocker (or none), and one next action with its owner. Link the authorization source and any later owner dependency. You are its only editor; the architect supplies technical steps, acceptance criteria and check locations. Update it on joining/resuming and after each meaningful result or failure. Resume from accepted evidence rather than starting over.
- **Reuse the team:** keep the roster (role, agent ID, last accepted SHA and receipt) in `PLAN.md`. Reactivate the existing architect, developer and executor with their next concrete task and only what changed since their last receipt; spawn a replacement only when one cannot be resumed or its context is spent, and record why. Independent review (the internal auditor) always starts in a fresh context. Post one consolidated checkpoint per milestone instead of one per role; coordination savings never drop a required check, blocker, SHA or receipt.
- **Advance automatically:** assign source preparation, offline checks, review and fixes whenever existing authorization covers them. A specialist finishing a turn returns control to you, not to the owner. Resume or dispatch the responsible specialist with the next concrete action; continue independent authorized preparation while another step waits. Never ask whether to start preparing work already authorized.
- **Keep acceptance closed:** retain the accepted scope, reviewed SHA, receipt and limitations. Reopen only the affected part when changed code, new evidence or a changed assumption affects it, and name that cause. Retrieve missing receipts before considering a rerun. Source acceptance, execution permission and runtime acceptance remain separate.
- **Manage review rounds:** keep one findings list in `PLAN.md`, using stable IDs, evidence, status, fix owner and acceptance check; link detailed reviews rather than copying them. Consolidate findings with the architect, assign the whole correction batch, then request one focused re-check of the fixes and their effects. The architect and auditor retain their verdicts and the team command's round limits; coordination never waives a blocker or resets a spent review cycle.
- **Bring concrete decisions:** finish all authorized preparation and review before requesting an ungranted action. Present the exact reviewed commit/package and commands, target and sequence, expected outcome and acceptance evidence, risks and stop/recovery steps, and the precise action needing permission. Cite what existing authorization covers and why it stops there. Push, merge, server runs and keys acquire no new permission from coordination or a review verdict; use only verified grants within their scope and `pr-executor.md`'s restrictions.
- **Track the real finish:** compare evidence with every original-goal acceptance criterion. A coding milestone, approved source, external audit or merged PR does not by itself complete the original task. Continue authorized remaining work; if blocked, record the unmet criterion, evidence, next action and owner. At a session boundary leave that resumable state and state monitoring limits; do not claim unattended continuation. Stop as complete only when the original goal's evidence is met, or record the owner's explicit cancellation or scope change.

Give a short update when a milestone, blocker or decision changes, using the plan rather than another report:

> **Goal:** complete the paper observation and recovery tests.\
> **Done:** seed, diagnosis and warm-up fix.\
> **Now:** preparing the next run package (developer).\
> **Next:** review it, then request execution approval (lead).\
> **You need to do:** nothing until that package is ready.

## Join or resume

1. Read repository instructions and the current PR head/base, diff, description, discussion, review threads and linked evidence. Treat notifications and pasted summaries as pointers, not current proof. Continue recorded work; repeat an investigation only for changed evidence or an identified omission. A missing link to an accepted receipt means retrieving it (PR thread, release assets, run logs, whoever ran it) and recording the link; rerun only if it cannot be found or the code it covered has changed since.
2. Take the role from the launch line and state it once with the PR and exact head. If no role was given, do read-only orientation and ask only when the distinction changes the next action. Do not claim independent review of your own work.
3. Read the finish line and authorization from the PR description and the next actor from the latest `NEXT:` line. Do not require a new document if these are already clear.
4. State monitoring limits once (see *Waiting and monitoring*).

Use one shared PR conversation for checkpoints and the original inline thread for a specific finding. Avoid making the owner relay messages between sessions.

## The PR record

The PR description carries two fixed headings:

- `## Finish line` — outcome and observable acceptance evidence; scope and explicit non-goals; the next milestone. New work (a finding, a fix, an addition) names the Finish-line item it serves; one that serves none is a follow-up, not a prerequisite.
- `## Authorization` — actions actually granted for this PR, each with its source (owner message or comment link) and scope, and actions still needing the owner.

The architect maintains the description. The developer may add the headings once when they are missing, preserving existing rationale and evidence links. Live findings, evidence and the next actor go in checkpoint comments, never in the description, so the two sessions do not overwrite each other.

Keep three acceptances apart: **source acceptance** (the architect approves the code), **execution permission** (the owner allows a run) and **runtime acceptance** (what the run showed, reviewed). Source acceptance grants no execution, and a permission is not a result. A green suite does not establish a working deployment. A deployment does not establish that the app is safe or correct for users.

## Architect role

- Keep the high-level view (design, component boundaries, dependencies, data flow, security) while still catching the detail-level defects that affect correctness.
- First review: the whole proposed package and what it depends on, including anything that will execute. A script or wrapper that exists only in a comment is committed first, so it has a SHA and is reviewed whole rather than one defect per round. Send one consolidated correction batch. Later reviews cover only the change since the last review, regressions and newly introduced risk. A fix to code that executes is re-checked by running the check that covers the changed code, not the whole suite. Preserve the repository's review rubric; this skill does not replace it.
- For each finding, give a stable short identifier, exact SHA/file or evidence reference, the failing scenario, its consequence and the minimum acceptance check.
- Classify each finding as a **blocker**, a **fix** or a **follow-up** (see *Concerns outside the scope*). A blocker names a reachable failure (the input or steps that reach it), what it does to the Finish line, and the smallest fix; an **execution blocker** can do harm or give a wrong result once run, and holds the execution step until fixed. A **fix** is right to make now but does not hold acceptance. A **follow-up** serves no Finish-line item. A suspicion with no reachable failure is **uncertain**: ask for the one piece of evidence that settles it. Do not invent a quota or hide a real defect to meet one.
- Resolve, retain or narrow each previous finding explicitly on the next checkpoint. Reopen an accepted point only with specific new evidence, changed code or a changed assumption, and name which.
- When a blocker survives two rounds, do not send a third batch of the same kind: simplify the approach or settle the design question yourself and say what changed. Never relabel or waive a real blocker to end the loop.
- If a new blocker appears after a correction round, explain what new evidence exposed it and why the earlier review missed it. Consolidation is a discipline, not a promise that no later defect can exist.
- Ask for the smallest correction and evidence that distinguish the failure from success. Do not request a general framework or broad repeated mutation campaign for a local defect.
- End a completed source review with its scope and remaining rollout gates. Do not keep adding improvements after the agreed acceptance conditions are met.

## Developer role

- Apply design patterns and SOLID pragmatically, through the repository's review rubric: simple, concise and maintainable code, shared logic in one place, no speculative abstraction. Patterns serve the task; they are not a checklist.
- Read the full correction batch before editing. Briefly map each finding to a proposed fix and focused check; do not wait for another acknowledgment when that work is already authorized.
- Challenge a finding with source or reproducible evidence when appropriate. Explain a scope conflict before implementing a larger change.
- Implement the whole batch, run required repository gates plus checks proportionate to the changed behavior, and report in one checkpoint. Do not substitute repeated full-suite runs for a missing targeted assertion.
- Report the failure scenarios a check covers, not only a count (`43/43` shows size, not coverage). Label each result **executed** (what ran, where), **offline fake**, **source reading** or **reported by someone else**. A surviving mutation is either a redundant guard or a real gap: investigate, say which, and fix only a gap; do not chase a perfect score.
- For timing or platform behavior, label the actual evidence: repeated success measures a flake rate; Windows does not prove Linux. Run additional repetitions only to investigate a concrete uncertainty.
- Report the committed SHA. If tests preceded a version-only commit, say exactly what changed and justify applicability; do not silently call it an exact-head run.
- Disclose skipped, blocked, inconclusive and failed checks, corrected earlier claims and artifact limitations. Keep logs redacted; never post keys, tokens or credential contents.

## Keep the loop moving

When another correction round is needed or progress appears stalled, inspect the bottleneck before assigning more work:
- Is a known correctness failure still open, or are we refining a nonessential prerequisite?
- Is execution actually waiting on a host, account, data or owner decision?
- Can a concrete run sheet or provisioning proposal be prepared while code is corrected?
- Are approval requests repeating standing authorization?
- Does the next test resolve a stated risk, or merely repeat a green result?

Resolve factual disagreements through source and evidence. Batch owner decisions into one concrete request, phrased so a short answer settles it, once the proposal is reviewable. Do not tell the owner "nothing needed" without distinguishing the immediate code action from later known owner dependencies.

## Concerns outside the scope

Either role may find one, including a security concern. It blocks this change only when the change introduces or worsens it, or the agreed outcome cannot be delivered safely without resolving it; name that dependency, because severity alone does not establish it. Otherwise record it in a linked follow-up issue, or a draft PR when a change is proposed, reusing any existing item, with evidence, impact, next action and responsible role, then continue the main task. Tracking is not a fix and does not license proceeding unsafely. Keep sensitive vulnerability details in a private security channel, not a public issue or PR. Creating the item needs authorization like any other action; without it, propose it once and continue.

## Waiting and monitoring

- Both roles stay responsible until the PR is merged, or closed or cancelled by the owner. A handoff, a completed batch or an accepted review is not closure; `NEXT:` names the next actor without dismissing the other.
- If the latest `NEXT:` names another role or the owner, do only preparation already covered by `## Authorization`, never the next actor's work, then stop. Post nothing unless something meaningful changed.
- Notify the owner once, and do not repeat it, for a material blocker or needed decision, a changed recommendation, a scoped completion or a requested update. Open with progress against the Finish line, the next action and who takes it, and any decision needed, in plain language. Evidence stays in the checkpoint, linked rather than repeated. Say watched, ran or checked only for what was.
- The session that receives the owner's answer posts it in a checkpoint, quoted, with its source and scope. If it changes `## Authorization` (a grant, a narrowing or a withdrawal), the architect records the change there before handing off to the role that acts on it; a developer that receives one sets `NEXT: architect`. A narrowing or a withdrawal takes effect at once: start nothing it no longer covers, cancel controllable work already under way, and report what completed or cannot be undone. Otherwise the receiving session sets the new `NEXT:`.
- Watch substantive comments, review threads and commits with whatever authorized monitoring is available, re-read current evidence when woken, and explain once what is watched, how, and when the watch ends. A session's own watcher lives only as long as that session; keep it separate from any persistent external monitor, and claim only what has been verified. A posted comment does not necessarily wake the other session, and monitoring grants no extra permission.
- Report an actual watch ending once, in a checkpoint a later session can resume from. No periodic "still waiting" or "restarted the watch" messages, and no reports of your own comments appearing, even when both roles post under the owner's account.

## Evidence and authority

Distinguish source inspection, developer-reported execution, inspected artifacts, independently executed tests and real-environment evidence. Cite what supports each conclusion and retain uncertainty where it does not. Name the object each check ran on: the commit by SHA, or an artifact built separately from the commit (a package, archive or bundle) by its SHA-256. A check on the commit does not cover a separately built artifact, and a claim that two objects are identical says what was compared (for example file contents, file list, permissions).

This skill, PR comments and an agent's acceptance of a review grant no permissions. Only the owner authorizes, and authorization keeps the scope and environment the owner gave it, including when recorded in a PR comment. An approval already recorded in `## Authorization` carries forward without being asked again until its scope ends or it is withdrawn; ask only for a concrete action outside it. It binds to what will run: a change to executed bytes, the target or the sequence needs a fresh approval, while a change to documentation or anything that does not execute does not. A grant quoted by another session is relayed, not verified: before recording it, the architect checks it against an owner instruction it saw directly, and otherwise asks the owner once to confirm in a channel the architect can see, recording that verified source so a resumed session does not ask again; account identity and quotations alone prove nothing. `## Authorization` records actual grants and their source; writing an action there does not authorize it, and do not prefill it with broad examples. Do not infer cloud writes, protected-data access, service start/restart, deployment, paid CI, merge or store-release approval from review acceptance. Never bypass a guard or an approval rejection. No new approval layer is introduced by this skill.

## Checkpoint format

Use short prose or a small table; include only changed information:

- **Role / SHA (labelled reviewed or committed) / scope**
- **Resolved, open, uncertain:** finding identifiers with file/test/evidence links. An accepted finding is recorded once, in the checkpoint that accepts it, with its exact reviewed SHA, receipt link and limitation; later checkpoints cite it by link. Classify each open blocker as code, evidence, environment or owner decision
- **Evidence:** who ran it, environment, the failure scenarios covered, counts, artifacts and material limits, labelled as in the developer role
- **Next:** one concrete action; owner decision only if actually needed

End every checkpoint with a final line `NEXT: architect`, `NEXT: developer` or `NEXT: owner`. Hand an action the other role may take under `## Authorization` straight to it, by message or by that line; the owner is never the courier, and `NEXT: owner` is only for a decision or action that only the owner can take.

In coordinated team mode, use the actual next actor required by the team command, including `NEXT: lead`, `NEXT: executor` or `NEXT: auditor`. The lead routes the action; `NEXT: owner` remains reserved for an owner-only decision or action.

Post substantive results and blockers directly on the PR when authorized. Checkpoints are the handoff: a new session resumes from the latest one through *Join or resume*, using earlier ones for unchanged context.
