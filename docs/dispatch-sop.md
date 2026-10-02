# Dispatch Standard Operating Procedure

Use this procedure as coordinator/reviewer for Issue dispatch, review, acceptance and user-approved closeout in `odradekk/train`. Implementation agents follow [implement-sop.md](implement-sop.md); GitHub operations follow [issue-tracker.md](agents/issue-tracker.md).

Dispatch implementation work through **Paseo**. Use the `paseo` skill and available Paseo MCP tools; the Paseo CLI is an alternative interface. If Paseo is unavailable, report the blocker rather than switching launchers or worktree managers.

Issue comments are the durable record of dispatch, implementation and acceptance; the PR contains the code to review. Agent notifications and workspace titles do not replace either.

When the coordinator performs Godot scene, resource, editor or runtime work, including independent review checks, follow [Godot MCP validation](implement-sop.md#godot-mcp-validation). Its MCP use, fallback, evidence and process-ownership rules apply to the coordinator as well as the implementation agent.

## Roles and authority

- **Coordinator / reviewer**: prepares and dispatches the task, reviews the delivered PR, records findings, verifies Acceptance criteria, and requests the user's final approval.
- **Implementation agent**: works in its assigned independent worktree, validates and commits its changes, pushes its task branch, opens or updates the PR, and reports in the Issue comments.
- **User**: gives final approval for the specific reviewed PR revision and proposed version change. Only then may the coordinator merge the PR, reclaim the worktree, close the Issue, and update the version.

A dispatch under this SOP authorizes the implementation agent's task-branch push, PR creation/update, and Issue completion/revision comments. It does not authorize merging, closing the Issue, force-pushing, or changing unrelated shared state. Acceptance by the reviewer is not the user's final approval.

## Parallel dispatch

When several ready Issues have no open blockers, dispatch them in parallel by default; dispatch one at a time only when the user asks. An Issue whose specification depends on code or decisions that another open task will produce is blocked even without a recorded blocker; record that dependency on the Issue. Overlapping files alone are not a reason to wait.

- Each Issue gets its own Paseo workspace, worktree, task branch, agent and dispatch comment, and goes through steps 0–5 on its own. Review each task when its agent finishes. User approval, merge and the version increase remain per PR.
- The MCP port is fixed, so at most one parallel task can have an MCP editor. Give it to the task whose Acceptance criteria depend most on interactive editor or runtime observation. The other briefs state that MCP belongs to another worktree and that the worker uses the CLI fallback; their required evidence still comes from repository checks and real-Godot scripts.
- After one PR merges, ask each remaining worker to merge the updated base into its task branch (no rebase or force-push), resolve conflicts and rerun the affected checks. Review the merge and the affected behavior again before acceptance; an acceptance written before the update does not cover the new head.
- Parallel validation shares one machine. If a check times out while another task is validating, rerun it with no other validation running before reporting it as failed, and record both results.

## 0. Prepare an independent worktree — coordinator

Before dispatch, verify:

- The Issue is open, labelled `ready-for-agent`, not superseded, and has no open native blockers.
- The specification and Acceptance criteria are executable without unresolved design choices.
- The selected baseline contains the required code and docs and is available to the eventual PR base; local-only baseline commits must not leave the PR missing prerequisites.
- The target repository, PR base branch, task branch, and authenticated push/PR access are known. If configuration or permissions are missing, resolve them before promising a PR; report any required user action.
- The requested Paseo provider is available and authenticated, setup succeeds, and required validation tools are located. Consult the checkout's `AGENTS.md` and [Local validation and Windows builds](local-environment.md); check the SDK, script runtime, and target-platform requirements before dispatch.

Use an independent top-level worktree from the repository's default base unless the user selects another base or stacked work. Preserve uncommitted main-workspace changes; do not implicitly carry them into the task.

Prepare it through Paseo:

1. Inspect existing workspaces and agents before creating anything, so a resumed dispatch does not create a second worker for the same Issue.
2. Read `list_profiles`, including profile notes. Honor the user's provider and model choices and select an applicable profile. If no profile fits, use `list_providers`, `list_models`, and `inspect_provider` to choose supported settings and report the selection. This SOP does not prescribe a particular model. Record the actual provider, model, mode, and thinking settings used.
3. Use `create_workspace` with `isolation: "worktree"`, `mode: "branch-off"`, the verified project/source checkout, a task branch name, and an explicit `baseBranch` resolving to the repository's default base, normally `origin/<default-branch>`. Copy the returned `workspaceId` and absolute `cwd`; verify the actual branch and baseline SHA in that checkout.
4. Inspect configured setup scripts with `list_workspace_scripts` and run applicable ones with `start_workspace_script`. Run any remaining repository setup and validation checks in the returned checkout. Wait for setup to succeed before sending implementation work; workspace creation alone does not prove setup passed.
5. For Godot work, verify that the selected baseline contains `addons/godot_mcp/`, enables the plugin in `project.godot`, and has the selected client's project MCP configuration as described in [godot-mcp.md](godot-mcp.md). Check the Godot executable in the worktree environment. The MCP port is fixed, so only one worktree can use it at a time (see [Parallel dispatch](#parallel-dispatch)): for the task that gets MCP, confirm no other task's editor holds it, then start the worktree's MCP editor before launching the worker, because the worker's client connects at session start. This editor is task-owned. Include the worker's MCP connection check and any known setup gap in the brief; the coordinator's own connection does not prove that a newly launched worker can access it.

**Complete when:** one valid Issue has one Paseo workspace owning an independent worktree, a self-contained baseline, successful setup, and a verified agent launch configuration.

## 1. Dispatch the implementation brief — coordinator

The brief must begin exactly with `/implement `: one space after the command, with no preceding text. Use the template below and fill every placeholder. The worker follows [Implementation SOP](implement-sop.md) and reports using its [completion brief](implement-sop.md#completion-brief--issue-comment).

Include the Issue URL, worktree path, task branch, baseline SHA, PR base branch, scope boundaries, repository instructions, required validation, PR delivery, and Issue-comment requirements. Select validation under [Testing standard](implement-sop.md#testing-standard) and explain its relationship to the changed behavior and Acceptance criteria. Provide verified local tool paths when needed.

For a new worker, call Paseo `create_agent` with the prepared `workspaceId`, a descriptive title, the selected `provider/model`, applicable `settings`, and the complete brief as `initialPrompt`. Set `notifyOnFinish: true`. Supplying the brief here starts the task; do not send it again with `send_agent_prompt`.

For an existing worker, first verify its `agentId`, workspace, and current task. Send a new or revision brief once with `send_agent_prompt`, using `background: true` and `notifyOnFinish: true`. Do not inject implementation briefs through a shell terminal when a Paseo agent session is available.

Confirm that the agent received the intended brief and actually started task work, using its acknowledgement and task-specific activity from `get_agent_activity`. A successful create/send response or `running` status alone is insufficient. Keep this check bounded; if startup fails or remains unconfirmed, report that state and inspect the existing session rather than sending a duplicate brief.

For Godot work, the worker checks MCP access in its own session before the first Godot operation. Record the result, or the explicit CLI fallback and reason, with the startup evidence under [Godot MCP validation](implement-sop.md#godot-mcp-validation).

Record the Issue, `workspaceId`, `agentId`, absolute worktree path, task branch, baseline SHA, PR base, launch settings, and startup evidence in a dispatch comment on the Issue. Report the handoff to the user. These identifiers are needed for revisions and recovery; a terminal ID is supplementary when a setup terminal is involved.

After startup is confirmed, stop monitoring by default. Use Paseo completion/error/permission notifications instead of continuous status polling. A notification does not constitute technical acceptance or user approval; resume acceptance when the user requests it or an already authorized review workflow applies.

**Complete when:** the exact brief was delivered once through Paseo, the assigned agent has started task work, and the Issue and user have the dispatch record.

### Implementation brief template

```text
/implement GitHub Issue #<issue>: <title> — <issue-url>

You own the independent worktree <absolute-path> and task branch <branch>.
Baseline: <sha>. Target repository: odradekk/train. PR base: <base-branch>.
Work only in this worktree; preserve unrelated changes and keep archive/mvp read-only.

Read docs/implement-sop.md, the full Issue body/comments, AGENTS.md,
and relevant domain docs/ADRs.
Implement only #<issue>; exclude <adjacent-scope>.
Required validation: <risk-based selection of tests/build/integration checks,
Acceptance-criteria coverage, verified tool paths; see Testing standard>.
Godot MCP: <applicable work and whether the worktree's MCP editor is running,
assigned to another parallel task (use the CLI fallback), or not applicable>.
For Godot work, read docs/godot-mcp.md and follow the Godot MCP validation
section of docs/implement-sop.md. Verify access in your own session and check
that get_project_info returns your assigned worktree. Record any CLI fallback and reason.
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

## 2. Review and return findings — coordinator / reviewer

Read the Issue completion brief and all subsequent discussion, then inspect the PR's actual base/head and full relevant diff. Use `code-review` for Standards and Spec review; review test quality and independently run key risk-appropriate checks under [Testing standard](implement-sop.md#testing-standard). Reuse prior evidence only when it still applies to the reviewed revision.

For MCP-assisted changes, inspect the saved scene/resource diff and runtime evidence, not just tool success messages. Apply the same required test, real Godot, cross-process, export, and user visual-acceptance gates as the Issue specifies; an MCP launch or log capture only proves the behavior it actually exercised.

If review fails:

1. Reply in an Issue comment identifying the PR/head SHA and each finding: severity, violated requirement, file/line or reproduction, observed versus expected result, and requested correction.
2. Send the same Paseo implementation agent a `/implement ` revision brief linking that comment through `send_agent_prompt`, with `background: true` and `notifyOnFinish: true`. Retain the same workspace, branch, and PR. The agent message supplements, rather than replaces, the Issue record; if the session is unavailable, follow Recovery before creating a replacement.
3. The agent fixes the same PR, reruns affected checks, and posts a new Issue comment responding to each finding with commit IDs and validation results.
4. Re-review the new head and affected behavior. Keep the Issue open and retain the worktree throughout this loop.

**Complete when:** no unresolved review findings or required validation gaps remain for the identified PR head.

## 3. Verify every Acceptance criterion — coordinator / reviewer

After the code review passes:

1. Read the current Issue body again. Map every Acceptance criterion to inspected implementation and applicable test/integration evidence.
2. Mark only satisfied criteria `[x]`; leave incomplete or unverified criteria `[ ]`. If a previously checked criterion no longer holds, clear it and explain why in a comment. Preserve the wording and unrelated Issue content; do not weaken requirements to obtain a pass.
3. If any criterion remains unmet, publish the gap and return to step 2. Scope changes require the user's decision.
4. Read back the saved Issue body to verify the actual checkbox state. Reconcile any concurrent edits before writing; do not overwrite newer discussion or requirements.
5. When every criterion passes, post the final acceptance conclusion in an Issue comment: PR URL/head SHA, per-criterion evidence, validation results, resolved review findings, and remaining nonblocking risks. State explicitly: **technical acceptance passed; awaiting user approval to merge**.

Present the PR, final acceptance comment, and proposed version change under step 5 to the user, then stop. Keep the PR unmerged, the worktree available, and the Issue open.

**Complete when:** every criterion is verified and checked, the saved state is confirmed, and the acceptance comment is bound to the reviewed head.

## 4. Final approval, merge, cleanup, close — coordinator

Proceed only after the user has reviewed the acceptance result and explicitly approved the specific PR for merge and closure.

1. Recheck that the PR head still matches the accepted revision, required checks pass, no unresolved review discussion remains, and the PR can merge. New commits require affected revalidation and renewed user approval. Preserve main-workspace changes.
2. Merge the PR using the repository's configured policy or the user's selected method. Verify the remote merged state and merge commit; a local branch merge is not a substitute.
3. Verify that the exact task worktree contains no unpreserved changes or unique commits. Account for squash/rebase merges when proving that work is retained. Archive the task's Paseo workspace with `archive_workspace` only after confirming it owns no unrelated active work; this archives its agents and terminals and removes a Paseo-owned worktree when its final active workspace reference is gone. Verify the actual workspace and Git worktree state, and remove the task branch only when safe. Do not equate archiving an agent with reclaiming its workspace, or assume a successful archive removed a worktree still referenced by another workspace.
4. Add an Issue closure comment linking the merged PR, merge commit, and final acceptance comment, and report worktree cleanup. Close the Issue as completed.
5. Read back PR/Issue state and confirm worktree removal, then continue to step 5.

If merge fails, retain the worktree and Issue. If cleanup fails after merge, report that state and keep the Issue open until cleanup is resolved; do not rerun the merge or discard unpreserved work.

Before archiving a worktree, retain required MCP logs and generated validation artifacts outside it, including evidence under ignored `.godot/validation/`, and record the retained location. Close only the task-owned Godot processes and editor windows; preserve unrelated sessions.

**Complete when:** the approved PR is remotely merged, the worktree is reclaimed without losing work, and the Issue is confirmed closed with its acceptance record intact.

## 5. Update the version — coordinator

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

## Recovery

After interruption, read the latest Issue comments, PR head/checks, worktree state, and any recorded version commit before acting. Resolve the recorded Paseo `workspaceId` and `agentId` against `list_workspaces`, `list_agents`, and the relevant agent status/activity. Verify the actual checkout and branch; neither a workspace title nor an old `running` status proves that the intended worker is still active.

Resume the first incomplete step instead of recreating worktrees, resending accepted briefs, opening duplicate PRs, repeating completed merges, or incrementing a version twice. For an ambiguous create/send failure, inspect existing Paseo sessions and activity before retrying. If the worker must be replaced, first confirm the old worker has stopped, preserve its changes, reuse the task workspace and branch where possible, and record the replacement agent ID and handoff in the Issue. Preserve the previous launch settings unless the user requests a change or they are no longer available; report any necessary change.

A newly discovered blocker, scope change, or changed PR head returns the task to the relevant earlier step. Never treat a stale acceptance comment as approval for unreviewed code.
