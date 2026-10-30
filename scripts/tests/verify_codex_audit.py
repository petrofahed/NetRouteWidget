"""
verify_codex_audit.py
---------------------
Pins `scripts/codex-audit.py`, the helper /pr-team's lead uses to put the external audit to Codex.

The cases that carry the weight are the ones where the answer must be "audit PENDING", never an approval:
  - a first turn that shows Codex has a connector tool (GitHub merge, Gmail send) or can write a file must stop
    the run BEFORE any PR text is sent - the fake records whether PR text ever reached it;
  - the restrictive flags must be on EVERY Codex call, including the resume of a re-check;
  - a reply for the wrong commit, from the wrong session, empty, malformed, or approving over a high finding;
  - a hang (killed), an exit code, an incomplete turn, a write during the audit, an unclean or wrong checkout.

A fake Codex stands in for the real one (no model quota, no network); its behaviour is chosen by FAKE_MODE.

Run:  python scripts/tests/verify_codex_audit.py
"""
from __future__ import annotations

import atexit
import importlib.util
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
TARGET = HERE.parent / "codex-audit.py"

FAKE = r'''
import json, os, sys, time
argv = sys.argv[1:]
if argv == ["--version"]:
    print("codex-cli fake"); sys.exit(0)
mode = os.environ.get("FAKE_MODE", "ok")
stdin = sys.stdin.read()
out = argv[argv.index("-o") + 1]
resumed = "resume" in argv
session = argv[-2] if resumed else "sess-1"
with open(os.environ["FAKE_LOG"], "a") as log:
    log.write(json.dumps({"argv": argv, "canary": "CANARY" in stdin, "has_pr": "SECRET-PR-TEXT" in stdin,
                          "stdin": stdin}) + "\n")
if mode == "exit1": sys.exit(1)
if mode == "timeout" and "CANARY" not in stdin: time.sleep(30)
shown = "other-session" if (mode == "wrongsession" and resumed) else session
print(json.dumps({"type": "thread.started", "thread_id": shown}))
if "CANARY" in stdin:
    if mode == "writes": open("CODEX_AUDIT_WRITE_PROBE.txt", "w").write("probe")
    # A real read-only canary shows the probe write as a command that the sandbox refused (exit 1, status failed).
    attempt = {"nowriteattempt": None,
               "writeok": {"exit_code": 0, "status": "completed"},
               "writeother": {"exit_code": 1, "status": "failed", "command": "pwsh -Command Get-Date"},
               "readfails": {"exit_code": 1, "status": "failed",
                             "command": "pwsh -Command Get-Content CODEX_AUDIT_WRITE_PROBE.txt",
                             "aggregated_output": "Cannot find path 'CODEX_AUDIT_WRITE_PROBE.txt' because it does not exist."},
               "badparameter": {"exit_code": 1, "status": "failed",
                                "aggregated_output": "Set-Content: A parameter cannot be found that matches parameter name 'Nonexistent'."},
               }.get(mode, {"exit_code": 1, "status": "failed"})
    if attempt is not None:
        item = {"id": "item_w", "type": "command_execution",
                "aggregated_output": "Set-Content: Access to the path 'F:\\repo\\CODEX_AUDIT_WRITE_PROBE.txt' is denied.",
                "command": "pwsh -Command Set-Content -LiteralPath 'CODEX_AUDIT_WRITE_PROBE.txt' -Value probe"}
        item.update(attempt)
        print(json.dumps({"type": "item.started", "item": dict(item, status="in_progress", exit_code=None)}))
        print(json.dumps({"type": "item.completed", "item": item}))
    if mode == "canaryother": open("sneaky.txt", "w").write("x")
    tools = "functions.exec, tools.exec_command, tools.apply_patch, collaboration.spawn_agent"
    if mode == "badtools": tools += ", mcp__codex_apps__gmail_send_email"
    # Codex 0.163.0 always offers the three MCP resource tools; a real canary lists both and gets empty answers.
    if mode not in ("resourcesunlisted", "resourcesomitted"):
        tools += ", tools.list_mcp_resources, tools.list_mcp_resource_templates, tools.read_mcp_resource"
    inbox = [{"uri": "gmail://inbox"}]
    res = ("list_mcp_resources", {"resources": []}, {})
    tpl = ("list_mcp_resource_templates", {"resourceTemplates": []}, {})
    listed = {"resourcesreach": [("list_mcp_resources", {"resources": inbox}, {}), tpl],
              "resourcesclaimed": [], "resourcesomitted": [],
              "resourcesrepeat": [("list_mcp_resources", {"resources": inbox}, {}), res, tpl],
              "resourcesscoped": [("list_mcp_resources", {"resources": []}, {"arguments": {"server": "other"}}), tpl],
              "resourcescursor": [("list_mcp_resources", {"resources": [], "nextCursor": "abc"}, {}), tpl],
              "resourcesread": [res, tpl, ("read_mcp_resource", {"contents": []}, {})],
              "resourcesunlisted": [("list_mcp_resources", {"resources": inbox}, {}), tpl],
              "resourceserror": [("list_mcp_resources", {"resources": []}, {"status": "failed"}), res, tpl],
              "resourcesstartedread": [res, tpl, ("read_mcp_resource", None,
                                                  {"_started_only": True, "arguments": {"server": "x", "uri": "gmail://inbox"}})],
              "resourcesstartedonly": [("list_mcp_resources", None, {"_started_only": True}), res, tpl],
              "resourcesextracontent": [("list_mcp_resources", {"resources": []},
                                         {"_extra_content": {"resources": inbox}}), tpl],
              "resourcesstructured": [("list_mcp_resources", {"resources": []},
                                       {"_structured": {"resources": inbox}}), tpl],
              }.get(mode, [res, tpl])
    for i, (name, body, over) in enumerate(listed):
        over = dict(over)
        started_only = over.pop("_started_only", False)
        extra = over.pop("_extra_content", None)
        structured = over.pop("_structured", None)
        content = [{"type": "text", "text": json.dumps(body)}]
        if extra is not None:
            content.append({"type": "text", "text": json.dumps(extra)})
        item = {"id": f"item_{i}", "type": "mcp_tool_call", "server": "codex", "tool": name, "arguments": {},
                "status": "completed", "error": None, "result": {"content": content, "structured_content": structured}}
        item.update(over)
        print(json.dumps({"type": "item.started", "item": dict(item, status="in_progress", result=None)}))
        if not started_only:
            print(json.dumps({"type": "item.completed", "item": item}))
    open(out, "w").write("TOOLS: " + tools)
else:
    sha = stdin.split("Commit under review: ")[1].split()[0]
    review = {"commit": sha, "verdict": "APPROVED", "summary": "fine", "findings": [],
              "coverage": ["scripts/codex-audit.py"], "limitations": []}
    if mode == "wrongcommit": review["commit"] = "0" * 40
    if mode == "approvedhigh": review["findings"] = [{"id": "F1", "severity": "high", "path": "a", "evidence": "e", "fix": "f"}]
    if mode == "dupids": review["findings"] = [{"id": "F1", "severity": "low", "path": "a", "evidence": "e", "fix": "f"}] * 2
    if mode == "writeduring": open("sneaky.txt", "w").write("x")
    if mode == "unverified": review["verdict"] = "UNVERIFIED"
    if mode == "thinfinding": review["findings"] = [{"id": "F1", "severity": "low"}]
    if mode == "badtypes": review["coverage"] = "read it"; review["limitations"] = [1]
    if mode == "badsummary": review["summary"] = 7
    if mode == "extrakey": review["extra"] = "x"
    text = {"malformed": "not json at all", "empty": ""}.get(mode, json.dumps(review))
    open(out, "w").write(text)
if mode != "incomplete": print(json.dumps({"type": "turn.completed"}))
'''


def load():
    spec = importlib.util.spec_from_file_location("codex_audit", TARGET)
    assert spec and spec.loader
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def sh(repo, *args):
    subprocess.run(["git", *args], cwd=repo, check=True, capture_output=True)


def remove_tree(path):
    """git writes read-only object files, which a plain rmtree cannot delete on Windows."""
    def unlock(func, target, _):
        os.chmod(target, stat.S_IWRITE)
        func(target)
    shutil.rmtree(path, onerror=unlock)


class Case:
    """A throwaway git repo, a fake Codex, and an output folder outside the repo."""

    def __init__(self, mode="ok"):
        self.root = Path(tempfile.mkdtemp(prefix="codex-audit-test-"))
        atexit.register(remove_tree, self.root)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        sh(self.repo, "init", "-q")
        sh(self.repo, "config", "user.email", "t@t")
        sh(self.repo, "config", "user.name", "t")
        (self.repo / "a.txt").write_text("one")
        sh(self.repo, "add", ".")
        sh(self.repo, "commit", "-qm", "one")
        self.first = self.head()
        (self.repo / "a.txt").write_text("two")
        sh(self.repo, "commit", "-qam", "two")
        self.sha = self.head()
        self.fake = self.root / "fake_codex.py"
        self.fake.write_text(FAKE)
        self.out = self.root / "out"
        self.log = self.root / "log.jsonl"
        os.environ["FAKE_LOG"] = str(self.log)
        os.environ["FAKE_MODE"] = mode

    def head(self):
        return subprocess.run(["git", "rev-parse", "HEAD"], cwd=self.repo, capture_output=True, text=True).stdout.strip()

    def calls(self):
        if not self.log.exists():
            return []
        return [json.loads(l) for l in self.log.read_text().splitlines()]

    def run(self, ca, sha=None, codex: object = "fake", **kw):
        exe = [sys.executable, str(self.fake)] if codex == "fake" else codex
        return ca.audit(self.repo, sha or self.sha, "SECRET-PR-TEXT audit this", self.out, "m", exe, **kw)


FAILURES = []


def check(name, condition, detail=""):
    print(("PASS  " if condition else "FAIL  ") + name + ("" if condition else f"   <- {detail}"))
    if not condition:
        FAILURES.append(name)


def pending(name, result, fragment=None):
    ok = result["status"] == "pending" and (fragment is None or fragment in result.get("reason", ""))
    check(name, ok, result.get("reason") or result["status"])


def main():
    ca = load()
    flags = set(ca.RESTRICT)

    c = Case()
    r = c.run(ca)
    check("happy path is ok with a verdict", r["status"] == "ok" and r["verdict"] == "APPROVED", r.get("reason"))
    calls = [x for x in c.calls()]
    check("canary goes first and carries no PR text", calls[0]["canary"] and not calls[0]["has_pr"])
    check("PR text reaches Codex only in the audit turn", calls[1]["has_pr"] and not calls[1]["canary"])
    check("every Codex call carries every restrictive flag", all(flags <= set(x["argv"]) for x in calls))
    check("the audit turn resumes the canary's session", "resume" in calls[1]["argv"] and calls[1]["argv"][-2] == "sess-1")
    check("the audit prompt names the audited commit", f"Commit under review: {c.sha}" in calls[1]["stdin"])
    check("result.json is written", json.loads((c.out / "result.json").read_text())["status"] == "ok")

    c = Case()
    r = c.run(ca, resume="sess-1", since=c.first)
    calls = c.calls()
    check("a re-check passes both turns through resume", r["status"] == "ok" and all("resume" in x["argv"] for x in calls), r.get("reason"))
    check("the restrictive flags are on the resume calls too", all(flags <= set(x["argv"]) for x in calls))
    check("a re-check canary runs again before the audit", calls[0]["canary"] and not calls[0]["has_pr"] and calls[1]["has_pr"])
    check("a re-check names the previous reviewed commit", c.first in calls[1]["stdin"])

    c = Case()
    pending("missing Codex is pending", c.run(ca, codex=None), "not installed")
    pending("a Codex that cannot start is pending", c.run(ca, codex=[str(c.root / "nope.exe")]), "could not be started")

    c = Case()
    (c.repo / "a.txt").write_text("edited")
    r = c.run(ca)
    pending("a dirty checkout is pending", r, "uncommitted")
    check("a dirty checkout never reaches Codex", c.calls() == [])

    c = Case()
    (c.repo / "PLAN.md").write_text("plan")
    (c.repo / ".pr-team-owned").write_text("")
    check("PLAN.md and the marker do not count as dirty", c.run(ca)["status"] == "ok")

    c = Case()
    r = c.run(ca, sha=c.first)
    pending("a worktree at a different commit is pending", r, "not the audited commit")
    check("a wrong commit never reaches Codex", c.calls() == [])

    c = Case()
    r = ca.audit(c.repo, c.sha, "SECRET-PR-TEXT", c.repo / "out", "m", [sys.executable, str(c.fake)])
    pending("an output folder inside the checkout is pending", r, "inside the checkout")

    c = Case("badtools")
    r = c.run(ca)
    pending("a connector tool in the canary is pending", r, "mcp__codex_apps__gmail_send_email")
    check("PR text is never sent when the canary fails", not any(x["has_pr"] for x in c.calls()))

    c = Case("resourcesempty")
    r = c.run(ca)
    check("MCP resource tools proven empty by Codex's own events do not block the audit",
          r["status"] == "ok" and r["verdict"] == "APPROVED", r.get("reason"))

    c = Case("resourcesreach")
    r = c.run(ca)
    pending("MCP resource tools that can reach a resource are pending", r, "can reach")
    check("PR text is never sent when a resource is reachable", not any(x["has_pr"] for x in c.calls()))

    c = Case("resourcesclaimed")
    r = c.run(ca)
    pending("MCP resource tools listed but never shown empty are pending", r, "not shown empty")
    check("PR text is never sent when the resource tools were not shown empty", not any(x["has_pr"] for x in c.calls()))

    # Post-merge review of 32d51aec1: each of these passed the first version of the check.
    for mode, fragment, name in [
            ("resourcesrepeat", "can reach", "a non-empty listing followed by an empty one"),
            ("resourcesscoped", "scoped", "a listing narrowed by arguments"),
            ("resourcescursor", "can reach", "an empty first page with more to come"),
            ("resourcesread", "other than", "a read_mcp_resource call in the canary"),
            ("resourcesunlisted", "can reach", "a non-empty listing the TOOLS line does not mention"),
            ("resourceserror", "did not complete", "a listing call that failed"),
            # Second review of 875a451d5: calls that start but never complete, and tools omitted with no evidence.
            ("resourcesstartedread", "other than", "a read_mcp_resource that starts but never completes"),
            ("resourcesstartedonly", "did not complete", "a listing that starts but never completes"),
            ("resourcesomitted", "not shown empty", "a reply that omits the resource tools and never lists"),
            ("resourcesextracontent", "can reach", "a listing whose second content entry has a resource"),
            ("resourcesstructured", "can reach", "a listing whose structured content has a resource")]:
        c = Case(mode)
        r = c.run(ca)
        pending(f"{name} is pending", r, fragment)
        check(f"PR text is never sent for {name}", not any(x["has_pr"] for x in c.calls()))

    for mode, name in [("nowriteattempt", "a canary that never tries the write"),
                       ("writeok", "a canary whose write command succeeds but leaves no file"),
                       ("writeother", "a canary whose only failed command is not the probe write"),
                       ("readfails", "a canary whose probe command only fails to read the missing file"),
                       ("badparameter", "a canary whose probe write fails on a mistyped parameter")]:
        c = Case(mode)
        r = c.run(ca)
        pending(f"{name} is pending", r, "no refused write attempt")
        check(f"PR text is never sent for {name}", not any(x["has_pr"] for x in c.calls()))

    c = Case("writes")
    r = c.run(ca)
    pending("a canary that can write is pending", r, "could write")
    check("the probe file is removed and no PR text was sent", not (c.repo / ca.PROBE).exists() and not any(x["has_pr"] for x in c.calls()))

    c = Case("canaryother")
    r = c.run(ca)
    pending("a canary that changes any file is pending", r, "uncommitted")
    check("PR text is never sent after a canary changed the checkout", not any(x["has_pr"] for x in c.calls()))

    for mode, fragment in [("malformed", "not valid JSON"), ("empty", "empty reply"), ("wrongcommit", "names commit"),
                           ("approvedhigh", "approves while"), ("dupids", "share an id"), ("exit1", "exited 1"),
                           ("incomplete", "did not complete"), ("writeduring", "uncommitted"),
                           ("unverified", "UNVERIFIED"), ("thinfinding", "finding"), ("badtypes", "coverage"),
                           ("badsummary", "summary"), ("extrakey", "unexpected field")]:
        c = Case(mode)
        pending(f"{mode} is pending", c.run(ca), fragment)

    c = Case("wrongsession")
    pending("a different session than asked is pending", c.run(ca, resume="sess-1"), "resumed session")

    c = Case("timeout")
    t0 = time.time()
    r = c.run(ca, timeout=3)
    pending("a hang is pending", r, "did not finish")
    check("a hang is stopped promptly", time.time() - t0 < 25, f"{time.time() - t0:.0f}s")

    print()
    if FAILURES:
        print(f"{len(FAILURES)} FAILED: " + "; ".join(FAILURES))
        return 1
    print("all codex-audit checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
