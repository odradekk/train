#!/usr/bin/env python3
"""Keep files small enough for an agent to read whole before editing them.

Usage: check_file_size.py [--all | --write-baseline] [--config PATH]

Checks the staged content of every staged file, or with --all every tracked
file, against a token budget estimated as bytes / 4. Settings live in a JSON
file at the repository root (default: .agents/file-size.json):

    {
      "max_tokens": 5000,
      "exclude": ["**/*.lock", "dist/**"],
      "exceptions": {"docs/spec.md": "one standard; splitting breaks its numbering"},
      "baseline": {"src/legacy.ts": 7400}
    }

- exclude: glob patterns, relative to the repository root, for files nobody
  reads whole to edit, such as lockfiles and generated or vendored code.
  Binary files are always skipped.
- exceptions: files that must stay whole, each with the reason.
- baseline: files that were already over the limit when the check was adopted,
  with their size at that time. Such a file may shrink but not grow, and its
  entry goes down as the file shrinks. --write-baseline creates the baseline
  when the key is absent; afterwards it only lowers or removes entries.

Exit status: 0 clean, 1 problems found, 2 usage, configuration, or git error.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

DEFAULT_CONFIG = ".agents/file-size.json"

SPLIT_INSTRUCTIONS = """\
How to split a file that is over the limit:
- Group the code that changes together, such as a function with its types and
  constants, and move each group to a new file named for what it does. A name
  like "part2" or "utils2" means the split follows line count instead of
  responsibility; choose the groups again.
- Split a document by topic into linked pages.
- Keep existing imports working: leave the original file as the entry point
  that re-exports the moved code, or update every import in the same commit.
- Move tests together with the code they cover, run them, stage the new files,
  and commit again.
A file that must stay whole, such as a single specification, belongs in
"exceptions" in {config} with the reason, for a reviewer to approve."""


class SetupError(Exception):
    """A configuration or git problem that stops the check before it starts."""


def tokens(size: int) -> int:
    return (size + 3) // 4


def glob_regex(pattern: str) -> re.Pattern[str]:
    """Translate a glob with ** (any directories), * and ? (within one path segment)."""
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out, i = out + "(?:.*/)?", i + 3
        elif pattern.startswith("**", i):
            out, i = out + ".*", i + 2
        elif pattern[i] == "*":
            out, i = out + "[^/]*", i + 1
        elif pattern[i] == "?":
            out, i = out + "[^/]", i + 1
        else:
            out, i = out + re.escape(pattern[i]), i + 1
    return re.compile(out)


def git(root: Path | None, *args: str, stdin: str | None = None) -> str:
    command = ["git", "-c", "core.quotepath=false", *(["-C", str(root)] if root else []), *args]
    result = subprocess.run(command, input=stdin, capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        raise SetupError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout


def index_files(root: Path) -> dict[str, tuple[int, bool]]:
    """Map each path in the index to (size in bytes, is binary)."""
    blobs = {}
    for entry in git(root, "ls-files", "-s", "-z").split("\0"):
        if entry:
            meta, _, path = entry.partition("\t")
            _, sha, stage = meta.split()
            if stage == "0":
                blobs[path] = sha
    binary = set()
    for entry in git(root, "ls-files", "--eol", "-z").split("\0"):
        if entry:
            meta, _, path = entry.partition("\t")
            if meta.startswith("i/-text"):
                binary.add(path)
    paths = list(blobs)
    sizes = git(root, "cat-file", "--batch-check", stdin="".join(f"{blobs[p]}\n" for p in paths)).splitlines()
    return {path: (int(line.split()[2]), path in binary) for path, line in zip(paths, sizes)}


def staged_paths(root: Path) -> list[str]:
    output = git(root, "diff", "--cached", "--name-only", "-z", "--diff-filter=ACMR")
    return [path for path in output.split("\0") if path]


def load_config(path: Path) -> dict:
    try:
        config = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise SetupError(f"{path} does not exist; the setup-agent-workflow skill creates it") from None
    except json.JSONDecodeError as error:
        raise SetupError(f"{path} is not valid JSON: {error}") from None
    max_tokens = config.get("max_tokens")
    if not isinstance(max_tokens, int) or isinstance(max_tokens, bool) or max_tokens <= 0:
        raise SetupError(f"{path}: max_tokens must be a positive integer")
    if not all(isinstance(p, str) for p in config.setdefault("exclude", [])):
        raise SetupError(f"{path}: exclude must be a list of glob strings")
    exceptions = config.setdefault("exceptions", {})
    if not isinstance(exceptions, dict) or not all(isinstance(r, str) and r.strip() for r in exceptions.values()):
        raise SetupError(f"{path}: exceptions must map each path to a non-empty reason")
    baseline = config.get("baseline", {})
    if not isinstance(baseline, dict) or not all(isinstance(t, int) and t > 0 for t in baseline.values()):
        raise SetupError(f"{path}: baseline must map each path to a positive token count")
    return config


def checked(path: str, files: dict[str, tuple[int, bool]], config: dict, excludes: list[re.Pattern[str]]) -> bool:
    return (path in files and not files[path][1] and path not in config["exceptions"]
            and not any(p.fullmatch(path) for p in excludes))


def find_problems(paths: list[str], files: dict, config: dict, excludes: list, rerun: str) -> list[tuple[str, str]]:
    """Return (kind, message) pairs; kind is "split" when the split instructions apply."""
    limit, baseline, problems = config["max_tokens"], config.get("baseline", {}), []
    for path in paths:
        if not checked(path, files, config, excludes):
            continue
        size = files[path][0]
        count, recorded = tokens(size), baseline.get(path)
        if recorded is None and count > limit:
            problems.append(("split", f"{path}: about {count:,} tokens ({size:,} bytes / 4); the limit is {limit:,}."))
        elif recorded is not None and count > recorded:
            problems.append(("split", f"{path}: about {count:,} tokens, more than its baseline of {recorded:,}. "
                                      "It was over the limit when this check was adopted, so it may shrink but not "
                                      "grow: put the new code in another file, or split this one."))
        elif recorded is not None and count < recorded:
            problems.append(("baseline", f"{path}: about {count:,} tokens, less than its baseline of {recorded:,}. "
                                         f"Lower the baseline with `{rerun} --write-baseline`, then stage the "
                                         "configuration file."))
    return problems


def write_baseline(config_path: Path, config: dict, files: dict, excludes: list) -> list[str]:
    """Create the baseline, or lower and prune it; never raise or add an entry. Return report lines."""
    limit, report = config["max_tokens"], []
    over = {p: tokens(files[p][0]) for p in files if checked(p, files, config, excludes) and tokens(files[p][0]) > limit}
    if "baseline" not in config:
        baseline = dict(sorted(over.items()))
        report.append(f"created a baseline of {len(baseline)} files over {limit:,} tokens")
    else:
        baseline = {}
        for path, recorded in sorted(config["baseline"].items()):
            if path not in over:
                report.append(f"removed {path}: no longer tracked or no longer over the limit")
            elif over[path] > recorded:
                baseline[path] = recorded
                report.append(f"kept {path} at {recorded:,}: it grew to {over[path]:,}, and a baseline never rises")
            else:
                baseline[path] = over[path]
                if over[path] < recorded:
                    report.append(f"lowered {path} from {recorded:,} to {over[path]:,}")
        for path in sorted(set(over) - set(config["baseline"])):
            report.append(f"not added {path} ({over[path]:,} tokens): split it, or add it to exceptions with a reason")
    config["baseline"] = baseline
    config_path.write_text(json.dumps(config, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Check staged files against a token budget.")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--all", action="store_true", help="check every tracked file instead of the staged files")
    mode.add_argument("--write-baseline", action="store_true", help="create the baseline, or lower and prune it")
    parser.add_argument("--config", help=f"settings file, relative to the repository root (default: {DEFAULT_CONFIG})")
    args = parser.parse_args(argv)
    sys.stdout.reconfigure(errors="backslashreplace")

    try:
        root = Path(git(None, "rev-parse", "--show-toplevel").strip())
        config_path = root / (args.config or DEFAULT_CONFIG)
        config = load_config(config_path)
        excludes = [glob_regex(p) for p in config["exclude"]]
        files = index_files(root)
        if args.write_baseline:
            for line in write_baseline(config_path, config, files, excludes):
                print(line)
            return 0
        paths = sorted(files) if args.all else staged_paths(root)
    except SetupError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    rerun = f"python {Path(sys.argv[0]).as_posix()}"
    problems = find_problems(paths, files, config, excludes, rerun)
    for _, message in problems:
        print(message)
    if any(kind == "split" for kind, _ in problems):
        print()
        print(SPLIT_INSTRUCTIONS.format(config=args.config or DEFAULT_CONFIG))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
