# Dispatch Standard Operating Procedure

Use this procedure as coordinator/reviewer for Issues in `odradekk/train`: prepare the worktree and Godot MCP editor, hand the task to the developer, review and verify acceptance when the developer asks, and carry out the user-approved closeout. The implementation itself is pair programming between the developer and an agent that the developer creates and runs under [implement-sop.md](implement-sop.md); the coordinator does not create, prompt or direct the agent. GitHub operations follow [issue-tracker.md](agents/issue-tracker.md).

Issue comments are the durable record of dispatch, design decisions made during implementation, and acceptance; the PR contains the code to review. Agent chat history does not replace either.

When the coordinator performs Godot scene, resource, editor or runtime work, including independent review checks, follow [Godot MCP validation](implement-sop.md#godot-mcp-validation). Its MCP use, fallback, evidence and process-ownership rules apply to the coordinator as well as the implementation agent.

## Roles and authority

- **Coordinator / reviewer**: prepares the worktree and Godot MCP editor for an Issue the developer selects, reviews the delivered PR and verifies Acceptance criteria when the developer asks, and requests the user's final approval.
- **Developer**: creates and runs the implementation agent in the prepared worktree and works with it in pair programming: discusses and decides the design, supervises the implementation, and decides when the agent commits, pushes, opens the PR and posts Issue comments. The developer asks the coordinator for acceptance and drives fixes for review findings.
- **Implementation agent**: works in its assigned worktree under the developer's direction.
- **User**: gives final approval for the specific reviewed PR revision and proposed version change. Only then may the coordinator merge the PR, reclaim the worktree, close the Issue, and update the version. The developer and the user may be the same person.

A dispatch under this SOP authorizes the implementation agent, at the developer's instruction, to commit, push its task branch, create or update the PR, and post Issue comments. It does not authorize merging, closing the Issue, force-pushing, or changing unrelated shared state. Acceptance by the reviewer is not the user's final approval.

## Selecting tasks

Dispatch only the Issues the developer names, one at a time unless the developer asks for several. An Issue whose specification depends on code or decisions that another open task will produce is blocked even without a recorded blocker; record that dependency on the Issue.

When several tasks run at once:

- Each Issue gets its own worktree, task branch and dispatch comment, and goes through the steps below on its own; the developer runs a separate agent in each worktree. User approval, merge and the version increase remain per PR.
- The MCP port is fixed, so at most one task can have an MCP editor. Give it to the task whose Acceptance criteria depend most on interactive editor or runtime observation. The other dispatch comments state that MCP belongs to another worktree and that the agent uses the CLI fallback.
- After one PR merges, tell the developer that the remaining task branches must merge the updated base (no rebase or force-push), resolve conflicts and rerun the affected checks. Review the merge and the affected behavior again before acceptance; an acceptance written before the update does not cover the new head.
- Parallel validation shares one machine. If a check times out while another task is validating, rerun it with no other validation running before reporting it as failed, and record both results.

## 0. Prepare the worktree and Godot MCP — coordinator

Before dispatch, verify:

- The Issue is open, labelled `ready-for-agent`, not superseded, and has no open native blockers.
- The specification and Acceptance criteria are clear enough to start. Note open design questions for the developer; they are settled in the pair session, not by the coordinator.
- The selected baseline contains the required code and docs and is available to the eventual PR base; local-only baseline commits must not leave the PR missing prerequisites.
- The target repository, PR base branch, task branch, and authenticated push/PR access are known. If configuration or permissions are missing, resolve them first; report any required user action.
- Required validation tools are located. Consult the checkout's `AGENTS.md` and [Local validation and Windows builds](local-environment.md); check the SDK, script runtime, and target-platform requirements.

Use an independent worktree from the repository's default base unless the developer selects another base or stacked work. Preserve uncommitted main-workspace changes; do not implicitly carry them into the task.

1. Check `git worktree list` and the existing branches first, so a resumed dispatch does not create a second worktree for the same Issue.
2. Fetch, then create the worktree on a new task branch from an explicit base, normally `origin/<default-branch>`: `git worktree add -b <branch> ../train-worktrees/<slug> origin/master`. Keep worktrees outside the main checkout so that Godot and other tools working in the main checkout do not scan them. Verify the actual branch and baseline SHA in the new checkout.
3. Run the repository setup checks in the new checkout, at least building `Train.csproj` and `Train.Tests/Train.Tests.csproj`, and wait for them to succeed.
4. For Godot work, verify that the baseline contains `addons/godot_mcp/`, enables the plugin in `project.godot`, and has the project MCP configuration for the clients described in [godot-mcp.md](godot-mcp.md). Check the Godot executable in the worktree environment. For the task that gets MCP, confirm that no other task's editor holds the port, start the worktree's MCP editor as godot-mcp.md describes, and call `get_project_info` to verify that `project_path` is this worktree. Start it before the developer creates the agent, because the client connects at session start. This editor is task-owned. If a client must trust the project before it loads the project MCP configuration, tell the developer.

**Complete when:** one valid Issue has one independent worktree with a self-contained baseline and successful setup checks, and any assigned MCP editor is running and verified against that worktree.

## 1. Record the dispatch and hand over — coordinator

Post a dispatch comment on the Issue recording the absolute worktree path, task branch, baseline SHA, PR base, setup checks, the MCP assignment (editor process IDs, port and verified `project_path`; or CLI fallback because another task has MCP; or not applicable), and open design questions for the developer. The agent reads this comment when it starts, and the identifiers are needed for acceptance and recovery.

Report the handoff to the developer with the worktree path, branch, MCP status and open questions. The developer creates the agent in that worktree and points it at the Issue and [implement-sop.md](implement-sop.md). Then stop. Do not create, prompt or monitor agents, or start a review until the developer asks for acceptance.

**Complete when:** the worktree and any assigned MCP editor are ready, the Issue has the dispatch record, and the developer has the handoff.

## 2. Review on the developer's request — coordinator / reviewer

Start only when the developer asks for acceptance, normally after the PR exists and the agent has posted its completion comment on the Issue.

Read the Issue body, the completion comment and all other comments, including design decisions recorded during the pair session. Inspect the PR's actual base/head and full relevant diff. Use `code-review` for Standards and Spec review; review test quality and independently run key risk-appropriate checks under [Testing standard](implement-sop.md#testing-standard). Reuse prior evidence only when it still applies to the reviewed revision. Judge scope against the Issue and the recorded decisions; a change outside both is a finding, even if it looks useful. Check the code against [Code comments](implement-sop.md#code-comments); missing required documentation and comments that contradict the behavior are findings.

For MCP-assisted changes, inspect the saved scene/resource diff and runtime evidence, not just tool success messages. Apply the same required test, real Godot, cross-process, export, and user visual-acceptance requirements as the Issue specifies; an MCP launch or log capture only proves the behavior it actually exercised.

If review fails:

1. Reply in an Issue comment identifying the PR/head SHA and each finding: severity, violated requirement, file/line or reproduction, observed versus expected result, and requested correction.
2. Tell the developer that findings are posted. The developer works through them with the agent in the same worktree, branch and PR, and asks for re-review when ready. Do not send instructions to the agent.
3. Re-review the new head and affected behavior when asked. Keep the Issue open and retain the worktree throughout this loop.

**Complete when:** no unresolved review findings or required validation gaps remain for the identified PR head.

## 3. Verify every Acceptance criterion — coordinator / reviewer

After the code review passes:

1. Read the current Issue body again. Map every Acceptance criterion to inspected implementation and applicable test/integration evidence.
2. Mark only satisfied criteria `[x]`; leave incomplete or unverified criteria `[ ]`. If a previously checked criterion no longer holds, clear it and explain why in a comment. Preserve the wording and unrelated Issue content; do not weaken requirements to obtain a pass.
3. If any criterion remains unmet, publish the gap, tell the developer, and return to step 2 when the developer asks for re-review. Scope changes require the user's decision.
4. Read back the saved Issue body to verify the actual checkbox state. Reconcile any concurrent edits before writing; do not overwrite newer discussion or requirements.
5. When every criterion passes, post the final acceptance conclusion in an Issue comment: PR URL/head SHA, per-criterion evidence, validation results, resolved review findings, and remaining nonblocking risks. State explicitly: **technical acceptance passed; awaiting user approval to merge**.

Present the PR, final acceptance comment, and proposed version change under step 5 to the user, then stop. Keep the PR unmerged, the worktree available, and the Issue open.

**Complete when:** every criterion is verified and checked, the saved state is confirmed, and the acceptance comment is bound to the reviewed head.

## 4. Final approval, merge, cleanup, close — coordinator

Proceed only after the user has reviewed the acceptance result and explicitly approved the specific PR for merge and closure.

1. Recheck that the PR head still matches the accepted revision, required checks pass, no unresolved review discussion remains, and the PR can merge. New commits require affected revalidation and renewed user approval. Preserve main-workspace changes.
2. Merge the PR using the repository's configured policy or the user's selected method. Verify the remote merged state and merge commit; a local branch merge is not a substitute.
3. Verify that the exact task worktree contains no unpreserved changes or unique commits. Account for squash/rebase merges when proving that work is retained. Ask the developer to end the agent sessions and close the editors that use the worktree; on Windows, an editor's helper processes, such as language servers, can keep the directory locked after the folder is closed. Stop the task-owned MCP editor, then remove the worktree with `git worktree remove <path>`. Verify with `git worktree list` that it is gone and that the directory no longer exists. Delete the task branch locally and remotely only when its head is contained in the base branch.
4. Add an Issue closure comment linking the merged PR, merge commit, and final acceptance comment, and report worktree cleanup. Close the Issue as completed.
5. Read back PR/Issue state and confirm worktree removal, then continue to step 5.

If merge fails, retain the worktree and Issue. If cleanup fails after merge, report that state and keep the Issue open until cleanup is resolved; do not rerun the merge or discard unpreserved work.

Before removing a worktree, retain required MCP logs and generated validation artifacts outside it, including evidence under ignored `.godot/validation/`, and record the retained location. Close only the task-owned Godot processes and editor windows; preserve unrelated sessions.

**Complete when:** the approved PR is remotely merged, the worktree is reclaimed without losing work, and the Issue is confirmed closed with its acceptance record intact.

## 5. Update the version — coordinator

After the PR is merged, its worktree reclaimed, and the Issue closed, update the version on the merged base branch. This is the final step of the task, not part of the task's PR.

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

After interruption, read the latest Issue comments (including the dispatch record), PR head/checks, `git worktree list`, the task worktree's Git state, whether the task-owned MCP editor is still running, and any recorded version commit before acting. Verify the actual checkout and branch.

Resume the first incomplete step instead of recreating worktrees, opening duplicate PRs, repeating completed merges, or incrementing a version twice. If the assigned MCP editor has stopped while the task is active, restart it for the same worktree, verify `project_path` again, and tell the developer that the agent session must reconnect.

A newly discovered blocker, scope change, or changed PR head returns the task to the relevant earlier step. Never treat a stale acceptance comment as approval for unreviewed code.
