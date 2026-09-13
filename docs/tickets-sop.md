# Issue Task Standard Operating Procedure

Use this procedure for dispatch, implementation delivery, acceptance, revision, closure, and the subsequent version update of a GitHub Issue in `odradekk/train`. GitHub operations follow [issue-tracker.md](agents/issue-tracker.md).

The Issue comments are the durable record of implementation and acceptance. The PR contains the code to review. A terminal reply or worktree card is a notification, not a substitute for either.

## Roles and authority

- **Coordinator / reviewer**: prepares and dispatches the task, reviews the delivered PR, records findings, verifies Acceptance criteria, and requests the user's final approval.
- **Implementation agent**: works in its assigned independent worktree, validates and commits its changes, pushes its task branch, opens or updates the PR, and reports in the Issue comments.
- **User**: gives final approval for the specific reviewed PR revision and proposed version change. Only then may the coordinator merge the PR, reclaim the worktree, close the Issue, and update the version.

A dispatch under this SOP authorizes the implementation agent's task-branch push, PR creation/update, and Issue completion/revision comments. It does not authorize merging, closing the Issue, force-pushing, or changing unrelated shared state. Acceptance by the reviewer is not the user's final approval.

## 0. Prepare an independent worktree — coordinator

Before dispatch, verify:

- The Issue is open, labelled `ready-for-agent`, not superseded, and has no open native blockers.
- The specification and Acceptance criteria are executable without unresolved design choices.
- The selected baseline contains the required code and docs and is available to the eventual PR base; local-only baseline commits must not leave the PR missing prerequisites.
- The target repository, PR base branch, task branch, and authenticated push/PR access are known. If configuration or permissions are missing, resolve them before promising a PR; report any required user action.
- The agent command is available, setup succeeds, and required validation tools are located. Consult the checkout's `AGENTS.md` and [Local validation and Windows builds](#local-validation-and-windows-builds); check the SDK, script runtime, and target-platform requirements before dispatch.

Use an independent top-level worktree from the repository's default base unless the user selects another base or stacked work. Preserve uncommitted main-workspace changes; do not implicitly carry them into the task.

For Orca-managed creation, setup, terminals, recovery, status, or cleanup, the coordinator uses the `orca-cli` skill and its version-matched guide. Keep this tooling context out of the implementation brief: the worker needs its absolute path, branch, baseline, and independent-worktree ownership, not Orca instructions.

**Complete when:** one valid Issue has one independent worktree, a self-contained baseline, successful setup, and one ready agent.

## 1. Dispatch the implementation brief — coordinator

The brief must begin exactly with `/implement `: one space after the command, with no preceding text. Use the template below and fill every placeholder.

Include the Issue URL, worktree path, task branch, baseline SHA, PR base branch, scope boundaries, repository instructions, required validation, PR delivery, and Issue-comment requirements. Provide verified local tool paths when needed.

Send the brief once to the verified agent terminal. Confirm receipt and that work has started; a transport acknowledgement alone is not enough. Retain the worktree/terminal identity for later revision requests. The coordinator, not the worker, maintains any Orca card metadata.

After receipt, stop monitoring by default and report the handoff. Resume acceptance when the user requests it or a previously authorized monitoring workflow delivers completion. This SOP defines the lifecycle; it does not itself start continuous polling.

**Complete when:** the exact brief was delivered once, the assigned agent is working, and the user knows the target Issue/worktree.

## 2. Implement and deliver a PR — implementation agent

1. Read the Issue body and comments, `AGENTS.md`, and relevant domain docs/ADRs.
2. Implement only the assigned scope, add observable regression coverage, run required tests/build/integration checks, and review the diff.
3. Commit intended changes with Conventional Commit messages. Push the task branch to the agreed repository and open a PR against the agreed base. For a revision, add commits to the same branch and update the same PR.
4. Reference the Issue with a normal link or `Refs #<issue>`. Avoid closing keywords in PR descriptions and commits so the coordinator can close the Issue after worktree cleanup.
5. Add an Issue comment with the completion brief below. The PR head SHA must identify the exact code tested.

If implementation or validation is blocked, post a blocker comment with evidence, missing prerequisites, and the next required action. If PR creation fails, report that delivery is incomplete; a local commit alone is not a delivered task.

Keep the worktree for review and revision. The agent leaves Acceptance-criteria checkboxes and final acceptance to the reviewer.

**Complete when:** the PR is accessible, its remote head matches the tested commit, and the Issue has a completion brief linking that PR. Required checks that failed or were not run remain explicit blockers, not passes.

### Implementation brief template

```text
/implement GitHub Issue #<issue>: <title> — <issue-url>

You own the independent worktree <absolute-path> and task branch <branch>.
Baseline: <sha>. Target repository: odradekk/train. PR base: <base-branch>.
Work only in this worktree; preserve unrelated changes and keep archive/mvp read-only.

Read the full Issue body/comments, AGENTS.md, and relevant domain docs/ADRs.
Implement only #<issue>; exclude <adjacent-scope>.
Required validation: <tests, build, real integration checks, verified tool paths>.
Review the diff and commit intended changes with Conventional Commit messages.

You are authorized to push this task branch, create/update its PR against the
specified base, and post completion/revision comments on this Issue.
After validation, submit the PR and post an Issue comment with the PR URL,
tested head SHA, changes, Acceptance-criteria coverage, exact validation
commands/results, reproduction steps, and any remaining risks or blockers.
Use a normal Issue reference, not an automatic closing keyword.
If blocked, report the evidence and required action in an Issue comment.

For review fixes, keep the same worktree, branch, and PR; add commits and reply
to each finding with its resolution and validation.
Leave checkboxes/final acceptance to the reviewer. Leave merge, worktree
cleanup, and Issue closure to the coordinator after explicit user approval.
```

### Completion brief — Issue comment

- PR URL, base branch, and tested head SHA.
- What changed and which Acceptance criteria it covers.
- Exact validation commands, host OS and tool versions where relevant, results, and anything not run. For Windows builds, include the export path and report export and Windows runtime validation separately.
- Reproduction steps or example content needed to verify the behavior.
- Remaining risks/blockers, or an explicit statement that none are known.

## 3. Review and return findings — coordinator / reviewer

Read the Issue completion brief and all subsequent discussion, then inspect the PR's actual base/head and full relevant diff. Use `code-review` for Standards and Spec review; verify agent claims against code and independently run risk-appropriate checks. Reuse prior evidence only when it still applies to the reviewed revision.

If review fails:

1. Reply in an Issue comment identifying the PR/head SHA and each finding: severity, violated requirement, file/line or reproduction, observed versus expected result, and requested correction.
2. Notify the same implementation agent with a `/implement ` revision brief linking that comment. The terminal notification supplements, rather than replaces, the Issue record.
3. The agent fixes the same PR, reruns affected checks, and posts a new Issue comment responding to each finding with commit IDs and validation results.
4. Re-review the new head and affected behavior. Keep the Issue open and retain the worktree throughout this loop.

**Complete when:** no unresolved review findings or required validation gaps remain for the identified PR head.

## 4. Verify every Acceptance criterion — coordinator / reviewer

After the code review passes:

1. Read the current Issue body again. Map every Acceptance criterion to inspected implementation and applicable test/integration evidence.
2. Mark only satisfied criteria `[x]`; leave incomplete or unverified criteria `[ ]`. If a previously checked criterion no longer holds, clear it and explain why in a comment. Preserve the wording and unrelated Issue content; do not weaken requirements to obtain a pass.
3. If any criterion remains unmet, publish the gap and return to step 3. Scope changes require the user's decision.
4. Read back the saved Issue body to verify the actual checkbox state. Reconcile any concurrent edits before writing; do not overwrite newer discussion or requirements.
5. When every criterion passes, post the final acceptance conclusion in an Issue comment: PR URL/head SHA, per-criterion evidence, validation results, resolved review findings, and remaining nonblocking risks. State explicitly: **technical acceptance passed; awaiting user approval to merge**.

Present the PR, final acceptance comment, and proposed version change under step 6 to the user, then stop. Keep the PR unmerged, the worktree available, and the Issue open.

**Complete when:** every criterion is verified and checked, the saved state is confirmed, and the acceptance comment is bound to the reviewed head.

## 5. Final approval, merge, cleanup, close — coordinator

Proceed only after the user has reviewed the acceptance result and explicitly approved the specific PR for merge and closure.

1. Recheck that the PR head still matches the accepted revision, required checks pass, no unresolved review discussion remains, and the PR can merge. New commits require affected revalidation and renewed user approval. Preserve main-workspace changes.
2. Merge the PR using the repository's configured policy or the user's selected method. Verify the remote merged state and merge commit; a local branch merge is not a substitute.
3. Verify that the exact task worktree contains no unpreserved changes or unique commits. Reclaim it through `orca-cli` when managed by Orca, closing its terminals and removing the task branch only when safe. Account for squash/rebase merges when proving that work is retained.
4. Add an Issue closure comment linking the merged PR, merge commit, and final acceptance comment, and report worktree cleanup. Close the Issue as completed.
5. Read back PR/Issue state and confirm worktree removal, then continue to step 6.

If merge fails, retain the worktree and Issue. If cleanup fails after merge, report that state and keep the Issue open until cleanup is resolved; do not rerun the merge or discard unpreserved work.

**Complete when:** the approved PR is remotely merged, the worktree is reclaimed without losing work, and the Issue is confirmed closed with its acceptance record intact.

## 6. Update the version — coordinator

After the PR is merged, its worktree reclaimed, and the Issue closed, update the version on the merged base branch. This is the final step of the task, not part of the implementation agent's PR.

During development, use `0.MINOR.PATCH`:

| Change | Version rule | Example |
| --- | --- | --- |
| Other completed changes (default) | Increment PATCH by 1 | `0.2.3 → 0.2.4` |
| Completed major system or staged milestone, confirmed by the user | Increment MINOR by 1 and reset PATCH to 0 | `0.2.3 → 0.3.0` |
| First formal release, explicitly confirmed by the user | Set the version to `1.0.0` | `0.x.y → 1.0.0` |

These rules describe this project's development milestones, not automatic classification by Conventional Commit prefix. Propose the default PATCH increase unless the accepted scope establishes a major system or milestone. A large diff or a breaking change alone does not authorize `1.0.0`; post-1.0 release policy requires a separate decision.

1. Read the latest version on the merged base branch and calculate the approved increase. If another task has advanced it since approval, recompute from the current value using the approved category; never overwrite a newer version.
2. Keep the existing version declarations consistent: `project.godot` `config/version`, `export_presets.cfg` `application/product_version` and `application/file_version`, and the current-version line in `AGENTS.md`. Map `X.Y.Z` to Windows file version `X.Y.Z.0`. Leave engine, schema, language, dependency, and archived project versions unchanged.
3. Check the diff and version consistency, run the relevant existing build/configuration checks, then record the version-only change as `chore: bump version to X.Y.Z`. This commit/push is part of the user-approved closeout, subject to base-branch protection; if a follow-up PR is required, request its approval instead of bypassing protection.
4. Add a comment to the closed Issue recording the old/new version, classification, version commit, and validation result. Report completion only after the version change is present on the target remote branch.

Apply one increase per completed task, not per implementation commit, repair round, or review attempt. The version-only closeout commit does not trigger another increase.

**Complete when:** the approved version is consistently recorded and published, and the closed Issue links its version commit. If this step is blocked, keep the already merged PR and closed Issue as they are, record the pending version update, and resume only this step when unblocked.

## Local validation and Windows builds

Run commands from the checkout under test. The development host is Fedora; the Windows export target remains `Windows Desktop` in `export_presets.cfg`.

### Fedora validation

Check the local tools before selecting validation commands:

```bash
godot-dotnet --headless --version
dotnet --list-sdks
dotnet --version
pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
```

`dotnet --version` must resolve the SDK required by `global.json`; having another SDK installed is insufficient when roll-forward is disabled. Both projects target `net10.0`. The `.ps1` scripts use PowerShell 7 (`pwsh`) on Fedora and Windows; Windows PowerShell 5 (`powershell.exe`) is not the validation host. Report missing prerequisites as validation blockers.

For changes that require domain tests, a project build, and real Godot voxel smoke, use the applicable commands below after resolving those prerequisites. Select the slice required by the Issue:

```bash
export GODOT="$(command -v godot-dotnet)"
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 1
```

Smoke scripts can discover `godot-dotnet` on PATH. Set `GODOT` explicitly when running observation/baseline scripts or selecting a particular engine. In an existing PowerShell 7 session, the equivalent setting is `$env:GODOT = (Get-Command godot-dotnet).Source`.

The four `scripts/run-godot-*.tests.ps1` harnesses reuse the current PowerShell 7 host and select fake native programs for the host OS. They verify the scripts' success/failure handling; they do not run the real game. Windows GUI-process capture cases run only on Windows and are explicitly skipped on Fedora. Distinguish these harness results from real Godot checks.

Visual observation requires a graphical session. For fresh evidence, select new report and capture paths so historical validation records remain intact. For example:

```bash
pwsh -NoProfile -File scripts/run-godot-voxel-observe.ps1 -Slice 3 -OutputPath .godot/validation/observe.md
pwsh -NoProfile -File scripts/run-godot-voxel-observe-resume.ps1 -Slice 3 -OutputPath .godot/validation/resume.md -CaptureOutputPath .godot/validation/resume
pwsh -NoProfile -File scripts/run-godot-voxel-baseline.ps1 -OutputPath .godot/validation/baseline.md
```

### Export to the mounted Windows filesystem

The local mounts are `/mnt/windows-c` and `/mnt/windows-d`. Verify the selected mount is available and writable, then choose a dedicated output directory. For example, from Bash:

```bash
windows_build_dir='/mnt/windows-d/train-builds/local'
mkdir -p "$windows_build_dir"
pwsh -NoProfile -File scripts/run-godot-content-export-smoke.ps1 -ExportOnly -OutputPath "$windows_build_dir/train.exe"
```

Export requires the project's .NET SDK and matching Godot .NET Windows export templates. Passing the output path on the command line keeps machine-specific mount paths out of `export_presets.cfg`. Keep the complete export directory, including the PCK and .NET output, together when running or copying the build.

The content/mods export smoke scripts' `-OutputPath` selects a fresh export destination; `-ExportOnly` stops after verifying that export completed and the executable exists. For runtime checks against an existing native build for the current host, set `TRAIN_CONTENT_EXPORT_BIN` or `TRAIN_MODS_EXPORT_BIN` to its executable path and invoke the corresponding script without `-ExportOnly`. Windows `.exe` builds require Windows. `-ExportOnly` cannot validate a pre-existing binary supplied through these environment variables; use `-OutputPath` for a new export instead.

A mounted Windows filesystem provides file access, not a Windows execution environment. Verify Windows-specific behavior in Windows and record the build's tested commit, runtime results, and logs. If Windows execution is unavailable, report that part as unverified even when export succeeds.

Historical records in `docs/validation/`, `docs/research/`, and `archive/mvp/` retain the OS, paths, commands, and results used at the time. New validation records must describe the environment actually used.

## Recovery

After interruption, read the latest Issue comments, PR head/checks, worktree state, and any recorded version commit before acting. Resume the first incomplete step instead of recreating worktrees, resending accepted briefs, opening duplicate PRs, repeating completed merges, or incrementing a version twice.

A newly discovered blocker, scope change, or changed PR head returns the task to the relevant earlier step. Never treat a stale acceptance comment as approval for unreviewed code.
