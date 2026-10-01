# Implementation Standard Operating Procedure

Use this procedure to execute an assigned Issue, validate changes, deliver a PR and respond to review. Workspace preparation, dispatch, acceptance and user-approved closeout belong to the coordinator under [dispatch-sop.md](dispatch-sop.md). GitHub operations follow [issue-tracker.md](agents/issue-tracker.md).

## Scope and authority

Work only in the assigned worktree, branch and Issue scope, and keep the worktree for review and revision. The dispatch grants task-branch commits/pushes, PR creation or updates, and Issue completion/revision comments under the [dispatch authority](dispatch-sop.md#roles-and-authority); reading this file alone does not. Acceptance checkboxes, technical acceptance, merge, worktree reclamation, Issue closure and version updates belong to the coordinator and user. Preserve published history.

For Godot scene, resource, editor or runtime work, use the project's Godot MCP Native plugin (`addons/godot_mcp/`) under [Godot MCP validation](#godot-mcp-validation).

## 0. Verify the assignment

Read the dispatch brief, the Issue body and discussion, `AGENTS.md`, relevant design docs/ADRs and the [Testing standard](#testing-standard). Verify the assigned absolute worktree, task branch, baseline and PR base, preserving unrelated changes. Before environment setup, validation or export, consult [Local validation and Windows builds](local-environment.md). Report missing prerequisites or conflicting scope instead of silently changing the assignment.

**Complete when:** the workspace and assignment match, applicable prerequisites are available, and the required validation is understood.

## 1. Implement and deliver a PR — implementation agent

1. Implement only the assigned scope, add the minimum sufficient observable regression coverage, run the checks selected under the [Testing standard](#testing-standard), and review the diff. For Godot changes, follow [Godot MCP validation](#godot-mcp-validation).
2. Commit intended changes with Conventional Commit messages, push the task branch to the agreed repository and open a PR against the agreed base. Revisions add commits to the same branch and PR.
3. Reference the Issue with a normal link or `Refs #<issue>`. Avoid closing keywords in PR descriptions and commits so the coordinator can close the Issue after worktree cleanup.
4. Post the completion brief below as an Issue comment. The PR head SHA must identify the exact code tested.

If implementation or validation is blocked, post a blocker comment with evidence, missing prerequisites and the next required action. If PR creation fails, report that delivery is incomplete; a local or unpushed commit is not delivered work.

**Complete when:** the PR is accessible, its remote head matches the tested commit, and the Issue has a completion brief linking that PR. Required checks that failed or were not run remain explicit blockers, not passes.

### Completion brief — Issue comment

- PR URL, base branch and tested head SHA.
- What changed and which Acceptance criteria it covers.
- Selected validation scope; exact commands, results, test-group durations, unified-entry summary paths and anything not run; host OS and tool versions where relevant. Identify reused evidence and why intervening changes do not invalidate it. For Windows builds, include the export path and report export and Windows runtime validation separately.
- For Godot MCP validation: plugin source commit, MCP port, actual project/worktree path, tool calls and relevant arguments, observed results, retained log/artifact paths, and any fallback or unverified coverage, tied to the tested head SHA.
- Manual and visual checks, listed separately from automated results.
- Reproduction steps or example content needed to verify the behavior.
- Remaining risks/blockers, or an explicit statement that none are known.

## 2. Respond to review

Read the coordinator's findings and linked Issue discussion. In the same worktree, branch and PR, fix the assigned findings, rerun affected checks under the evidence-reuse rules, and add commits. Reply to each finding in the Issue with the resolution, commit IDs, actual validation and remaining gaps, and update the PR for its final scope.

**Complete when:** the revision is pushed to the same PR, its tested code is identified, and every requested finding has a traceable response.

## Recovery

After interruption, read the latest brief, Issue comments, PR head and local Git state. Check which build/test processes belong to this worktree and whether they are still running before starting replacements. Preserve existing changes and evidence and resume the first incomplete action. If the assignment or worker ownership is unclear, report the blocker to the coordinator instead of creating another branch, agent or workspace. A changed head requires affected revalidation.

## Testing standard

This section is the single source of testing policy; inventories, commands and historical reports may live elsewhere and link here. Use the smallest sufficient set that proves the changed contracts; test counts and coverage percentages are not default targets. An Issue's explicit Acceptance criteria still apply; changing them requires the user's decision.

### Responsibilities and real boundaries

| Test family | Responsibility | Boundary |
| --- | --- | --- |
| Unit tests | Small rules, calculations and validity checks with fast feedback. | Prefer execution without the engine or child processes. |
| Headless automation | Collaboration between modules, command sequences, real persistence, and required Godot or cross-process behavior. | Use ordinary C# where sufficient; start Godot or separate processes when the claimed contract requires them. |
| Benchmark | Repeatable performance measurement and comparison. | Separate from correctness tests; see the Benchmark gate below. |

Graphical interaction, visual quality, export/platform behavior and target-hardware experience receive supplementary checks when affected or required by a stage gate, not in every quick run. A headless run cannot establish visual quality or real-window responsiveness.

Classify by the behavior actually exercised, not by framework or filename. `Train.Tests` compiles linked production sources, so its passing results alone do not verify the deployed Godot assembly or scene/resource wiring. Script harnesses using fake programs validate orchestration such as arguments, exit propagation, timeout, cancellation and owned-process cleanup; they do not run the real game or copy its assertions. Startup markers and screenshots prove only what their accompanying assertions or observation check.

### Choose checks from the change

During development, run affected tests for quick feedback. Before delivery, run the relevant checks and necessary builds by this default selection plus the Issue's explicit requirements:

| Change | Default checks |
| --- | --- |
| Documentation only | Review the text and diff; run applicable existing documentation checks. |
| Local domain behavior | Related unit or session-contract tests. |
| Commands, content, persistence or simulation | Affected headless integration; cross-process comparison when recovery or determinism is part of the contract. |
| Scenes, resources, input or presentation | Build, necessary resource import and real Godot checks; manual acceptance when interaction or visual quality changes. |
| Export or platform wiring | Corresponding export and actual target-platform runtime checks. |
| Broad shared-contract changes, multi-feature integration affecting cross-module behavior, or stage completion | One complete correctness regression at the relevant integration/stage gate. |
| Performance optimization or a change with a clear complexity, memory or scale risk | Targeted measurements; optimizations require comparable before/after evidence. |

If the impact is uncertain or a failure points beyond the selected scope, expand validation. Complete correctness regression does not automatically include Benchmark, long runs, every platform or all manual checks. Revisions follow the evidence-reuse rules below rather than restarting every suite.

### Test entry and CI

Run tests through `scripts/run-tests.ps1`; its usage, selection, isolation and reporting are specified in [run-tests.md](run-tests.md). [Windows CI](ci.md) runs a small selection through the same entry. CI success proves only its selected checks; required local Godot, manual and target-hardware evidence stays separate, and shared CI runners are not a performance baseline.

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

An exception for a demonstrated unrelated baseline fault or environment limitation requires the user's explicit acceptance of the risk and a follow-up validation plan. Record it separately from passes. It does not satisfy an unverified Acceptance criterion or bypass the [coordinator acceptance and approval workflow](dispatch-sop.md#3-verify-every-acceptance-criterion--coordinator--reviewer): if an affected criterion must be deferred, the user must explicitly decide that scope change before acceptance proceeds. World-state corruption, save/recovery data loss, train-safety failures and critical determinism errors remain blockers unless the user separately changes the corresponding product contract.

### Execution cost and Benchmark gate

Measure test-group durations before setting speed targets. Prefer related quick tests for daily work; in slow cases, investigate repeated setup, oversized inputs and ineffective waits, then group them by actual responsibility. Preserve necessary assertions and real boundaries; extending a timeout must not hide a hang. Reorganize existing tests incrementally, starting with cases that obstruct development, rather than rewriting the whole suite at once.

Benchmark belongs to stage gates, not to every PR, and not every change needs a performance case; targeted measurements are required earlier for the risks in the selection table. Unit-suite wall time or headless throughput does not replace real-window interaction measurements. Workloads, environments and thresholds come from the current stage's [performance requirements](design/00-product-and-core-loop.md#本阶段性能目标); measured thresholds require user confirmation.

**Complete when:** the changed risks have sufficient applicable evidence, tests make valid claims about their boundaries, required failures/gaps are resolved or handled through the explicit scope/exception rules, and the reviewed revision's results and reused evidence are distinguishable.

## Godot MCP validation

Apply this sequence to Godot scene/resource edits, editor checks and runtime diagnosis. Documentation-only or pure domain tasks need no MCP run unless their Acceptance criteria require Godot behavior. Server startup, client approval, the pinned plugin commit and known limitations are in [godot-mcp.md](godot-mcp.md).

1. **Check your own connection** before the first Godot operation; another agent's connection does not prove yours. Start the MCP editor for the assigned worktree as godot-mcp.md describes, then call `get_project_info` in the actual session and verify that `project_path` is the assigned worktree and the engine version is expected. The client connects to a fixed port, not a worktree: if the path differs, another task's editor holds the port, so stop and report instead of operating on it. Record the plugin commit from godot-mcp.md, not the server's reported version. Reconnect or restart the session after changing its configuration or restarting the editor, and keep registration project-scoped.
2. **Prepare the project.** Build C# and import resources through the [local CLI workflow](local-environment.md) before runtime checks; import new assets/scripts before UID operations. MCP does not build the .NET project or replace required repository checks.
3. **Use the relevant tools.** Use project, scene, node and script queries for inspection. Edit scenes, nodes, properties or scripts only within the authorized scope, then inspect the resulting files and reload affected resources to verify the values. Test the MCP itself in an isolated disposable project. For runtime checks, call `run_project` with `allow_window=true`, then `get_editor_logs` with `source` set to `editor_panel`; confirm the log timestamps belong to this run and check the expected ready/behavior markers, errors and running or exit status. A start acknowledgement alone is not a pass. `run_project` saves open scenes first; revert unrelated scene or `.uid` changes. When editor interaction is needed, use the graphical editor and record what was actually observed.
4. **Preserve evidence and stop owned processes.** Save tool inputs, output/error logs, exit status and artifact checks in the task's validation directory before another run or the end of the session; a new run replaces the editor's current one. Never start or stop anything through an editor serving another worktree. Stop a task-owned game with `stop_project` (`allow_window=true`) and verify termination; close the task-owned MCP editor, identified by its `--path` and `--mcp-server` command line. MCP output is not evidence of visual quality or user acceptance.
5. **Handle unavailable or insufficient tools.** Report connection, approval, platform or tool failures, use the existing Godot CLI/scripts for checks they can equivalently perform, and record the fallback and its results. Keep uncovered Acceptance criteria unverified. Do not replace the configured plugin, patch `addons/godot_mcp/` or add a global registration to work around a failure.

**Complete when:** the applicable behavior has verified artifact/runtime evidence bound to the worktree and game head, required repository checks are accounted for, logs are retained, and task-owned processes are closed. Any remaining manual or unsupported check is explicitly pending.
