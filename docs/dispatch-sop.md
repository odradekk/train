# Dispatch Standard Operating Procedure

Use this procedure as the coordinator of the full Demo in `odradekk/train`. The coordinator schedules Issues, starts one agent pair per Issue in Paseo, merges accepted work, tracks progress and organizes stage acceptance. It does not implement, review or test. Each pair works under [review-sop.md](review-sop.md) (reviewer) and [implement-sop.md](implement-sop.md) (worker). GitHub operations follow [issue-tracker.md](agents/issue-tracker.md).

Issue comments are the durable record of dispatch, decisions, review findings and acceptance. Paseo workspaces show which pairs are active. Agent chat history replaces neither.

## Roles and authority

| Role | Paseo agent | Responsibility |
| --- | --- | --- |
| Coordinator | Resident Claude agent with a heartbeat | Selects Issues, creates the workspace and the reviewer, assigns Godot MCP, merges, archives, closes Issues, keeps the progress record, organizes stage acceptance |
| Reviewer | `claude/claude-opus-5-5`, thinking `xhigh`, mode `bypassPermissions` | Creates and directs the worker, reviews the plan and the delivery, verifies Acceptance criteria, reports the result to the coordinator |
| Worker | `codex/gpt-6.1-sol`, thinking `xhigh`, mode `full-access`, Fast speed (`service_tier: priority`) | Implements, tests, commits, pushes the task branch, opens the PR, fixes review findings |
| User | — | Plays each stage build and confirms the stage; answers decisions the docs cannot settle; approves scope or Acceptance-criteria changes |

The user has given the coordinator standing authority to merge reviewer-accepted PRs of current-stage Issues after the checks in [step 3](#3-merge-and-close--coordinator), including updating an accepted PR branch with master when it has no conflict, close those Issues or label them `awaiting-user`, archive their workspaces, delete merged task branches, file current-stage fix Issues, and record the version change after a stage passes. This does not authorize closing a stage acceptance Issue without the user's confirmation, changing scope or Acceptance criteria, force-pushing, changing branch protection, or starting the next stage early.

Art Issues go through the same pairs; their production and inspection rules are in [art 08](art/08-asset-pipeline-and-review.md).

## Selecting tasks

The current stage is the lowest-numbered open [stage acceptance Issue](#stage-integration). A candidate Issue:

- blocks the current stage acceptance Issue, is open and labelled `ready-for-agent`, and is not labelled `needs-info`;
- has every native blocker closed or labelled `awaiting-user`;
- has no active Paseo workspace.

**Modules and slots.** The module is the `责任` field in the Issue body: 核心, 内容 (including 内容数据), 体素, Godot or 美术; `工具／X` counts as X and `Godot／美术设计` counts as 美术. At most five pairs run at once. Each module normally has at most one active pair. When a module has no candidate, its slot may go to a module that has more candidates. Running pairs are never stopped to return a slot; when a slot frees, a module with candidates and no active pair gets it first. Within a module, prefer the candidate that unblocks the most open current-stage Issues, then the lowest number. Do not run two pairs of the same module on Issues that depend on each other or change the same contract.

**Shared resources.**

- Each pair that needs Godot or Blender gets its own ports: the pair in slot k (1–5) uses Godot MCP port `9080 + k` and Blender MCP port `9876 + k`. The defaults 9080 and 9876 stay free for the main checkout. At most five editors can run games at once ([godot-mcp.md](godot-mcp.md)).
- Changes to `Contracts/` follow the [controlled contract change procedure](contracts/conventions.md#契约基线与受控变更) one at a time.
- Parallel validation shares one machine. A check that times out while other pairs are validating is rerun alone before it counts as failed; reviewers and workers record both results.

## 0. Dispatch — coordinator

1. Verify the candidate's Acceptance criteria are clear enough to start. If not, comment the gap, label it `needs-info`, and pick another candidate.
2. Create the workspace with Paseo `create_workspace`: `isolation: worktree`, `mode: branch-off`, `branchName: issue-<n>-<slug>`, `baseBranch: origin/master`, `title: #<n> <module>`. Verify the branch and baseline SHA in the checkout. If the branch tracks `origin/master`, run `git branch --unset-upstream` so a bare `git push` cannot target master.
3. If the Issue needs Godot, start the worktree's editor on its port and verify that `get_project_info` returns this worktree as `project_path` ([godot-mcp.md](godot-mcp.md)). If it needs Blender, start a Blender instance on its port ([blender-mcp.md](blender-mcp.md)). Point the worktree's `.mcp.json` and `.codex/config.toml` at these ports and run `git update-index --skip-worktree .mcp.json .codex/config.toml`. Do this before creating the reviewer, because clients connect at session start.
4. Post the dispatch comment: worktree path, task branch, baseline SHA, PR base `master`, module, stage acceptance Issue, contract baseline, MCP ports (Godot with its verified path, Blender) or not applicable, the Paseo workspace ID, and your own Paseo agent ID for the reviewer's report.
5. Create the reviewer with `create_agent` in that workspace (`provider: claude/claude-opus-5-5`, `settings: { modeId: bypassPermissions, thinkingOptionId: xhigh }`, title `review #<n>`) and this initial prompt:

   > You are the reviewer for Issue #\<n\> in this worktree. Follow docs/review-sop.md. The dispatch comment on the Issue has your worktree, branch, MCP assignment and stage. Create the worker, run the plan and delivery reviews, end your final message with a `PAIR-RESULT:` line, and send that line to the coordinator agent named in the dispatch comment.

6. Add the Paseo workspace and reviewer agent IDs to the dispatch comment and update the [progress record](#progress-record).

**Complete when:** the Issue has a workspace, a dispatch comment and a running reviewer.

## 1. While the pair works — coordinator

Do not prompt the worker or join the review. Act only on:

- **A `PAIR-RESULT:` line from a reviewer.** The reviewer sends it to you with `send_agent_prompt`; finish notifications are not enough, because Paseo notifies you only about turns you started. `accepted` goes to [step 3](#3-merge-and-close--coordinator); `blocked` goes to [step 2](#2-blocked-pairs--coordinator). Notifications without that line are intermediate turns; ignore them.
- **The heartbeat.** Create one Paseo heartbeat for yourself, about every 30 minutes. On each heartbeat:
  - A reviewer that stopped with a model-capacity or transient error gets a short continue prompt on the same model.
  - A pair where both agents are idle and no `PAIR-RESULT:` arrived gets a prompt asking the reviewer to continue or report.
  - Fill free slots under [Selecting tasks](#selecting-tasks).
  - Retry any pending workspace cleanup.
  - Check that the nightly runner `box-linux-nightly` is online (`gh api repos/odradekk/train/actions/runners`) and that the latest nightly run is under 36 hours old. Its host has no service manager, so a reboot stops the runner; tell the user when it is offline.
  - Check whether the stage is ready for [integration](#stage-integration).

## 2. Blocked pairs — coordinator

Read the reviewer's blocking comment on the Issue, then:

| Reason | Action |
| --- | --- |
| A decision the docs cannot settle, or a scope or Acceptance-criteria change | Label `needs-info`, add the question to the progress record and tell the user. The pair stays in its workspace but releases its slot. When the user answers, record the decision in an Issue comment, then send it to the reviewer once a slot is free; it takes priority over new candidates of that module. |
| Review-round limit reached | If the reviewer says the Issue is too large, split it into smaller Issues with native dependencies and archive the pair. Otherwise archive both agents and start a fresh reviewer in the same workspace once. A second failure goes to the user. |
| Missing prerequisite or Issue defect | Record the dependency or the defect on the Issue, label `needs-info`, release the slot. |

## 3. Merge and close — coordinator

After an `accepted` result:

1. Check that the PR head equals the accepted SHA, the reviewer's acceptance comment names that SHA, and all Acceptance criteria are checked.
2. Check that the head contains the current `origin/master` (`git merge-base --is-ancestor origin/master <head>`). If master has moved and GitHub reports the PR as mergeable, run `gh pr update-branch <pr>`: GitHub merges master into the task branch without a rebase. The acceptance carries over to the new head because the update adds only master's commits. If the update conflicts, or the Windows checks fail on the new head, ask the reviewer to have the worker merge master, fix and accept again; wait for a new `PAIR-RESULT:`.
3. Wait for the Windows checks on the head you are about to merge, which is the accepted SHA or your update merge. Merge with `gh pr merge <pr> --merge --match-head-commit <head>` and verify the remote merged state and merge commit.
4. Stop the task's Godot editor and Blender instance if it has them. Run `archive_workspace`, then check `git worktree list` and that the directory is gone. If Windows locks keep the directory, record it and retry on the next heartbeat; do not block other work. Delete the remote task branch once its head is in master.
5. If the acceptance comment lists criteria waiting for the user, label the Issue `awaiting-user`, keep it open, and add those criteria to the progress record; for an art Issue, also tell the user where its screenshots are. Otherwise post a closure comment linking the PR, merge commit and acceptance comment, and close the Issue as completed.
6. Update the progress record and fill the freed slot.

Tasks do not change the version; see [Stage integration](#stage-integration).

## Progress record

Keep one progress comment on the current stage acceptance Issue and edit it in place after each dispatch, merge or block. It lists, with links: Issues in progress (module, workspace, review round when known), merged Issues, `awaiting-user` Issues with the criteria the user must confirm, blocked Issues with reasons, questions waiting for the user, and the next candidates per module. When a stage closes, start a new comment on the next stage's Issue.

## Stage integration

The full Demo uses the six [development stages](design/00-product-and-core-loop.md#开发阶段) confirmed in [the staged delivery decision](https://github.com/odradekk/train/issues/293). Every development Issue of the next stage has the preceding stage's acceptance Issue as a native blocker, so it cannot become a candidate until that stage passes. Accepted tasks merge into master; there are no module or stage integration branches. Merging every task does not establish stage acceptance. Required evidence follows [stage acceptance](contracts/testing.md#阶段性联调的验收).

1. **Start.** When every other native blocker of the stage acceptance Issue is closed or labelled `awaiting-user`, dispatch the stage acceptance Issue itself as a pair, with a workspace on the current `origin/master` commit. The worker runs the stage's complete regression, real four-module paths, real Godot, Windows export and post-export run on that commit, writes the playtest checklist, and copies the export to `D:\Code\train-builds\stage-<k>-<sha>\`. The reviewer verifies the evidence and checks the technical criteria. A failure becomes a `blocked` result listing the failing paths.
2. **Fix failures.** For each failure, file a current-stage fix Issue in the owning module, make it a native blocker of the stage acceptance Issue, and dispatch it normally. After the fixes merge, run step 1 again on the new master commit.
3. **Hand to the user.** Give the user the build path, tested commit, playtest checklist, evidence comment and known gaps. The checklist includes every criterion of the stage's `awaiting-user` Issues. Keep the stage workspace until the stage passes.
4. **Playtest.** Problems the user reports become fix Issues as in step 2, followed by step 1 again. A problem with an `awaiting-user` Issue is recorded on that Issue, which loses the label and is dispatched again.
5. **Pass.** Only the user's confirmation that the stage passed lets the coordinator check the user-confirmed criteria of the `awaiting-user` Issues and close them, check the stage's user-confirmation criterion, close the stage acceptance Issue, archive the stage workspace and update the version. The next stage's Issues then become candidates.

### Version

Each passed stage increases MINOR by 1 and resets PATCH to 0 (`0.0.56 → 0.1.0`). Tasks and fixes do not change the version; setting `1.0.0` requires the user's explicit decision. Update `project.godot` `config/version`, `export_presets.cfg` `application/product_version` and `application/file_version` (`X.Y.Z.0`), and the current-version line in `AGENTS.md`. Commit as `chore: bump version to X.Y.Z` on master, push, and record the old and new version and the commit on the closed stage Issue.

## Recovery

After an interruption, read the progress record, open Issues with dispatch comments, Paseo `list_workspaces` and `list_agents`, `git worktree list`, open PRs, and running Godot editors and Blender instances. Resume each pair at its first incomplete step: send a continue prompt to an idle reviewer, rather than creating a second workspace, PR or merge. Restart a stopped Godot editor or Blender instance on the same port and tell the reviewer that the worker must reconnect. A PR head that changed after acceptance needs a new `accepted` result before merging, unless the only change is your own `gh pr update-branch` merge from step 3.
