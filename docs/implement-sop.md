# Implementation Standard Operating Procedure

Use this procedure to execute an assigned Issue, validate changes, deliver a PR and respond to review. Workspace preparation, dispatch, acceptance and user-approved closeout belong to the coordinator under [dispatch-sop.md](dispatch-sop.md). GitHub operations follow [issue-tracker.md](agents/issue-tracker.md).

## Scope and authority

Work only in the assigned worktree, branch and Issue scope. The dispatch grants task-branch commits/pushes, PR creation or updates, and Issue completion/revision comments under the [dispatch authority](dispatch-sop.md#roles-and-authority). Reading this file alone does not grant those actions. Leave acceptance checkboxes, merge, worktree reclamation, Issue closure and version updates to the coordinator and user; preserve published history.

Use the maintained `odradekk/godot-mcp` fork for supported Godot scene/resource/editor/runtime operations under [Godot MCP validation](#godot-mcp-validation). Follow [godot-mcp.md](godot-mcp.md) for configuration. Check access in your actual session before Godot work and report the result or CLI fallback; another agent's connection does not prove yours works.

## 0. Verify the assignment

Read the dispatch brief and Issue discussion; verify the assigned absolute worktree, task branch, baseline and PR base, preserving unrelated changes. Read `AGENTS.md`, relevant design/ADRs and the [Testing standard](#testing-standard). Before environment setup, validation or export, consult [Local validation and Windows builds](#local-validation-and-windows-builds). Report missing prerequisites or conflicting scope instead of silently changing the assignment.

**Complete when:** the workspace and assignment match, applicable prerequisites are available, and the required validation is understood.

## 1. Implement and deliver a PR — implementation agent

1. Read the Issue body and comments, `AGENTS.md`, and relevant domain docs/ADRs.
2. Implement only the assigned scope, add the minimum sufficient observable regression coverage, run the checks selected under [Testing standard](#testing-standard), and review the diff. For Godot changes, follow [Godot MCP validation](#godot-mcp-validation) and include its applicable evidence in the completion brief.
3. Commit intended changes with Conventional Commit messages. Push the task branch to the agreed repository and open a PR against the agreed base. For a revision, add commits to the same branch and update the same PR.
4. Reference the Issue with a normal link or `Refs #<issue>`. Avoid closing keywords in PR descriptions and commits so the coordinator can close the Issue after worktree cleanup.
5. Add an Issue comment with the completion brief below. The PR head SHA must identify the exact code tested.

If implementation or validation is blocked, post a blocker comment with evidence, missing prerequisites, and the next required action. If PR creation fails, report that delivery is incomplete; a local commit alone is not a delivered task.

Keep the worktree for review and revision. The agent leaves Acceptance-criteria checkboxes and final acceptance to the reviewer.

**Complete when:** the PR is accessible, its remote head matches the tested commit, and the Issue has a completion brief linking that PR. Required checks that failed or were not run remain explicit blockers, not passes.

### Completion brief — Issue comment

- PR URL, base branch, and tested head SHA.
- What changed and which Acceptance criteria it covers.
- Selected validation scope and exact commands, results, test-group durations, and anything not run; include host OS and tool versions where relevant. Identify reused evidence and why intervening changes do not invalidate it. For Windows builds, include the export path and report export and Windows runtime validation separately.
- For Godot MCP validation: fork commit, actual project/worktree path, tool calls and relevant arguments, observed results, retained log/artifact paths, and any fallback or unverified coverage. Tie the evidence to the tested game head SHA; list manual visual checks separately.
- Reproduction steps or example content needed to verify the behavior.
- Remaining risks/blockers, or an explicit statement that none are known.

## 2. Respond to review

Read the coordinator's findings and linked Issue discussion. Keep the same worktree, branch and PR; fix the assigned findings, rerun affected checks using the evidence-reuse rules, and add commits. Reply to each finding in the Issue with the resolution, commit IDs, actual validation and remaining gaps. Update the PR for its final scope and retain the worktree for re-review. Do not mark technical acceptance or close the Issue yourself.

**Complete when:** the revision is pushed to the same PR, its tested code is identified, and every requested finding has a traceable response.

## Recovery

After interruption, read the latest brief, Issue comments, PR head and local Git state. Verify which build/test processes belong to this worktree and whether they are still running before starting replacements. Preserve existing changes and evidence; resume the first incomplete action. If the assignment or worker ownership is unclear, report the blocker to the coordinator instead of creating another branch, agent or workspace. A changed head requires affected revalidation, and an unpushed local commit is not delivered work.

## Testing standard

> Confirmed through Q1–Q11 in [确定测试分层、编写规范与审查门槛](https://github.com/odradekk/train/issues/136).

This section is the single source of testing policy. Test inventories, commands and historical reports may live elsewhere and link here. Use the smallest sufficient set that proves the changed contracts; neither test counts nor coverage percentages are default targets. An Issue's explicit Acceptance criteria still apply; changing them requires the user's decision.

### Responsibilities and real boundaries

| Test family | Responsibility | Boundary |
| --- | --- | --- |
| Unit tests | Small rules, calculations and validity checks with fast feedback. | Prefer execution without the engine or child processes. |
| Headless automation | Collaboration between modules, command sequences, real persistence, and required Godot or cross-process behavior. | Use ordinary C# where sufficient; start Godot or separate processes when the claimed contract requires them. |
| Benchmark | Repeatable performance measurement and comparison. | Separate from correctness tests; see the Benchmark gate below. |

Graphical interaction, visual quality, export/platform behavior and target-hardware experience receive supplementary checks when affected or required by a stage gate. They are not part of every quick test run. A headless run cannot establish visual quality or real-window responsiveness.

Classify by the behavior actually exercised, not by the framework or filename. The current `Train.Tests` project links production source files; its passing results alone do not verify the deployed Godot assembly or scene/resource wiring. Script harnesses using fake programs validate orchestration behavior, not the real game. Startup markers and captured screenshots prove only what their accompanying assertions or observation actually check.

### Choose checks from the change

During development, run affected tests for quick feedback. Before delivery, run relevant unit/headless checks and necessary builds. Use this default selection, adding the Issue's explicit requirements:

| Change | Default checks |
| --- | --- |
| Documentation only | Review the text and diff; run applicable existing documentation checks. |
| Local domain behavior | Related unit or session-contract tests. |
| Commands, content, persistence or simulation | Affected headless integration; cross-process comparison when recovery or determinism is part of the contract. |
| Scenes, resources, input or presentation | Build, necessary resource import and real Godot checks; manual acceptance when interaction or visual quality changes. |
| Export or platform wiring | Corresponding export and actual target-platform runtime checks. |
| Broad shared-contract changes, multi-feature integration affecting cross-module behavior, or stage completion | One complete correctness regression at the relevant integration/stage gate. |
| Performance optimization or a change with a clear complexity, memory or scale risk | Targeted measurements; optimizations require comparable before/after evidence. |

If the impact is uncertain or a failure points beyond the selected scope, expand validation. Complete correctness regression does not automatically include Benchmark, long runs, every platform or all manual checks. Revisions use the evidence-reuse rules below rather than automatically restarting every suite.

### Write tests that can detect defects

- Name and assert observable contracts. Obtain expected results independently of the implementation being tested; calling that implementation again is not an independent oracle.
- Add tests for new/changed behavior and realistically regressible contracts at the lowest-cost sufficient boundary. Add higher-level coverage for additional integration risks, not to repeat the same assertion at every layer. Select valid, rejected and rollback cases according to the actual risk rather than copying a fixed matrix for every feature.
- For a bug fix, prefer a regression that exposes the original defect. Preserve before-fix failure and after-fix success evidence where feasible; explain when reproducing the old version is impractical.
- Use minimal representative inputs and reuse suitable existing fixtures. Avoid speculative generic test frameworks, implementation-detail assertions and mechanical tests for trivial forwarding or non-behavioral changes.
- Fix random seeds, control time where practical, and isolate and clean up test-owned files and processes. Real process checks may use bounded timeouts; elapsed waiting alone is not a success condition.
- Exercise the real boundary claimed by the test. Replacing persistence, content loading or a second process with a fake cannot prove that boundary works. A smaller unit test may use a substitute without making that larger claim.

### Review tests and evidence

Reviewers inspect whether the selected tests cover the changed risks, whether assertions are trustworthy and whether the claimed boundaries are real. Examine duplicate and fragile cases as maintenance costs. Independently run key affected checks according to risk; do not automatically repeat the implementer's entire suite. For high-risk regressions, use the original reproduction or a temporary deliberate fault when useful to establish that the test detects the defect. Mutation testing is not mandatory for every test.

Tie results to the tested commit, commands and outcomes, with build/content/environment identities and raw logs where relevant. Map Acceptance criteria to sufficient evidence; this does not require a new test for each criterion. One criterion may need several kinds of evidence, and one test may support several criteria.

Reuse results only with an explanation of why the intervening changes cannot affect those checks, such as a documentation-only revision. If applicability cannot be established, rerun. Distinguish an earlier complete-suite pass from tests actually executed on the current revision; never report the former as the latter.

### Failures and exceptions

Real failures require resolution. Missing environments, skipped checks and unexecuted checks remain unverified. Preserve the first failure and classify intermittent failures as product, test or environment problems; rerunning until green or silently skipping does not establish acceptance.

An exception for a demonstrated unrelated baseline fault or environment limitation requires the user's explicit acceptance of the risk and a follow-up validation plan. Record it separately from passes. It does not satisfy an unverified Acceptance criterion or bypass the [coordinator acceptance and approval workflow](dispatch-sop.md#3-verify-every-acceptance-criterion--coordinator--reviewer): if an affected criterion must be deferred, the user must explicitly decide that scope change before acceptance proceeds. World-state corruption, save/recovery data loss, train-safety failures and critical determinism errors remain blockers unless the user separately changes the corresponding product contract.

### Execution cost and Benchmark gate

Measure test-group durations before setting speed targets. Prefer related quick tests for daily work; investigate repeated setup, oversized inputs and ineffective waits in slow cases, then group them by their actual responsibilities. Preserve necessary assertions and real boundaries. Extending a timeout must not hide a hang. Reorganize existing tests incrementally, starting with cases that obstruct development, rather than requiring an immediate rewrite of the whole suite.

Benchmark is a required stage-completion capability and may be implemented and measured near the end of the stage. It is not required for every PR, and every change need not add a performance case. Targeted measurements are required earlier for the risks in the selection table. Unit-suite wall time or headless throughput does not replace real-window interaction measurements.

The [Unified test execution contract](#unified-test-execution-contract) specifies the planned entry, organization, evidence and migration. Benchmark workloads, environments, statistics, regression thresholds and hardware requirements are determined in [确定性能 Benchmark 与阶段收尾验收门槛](https://github.com/odradekk/train/issues/143). Existing explicit performance requirements remain in force until revised. These contracts do not claim the tools or measurements already exist.

**Complete when:** the changed risks have sufficient applicable evidence, tests make valid claims about their boundaries, required failures/gaps are resolved or handled through the explicit scope/exception rules, and the reviewed revision's results and reused evidence are distinguishable.

### Unified test execution contract

> Confirmed through Q1–Q11 in [确定统一测试入口、证据格式与旧脚本迁移](https://github.com/odradekk/train/issues/137). This is an implementation contract, not an installed runner or CI workflow. Until delivered, use the existing applicable commands under the testing policy above.

**Entry and classification.** Provide `scripts/run-tests.ps1` as a thin PowerShell 7 dispatcher for discovery, selection, prerequisites, runner invocation and reporting. Keep assertions in their test implementations and preserve independently callable runners. No arguments show help and available groups; execution of the full regression set requires explicit selection.

Classify tests by family (unit, headless automation, Benchmark) and stable functional scope such as voxels, buildings, railway, persistence or content. Record actual requirements such as Godot, separate processes, export or a graphical session. Speed follows measurements, not a blanket assumption that C# tests are unit tests. List manual observation separately from automated passes.

Discover MSTest cases and classification from test metadata; register only the necessary invocation information for other runners. Use stable case/group names rather than adding permanent entry points for individual Issues. Show selected cases and prerequisites before execution, combine family/scope filters and deduplicate selected cases. Invalid arguments, unknown names and an explicit selection matching no cases are errors. Report unavailable required environments as unverified with a non-success result instead of silently shrinking the selection.

Full regression includes registered correctness checks; Benchmark, long runs, manual checks and additional platform checks require explicit selection. Publish the actual selected coverage so a partial migration or small CI selection cannot be presented as all repository checks. Unimplemented Benchmark is a pending stage requirement, not an empty runnable group that reports success.

**Execution and isolation.** Give each run a fresh result directory and isolate save files, mutable content and user data. Control test environment settings in child processes without leaking task-specific settings into other runs. Prepare only necessary builds/imports and reuse them within that invocation when applicable. Initially serialize checks that need Godot, builds or shared resources; cross-run caching and automatic parallel scheduling are outside the first delivery.

Use measured, bounded timeouts for process checks. Cancellation or timeout preserves available evidence and terminates only this run's owned process tree. If cleanup cannot establish isolation, stop dispatching further groups and report the blocker. Preserve other worktrees' processes and ordinary game saves.

After an ordinary assertion failure, continue other independent, safe groups and retain all results. A failed build/import blocks its dependent groups; cancellation stops dispatch. Within a case, serious state corruption stops that case at the first evidence. Return a non-success exit whenever a selected required check fails, is blocked or does not finish. Distinguish failed, blocked, cancelled and not-executed outcomes in the report; later passes never overwrite earlier failures.

**Results and evidence.** Produce one machine-readable summary and one concise Markdown report per run, alongside the necessary original evidence. Their common information includes selection, actual execution, per-check outcome, preparation/test durations, tested commit and working-tree dirty state, relevant build/content/environment identities and artifact locations. Preserve native TRX, Godot logs and checkpoints rather than copying their detailed assertions into a second system. Results from a dirty tree do not establish that the recorded clean commit passed.

Place generated results in one Git-ignored run hierarchy, with unique directories and no overwrite of historical reports. PR completion briefs reference the summaries, with manual results separate. Retain acceptance evidence outside a worktree before reclaiming it. Exact summary fields, numeric nonzero exit codes and helper-file splits are implementation choices within these observable contracts; keep them minimal and document them when delivered.

**Existing code and migration.** Retain MSTest and the current production-source links initially, with the coverage boundary stated above. Classify tests incrementally within `Train.Tests`; extracting a core assembly or moving all production/test files is not a prerequisite. Keep test-only input fixtures distinct from user-facing example saves.

Inventory every existing validation entry and its contract before replacing it. First register useful existing runners behind the unified entry; consolidate duplicated startup, environment and reporting logic as each runner is migrated. Harnesses test orchestration risks such as arguments, exit propagation, timeout, cancellation and owned-process cleanup, not copies of game assertions. Verify equivalent behavior through the new entry before retiring an old command or driver, and update its callers and documentation.

Account for the current entry families in that inventory:

- Content/logging smoke, content/mods export smoke and their four fake-process harnesses: preserve their distinct game, export and orchestration claims.
- Playground command, observation, world-switch, authority-checkpoint and cross-process runners: retain distinct contracts; merge only demonstrated duplication.
- Legacy voxel smoke, observe, observe-resume and baseline runners: give each valid case a migration destination. Keep historical measurements identifiable; the baseline runner alone is not completion of the new Benchmark requirement.
- The Issue-specific Windows authority wrapper: move still-useful checks into stable functional groups and retire the wrapper after equivalence is demonstrated.
- Process/hardware helpers, harness fixtures and asset-generation utilities: distinguish support tooling from test cases rather than registering every PowerShell file as a test.

Scene removal follows [确定旧切片退役与示例存档、回归用例迁移](https://github.com/odradekk/train/issues/144). Wrapping a legacy runner does not prove its scene dependencies have been removed. Keep historical reports; avoid a wholesale directory reshuffle solely for appearance.

**Initial CI.** Include a lightweight GitHub Windows check using the same entry as local runs and explicitly selected, pinned SDK/tool versions. Run an explicit small correctness set and required builds; preserve failure reports. Support explicit invocation for complete regression or heavier Godot checks, with unmet prerequisites reported honestly. Do not build a complex path-impact inference system in the first delivery. CI success proves its selected checks only; required local Godot, manual and target-hardware evidence remains separate. Shared CI runners are not the formal performance baseline.

**Delivery boundary.** Deliver the entry, classification/discovery, isolation and failure behavior, unified reports, initial migration inventory and adapters, and lightweight CI before expanding subsequent editor functionality. Validate real representative MSTest and Godot/cross-process paths, plus orchestration failure cases; a fake-runner harness alone does not establish delivery. Migrate legacy internals gradually under the inventory. Benchmark implementation/measurement and four-scene removal remain their separate gates. Document actual commands and remaining migration gaps when the tools become available; do not dispatch agents against these planned commands before the required baseline contains them.

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

For a fresh checkout or after an SDK change, prepare the required build/import environment, then select checks under [Testing standard](#testing-standard). The commands below are existing entry examples, not a mandatory sequence for every change. The unfiltered `dotnet test` command runs the whole current MSTest project, not all repository correctness checks:

```powershell
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
& $env:GODOT --headless --path . --editor --import
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4
```

Use the Playground or other validation entry required by the changed contract. The shown `-Slice 4` is a legacy regression example; select an old slice (1–4) only when its contract is relevant while migration is incomplete. Resource import alone does not build the C# project. To open the editor after a successful build/import, run `& $env:GODOT --path . --editor`.

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

When using a Fedora checkout, install the SDK required by `global.json`, PowerShell 7, and Godot .NET. Check `dotnet --version`, `pwsh --version`, and `godot-dotnet --headless --version`. Set the executable path before running the applicable checks selected under [Testing standard](#testing-standard). The following are equivalent entry examples, not a requirement to run them all:

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
