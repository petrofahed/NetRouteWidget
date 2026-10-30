"""Describe the host's native PR-team profiles or dispatch its external audit.

The entry point supplies --host explicitly. Installed CLIs never decide the host.
No team workflow or permissions live in this dispatcher.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import re
import sys
import tomllib

ROOT = Path(__file__).resolve().parents[1]
ROLES = ("lead", "architect", "developer", "executor", "auditor")


def load_script(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def claude_profile(role):
    path = ROOT / ".claude/agents/tasks" / f"pr-{role}.md"
    frontmatter = path.read_text(encoding="utf-8").split("---", 2)[1]
    values = dict(re.findall(r"^(model|effort):\s*(\S+)\s*$", frontmatter, re.M))
    if "model" not in values:
        raise ValueError(f"Missing model in {path}")
    return {"model": values["model"], "effort": values.get("effort", "model-default"),
            "profile": path.relative_to(ROOT).as_posix()}


def assignments(host):
    if host not in ("claude", "codex"):
        raise ValueError(f"Unknown PR-team host: {host}")
    if host == "claude":
        roles = {role: claude_profile(role) for role in ROLES}
        external = {"provider": "codex", "model": "gpt-6-astra", "effort": "existing-helper-default",
                    "helper": "codex-audit"}
    else:
        roles = {}
        for role in ROLES:
            path = ROOT / ".codex/agents" / f"pr-{role}.toml"
            native = tomllib.loads(path.read_text(encoding="utf-8"))
            if native.get("name") != f"pr-{role}" or not native.get("developer_instructions"):
                raise ValueError(f"Invalid role profile: {path}")
            roles[role] = {"model": native["model"], "effort": native["model_reasoning_effort"],
                           "profile": path.relative_to(ROOT).as_posix()}
        external = {"provider": "claude", "model": claude_profile("auditor")["model"],
                    "effort": "high", "helper": "claude-audit"}
    return {"host": host, "roles": roles, "external": external}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, choices=("claude", "codex"))
    parser.add_argument("--describe", action="store_true")
    for name in ("repo", "sha", "prompt", "out", "resume", "since", "base"):
        parser.add_argument(f"--{name}")
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args(argv)
    try:
        profile = assignments(args.host)
    except (OSError, ValueError, KeyError, IndexError) as exc:
        print(f"PR-TEAM BLOCKED: {exc}")
        return 3
    if args.describe:
        print(json.dumps(profile, indent=2))
        return 0
    required = ("repo", "sha", "prompt", "out")
    if any(not getattr(args, key) for key in required):
        parser.error("audit dispatch requires --repo, --sha, --prompt and --out")
    external = profile["external"]
    audit_args = [item for key in required for item in (f"--{key}", getattr(args, key))]
    audit_args += ["--model", external["model"], "--timeout", str(args.timeout)]
    for key in ("resume", "since"):
        if getattr(args, key):
            audit_args += [f"--{key}", getattr(args, key)]
    if external["provider"] == "claude":
        audit_args += ["--effort", external["effort"]]
        if args.base:
            audit_args += ["--base", args.base]
    return load_script(external["helper"]).main(audit_args)


if __name__ == "__main__":
    sys.exit(main())
