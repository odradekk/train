#!/usr/bin/env python3
"""Move decision notes between lifecycle folders, the only way frozen notes change.

Usage:
  note_lifecycle.py NOTES_DIR accept ID
  note_lifecycle.py NOTES_DIR reject ID REASON
  note_lifecycle.py NOTES_DIR supersede NEW_ID OLD_ID [--part TEXT]
  note_lifecycle.py NOTES_DIR archive ID
  note_lifecycle.py NOTES_DIR protect

Notes in accepted/, rejected/, and archived/ are frozen and read-only. accept and
reject move a proposal there and set its status line; supersede links two accepted
notes in both directions and, without --part, archives the old one; archive moves
an accepted or rejected note to archived/ with an Archived line. These header lines
are the only edits a frozen note ever gets, and only this script makes them, lifting
the read-only flag for its own edit. protect makes every frozen note read-only and
every proposal writable again; the post-checkout, post-merge, and post-rewrite hooks
run it, since git does not record the flag. Exit status: 0 done, 1 refused, 2 usage.
"""

from __future__ import annotations

import argparse
import os
import re
import stat
import sys
from datetime import date
from pathlib import Path

LIFECYCLES = ("proposed", "accepted", "rejected", "archived")
FROZEN = ("accepted", "rejected", "archived")
NOTE_ID = re.compile(r"\d{4}-\d{2}-\d{2}-[a-z0-9]+(?:-[a-z0-9]+)*")

GUIDES = {
    "accepted": """\
# Accepted decision notes

These are decisions the user accepted. They are frozen: the files are read-only, and nobody edits, moves, renames, or deletes them by hand. Work carries them out; when work conflicts with one, report the conflict instead of changing the note, and leave a change of the decision to planning.

Only `note_lifecycle.py` of the `decision-notes` skill changes these files, on the user's word: `supersede` adds the lines that link a note to the one that replaces it, and `archive` moves a note to `archived/`. The `decision-notes` verifier rejects any other change.
""",
    "rejected": """\
# Rejected decision notes

These are proposals the user declined, kept as guardrails: read the reason before proposing the same idea again. They are frozen: the files are read-only, and nobody edits, moves, renames, or deletes them by hand.

Only `note_lifecycle.py` of the `decision-notes` skill changes these files, on the user's word: `archive` moves a note to `archived/` once its idea no longer tempts anyone. The `decision-notes` verifier rejects any other change.
""",
    "archived": """\
# Archived decision notes

These notes are history: they no longer guide new work, but they explain why existing code is the way it is, and code may still cite them. A `Superseded by:` line names the note that holds the decision now. They are frozen: the files are read-only, and nobody edits, moves, renames, or deletes them. Searches through the notes directory skip this folder; name it to search it.
""",
}


class Refused(Exception):
    """The notes are not in a state that allows the command."""


class Note:
    def __init__(self, notes_dir: Path, path: Path):
        self.notes_dir = notes_dir
        self.path = path
        self.id = path.stem
        self.cls = path.parent.name
        self.lifecycle = path.parent.parent.name

    def relative(self) -> str:
        return self.path.relative_to(self.notes_dir).as_posix()

    def read(self) -> tuple[list[str], str, bool]:
        """Return the lines, the file's newline, and whether it ends with one."""
        text = self.path.read_bytes().decode("utf-8")
        newline = "\r\n" if "\r\n" in text else "\n"
        return text.splitlines(), newline, text.endswith("\n")

    def write(self, lines: list[str], newline: str, final_newline: bool) -> None:
        set_writable(self.path, True)
        self.path.write_bytes((newline.join(lines) + (newline if final_newline else "")).encode("utf-8"))

    def edit(self, change) -> None:
        lines, newline, final_newline = self.read()
        change(lines)
        self.write(lines, newline, final_newline)

    def move_to(self, lifecycle: str) -> None:
        target = self.notes_dir / lifecycle / self.cls / self.path.name
        if target.exists():
            raise Refused(f"{target.relative_to(self.notes_dir).as_posix()} already exists")
        set_writable(self.path, True)
        target.parent.mkdir(parents=True, exist_ok=True)
        os.replace(self.path, target)
        self.path, self.lifecycle = target, lifecycle
        write_guide(self.notes_dir / lifecycle)


def set_writable(path: Path, writable: bool) -> None:
    mode = os.stat(path).st_mode
    os.chmod(path, mode | stat.S_IWUSR if writable else mode & ~(stat.S_IWUSR | stat.S_IWGRP | stat.S_IWOTH))


def write_guide(lifecycle_dir: Path) -> None:
    guide = GUIDES.get(lifecycle_dir.name)
    if guide and not (lifecycle_dir / "AGENTS.md").exists():
        (lifecycle_dir / "AGENTS.md").write_text(guide, encoding="utf-8", newline="\n")
    if guide and not (lifecycle_dir / "CLAUDE.md").exists():
        (lifecycle_dir / "CLAUDE.md").write_text("@AGENTS.md\n", encoding="utf-8", newline="\n")


def find(notes_dir: Path, note_id: str) -> Note:
    if not NOTE_ID.fullmatch(note_id):
        raise Refused(f"'{note_id}' is not a note ID, which is a filename without .md, such as 2026-01-02-parse-policy-once")
    found = [path for lifecycle in LIFECYCLES for path in (notes_dir / lifecycle).glob(f"*/{note_id}.md")]
    if not found:
        raise Refused(f"no note has the ID {note_id}")
    if len(found) > 1:
        raise Refused(f"several notes have the ID {note_id}: " + ", ".join(p.relative_to(notes_dir).as_posix() for p in found))
    return Note(notes_dir, found[0])


def require(note: Note, *lifecycles: str) -> None:
    if note.lifecycle not in lifecycles:
        raise Refused(f"{note.id} is in {note.lifecycle}/; this needs a note in {' or '.join(l + '/' for l in lifecycles)}")


def header_end(lines: list[str]) -> int:
    """Return the index just after the header lines that follow the status line."""
    end = 3
    while end < len(lines) and lines[end].strip():
        end += 1
    return end


def one_line(text: str, what: str) -> str:
    if not text.strip() or "\n" in text or "\r" in text:
        raise Refused(f"{what} must be one line of text")
    return text.strip()


def freeze(note: Note) -> None:
    set_writable(note.path, False)
    print(f"{note.id}: now {note.relative()}, read-only")


def accept(notes_dir: Path, note_id: str) -> None:
    note = find(notes_dir, note_id)
    require(note, "proposed")
    note.edit(lambda lines: lines.__setitem__(2, "Status: accepted"))
    note.move_to("accepted")
    freeze(note)


def reject(notes_dir: Path, note_id: str, reason: str) -> None:
    note = find(notes_dir, note_id)
    require(note, "proposed")
    reason = one_line(reason, "the reason")
    note.edit(lambda lines: lines.__setitem__(2, f"Status: rejected — {reason}"))
    note.move_to("rejected")
    freeze(note)


def archive(notes_dir: Path, note_id: str) -> None:
    note = find(notes_dir, note_id)
    require(note, "accepted", "rejected")
    note.edit(lambda lines: lines.insert(3, f"Archived: {date.today().isoformat()}"))
    note.move_to("archived")
    rgignore = notes_dir / ".rgignore"
    existing = rgignore.read_text(encoding="utf-8").splitlines() if rgignore.exists() else []
    if "archived/" not in existing:
        rgignore.write_text("\n".join(existing + ["archived/"]) + "\n", encoding="utf-8", newline="\n")
    freeze(note)


def supersede(notes_dir: Path, new_id: str, old_id: str, part: str | None) -> None:
    if new_id == old_id:
        raise Refused("a note cannot supersede itself")
    new, old = find(notes_dir, new_id), find(notes_dir, old_id)
    require(new, "accepted")
    require(old, "accepted")
    if part is None:
        forward, backward = f"Supersedes: {old_id}", f"Superseded by: {new_id}"
    else:
        part = one_line(part, "the part")
        forward, backward = f"Supersedes in part: {old_id} — {part}", f"Superseded in part by: {new_id} — {part}"
    if any(line.startswith(f"Supersedes: {old_id}") or line.startswith(f"Supersedes in part: {old_id} ")
           for line in new.read()[0][3:header_end(new.read()[0])]):
        raise Refused(f"{new_id} already supersedes {old_id}")
    new.edit(lambda lines: lines.insert(header_end(lines), forward))
    old.edit(lambda lines: lines.insert(header_end(lines), backward))
    freeze(new)
    if part is None:
        archive(notes_dir, old_id)
    else:
        freeze(old)


def protect(notes_dir: Path) -> None:
    count = 0
    for lifecycle in LIFECYCLES:
        for path in (notes_dir / lifecycle).glob("*/*.md"):
            set_writable(path, lifecycle not in FROZEN)
            count += lifecycle in FROZEN
    print(f"{count} frozen notes read-only")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Move decision notes between lifecycle folders.")
    parser.add_argument("notes_dir", type=Path, help="the decision-notes directory, such as .agents/notes")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("accept", help="freeze a proposal the user accepted").add_argument("id")
    rejecting = commands.add_parser("reject", help="freeze a proposal the user declined")
    rejecting.add_argument("id")
    rejecting.add_argument("reason", help="why, in one line")
    superseding = commands.add_parser("supersede", help="link an accepted note to the accepted note it replaces")
    superseding.add_argument("new_id")
    superseding.add_argument("old_id")
    superseding.add_argument("--part", help="the part replaced, in one line; without it the old note is archived")
    commands.add_parser("archive", help="move an accepted or rejected note to archived/").add_argument("id")
    commands.add_parser("protect", help="make frozen notes read-only and proposals writable")
    args = parser.parse_args(argv)
    sys.stdout.reconfigure(errors="backslashreplace")
    if not args.notes_dir.is_dir():
        print(f"error: {args.notes_dir} is not a directory", file=sys.stderr)
        return 2
    try:
        if args.command == "accept":
            accept(args.notes_dir, args.id)
        elif args.command == "reject":
            reject(args.notes_dir, args.id, args.reason)
        elif args.command == "supersede":
            supersede(args.notes_dir, args.new_id, args.old_id, args.part)
        elif args.command == "archive":
            archive(args.notes_dir, args.id)
        else:
            protect(args.notes_dir)
    except Refused as error:
        print(f"refused: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
