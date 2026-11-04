"""PreToolUse hook: keeps each M1 subagent inside its area (docs/implementation-plan.md, Guardrails).

The main session (orchestrator) has no agent_type and is not restricted here; project deny rules in
.claude/settings.json still apply to it. Subagents are checked on every edit and shell command:

- Edits must target a file inside the agent's own worktree (.claude/worktrees/<name>/) and inside the
  folders that agent may change. Unknown agents may not edit at all.
- Shell commands that push, merge, deploy, or touch Azure or secrets are blocked for every subagent.

Exit code 2 blocks the tool call; the message on stderr is shown to the agent.
"""

import json
import os
import re
import sys

EDIT_TOOLS = {"Edit", "Write", "MultiEdit", "NotebookEdit"}

ALLOWED_EDIT_PREFIXES = {
    "implementer": ("src/", "tests/", "tools/"),
    "verifier": ("tests/",),
    "config-author": ("config/", "docs/runbooks/"),
    "planner": (),
    "researcher": (),
}

BLOCKED_COMMANDS = [
    (r"\bgit\b[^;&|]*\bpush\b", "pushing is done by the orchestrator"),
    (r"\bgh\s+pr\s+(merge|close|review)\b", "only Raisel merges or closes PRs"),
    (r"\bgh\s+(release|repo|secret|variable|workflow\s+run)\b", "GitHub admin actions are not allowed"),
    (r"(^|[\s;&|(])az\s", "Azure CLI is not allowed for agents"),
    (r"\bfunc\s+azure\b", "Functions deployment is not allowed for agents"),
    (r"scripts/seed-", "secret seeding scripts are not allowed for agents"),
    (r"\bgit\s+branch\s+-[dD]\b", "deleting branches is not allowed"),
    (r"\bgit\s+reset\s+--hard\s+origin", "resetting to a remote branch is not allowed"),
]


def block(message):
    print(f"Blocked by M1 agent guardrail: {message}", file=sys.stderr)
    sys.exit(2)


def check_edit(agent, tool_input):
    raw_path = tool_input.get("file_path") or tool_input.get("notebook_path")
    if not raw_path:
        block("edit without a file path")

    path = os.path.realpath(raw_path)
    match = re.search(r"/\.claude/worktrees/[^/]+/(.+)$", path)
    if not match:
        block(f"{agent} may only edit files inside its own worktree, not {raw_path}")

    relative = match.group(1)
    prefixes = ALLOWED_EDIT_PREFIXES.get(agent)
    if prefixes is None:
        block(f"agent '{agent}' is not allowed to edit files")
    if not relative.startswith(prefixes):
        allowed = ", ".join(prefixes) if prefixes else "nothing"
        block(f"{agent} may edit only {allowed}; {relative} is outside its area")


def check_command(command):
    for pattern, reason in BLOCKED_COMMANDS:
        if re.search(pattern, command):
            block(reason)


def main():
    try:
        data = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        block("hook input could not be read")

    agent = data.get("agent_type")
    if not agent:
        sys.exit(0)

    tool = data.get("tool_name", "")
    tool_input = data.get("tool_input") or {}
    if tool in EDIT_TOOLS:
        check_edit(agent, tool_input)
    elif tool == "Bash":
        check_command(tool_input.get("command", ""))
    sys.exit(0)


if __name__ == "__main__":
    main()
