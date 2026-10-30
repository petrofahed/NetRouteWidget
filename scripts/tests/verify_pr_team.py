"""Offline contract tests: host routing, audit restrictions and fail-closed receipts.

Run: python scripts/tests/verify_pr_team.py
No provider calls, credentials or live services are used.
"""
import contextlib
import importlib.util
import io
import json
import re
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def load(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


dispatch = load("pr-team")
claude = load("claude-audit")
MODEL = "claude-opus-5-5"


def events(review=None, session="session-1"):
    init = {"type": "system", "subtype": "init", "tools": ["Read", "Glob", "Grep"],
            "mcp_servers": [], "permissionMode": "dontAsk", "model": MODEL, "session_id": session}
    result = {"type": "result", "subtype": "success", "is_error": False,
              "session_id": session, "modelUsage": {MODEL: {}}, "result": "CANARY"}
    if review is not None:
        init["tools"].append("StructuredOutput")
        result["structured_output"] = review
    return [init, result]


def stream(items):
    return "\n".join(json.dumps(item) for item in items)


class RoutingTests(unittest.TestCase):
    def test_codex_native_personas(self):
        actual = dispatch.assignments("codex")
        self.assertEqual({k: (v["model"], v["effort"]) for k, v in actual["roles"].items()}, {
            "lead": ("gpt-6.1-sol", "medium"), "architect": ("gpt-6.1-sol", "high"),
            "developer": ("gpt-6.1-sol", "medium"), "executor": ("gpt-6-luna", "low"),
            "auditor": ("gpt-6.1-sol", "high")})
        self.assertEqual(actual["external"]["model"], MODEL)
        self.assertEqual(actual["external"]["effort"], "high")

    def test_claude_profiles_preserved(self):
        roles = dispatch.assignments("claude")["roles"]
        self.assertEqual((roles["lead"]["model"], roles["lead"]["effort"]), ("claude-opus-5-5", "medium"))
        self.assertEqual(roles["lead"]["profile"], ".claude/agents/tasks/pr-lead.md")
        self.assertEqual(roles["architect"]["model"], "claude-sonnet-5-5")
        self.assertEqual(roles["developer"]["effort"], "medium")
        self.assertEqual(roles["executor"]["model"], "claude-haiku-5-5")
        self.assertEqual(roles["executor"]["effort"], "model-default")
        self.assertEqual(roles["auditor"]["effort"], "high")

    def test_both_hosts_define_the_same_roles_with_a_profile_each(self):
        claude_roles, codex_roles = (dispatch.assignments(h)["roles"] for h in ("claude", "codex"))
        self.assertEqual(set(claude_roles), set(codex_roles))
        for roles in (claude_roles, codex_roles):
            self.assertTrue(all((ROOT / r["profile"]).is_file() for r in roles.values()))

    def test_audit_routes_to_other_provider_and_keeps_change_pass(self):
        for host, helper, model in (("claude", "codex-audit", "gpt-6-astra"),
                                    ("codex", "claude-audit", MODEL)):
            with self.subTest(host=host), patch.object(dispatch, "load_script") as loader:
                loader.return_value.main.return_value = 3
                code = dispatch.main(["--host", host, "--repo", "repo", "--sha", "a" * 40,
                                      "--prompt", "prompt", "--out", "out", "--resume", "s1",
                                      "--since", "b" * 40])
                self.assertEqual(code, 3)
                loader.assert_called_once_with(helper)
                args = loader.return_value.main.call_args.args[0]
                self.assertEqual(args[args.index("--model") + 1], model)
                self.assertEqual(args[args.index("--resume") + 1], "s1")
                self.assertEqual(args[args.index("--since") + 1], "b" * 40)
                self.assertEqual("--effort" in args, host == "codex")

    def test_no_host_guess_or_unknown_host(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            dispatch.main(["--describe"])
        with self.assertRaises(ValueError):
            dispatch.assignments("auto")

    def test_inventory_rejects_capability_or_model_drift(self):
        for change in ({"tools": ["Read", "Bash"]}, {"mcp_servers": [{"name": "github"}]},
                       {"permissionMode": "bypassPermissions"}, {"model": "claude-sonnet-5-5"},
                       {"tools": []}):
            with self.subTest(change=change):
                items = events()
                items[0].update(change)
                with self.assertRaises(claude.Pending):
                    claude.parse_turn(stream(items), MODEL)

    def test_result_errors_wrong_session_and_fallback_rejected(self):
        for change in ({"is_error": True}, {"subtype": "error_max_turns"},
                       {"session_id": "wrong"}, {"modelUsage": {"claude-sonnet-5-5": {}}}):
            with self.subTest(change=change):
                items = events()
                items[-1].update(change)
                with self.assertRaises(claude.Pending):
                    claude.parse_turn(stream(items), MODEL)


HANDOFF = ROOT / ".claude/skills/architect-developer-handoff"
MODEL_NAMES = re.compile(r"gpt-\d|\b(?:Sol|Luna|Astra|Opus|Sonnet|Haiku)\b")


class HostProseTests(unittest.TestCase):
    """The two hosts' written rules cannot drift apart unnoticed."""

    @staticmethod
    def read(*parts):
        return HANDOFF.joinpath(*parts).read_text(encoding="utf-8")

    def assignments(self, host):
        return self.read("hosts", f"{host}.md").split("\n## External audit", 1)[0]

    def test_each_adapter_names_every_role_profile(self):
        for host in ("claude", "codex"):
            for role in dispatch.ROLES:
                with self.subTest(host=host, role=role):
                    self.assertIn(f"pr-{role}", self.read("hosts", f"{host}.md"))

    def test_models_live_in_profiles_never_in_assignment_prose(self):
        for name, text in (("claude adapter", self.assignments("claude")),
                           ("codex adapter", self.assignments("codex")),
                           ("shared workflow", self.read("pr-team.md"))):
            with self.subTest(file=name):
                self.assertIsNone(MODEL_NAMES.search(text))

    def test_lead_rule_is_written_once_and_both_adapters_point_to_it(self):
        shared = self.read("pr-team.md")
        self.assertEqual(shared.count("## Who the lead is"), 1)
        for role in dispatch.ROLES:
            self.assertRegex(shared, rf"\b{role}\b")
        for host in ("claude", "codex"):
            with self.subTest(host=host):
                self.assertIn("pr-team.md#who-the-lead-is", self.read("hosts", f"{host}.md"))
                self.assertNotIn("transport", self.read("hosts", f"{host}.md"))


class AuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.git("init", "-q")
        (self.repo / "code.txt").write_text("source\n")
        self.git("add", "code.txt")
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "base")
        self.sha = self.git("rev-parse", "HEAD").strip()
        self.out = self.root / "out"
        self.review = {"commit": self.sha, "verdict": "APPROVED", "summary": "Source checked",
                       "findings": [], "coverage": ["code.txt"],
                       "limitations": ["Source inspection only; no tests executed by external auditor."]}

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.repo, text=True)

    def run_audit(self, **kwargs):
        opts = dict(repo=self.repo, sha=self.sha, prompt="PR PRIVATE CONTENT", out=self.out,
                    model=MODEL, exe="claude", base=self.sha)
        opts.update(kwargs)
        return claude.audit(**opts)

    def test_success_and_restrictions_on_both_turns(self):
        calls = []
        def fake(argv, prompt, repo, timeout):
            calls.append((argv, prompt))
            return 0, stream(events(self.review if len(calls) == 2 else None)), ""
        with patch.object(claude, "run_claude", side_effect=fake):
            result = self.run_audit()
        self.assertEqual(result["status"], "ok")
        self.assertEqual(len(calls), 2)
        self.assertNotIn("PR PRIVATE CONTENT", calls[0][1])
        self.assertIn("PR PRIVATE CONTENT", calls[1][1])
        for argv, _ in calls:
            self.assertNotIn("--bare", argv)  # Bare mode disables the owner's subscription login.
            for flag in ("--safe-mode", "--restricted", "--strict-mcp-config", "--disable-slash-commands"):
                self.assertIn(flag, argv)
            self.assertEqual(argv[argv.index("--tools") + 1], "Read,Glob,Grep")
            self.assertEqual(argv[argv.index("--effort") + 1], "high")
            self.assertEqual(argv[argv.index("--model") + 1], MODEL)
        self.assertEqual(calls[1][0][calls[1][0].index("--resume") + 1], "session-1")
        self.assertEqual(json.loads((self.out / "result.json").read_text())["sha"], self.sha)

    def test_bad_canary_never_receives_pr_content(self):
        items = events()
        items[0]["tools"].append("mcp__github__merge")
        with patch.object(claude, "run_claude", return_value=(0, stream(items), "")) as call:
            result = self.run_audit()
        self.assertEqual(result["status"], "pending")
        self.assertEqual(call.call_count, 1)
        self.assertNotIn("PR PRIVATE CONTENT", call.call_args.args[1])

    def test_missing_cli_dirty_checkout_wrong_sha_and_output_inside(self):
        for kwargs in ({"exe": None}, {"sha": "a" * 40}, {"out": self.repo / "out"},
                       {"effort": "medium"}, {"resume": "session-1"}, {"base": None}):
            with self.subTest(kwargs=kwargs), patch.object(claude, "run_claude") as call:
                self.assertEqual(self.run_audit(**kwargs)["status"], "pending")
                call.assert_not_called()
        (self.repo / "code.txt").write_text("dirty")
        with patch.object(claude, "run_claude") as call:
            self.assertEqual(self.run_audit()["status"], "pending")
            call.assert_not_called()

    def test_wrong_sha_malformed_or_unverified_review_never_approves(self):
        variants = [None, {"commit": self.sha}, {**self.review, "commit": "b" * 40},
                    {**self.review, "verdict": "UNVERIFIED"},
                    {**self.review, "findings": [{"id": "R1", "severity": "high", "path": "code.txt",
                       "evidence": "bug", "fix": "fix"}]}]
        for review in variants:
            with self.subTest(review=review), patch.object(claude, "run_claude", side_effect=[
                    (0, stream(events()), ""), (0, stream(events(review)), "")]):
                self.assertEqual(self.run_audit()["status"], "pending")

    def test_revise_is_completed_review_but_not_approval(self):
        review = {**self.review, "verdict": "REVISE"}
        with patch.object(claude, "run_claude", side_effect=[
                (0, stream(events()), ""), (0, stream(events(review)), "")]):
            result = self.run_audit()
        self.assertEqual((result["status"], result["verdict"]), ("ok", "REVISE"))

    def test_post_audit_mutation_invalidates_receipt(self):
        count = 0
        def fake(*args):
            nonlocal count
            count += 1
            if count == 2:
                (self.repo / "code.txt").write_text("changed unexpectedly")
            return 0, stream(events(self.review if count == 2 else None)), ""
        with patch.object(claude, "run_claude", side_effect=fake):
            result = self.run_audit()
        self.assertEqual(result["status"], "pending")
        self.assertNotIn("verdict", result)

    def test_change_pass_uses_same_session_and_actual_diff(self):
        old = self.sha
        (self.repo / "code.txt").write_text("updated source\n")
        self.git("add", "code.txt")
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "update")
        self.sha = self.git("rev-parse", "HEAD").strip()
        self.review["commit"] = self.sha
        with patch.object(claude, "run_claude", side_effect=[
                (0, stream(events()), ""), (0, stream(events(self.review)), "")]) as call:
            result = self.run_audit(resume="session-1", since=old)
        self.assertEqual(result["status"], "ok")
        self.assertIn("+updated source", call.call_args.args[1])
        self.assertIn(old, call.call_args.args[1])

    def test_initial_audit_preserves_unicode_in_git_diff(self):
        old = self.sha
        content = "source \u2014 quote \u201d\n"
        (self.repo / "code.txt").write_text(content, encoding="utf-8")
        self.git("add", "code.txt")
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "unicode")
        self.sha = self.git("rev-parse", "HEAD").strip()
        self.review["commit"] = self.sha
        with patch.object(claude, "run_claude", side_effect=[
                (0, stream(events()), ""), (0, stream(events(self.review)), "")]) as call:
            result = self.run_audit(base=old)
        self.assertEqual(result["status"], "ok")
        self.assertIn("+" + content.rstrip("\n"), call.call_args.args[1])

    def test_initial_audit_includes_deleted_source_from_verified_base(self):
        old = self.sha
        self.git("rm", "code.txt")
        self.git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "delete")
        self.sha = self.git("rev-parse", "HEAD").strip()
        self.review["commit"] = self.sha
        with patch.object(claude, "run_claude", side_effect=[
                (0, stream(events()), ""), (0, stream(events(self.review)), "")]) as call:
            result = self.run_audit(base=old)
        self.assertEqual(result["status"], "ok")
        self.assertIn("deleted file mode", call.call_args.args[1])
        self.assertIn("-source", call.call_args.args[1])
        self.assertIn(old, call.call_args.args[1])


if __name__ == "__main__":
    unittest.main(verbosity=2)
