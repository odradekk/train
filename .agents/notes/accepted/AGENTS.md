# Accepted decision notes

These are decisions the user accepted. They are frozen: the files are read-only, and nobody edits, moves, renames, or deletes them by hand. Work carries them out; when work conflicts with one, report the conflict instead of changing the note, and leave a change of the decision to planning.

Only `note_lifecycle.py` of the `decision-notes` skill changes these files, on the user's word: `supersede` adds the lines that link a note to the one that replaces it, and `archive` moves a note to `archived/`. The `decision-notes` verifier rejects any other change.
