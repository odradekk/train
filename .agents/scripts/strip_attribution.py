#!/usr/bin/env python3
"""Keep AI attribution out of commit messages.

Usage:
  strip_attribution.py MESSAGE_FILE   commit-msg hook: remove attribution lines in place
  strip_attribution.py --pre-push     pre-push hook: reject outgoing commits that still carry them
  strip_attribution.py --amend        rewrite HEAD's message without them (used by the pre-push fix)

An attribution line is one of:
- a "...-by:" trailer, such as Co-authored-by or Signed-off-by, whose name or
  email belongs to an AI tool;
- a hyphenated trailer named after an AI tool, such as Claude-Session;
- a "Generated with / by / using ..." line that names an AI tool, with or
  without a leading emoji, such as "Generated with [Claude Code](...)".
Lines that only mention a tool, such as "Add the Claude API client", stay.

The pre-push check also rejects commits whose author or committer is an AI
tool. Projects add tool names as regular expressions in .agents/attribution.json at
the repository root: {"identities": ["my-agent"]}.

Exit status: 0 clean or cleaned, 1 outgoing commits need fixing, 2 usage or git error.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

IDENTITIES = [
    r"claude", r"anthropic", r"codex", r"chatgpt", r"openai", r"gpt-?\d[\w.]*",
    r"copilot", r"cursor", r"gemini", r"aider", r"windsurf", r"codeium",
    r"cline", r"codebuddy", r"antigravity", r"qwen", r"deepseek", r"devin-ai",
]
CONFIG = ".agents/attribution.json"
ZERO = "0" * 40
TRAILER = re.compile(r"\s*([A-Za-z][\w-]*)\s*:\s*(.*)")
GENERATED = re.compile(r"\W*(?:generated|created|made|written|built)\s+(?:with|by|using|via)\b", re.IGNORECASE)


class GitError(Exception):
    """A git command failed."""


def git(*args: str, stdin: str | None = None) -> str:
    result = subprocess.run(["git", *args], input=stdin, capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        raise GitError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout


def identity_pattern() -> re.Pattern[str]:
    names = list(IDENTITIES)
    try:
        root = Path(git("rev-parse", "--show-toplevel").strip())
        names += json.loads((root / CONFIG).read_text(encoding="utf-8")).get("identities", [])
    except (GitError, FileNotFoundError):
        pass
    return re.compile(r"(?<![a-z0-9])(?:" + "|".join(names) + r")(?![a-z0-9])", re.IGNORECASE)


def is_attribution(line: str, ai: re.Pattern[str]) -> bool:
    trailer = TRAILER.fullmatch(line)
    if trailer:
        key, value = trailer.groups()
        if key.lower().endswith("-by") and ai.search(value):
            return True
        if "-" in key and ai.match(key):
            return True
    return bool(GENERATED.match(line) and ai.search(line))


def strip(message: str, ai: re.Pattern[str]) -> tuple[str, list[str]]:
    """Return the message without attribution lines, and the lines removed."""
    newline = "\r\n" if "\r\n" in message else "\n"
    kept, removed, just_removed = [], [], False
    for line in message.splitlines():
        if is_attribution(line, ai):
            removed.append(line)
            just_removed = True
            continue
        if not line.strip() and just_removed and kept and not kept[-1].strip():
            continue  # a removal left two blank lines in a row
        just_removed = just_removed and not line.strip()
        kept.append(line)
    while kept and not kept[-1].strip():
        kept.pop()
    return newline.join(kept) + newline if kept else "", removed


def commit_msg(path: Path) -> int:
    message = path.read_text(encoding="utf-8", errors="surrogateescape")
    cleaned, removed = strip(message, identity_pattern())
    if removed:
        path.write_text(cleaned, encoding="utf-8", errors="surrogateescape", newline="")
        for line in removed:
            print(f"commit-msg: removed AI attribution: {line.strip()}", file=sys.stderr)
    return 0


def amend() -> int:
    message = git("log", "-1", "--format=%B")
    cleaned, removed = strip(message, identity_pattern())
    if removed:
        git("commit", "--amend", "--no-verify", "--allow-empty", "--cleanup=verbatim", "-F", "-", stdin=cleaned)
    return 0


def outgoing_commits(updates: str) -> list[tuple[str, list[str]]]:
    """Return (local ref, new commit SHAs) for each ref the push updates."""
    pushes = []
    for line in updates.splitlines():
        if not line.strip():
            continue
        local_ref, local_sha, _, remote_sha = line.split()
        if local_sha == ZERO:
            continue  # deleting a remote ref
        known = remote_sha != ZERO and subprocess.run(
            ["git", "cat-file", "-e", f"{remote_sha}^{{commit}}"], capture_output=True).returncode == 0
        revs = [f"{remote_sha}..{local_sha}"] if known else [local_sha, "--not", "--remotes"]
        pushes.append((local_ref, git("rev-list", "--reverse", *revs).split()))
    return pushes


def pre_push(updates: str) -> int:
    ai = identity_pattern()
    problems = []
    for ref, shas in outgoing_commits(updates):
        if not shas:
            continue
        log = git("log", "--no-walk=unsorted", "--stdin", "--format=%H%x00%an <%ae>%x00%cn <%ce>%x00%B%x1e",
                  stdin="\n".join(shas) + "\n")
        bad_message, bad_identity = [], []
        for record in filter(str.strip, log.split("\x1e")):
            sha, author, committer, body = record.strip("\n").split("\x00", 3)
            if strip(body, ai)[1]:
                bad_message.append(sha)
            if ai.search(author) or ai.search(committer):
                bad_identity.append(sha)
        if bad_message or bad_identity:
            problems.append((ref, shas[0], bad_message, bad_identity))

    amend_command = f"'{Path(sys.executable).as_posix()}' {Path(sys.argv[0]).as_posix()} --amend"
    for ref, first, bad_message, bad_identity in problems:
        base = f"{first}^" if git("rev-list", "--parents", "-n", "1", first).split()[1:] else "--root"
        print(f"pre-push: {ref} has commits that credit an AI tool.", file=sys.stderr)
        if bad_message:
            print(f"  Attribution lines in: {', '.join(s[:9] for s in bad_message)}", file=sys.stderr)
            print(f"  Remove them, on that branch: git rebase --exec \"{amend_command}\" {base}", file=sys.stderr)
        if bad_identity:
            print(f"  AI author or committer in: {', '.join(s[:9] for s in bad_identity)}", file=sys.stderr)
            print("  Reset to your identity, on that branch: git rebase --exec "
                  f"\"git commit --amend --no-edit --no-verify --reset-author\" {base}", file=sys.stderr)
        print("  These commits are not pushed yet, so rewriting them is safe. Then push again.", file=sys.stderr)
    return 1 if problems else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Keep AI attribution out of commit messages.")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("message_file", nargs="?", type=Path, help="commit message file (commit-msg hook)")
    mode.add_argument("--pre-push", action="store_true", help="check the commits a push sends (reads stdin)")
    mode.add_argument("--amend", action="store_true", help="rewrite HEAD's message without attribution")
    args = parser.parse_args(argv)
    sys.stderr.reconfigure(errors="backslashreplace")
    try:
        if args.pre_push:
            return pre_push(sys.stdin.read())
        if args.amend:
            return amend()
        return commit_msg(args.message_file)
    except GitError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
