# Issue Task Standard Operating Procedure

Use this procedure for dispatch, implementation delivery, acceptance, revision, closure, and the subsequent version update of a GitHub Issue in `odradekk/train`. GitHub operations follow [issue-tracker.md](agents/issue-tracker.md).

Dispatch implementation work through **Paseo**. The coordinator uses the `paseo` skill and the available Paseo MCP tools; the Paseo CLI is an alternative interface to the same workflow. If Paseo is unavailable, report the dispatch blocker instead of silently switching to another agent launcher or worktree manager.

For Godot scene, resource, editor, or runtime work, use the project's **Godot MCP** for supported interactive operations under [Godot MCP validation](#godot-mcp-validation). Use the maintained `odradekk/godot-mcp` fork; configuration and version updates follow [godot-mcp.md](godot-mcp.md).

The Issue comments are the durable record of dispatch, implementation, and acceptance. The PR contains the code to review. A Paseo message, completion notification, or workspace title is not a substitute for either.

## Roles and authority

- **Coordinator / reviewer**: prepares and dispatches the task, reviews the delivered PR, records findings, verifies Acceptance criteria, and requests the user's final approval.
- **Implementation agent**: works in its assigned independent worktree, validates and commits its changes, pushes its task branch, opens or updates the PR, and reports in the Issue comments.
- **User**: gives final approval for the specific reviewed PR revision and proposed version change. Only then may the coordinator merge the PR, reclaim the worktree, close the Issue, and update the version.

A dispatch under this SOP authorizes the implementation agent's task-branch push, PR creation/update, and Issue completion/revision comments. It also authorizes reporting Godot MCP bugs and required features in `odradekk/godot-mcp` as described under [Godot MCP validation](#godot-mcp-validation), without a separate confirmation. It does not authorize merging, closing the Issue, force-pushing, or changing unrelated shared state. Acceptance by the reviewer is not the user's final approval.

## 0. Prepare an independent worktree — coordinator

Before dispatch, verify:

- The Issue is open, labelled `ready-for-agent`, not superseded, and has no open native blockers.
- The specification and Acceptance criteria are executable without unresolved design choices.
- The selected baseline contains the required code and docs and is available to the eventual PR base; local-only baseline commits must not leave the PR missing prerequisites.
- The target repository, PR base branch, task branch, and authenticated push/PR access are known. If configuration or permissions are missing, resolve them before promising a PR; report any required user action.
- The requested Paseo provider is available and authenticated, setup succeeds, and required validation tools are located. Consult the checkout's `AGENTS.md` and [Local validation and Windows builds](#local-validation-and-windows-builds); check the SDK, script runtime, and target-platform requirements before dispatch.

Use an independent top-level worktree from the repository's default base unless the user selects another base or stacked work. Preserve uncommitted main-workspace changes; do not implicitly carry them into the task.

Prepare it through Paseo:

1. Inspect existing workspaces and agents before creating anything, so a resumed dispatch does not create a second worker for the same Issue.
2. Read `list_profiles`, including profile notes. Honor the user's provider and model choices and select an applicable profile. If no profile fits, use `list_providers`, `list_models`, and `inspect_provider` to choose supported settings and report the selection. This SOP does not prescribe a particular model. Record the actual provider, model, mode, and thinking settings used.
3. Use `create_workspace` with `isolation: "worktree"`, `mode: "branch-off"`, the verified project/source checkout, a task branch name, and an explicit `baseBranch` resolving to the repository's default base, normally `origin/<default-branch>`. Copy the returned `workspaceId` and absolute `cwd`; verify the actual branch and baseline SHA in that checkout.
4. Inspect configured setup scripts with `list_workspace_scripts` and run applicable ones with `start_workspace_script`. Run any remaining repository setup and validation checks in the returned checkout. Wait for setup to succeed before sending implementation work; workspace creation alone does not prove setup passed.
5. For Godot work, verify that the selected baseline includes the selected client's project MCP configuration and that its pinned fork revision and `GODOT_PATH` match [godot-mcp.md](godot-mcp.md). Check Node.js, npm, Git, and the Godot executable in the worktree environment. Include the worker's MCP connection check and any known setup gap in the brief; the coordinator's own connection does not prove that a newly launched worker can access it.

**Complete when:** one valid Issue has one Paseo workspace owning an independent worktree, a self-contained baseline, successful setup, and a verified agent launch configuration.

## 1. Dispatch the implementation brief — coordinator

The brief must begin exactly with `/implement `: one space after the command, with no preceding text. Use the template below and fill every placeholder.

Include the Issue URL, worktree path, task branch, baseline SHA, PR base branch, scope boundaries, repository instructions, required validation, PR delivery, and Issue-comment requirements. Provide verified local tool paths when needed.

For a new worker, call Paseo `create_agent` with the prepared `workspaceId`, a descriptive title, the selected `provider/model`, applicable `settings`, and the complete brief as `initialPrompt`. Set `notifyOnFinish: true`. Supplying the brief here starts the task; do not send it again with `send_agent_prompt`.

For an existing worker, first verify its `agentId`, workspace, and current task. Send a new or revision brief once with `send_agent_prompt`, using `background: true` and `notifyOnFinish: true`. Do not inject implementation briefs through a shell terminal when a Paseo agent session is available.

Confirm that the agent received the intended brief and actually started task work, using its acknowledgement and task-specific activity from `get_agent_activity`. A successful create/send response or `running` status alone is insufficient. Keep this check bounded; if startup fails or remains unconfirmed, report that state and inspect the existing session rather than sending a duplicate brief.

For Godot work, the worker checks MCP access in its own session before the first Godot operation. Record the result, or the explicit CLI fallback and reason, with the startup evidence under [Godot MCP validation](#godot-mcp-validation).

Record the Issue, `workspaceId`, `agentId`, absolute worktree path, task branch, baseline SHA, PR base, launch settings, and startup evidence in a dispatch comment on the Issue. Report the handoff to the user. These identifiers are needed for revisions and recovery; a terminal ID is supplementary when a setup terminal is involved.

After startup is confirmed, stop monitoring by default. Use Paseo completion/error/permission notifications instead of continuous status polling. A notification does not constitute technical acceptance or user approval; resume acceptance when the user requests it or an already authorized review workflow applies.

**Complete when:** the exact brief was delivered once through Paseo, the assigned agent has started task work, and the Issue and user have the dispatch record.

## 2. Implement and deliver a PR — implementation agent

1. Read the Issue body and comments, `AGENTS.md`, and relevant domain docs/ADRs.
2. Implement only the assigned scope, add observable regression coverage, run required tests/build/integration checks, and review the diff. For Godot changes, follow [Godot MCP validation](#godot-mcp-validation) and include its applicable evidence in the completion brief.
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
Godot MCP: <applicable work and pinned fork revision, or not applicable>.
For Godot work, read docs/godot-mcp.md and follow the Godot MCP validation
section of docs/tickets-sop.md. Verify access in your own session and use your
assigned worktree's absolute projectPath. Record any CLI fallback and reason.
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
- For Godot MCP validation: fork commit, actual project/worktree path, tool calls and relevant arguments, observed results, retained log/artifact paths, and any fallback or unverified coverage. Tie the evidence to the tested game head SHA; list manual visual checks separately.
- Reproduction steps or example content needed to verify the behavior.
- Remaining risks/blockers, or an explicit statement that none are known.

## 3. Review and return findings — coordinator / reviewer

Read the Issue completion brief and all subsequent discussion, then inspect the PR's actual base/head and full relevant diff. Use `code-review` for Standards and Spec review; verify agent claims against code and independently run risk-appropriate checks. Reuse prior evidence only when it still applies to the reviewed revision.

For MCP-assisted changes, inspect the saved scene/resource diff and runtime evidence, not just tool success messages. Apply the same required test, real Godot, cross-process, export, and user visual-acceptance gates as the Issue specifies; an MCP launch or log capture only proves the behavior it actually exercised.

If review fails:

1. Reply in an Issue comment identifying the PR/head SHA and each finding: severity, violated requirement, file/line or reproduction, observed versus expected result, and requested correction.
2. Send the same Paseo implementation agent a `/implement ` revision brief linking that comment through `send_agent_prompt`, with `background: true` and `notifyOnFinish: true`. Retain the same workspace, branch, and PR. The agent message supplements, rather than replaces, the Issue record; if the session is unavailable, follow Recovery before creating a replacement.
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
3. Verify that the exact task worktree contains no unpreserved changes or unique commits. Account for squash/rebase merges when proving that work is retained. Archive the task's Paseo workspace with `archive_workspace` only after confirming it owns no unrelated active work; this archives its agents and terminals and removes a Paseo-owned worktree when its final active workspace reference is gone. Verify the actual workspace and Git worktree state, and remove the task branch only when safe. Do not equate archiving an agent with reclaiming its workspace, or assume a successful archive removed a worktree still referenced by another workspace.
4. Add an Issue closure comment linking the merged PR, merge commit, and final acceptance comment, and report worktree cleanup. Close the Issue as completed.
5. Read back PR/Issue state and confirm worktree removal, then continue to step 6.

If merge fails, retain the worktree and Issue. If cleanup fails after merge, report that state and keep the Issue open until cleanup is resolved; do not rerun the merge or discard unpreserved work.

Before archiving a worktree, retain required MCP logs and generated validation artifacts outside it, including evidence under ignored `.godot/validation/`, and record the retained location. Close only the task-owned Godot processes and editor windows; preserve unrelated sessions.

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

Run commands from the checkout under test. The current development host is Windows; the export target is `Windows Desktop` in `export_presets.cfg`.

### Godot MCP validation

Apply this sequence to Godot scene/resource edits, editor checks, and runtime diagnosis. Documentation-only or pure domain tasks do not need an MCP run unless their Acceptance criteria require Godot behavior.

1. **Check the worker's connection.** Follow [godot-mcp.md](godot-mcp.md) for the client's project configuration, pinned fork, prerequisites, trust/approval, and known limitations. In the actual agent session, call `get_godot_version`, then `get_project_info` with the assigned worktree's absolute `projectPath`; verify the returned engine and path. Record the configured Git commit rather than relying on the server's reported package version. Restart the MCP session after changing its configuration. Keep registration project-scoped.
2. **Prepare the project.** Build C# code and import resources through the existing CLI workflow before runtime checks. Import newly added assets/scripts before UID operations. MCP does not build the .NET project or replace the repository's required test and smoke scripts.
3. **Use the relevant tools.** Use project/UID queries for inspection; use scene creation, node/property edits, texture loading, scene saving, or mesh export only within the task's authorized scope. Inspect the resulting files and reload affected resources to verify the requested values. Use an isolated disposable project for testing the MCP itself. For runtime checks, call `run_project`, then `get_debug_output`; confirm the expected application-ready/behavior markers, errors, and running or exit status. A start acknowledgement alone is not a pass. Use `launch_editor` when editor interaction is needed and record what was actually observed.
4. **Preserve evidence and stop owned processes.** Save the relevant tool inputs, output/error logs, exit status, and artifact checks in the task's validation directory before starting another run or ending the session. A server instance tracks one current run; starting another can replace it. Keep concurrent tasks on separate instances, and do not start/stop through an instance controlling another task. Use `stop_project` for a task-owned game and verify termination. Editor windows opened by `launch_editor` require separate closure; identify them by the task project before closing. MCP output is not evidence of visual quality or user acceptance.
5. **Handle unavailable or insufficient tools.** Report connection, approval, platform, or tool failures and use the existing Godot CLI/scripts for checks they can equivalently perform. Record the fallback and actual results; keep uncovered Acceptance criteria unverified and report the remaining blocker. Do not replace the configured fork with another package or add a global registration to work around a failure. Reuse valid evidence and rerun only affected checks unless new failures justify broader validation.
6. **Report MCP bugs and required features.** When use reveals a Godot MCP defect or a missing capability needed for the task, file an Issue directly in [odradekk/godot-mcp](https://github.com/odradekk/godot-mcp/issues). Search existing Issues first; add new evidence to a matching Issue instead of duplicating it. Bug reports include the fork commit, client/OS/Godot versions, minimal tool calls and inputs, expected versus actual behavior, and relevant redacted logs. Feature requests explain the concrete task, current limitation or workaround, and desired behavior. Link the MCP Issue in the current Train task's comments and completion brief, stating its impact and any fallback. If posting fails, retain the report and state the publishing blocker; do not claim it was filed. Filing an Issue does not itself authorize implementing changes in the MCP repository.

**Complete when:** the applicable behavior has verified artifact/runtime evidence bound to the worktree and game head, required repository checks are accounted for, logs are retained, and task-owned processes are closed. Any remaining manual or unsupported check is explicitly pending.

### Windows development and validation

Use PowerShell 7 (`pwsh`), not Windows PowerShell 5 (`powershell.exe`). Both projects target `net10.0`; install the exact x64 SDK selected by `global.json`. Because roll-forward is disabled, another .NET 10 SDK, including a preview, does not satisfy the requirement. Existing SDKs can remain installed alongside it. Obtain the versioned installer from Microsoft's [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

Configure Godot for the current PowerShell session and persist the setting for new processes:

```powershell
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
$env:GODOT_EDITOR = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe'
[Environment]::SetEnvironmentVariable('GODOT', $env:GODOT, 'User')
[Environment]::SetEnvironmentVariable('GODOT_EDITOR', $env:GODOT_EDITOR, 'User')
& $env:GODOT --headless --version
dotnet --list-sdks
dotnet --version
$PSVersionTable.PSVersion.ToString()
```

`GODOT` must contain an executable path without arguments. Prefer the console executable for command-line checks. Restart existing terminals and editors after installing the SDK or changing user environment variables. Report missing prerequisites as validation blockers.

The checkout may retain ignored settings from a historical acceptance environment. Before returning it to normal development, inspect `NuGet.Config` for an obsolete offline-only source and `override.cfg` for a custom acceptance user-data directory. Back up those local files outside the checkout before disabling them; preserve the associated acceptance evidence and saves. Normal dependency restore needs nuget.org access, and normal game data uses `%APPDATA%\Godot\app_userdata\Train`.

For a fresh checkout or after an SDK change, restore/build, import Godot resources, and run the relevant smoke slice:

```powershell
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
& $env:GODOT --headless --path . --editor --import
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4
```

Select the slice required by the Issue (1–4). Resource import alone does not build the C# project. To open the editor after a successful build/import, run `& $env:GODOT --path . --editor`.

VS Code uses the repository's `build`, `test`, and `export-windows` tasks. With the C# extension installed, the `Train (Godot)` launch configuration builds the solution and starts the game. `GODOT_EDITOR` points to the main Godot executable for debugging; `GODOT` points to its console wrapper for validation output. This follows the [Godot C# editor setup](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_basics.html#visual-studio-code).

The four `scripts/run-godot-*.tests.ps1` harnesses reuse the current PowerShell 7 host and select fake native programs for the host OS. They verify the scripts' success/failure handling; they do not run the real game. Windows GUI-process capture cases run only on Windows and are explicitly skipped on Fedora. Distinguish these harness results from real Godot checks.

Visual observation requires a graphical session. For fresh evidence, select new report and capture paths so historical validation records remain intact. For example:

```powershell
pwsh -NoProfile -File scripts/run-godot-voxel-observe.ps1 -Slice 3 -OutputPath .godot/validation/observe.md
pwsh -NoProfile -File scripts/run-godot-voxel-observe-resume.ps1 -Slice 3 -OutputPath .godot/validation/resume.md -CaptureOutputPath .godot/validation/resume
pwsh -NoProfile -File scripts/run-godot-voxel-baseline.ps1 -OutputPath .godot/validation/baseline.md
```

### Windows export and runtime validation

Export requires the project's .NET SDK and matching Godot .NET Windows export templates. On this machine, the templates belong under `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`. Use a dedicated output directory; on Windows the content export smoke can validate both export and the resulting executable:

```powershell
$windowsBuild = Join-Path (Get-Location).Path 'builds/windows-content-smoke/train.exe'
pwsh -NoProfile -File scripts/run-godot-content-export-smoke.ps1 -OutputPath $windowsBuild
$env:TRAIN_MODS_EXPORT_BIN = $windowsBuild
pwsh -NoProfile -File scripts/run-godot-mods-export-smoke.ps1
Remove-Item Env:TRAIN_MODS_EXPORT_BIN
```

Use an absolute executable path for Windows runtime checks: the process launcher may fail to resolve a relative path containing forward slashes even after export succeeds. Passing the output path on the command line keeps machine-specific paths out of `export_presets.cfg`. Keep the complete export directory, including the PCK and .NET output, together when running or copying the build.

The content/mods export smoke scripts' `-OutputPath` selects a fresh export destination; `-ExportOnly` stops after verifying that export completed and the executable exists. For runtime checks against an existing native build for the current host, set `TRAIN_CONTENT_EXPORT_BIN` or `TRAIN_MODS_EXPORT_BIN` to its executable path and invoke the corresponding script without `-ExportOnly`. Windows `.exe` builds require Windows. `-ExportOnly` cannot validate a pre-existing binary supplied through these environment variables; use `-OutputPath` for a new export instead.

Verify Windows-specific behavior in Windows and record the build's tested commit, runtime results, and logs. If Windows execution is unavailable, report that part as unverified even when export succeeds.

### Fedora validation and cross-export

When using a Fedora checkout, install the SDK required by `global.json`, PowerShell 7, and Godot .NET. Check `dotnet --version`, `pwsh --version`, and `godot-dotnet --headless --version`. Set the executable path before running the same validation scripts:

```bash
export GODOT="$(command -v godot-dotnet)"
export GODOT_EDITOR="$GODOT"
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
"$GODOT" --headless --path . --editor --import
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4
```

For Windows cross-export, verify a writable Windows mount and choose a dedicated output directory. For example, if `/mnt/windows-d` is mounted:

```bash
windows_build_dir='/mnt/windows-d/train-builds/local'
mkdir -p "$windows_build_dir"
pwsh -NoProfile -File scripts/run-godot-content-export-smoke.ps1 -ExportOnly -OutputPath "$windows_build_dir/train.exe"
```

A mounted Windows filesystem provides file access, not a Windows execution environment. `-ExportOnly` verifies export; runtime validation still requires Windows.

Historical records in `docs/validation/`, `docs/research/`, and `archive/mvp/` retain the OS, paths, commands, and results used at the time. New validation records must describe the environment actually used.

## Recovery

After interruption, read the latest Issue comments, PR head/checks, worktree state, and any recorded version commit before acting. Resolve the recorded Paseo `workspaceId` and `agentId` against `list_workspaces`, `list_agents`, and the relevant agent status/activity. Verify the actual checkout and branch; neither a workspace title nor an old `running` status proves that the intended worker is still active.

Resume the first incomplete step instead of recreating worktrees, resending accepted briefs, opening duplicate PRs, repeating completed merges, or incrementing a version twice. For an ambiguous create/send failure, inspect existing Paseo sessions and activity before retrying. If the worker must be replaced, first confirm the old worker has stopped, preserve its changes, reuse the task workspace and branch where possible, and record the replacement agent ID and handoff in the Issue. Preserve the previous launch settings unless the user requests a change or they are no longer available; report any necessary change.

A newly discovered blocker, scope change, or changed PR head returns the task to the relevant earlier step. Never treat a stale acceptance comment as approval for unreviewed code.
