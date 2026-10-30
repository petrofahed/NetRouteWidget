"""Remove /pr-team worktrees whose PR is merged or closed.

Run at the start of every /pr-team. A worktree is removed only when ALL of these are proven:
- it carries the `.pr-team-owned` marker that /pr-team writes when it creates the folder;
- GitHub answers, and says the PR is MERGED or CLOSED;
- it has no uncommitted or untracked work other than PLAN.md and the marker;
- every ignored file in it is disposable build/test output (bin/, obj/, TestResults/, .vs/, __pycache__/);
- every local commit is on GitHub: on a freshly fetched origin branch, or inside the PR head GitHub recorded
  (refs/pull/<N>/head survives a squash merge and a deleted branch).
If any check cannot be completed, the folder is kept and the reason printed — never discarded.

Usage: python scripts/pr-team-cleanup.py [--dry-run]
"""
import os
import re
import subprocess
import sys

MARKER = ".pr-team-owned"
ALLOWED_UNTRACKED = {"?? PLAN.md", f"?? {MARKER}"}
DISPOSABLE_DIRS = {"bin", "obj", "TestResults", ".vs", "__pycache__"}


def run(*args, cwd=None):
    return subprocess.run(list(args), cwd=cwd, capture_output=True, text=True)


def team_worktrees():
    """Yield (path, branch, pr_number) for every worktree on a pr-<N>-team branch."""
    path = None
    for line in run("git", "worktree", "list", "--porcelain").stdout.splitlines():
        if line.startswith("worktree "):
            path = line[len("worktree "):]
        elif line.startswith("branch refs/heads/"):
            branch = line[len("branch refs/heads/"):]
            m = re.fullmatch(r"pr-(\d+)-team", branch)
            if m:
                yield path, branch, m.group(1)


def disposable(entry):
    """An ignored-file status line ('!! path') is disposable when a path segment is a build/test dir."""
    parts = entry[3:].strip().strip('"').replace("\\", "/").rstrip("/").split("/")
    return any(p in DISPOSABLE_DIRS for p in parts)


def reasons_to_keep(path, branch, n):
    if not os.path.exists(os.path.join(path, MARKER)):
        return "not made by /pr-team (no marker)"
    state = run("gh", "pr", "view", n, "--json", "state", "--jq", ".state")
    if state.returncode != 0:
        return "could not read its state on GitHub"
    state = state.stdout.strip()
    if state not in ("MERGED", "CLOSED"):
        return f"state is {state or 'unknown'}"
    # --ignored=matching + all: list each ignored path, so a folder holding only build output is
    # not collapsed into one "!! dir/" line that hides its bin/ or obj/ segment.
    status = run("git", "status", "--porcelain", "--ignored=matching", "--untracked-files=all", cwd=path)
    if status.returncode != 0:
        return "could not read its status"
    lines = status.stdout.splitlines()
    local = [l for l in lines if not l.startswith("!! ") and l.strip() not in ALLOWED_UNTRACKED]
    kept_ignored = [l for l in lines if l.startswith("!! ") and not disposable(l)]
    pull = run("git", "fetch", "-q", "origin", f"pull/{n}/head")
    if pull.returncode != 0:
        return "could not fetch the PR head GitHub recorded"
    pull_head = run("git", "rev-parse", "FETCH_HEAD").stdout.strip()
    # Only origin (just refreshed with --prune) and the PR head GitHub recorded count as published;
    # a stale tracking branch of any other remote is not proof the commit still exists anywhere.
    log = run("git", "log", "--oneline", branch, "--not", "--remotes=origin", pull_head)
    if log.returncode != 0:
        return "could not compare its commits with GitHub"
    unpushed = log.stdout.splitlines()
    why = []
    if local:
        why.append(f"{len(local)} unsaved file(s)")
    if kept_ignored:
        why.append(f"{len(kept_ignored)} ignored non-build file(s)")
    if unpushed:
        why.append(f"{len(unpushed)} commit(s) not on GitHub")
    return f"{state.lower()}, but " + " and ".join(why) if why else None


def main():
    dry = "--dry-run" in sys.argv
    fetch = run("git", "fetch", "-q", "--prune", "origin")
    if fetch.returncode != 0:
        print(f"Could not reach GitHub — nothing removed. ({fetch.stderr.strip()[:200]})")
        return
    found = False
    for path, branch, n in team_worktrees():
        found = True
        why = reasons_to_keep(path, branch, n)
        if why:
            print(f"PR #{n}: kept — {why} ({path})")
            continue
        if dry:
            print(f"PR #{n}: would remove — closed or merged, nothing unsaved ({path})")
            continue
        removed = run("git", "worktree", "remove", "--force", path)
        if removed.returncode != 0:
            print(f"PR #{n}: kept — removal failed: {removed.stderr.strip()} ({path})")
            continue
        run("git", "branch", "-D", branch)
        run("git", "update-ref", "-d", f"refs/pr-team/{n}")
        print(f"PR #{n}: removed ({path})")
    if not found:
        print("No /pr-team folders.")


if __name__ == "__main__":
    main()
