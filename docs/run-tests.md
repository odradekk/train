# Unified test execution contract

This document specifies the usage and behavior of `scripts/run-tests.ps1`. Which checks a change needs is decided by the [Testing standard](implement-sop.md#testing-standard); [Windows CI](ci.md) calls the same entry.

`scripts/run-tests.ps1` (PowerShell 7) is the single entry for discovery, selection, prerequisites, runner invocation and reporting. It is a thin dispatcher: assertions stay in their test implementations, and runners remain independently callable. Without arguments it prints help and the discovered groups; running requires an explicit selection such as `-Family`, `-Scope`, `-Test` or `-Correctness`.

MSTest cases and classification come from test metadata. Other runners are registered in `scripts/runner-catalog.json` with only the invocation information they need; the same file inventories the unconnected runners and their migration gaps. Unconnected runners keep using their own scripts and environment until an equivalent check is registered. Registered Godot and new-process checks:

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.logging-smoke
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.authority-checkpoint
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.world-editor-workspace
```

`godot.world-editor-workspace` runs the existing cross-process script with `-Workspace`: real menu/workspace navigation,
blank creation, shared leave protection through editor buttons, console commands and window-close notifications,
suspended input and cancellation, reload failure and save-as-then-reload of the original target,
return cancellation, failed save, discard and successful save-and-return protection, save-as cancellation
and success through button signals, and preservation of the original save. The save process exports its current world to a new JSON file.
A fresh process imports that file through the workspace, directly observes the refreshed list, opens the new local UUID, and verifies
the exported world's simulation position, pending input and world
content, then continues editing, running and saving. Separate fresh processes continue the original and save-as copy; a final
process reopens the original, save-as copy and imported save to prove that their edits stayed isolated. Only the final process reports isolation; the save process
verifies its current save identity, clean state and voxel count after save-and-return, and its next workspace visit verifies the return.
Two further real Godot processes use a separate user-data directory. The first creates a paused unsaved copy from the distributed
basic-editing example through the workspace, continues formal editing and running while retaining a future pending edit, and gives
the first save a UUID distinct from the example document. The next process restores that saved copy, then creates the example again
and verifies its documented step-1 boundary, pending edit and Godot-read source bytes are unchanged and exclude copy-only edits.
Two further real Godot processes use another isolated user-data directory for the voxel tools. The first selects definition,
orientation and horizontal-plane height through real Godot input in a blank world, places a cell, verifies the shared precise
preview and rejected draft, and exercises tool/target/level/leave guards. In the same process it drives a brush press, several
held-button motions and release, verifies that only release creates one edit and undo record, interrupts later strokes through
focus loss and an open console, and handles the retained drafts explicitly. It then creates a box from two mouse corners, changes
the same preview through inspector input, commits it, and schedules a region as an explicit cell-list input. Saving retains an
uncommitted draft but excludes it from the saved world. The second process opens that saved world, verifies the committed brush,
box and pending region while no UI draft is restored, executes the pending cells in formal order, and continues mouse editing and saving.
Two final processes use a fourth isolated directory to exercise shared undo/redo, confirmation and the rule that history is not
restored in a new process. The workspace check therefore runs eleven processes in total. Its assertions do not replace manual
window input or visual acceptance.
Graphical workspace, window close, quit and dialog acceptance remain separate manual checks. Manual voxel-tool acceptance also interrupts a brush stroke, uses precise coordinates to translate the full retained stroke, changes its definition/orientation/operation without changing its geometry, exercises draft undo/redo, and verifies that choosing to redraw first asks to discard the old draft; box acceptance changes both corners and height in the inspector. Injected window-close notifications prove the handler; they do not prove a real window close-button interaction.

`--quit-after` in those scripts is a Godot frame budget, not the entry timeout and not a pass condition. `-Correctness` selects these Godot checks together with MSTest, so it needs `GODOT` and a `Train.csproj` build plus import. Each Godot check gets its own `APPDATA` directory under the run, shared by the processes it starts; the import and the version probe get one each. The entry does not write `override.cfg`, so killing it leaves no user-data redirect; an existing `override.cfg` is left in place, and the summary records whether it exists and its SHA256.

## Classification and selection

Classify tests by family (unit, headless automation, Benchmark) and stable functional scope such as voxels, buildings, railway, persistence or content, and record actual requirements such as Godot, separate processes, export or a graphical session. Speed follows measurements, not a blanket assumption that C# tests are unit tests. Use stable case/group names rather than permanent entry points for individual Issues.

Show the selected cases and prerequisites before execution; combine family/scope filters and deduplicate selected cases. Invalid arguments, unknown names and an explicit selection matching no cases are errors. An unavailable required environment is reported as unverified with a non-success result, never by silently shrinking the selection. Full regression includes the registered correctness checks; unimplemented Benchmark, long-run, manual or platform groups are pending requirements, not empty groups that pass. Publish the actual selected coverage so a partial migration or small CI selection cannot be presented as all repository checks, and list manual observation separately from automated passes.

## Execution and isolation

Each run gets a fresh result directory with isolated saves, mutable content and user data. Child processes get controlled test settings without leaking task-specific settings into other runs. Only necessary builds/imports are prepared and are reused within the invocation. Checks that need Godot, builds or shared resources run serially; there is no cross-run caching or automatic parallel scheduling.

Process checks use measured, bounded timeouts. Cancellation or timeout preserves available evidence and terminates only this run's owned process tree; if cleanup cannot establish isolation, dispatch stops and the blocker is reported. Other worktrees' processes and ordinary game saves are left untouched.

After an ordinary assertion failure, other independent, safe groups continue and all results are kept. A failed build/import blocks its dependent groups, cancellation stops dispatch, and serious state corruption stops that case at the first evidence. The exit is non-success whenever a selected required check fails, is blocked or does not finish. The report distinguishes failed, blocked, cancelled and not-executed outcomes; later passes never overwrite earlier failures.

## Results and evidence

Each run produces one machine-readable summary and one concise Markdown report beside the original evidence, in a unique directory under one Git-ignored hierarchy that never overwrites earlier reports. Both record the selection, actual execution, per-check outcome, preparation/test durations, tested commit and working-tree dirty state, relevant build/content/environment identities and artifact locations. Native TRX, Godot logs and checkpoints are preserved rather than copied into a second system. Results from a dirty tree do not establish that the recorded clean commit passed. Exact summary fields, nonzero exit codes and helper-file splits are implementation choices within these contracts; keep them minimal and documented.
