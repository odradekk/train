# Implementation Standard Operating Procedure

Use this procedure as the worker of one Issue. You implement, test and deliver the Issue under the direction of your reviewer, who follows [review-sop.md](review-sop.md). Scheduling, merging and closure belong to the coordinator under [dispatch-sop.md](dispatch-sop.md). Tracker, labels and the check table are in the `## Agent workflow` section of `AGENTS.md`.

Carry out the Issue with the `implement` skill. The pair process changes it in three places: you post a plan and wait for the reviewer's approval before writing code ([step 1](#1-plan)); you skip its review step, because your reviewer reviews the delivery; and you deliver a PR instead of merging ([step 3](#3-deliver)). The rest of `implement`, including `simplify-change`, runs as the skill describes.

## Scope and authority

Work only in the assigned worktree, branch and Issue scope. You may commit, push the task branch, open and update its PR, and post Issue comments. Do not push to master, merge, force-push, rebase a pushed branch, close Issues, check Acceptance criteria or change their wording, or edit decision notes.

You talk only to your reviewer. Ask questions only by ending your turn with `WORKER: question`, not with interactive input tools. Each of your turns ends with a reply to the reviewer whose last line is exactly one of:

- `WORKER: plan` — your plan is ready for approval.
- `WORKER: question` — you need a decision before continuing.
- `WORKER: delivered <PR URL> <head SHA>` — the PR and completion comment are ready for review.
- `WORKER: blocked` — you cannot continue; say why and what is needed.

The reviewer decides design choices the docs leave open. Do not make such choices silently or widen the scope; ask instead. A conflict with an accepted decision note, or a hard-to-reverse choice that no note settles, is also a question: the reviewer takes it to the coordinator, who plans it with the user.

Ignored leftovers inside the worktree, such as old build outputs, go away with the worktree; do not spend work on deleting them.

**Godot.** For scene, resource, editor or runtime work, use the project's Godot MCP plugin under [Godot MCP validation](#godot-mcp-validation). Every Godot process you start, including imports, exports, editors and games, uses isolated user data (`APPDATA` and `LOCALAPPDATA` on Windows). For manual commands, follow the [isolated child-process recipe](local-environment.md#agent-手动启动-godot), keeping the redirection inside the child process so later test runs still find the real export templates. Export another checkout, which must be clean, with `scripts/measure/export-measurement.ps1 -SourceDirectory <checkout> -OutputDirectory <new directory>`.

**The user's desktop.** Never drive it: no Computer Use and no global keyboard or mouse simulation (`SendKeys`, `SendInput`, AutoHotkey, pasting the clipboard into whatever has focus). When the target window loses focus, that input lands in the user's foreground window instead (#301, 2026-10-19). Drive the game through console commands, Godot MCP or test hooks; drive a native dialog only through messages addressed to its window handle, or avoid the dialog.

**The Linux runner.** The repository is public and `lan-linux-nightly` is a self-hosted runner. Workflow and script output must not print sudo status, hostnames, usernames or host paths, and no `pull_request` event may start a self-hosted job.

## 0. Prepare

Read the dispatch comment, the Issue body and all comments, the decision note named on its first line if any, `AGENTS.md`, the referenced design docs and contracts, and the [Testing standard](#testing-standard). Check the worktree path, branch, baseline and MCP assignment against the dispatch comment. Before environment setup, validation or export, read [Local validation and Windows builds](local-environment.md).

Build the module's production target and run its existing tests once through the [unified test entry](run-tests.md) (`-Project core|content|scenes|godot|contract-support`), so later failures can be told apart from baseline failures.

## 1. Plan

Take in the work as step 1 of `implement` describes. Send the reviewer a plan ending with `WORKER: plan`:

- your understanding of the Issue and each Acceptance criterion;
- the route through `implement` (`tdd`, `diagnose-bug` or `refactor`), the approach, the files and modules it touches, the interface shape when it adds or changes one, and an order of small, separately verifiable steps;
- the checks you will run under the [Testing standard](#testing-standard), baseline failures, and any missing production check under [test organization](contracts/testing.md#模块测试的组织与入口);
- open questions, each with options and your recommendation.

Do not write code until the reviewer approves the plan.

## 2. Implement

Continue `implement` from its step 2 with the approved plan, through its final checks. Stop and ask with `WORKER: question` when the work contradicts the plan, needs a new design decision, or would leave the Issue's scope. Select checks under the [Testing standard](#testing-standard); for Godot changes, also follow [Godot MCP validation](#godot-mcp-validation).

## 3. Deliver

1. Commit with the `commit` skill and open the PR against `master` with the `pr` skill. The description references the Issue with `Refs #<issue>`, never a closing keyword, and names the decision note ID when the Issue has one. Later fixes add commits to the same branch and PR.
2. Post the completion comment below on the Issue, then report `WORKER: delivered <PR URL> <head SHA>`. The SHA must identify the exact code you tested.

A local or unpushed commit is not delivered work. Report required checks that failed or did not run as such, not as passes.

### Completion comment

The `implement` report, plus:

- PR URL, base branch and tested head SHA, and the Acceptance criteria the change covers.
- Validation scope, exact commands, results and durations, and anything not run; host OS and tool versions where relevant. Name reused evidence and why intervening changes cannot affect it. For Windows builds, the export path and separate export and runtime results.
- For Godot MCP validation: plugin commit, MCP port, actual project path, tool calls and their relevant arguments, observed results, retained logs, and any fallback or unverified coverage.
- Manual and visual checks, listed separately from automated results.
- Remaining risks or blockers, or a statement that none are known.

## 4. Fix review findings

The reviewer posts each round of findings on the Issue and sends them to you. Fix each finding that holds as step 5 of `implement` fixes one, in the same branch and PR; rerun the affected checks under the evidence-reuse rules, commit and push. Post one Issue comment per round that answers every finding with its resolution, commit IDs and validation, or with the evidence that it does not hold. Then report `WORKER: delivered` with the new head SHA.

When the reviewer asks you to sync with master, merge `origin/master` into the branch (no rebase), resolve conflicts, rerun the affected checks and push.

## Art Issues

Produce and inspect assets under [art 08](art/08-asset-pipeline-and-review.md). For each texture, shared texture, wear overlay, paint card and icon, choose between your image model and a generator script, and record the choice; an image-model PNG is its own source, and the completion comment records the model and prompt. Build custom models in Blender through Blender MCP and keep the `.blend` source under `art-source/`. Take screenshots with the [art inspection screenshot entry](art/08-asset-pipeline-and-review.md#检查人与记录). Put the checklist table and screenshot paths in the completion comment.

## Recovery

After an interruption, read the Issue comments, the PR head and the local Git state. Check which build and test processes belong to this worktree before starting new ones. Keep existing changes and evidence, tell the reviewer where the work stands, and continue from the first incomplete step.

## Testing standard

This section is the project's testing policy; other documents link here for it. Use the smallest set of checks that proves the changed contracts; test counts and coverage percentages are not targets. An Issue's explicit Acceptance criteria still apply; changing them requires the user's decision.

### Responsibilities and real boundaries

| Layer or check | Responsibility | Boundary |
| --- | --- | --- |
| fast | Small rules, calculations, validity and session contracts for quick feedback. | In-process, without real file I/O or subprocesses; each test within 1 second. |
| integration | Real file I/O and cross-process behavior; repository-wide selection also checks structure, public APIs and Schema. | Only the current run directory; cleans up test-owned files and processes. |
| engine | Real Godot behavior of the current App. | Ordinary C# tests cannot replace engine evidence. |
| stage | Correctness, platform and performance checks at a stage gate. | See [stage checks and candidate baselines](run-tests.md#阶段检查与候选基准) for the platform scope. |
| Benchmark | Repeatable performance measurement and comparison. | Separate from correctness tests; see [Performance measurement](#performance-measurement). |

Classify a test by the behavior it exercises, not by framework or filename. `Train.Godot.Tests` exercises pure host code and does not prove scene or resource wiring; `Tests/Engine/` provides real Godot evidence. Script harnesses with fake programs validate orchestration, not the real game. Startup markers and screenshots prove only what their assertions or observations check. A headless run cannot establish visual quality or real-window responsiveness, so interaction, visual quality, export, platform and target-hardware behavior get their own checks when a change affects them.

### Choose checks from the change

During development, run the affected tests for quick feedback; the check table in `AGENTS.md` gives the narrowest command per path. Before delivery, run the checks and builds this table selects, plus the Issue's explicit requirements:

| Change | Default checks |
| --- | --- |
| Documentation only | Review the text and diff; run applicable existing documentation checks. |
| Local domain behavior | Related fast-layer tests. |
| Commands, content, persistence or simulation | Affected integration-layer checks; cross-process comparison when recovery or determinism is part of the contract. |
| Scenes, resources, input or presentation | Build, necessary resource import and `-Layer engine`; manual acceptance when interaction or visual quality changes. |
| Export or platform wiring | Corresponding export and actual target-platform runtime checks. |
| Broad shared-contract changes or multi-feature integration affecting cross-module behavior | One complete correctness regression. |
| Performance optimization, or a clear complexity, memory or scale risk | Targeted measurements under [Performance measurement](#performance-measurement). |

If the impact is uncertain or a failure points beyond the selected scope, widen validation. A complete correctness regression does not automatically include Benchmark, long runs, every platform or all manual checks.

### Test entry and CI

Use the [unified test entry](run-tests.md): the fast layer for daily feedback, and `-Layer fast,integration` before delivery, as routine [Windows CI](ci.md) does; it selects the affected projects from the changes since the merge base with `origin/master`. Godot-related changes also need `-Layer engine` before delivery, which CI runs on every PR without blocking. `-All -Layer all` is an explicit correctness regression and `-Layer engine -ExportWindows` the Windows export and runtime check. Selection rules, stage options, execution limits and result rules are in the test entry; what Linux nightly covers is in [CI](ci.md#linux-夜间运行).

### Writing tests

The `tdd` skill's rules for a test and blocking checks 12–15 of the `train-code-review` skill apply to every test. In this repository also:

- Keep test-only input fixtures apart from the user-facing example saves.
- A test claims only the boundary it exercises: a fake persistence, content loader or second process cannot prove that boundary, though a smaller unit test may use one without making the larger claim.
- Before retiring a test command, runner or driver, verify the same behavior through its replacement and update its callers and documentation.

### Review tests and evidence

Reviewers review tests with `train-code-review` and run the key affected checks themselves according to risk, not the worker's entire suite. For a high-risk regression, the original reproduction or a temporary deliberate fault shows that the test detects the defect.

Tie results to the tested commit, commands and outcomes, with build, content and environment identities and raw logs where relevant. Map each Acceptance criterion to sufficient evidence; one criterion may need several kinds of evidence, and one test may support several criteria. Keep evidence in Issue comments and in ignored run directories such as `TestResults/` and `.godot/validation/`; do not add validation reports, captures or evidence archives to the repository.

Reuse a result only with an explanation of why the intervening changes cannot affect it, such as a documentation-only revision; if that cannot be shown, rerun. Never report an earlier complete-suite pass as tests run on the current revision.

### Failures and exceptions

Real failures require resolution. Missing environments, skipped checks and unexecuted checks remain unverified. Preserve the first failure and classify intermittent failures as product, test or environment problems; rerunning until green or silently skipping does not establish acceptance.

An exception for a demonstrated unrelated baseline fault or environment limitation needs the user's explicit acceptance of the risk and a follow-up validation plan, recorded separately from passes. It does not satisfy an unverified Acceptance criterion or bypass [acceptance](review-sop.md#4-accept): the reviewer reports it as [blocked](review-sop.md#blocked), and deferring an affected criterion needs the user's decision first. World-state corruption, save or recovery data loss, train-safety failures and critical determinism errors remain blockers unless the user changes the corresponding product contract.

### Execution cost

Keep test groups within the [test entry's execution limits](run-tests.md#执行与边界); budgets change only under the [budget rule](contracts/testing.md#预算调整原则), and a longer timeout must not hide a hang. In slow cases, look first for repeated setup, oversized inputs and ineffective waits, keeping the necessary assertions and real boundaries.

### Performance measurement

Measure with the `measure-performance` skill. Optimizations need comparable before and after evidence; changes with a clear complexity, memory or scale risk need targeted measurements. Benchmark is not part of every PR. Fast- or integration-layer wall time and engine throughput do not replace real-window measurements. Workloads and targets come from the [performance targets](design/00-product-and-core-loop.md#本阶段性能目标); new thresholds need the user's confirmation.

- Formal before/after comparisons run on the Linux runner `lan-linux-nightly`, one job at a time. The reviewer announces each job to the coordinator before the worker starts it.
- The dev machine is for diagnosis, and for quick comparisons that alternate both sides within one window.
- Timing runs on the dev machine happen only in a quiet window the coordinator schedules. During a window, the other pairs start no builds, tests, exports or games; during a strict window, the other pairs make no tool calls and the measuring pair only runs the approved measurement until it ends.

**Complete when:** the changed risks have sufficient applicable evidence, tests make valid claims about their boundaries, required failures and gaps are resolved or handled through the explicit scope and exception rules, and the reviewed revision's results can be told apart from reused evidence.

## Godot MCP validation

Apply this sequence to Godot scene and resource edits, editor checks and runtime diagnosis. Documentation-only or pure domain tasks need no MCP run unless their Acceptance criteria require Godot behavior. Server startup, client approval, the pinned plugin commit and known limitations are in [godot-mcp.md](godot-mcp.md).

1. **Check your own connection** before the first Godot operation; another agent's connection does not prove yours. The coordinator starts the worktree's MCP editor on the port in the dispatch comment and stops it when the task merges. If it is not running, tell the reviewer, who asks the coordinator to restart it; do not start it yourself, because the coordinator's launch keeps the editor's user data out of the shared Godot user directory that the test entry checks. Call `get_project_info` and verify that `project_path` is the assigned worktree and the engine version is expected. If the path differs, another task's editor holds the port: stop and report instead of operating on it. Record the plugin commit from godot-mcp.md, not the server's reported version. Reconnect after changing the configuration or restarting the editor, and keep registration project-scoped.
2. **Prepare the project.** Build C# and import resources through the [local CLI workflow](local-environment.md) before runtime checks; import new assets and scripts before UID operations. MCP does not build the .NET project or replace required repository checks.
3. **Use the relevant tools.** Inspect with project, scene, node and script queries. Edit only within the authorized scope, then inspect the resulting files and reload affected resources to verify the values. Test the MCP itself only in a disposable project. For runtime checks, call `run_project` with `allow_window=true`, then `get_editor_logs` with `source` set to `editor_panel`; confirm that the log timestamps belong to this run and check the expected markers, errors and running or exit status. A start acknowledgement alone is not a pass. `run_project` saves open scenes first; revert unrelated scene or `.uid` changes. When editor interaction is needed, use the graphical editor and record what you observed.
4. **Preserve evidence and stop owned processes.** Save tool inputs, logs, exit status and artifact checks in the task's validation directory before another run replaces them. Never start or stop anything through an editor serving another worktree. Stop a task-owned game with `stop_project` (`allow_window=true`) and verify that it ended; leave the editor running for later review rounds. MCP output is not evidence of visual quality or user acceptance.
5. **Handle unavailable or insufficient tools.** Report connection, approval, platform or tool failures, use the existing Godot CLI and scripts for checks they can equivalently perform, and record the fallback and its results. Keep uncovered Acceptance criteria unverified. Do not replace the configured plugin, patch `addons/godot_mcp/` or add a global registration to work around a failure.

**Complete when:** the applicable behavior has verified artifact or runtime evidence bound to the worktree and tested head, required repository checks are accounted for, logs are retained, and task-owned games are stopped. Any remaining manual or unsupported check is explicitly pending.
