# Implementation Standard Operating Procedure

Use this procedure as the worker of one Issue. The worker implements, tests and delivers the Issue under the direction of its reviewer, who follows [review-sop.md](review-sop.md). Scheduling, merging and closure belong to the coordinator under [dispatch-sop.md](dispatch-sop.md). GitHub operations follow [issue-tracker.md](agents/issue-tracker.md).

## Scope and authority

Work only in the assigned worktree, branch and Issue scope. You may commit, push the task branch with `git push -u origin <branch>`, open and update the PR, and post Issue comments. Do not push to master, merge, force-push, rebase a pushed branch, close Issues, check Acceptance criteria or change their wording.

You talk only to your reviewer. Ask questions only by ending your turn with `WORKER: question`, not with interactive input tools. Each of your turns ends with a reply to the reviewer whose last line is exactly one of:

- `WORKER: plan` — your plan is ready for approval.
- `WORKER: question` — you need a decision before continuing.
- `WORKER: delivered <PR URL> <head SHA>` — the PR and completion comment are ready for review.
- `WORKER: blocked` — you cannot continue; say why and what is needed.

The reviewer decides design choices the docs leave open. Do not make such choices silently or widen the scope; ask instead.

Ignored leftovers inside the worktree, such as old build outputs, go away with the worktree; do not spend work on deleting them.

For Godot scene, resource, editor or runtime work, use the project's Godot MCP Native plugin (`addons/godot_mcp/`) under [Godot MCP validation](#godot-mcp-validation).

## 0. Prepare

Read the coordinator's dispatch comment, the Issue body and all comments, `AGENTS.md`, the referenced design docs, ADRs and contracts, and the [Testing standard](#testing-standard). Verify the worktree path, branch, baseline and MCP assignment in the dispatch comment. Before environment setup, validation or export, read [Local validation and Windows builds](local-environment.md).

Build the assigned module's production target and run its existing tests once, so later failures can be told apart from baseline failures. Use the [unified test entry](run-tests.md) with `-Project core|content|godot|contract-support` for the responsibility being changed, and `-Layer` for combined selections. Report baseline failures and any missing production check under [test organization](contracts/testing.md#模块测试的组织与入口) in your plan.

## 1. Plan

Send the reviewer a plan ending with `WORKER: plan`:

- your understanding of the Issue and each Acceptance criterion;
- the approach, the files and modules it touches, and an order of small, separately verifiable steps;
- the checks you will run, chosen under the [Testing standard](#testing-standard);
- open questions, each with options and your recommendation.

Do not write code until the reviewer approves the plan.

## 2. Implement

1. Implement the approved plan in small steps and run the affected checks after each one.
2. If the work contradicts the plan, needs a new design decision, or would leave the Issue's scope, stop and ask with `WORKER: question`.
3. Add the minimum observable regression coverage and run the checks selected under the [Testing standard](#testing-standard). For Godot changes, follow [Godot MCP validation](#godot-mcp-validation).
4. Make the code readable through naming and structure, following `AGENTS.md` code taste.
5. Commit with Conventional Commit messages.

## 3. Deliver

1. Push the task branch and open a PR against `master`. Later fixes add commits to the same branch and PR.
2. Reference the Issue with `Refs #<issue>`. Do not use closing keywords; the coordinator closes the Issue after merging.
3. Post the completion comment below on the Issue, then report `WORKER: delivered <PR URL> <head SHA>`. The SHA must identify the exact code you tested.

A local or unpushed commit is not delivered work. Required checks that failed or were not run are reported as such, not as passes.

### Completion comment

- PR URL, base branch and tested head SHA.
- What changed and which Acceptance criteria it covers.
- For staged work, the stage acceptance Issue and the real integration paths this task enables, separating module evidence from stage integration evidence.
- Validation scope, exact commands, results and durations, and anything not run; host OS and tool versions where relevant. Identify reused evidence and why intervening changes do not affect it. For Windows builds, the export path and separate export and runtime results.
- For Godot MCP validation: plugin commit, MCP port, actual project path, tool calls and their relevant arguments, observed results, retained logs, and any fallback or unverified coverage.
- Manual and visual checks, listed separately from automated results.
- Remaining risks or blockers, or a statement that none are known.

## 4. Fix review findings

The reviewer posts each round of findings on the Issue and sends them to you. Fix them in the same branch and PR in small steps, rerun the affected checks under the evidence-reuse rules, and push. Post one Issue comment per round that answers every finding with its resolution, commit IDs and validation, then report `WORKER: delivered` with the new head SHA.

When the reviewer asks you to sync with master, merge `origin/master` into the branch (no rebase), resolve conflicts, rerun the affected checks and push.

## Art Issues

Produce and inspect assets under [art 08](art/08-asset-pipeline-and-review.md). For each texture, shared texture, wear overlay, paint card and icon, choose between your image model and a generator script, and record the choice; an image-model PNG is its own source, and the completion comment records the model and prompt. Build custom models in Blender through Blender MCP and keep the `.blend` source under `art-source/`. Take screenshots with the [art inspection screenshot entry](https://github.com/odradekk/train/issues/400); until it exists, use Godot MCP. Put the checklist table and screenshot paths in the completion comment.

## Recovery

After an interruption, read the Issue comments, the PR head and the local Git state. Check which build and test processes belong to this worktree before starting new ones. Keep existing changes and evidence, tell the reviewer where the work stands, and continue from the first incomplete step.

## Testing standard

This section is the single source of testing policy; inventories, commands and historical reports may live elsewhere and link here. Use the smallest sufficient set that proves the changed contracts; test counts and coverage percentages are not default targets. An Issue's explicit Acceptance criteria still apply; changing them requires the user's decision.

### Responsibilities and real boundaries

| Layer or check | Responsibility | Boundary |
| --- | --- | --- |
| fast | Small rules, calculations, validity and session contracts for quick feedback. | Run in-process without real file I/O or subprocesses; each test completes within 1 second. |
| integration | Real file I/O and cross-process behavior; repository-wide selection also checks structure, public APIs and Schema. | Use only the current run directory and clean up test-owned files and processes; only an exercised real boundary is verified. |
| engine | Real Godot behavior of the current App. | Ordinary C# tests cannot replace engine evidence. |
| Benchmark | Repeatable performance measurement and comparison. | Separate from correctness tests; see the Benchmark gate below. |

Graphical interaction, visual quality, export/platform behavior and target-hardware experience receive supplementary checks when affected or required by a stage gate, not in every quick run. A headless run cannot establish visual quality or real-window responsiveness.

Classify by the behavior actually exercised, not by framework or filename. `Train.Core.Tests` and `Train.Content.Tests` exercise their own implementations; `Train.Godot.Tests` exercises pure host code through a project reference and does not prove scene/resource wiring. Real Godot checks provide engine evidence. Script harnesses using fake programs validate orchestration such as arguments, exit propagation, timeout, cancellation and owned-process cleanup; they do not run the real game or copy its assertions. Startup markers and screenshots prove only what their accompanying assertions or observation check.

### Choose checks from the change

During development, run affected tests for quick feedback. Before delivery, run the relevant checks and necessary builds by this default selection plus the Issue's explicit requirements:

| Change | Default checks |
| --- | --- |
| Documentation only | Review the text and diff; run applicable existing documentation checks. |
| Local domain behavior | Related fast-layer tests. |
| Commands, content, persistence or simulation | Affected integration-layer checks; cross-process comparison when recovery or determinism is part of the contract. |
| Scenes, resources, input or presentation | Build, necessary resource import and `-Layer engine`; manual acceptance when interaction or visual quality changes. |
| Export or platform wiring | Corresponding export and actual target-platform runtime checks. |
| Broad shared-contract changes, multi-feature integration affecting cross-module behavior, or stage completion | One complete correctness regression at the relevant integration/stage gate. |
| Performance optimization or a change with a clear complexity, memory or scale risk | Targeted measurements; optimizations require comparable before/after evidence. |

If the impact is uncertain or a failure points beyond the selected scope, expand validation. Complete correctness regression does not automatically include Benchmark, long runs, every platform or all manual checks. Revisions follow the evidence-reuse rules below rather than restarting every suite.

### Test entry and CI

Use the fast layer for daily feedback and `-Layer fast,integration` before delivery and in routine [Windows CI](ci.md). These commands select affected test projects and independent checks from changes since the merge-base with `origin/master`; shared-file changes select all test projects. See the [unified test entry](run-tests.md#按改动选择) for the rules, `-BaseRef` and explicit `-Project` selection. Godot-related changes also require `-Layer engine` before delivery. Explicit correctness regression uses `-All -Layer all`; Windows export and runtime validation uses `-Layer engine -ExportWindows`. Execution limits and result rules are in the unified test entry.

### Write tests that can detect defects

- Name and assert observable contracts. Obtain expected results independently of the implementation under test; calling that implementation again is not an independent oracle.
- Add tests for new/changed behavior and realistically regressible contracts at the lowest-cost sufficient boundary. Add higher-level coverage for additional integration risks, not to repeat the same assertion at every layer. Select valid, rejected and rollback cases by actual risk rather than copying a fixed matrix for every feature.
- For a bug fix, prefer a regression that exposes the original defect. Preserve before-fix failure and after-fix success evidence where feasible; explain when reproducing the old version is impractical.
- Use minimal representative inputs and reuse suitable existing fixtures; keep test-only input fixtures distinct from user-facing example saves. Avoid speculative generic test frameworks, implementation-detail assertions and mechanical tests for trivial forwarding or non-behavioral changes.
- Fix random seeds, control time where practical, and isolate and clean up test-owned files and processes. Real process checks may use bounded timeouts; elapsed waiting alone is not a success condition.
- Exercise the real boundary the test claims. Replacing persistence, content loading or a second process with a fake cannot prove that boundary; a smaller unit test may use a substitute without making that larger claim.
- Before retiring a test command, runner or driver, verify equivalent behavior through its replacement and update its callers and documentation.

### Review tests and evidence

Reviewers check whether the selected tests cover the changed risks, whether assertions are trustworthy and whether the claimed boundaries are real, treating duplicate and fragile cases as maintenance costs. Independently run key affected checks according to risk rather than repeating the implementer's entire suite. For high-risk regressions, use the original reproduction or a temporary deliberate fault when useful to show that the test detects the defect. Mutation testing is not mandatory for every test.

Tie results to the tested commit, commands and outcomes, with build/content/environment identities and raw logs where relevant. Map Acceptance criteria to sufficient evidence; this does not require a new test for each criterion. One criterion may need several kinds of evidence, and one test may support several criteria. Keep validation evidence in Issue comments and ignored run directories such as `TestResults/` and `.godot/validation/`; do not add validation reports, captures or evidence archives to the repository.

Reuse results only with an explanation of why the intervening changes cannot affect those checks, such as a documentation-only revision; if applicability cannot be established, rerun. Never report an earlier complete-suite pass as tests actually executed on the current revision.

### Failures and exceptions

Real failures require resolution. Missing environments, skipped checks and unexecuted checks remain unverified. Preserve the first failure and classify intermittent failures as product, test or environment problems; rerunning until green or silently skipping does not establish acceptance.

An exception for a demonstrated unrelated baseline fault or environment limitation requires the user's explicit acceptance of the risk and a follow-up validation plan. Record it separately from passes. It does not satisfy an unverified Acceptance criterion or bypass [acceptance](review-sop.md#4-accept): the reviewer reports it as [blocked](review-sop.md#blocked), and if an affected criterion must be deferred, the user must explicitly decide that scope change before acceptance proceeds. World-state corruption, save/recovery data loss, train-safety failures and critical determinism errors remain blockers unless the user separately changes the corresponding product contract.

### Execution cost and Benchmark gate

Measure test-group durations against the [test entry's execution limits](run-tests.md#执行与边界); do not silently relax them. Prefer related quick tests for daily work; in slow cases, investigate repeated setup, oversized inputs and ineffective waits, then group them by actual responsibility. Preserve necessary assertions and real boundaries; extending a timeout must not hide a hang. Reorganize existing tests incrementally, starting with cases that obstruct development, rather than rewriting the whole suite at once.

Benchmark belongs to stage gates, not to every PR, and not every change needs a performance case; targeted measurements are required earlier for the risks in the selection table. Fast- or integration-layer wall time or engine throughput does not replace real-window interaction measurements. Workloads, environments and thresholds come from the current stage's [performance requirements](design/00-product-and-core-loop.md#本阶段性能目标); measured thresholds require user confirmation.

**Complete when:** the changed risks have sufficient applicable evidence, tests make valid claims about their boundaries, required failures/gaps are resolved or handled through the explicit scope/exception rules, and the reviewed revision's results and reused evidence are distinguishable.

## Godot MCP validation

Apply this sequence to Godot scene/resource edits, editor checks and runtime diagnosis. Documentation-only or pure domain tasks need no MCP run unless their Acceptance criteria require Godot behavior. Server startup, client approval, the pinned plugin commit and known limitations are in [godot-mcp.md](godot-mcp.md).

1. **Check your own connection** before the first Godot operation; another agent's connection does not prove yours. The coordinator starts the worktree's MCP editor at dispatch on the port recorded in the dispatch comment; if it is not running, start it on that port as godot-mcp.md describes. Then call `get_project_info` in the actual session and verify that `project_path` is the assigned worktree and the engine version is expected. The client connects to a port, not a worktree: if the path differs, another task's editor holds the port, so stop and report instead of operating on it. Record the plugin commit from godot-mcp.md, not the server's reported version. Reconnect or restart the session after changing its configuration or restarting the editor, and keep registration project-scoped.
2. **Prepare the project.** Build C# and import resources through the [local CLI workflow](local-environment.md) before runtime checks; import new assets/scripts before UID operations. MCP does not build the .NET project or replace required repository checks.
3. **Use the relevant tools.** Use project, scene, node and script queries for inspection. Edit scenes, nodes, properties or scripts only within the authorized scope, then inspect the resulting files and reload affected resources to verify the values. Test the MCP itself in an isolated disposable project. For runtime checks, call `run_project` with `allow_window=true`, then `get_editor_logs` with `source` set to `editor_panel`; confirm the log timestamps belong to this run and check the expected ready/behavior markers, errors and running or exit status. A start acknowledgement alone is not a pass. `run_project` saves open scenes first; revert unrelated scene or `.uid` changes. When editor interaction is needed, use the graphical editor and record what was actually observed.
4. **Preserve evidence and stop owned processes.** Save tool inputs, output/error logs, exit status and artifact checks in the task's validation directory before another run or the end of the session; a new run replaces the editor's current one. Never start or stop anything through an editor serving another worktree. Stop a task-owned game with `stop_project` (`allow_window=true`) and verify termination; close the task-owned MCP editor, identified by its `--path` and `--mcp-server` command line. MCP output is not evidence of visual quality or user acceptance.
5. **Handle unavailable or insufficient tools.** Report connection, approval, platform or tool failures, use the existing Godot CLI/scripts for checks they can equivalently perform, and record the fallback and its results. Keep uncovered Acceptance criteria unverified. Do not replace the configured plugin, patch `addons/godot_mcp/` or add a global registration to work around a failure.

**Complete when:** the applicable behavior has verified artifact/runtime evidence bound to the worktree and game head, required repository checks are accounted for, logs are retained, and task-owned processes are closed. Any remaining manual or unsupported check is explicitly pending.
