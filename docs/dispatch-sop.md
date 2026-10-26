# Dispatch Standard Operating Procedure

Use this procedure as the coordinator of `odradekk/train`. The coordinator schedules Issues, starts one agent pair per Issue in Paseo, merges accepted work and keeps the progress record. It does not implement, review or test. Each pair works under [review-sop.md](review-sop.md) (reviewer) and [implement-sop.md](implement-sop.md) (worker). Tracker, labels and decision notes follow the `## Agent workflow` section of `AGENTS.md`.

Issue comments are the durable record of dispatch, decisions, review findings and acceptance. Paseo workspaces show which pairs are active. Agent chat history replaces neither.

## Roles and authority

| Role | Paseo agent | Responsibility |
| --- | --- | --- |
| Coordinator | Resident Claude agent with a heartbeat | Selects Issues, creates the workspace and the reviewer, assigns MCP ports, schedules quiet windows and Linux runner jobs, merges, archives, closes Issues, keeps the progress record, takes questions and conflicts with decision notes to the user |
| Reviewer | `claude/claude-opus-5-5`, thinking `xhigh`, mode `bypassPermissions` | Creates and directs the worker, reviews the plan and the delivery, verifies Acceptance criteria, reports the result to the coordinator |
| Worker | Chosen by the reviewer under [review-sop §1](review-sop.md#1-create-the-worker): `codex/gpt-6.1-sol` (full access, `xhigh`, no Fast); DeepSeek v4.1 Flash or Grok 4.7 only when GPT is unavailable; never Opus | Carries out the Issue with the `implement` skill after plan approval, commits, pushes the task branch, opens the PR, fixes review findings |
| User | — | Accepts decision notes and approves the tickets split from them; answers decisions the docs and notes cannot settle; approves scope or Acceptance-criteria changes; confirms criteria left for the user |

The user has given the coordinator standing authority to merge reviewer-accepted PRs after the checks in [step 3](#3-merge-and-close--coordinator), including updating an accepted PR branch with master when it has no conflict, close those Issues or label them `awaiting-user`, archive their workspaces, and delete merged task branches. This does not authorize changing scope or Acceptance criteria, editing decision notes, force-pushing or changing branch protection.

Art Issues go through the same pairs; their production and inspection rules are in [art 08](art/08-asset-pipeline-and-review.md).

## Selecting tasks

A candidate Issue:

- is an open ticket split from an accepted decision note (its first line is `Decision note: <ID>`), or a bug ticket the user asked for. The coordinator drafts a note's tickets with the `note-to-tickets` skill and publishes them after the user approves the drafts;
- has no question waiting for the user in the [progress record](#progress-record);
- has every native blocker closed or labelled `awaiting-user`;
- has no active Paseo workspace.

Issue status lives in the progress record; `awaiting-user` is the only status label.

**Modules and slots.** At most six pairs run at once. Each module (Core, Content, Scenes, Godot host, art) normally has at most one active pair. The module is the project most of the Issue's changes go into; older Issues name it in their `责任` field. When a module has no candidate, its slot may go to a module that has more candidates. Running pairs are never stopped to return a slot; when a slot frees, a module with candidates and no active pair gets it first. Within a module, prefer the candidate that unblocks the most open Issues, then the lowest number. Do not run two pairs of the same module on Issues that depend on each other or change the same contract.

**Shared resources.**

- Each pair that needs Godot or Blender gets its own ports: the pair in slot k (1–6) uses Godot MCP port `9080 + k` and Blender MCP port `9876 + k`. The defaults 9080 and 9876 stay free for the main checkout. At most five editors can run games at once ([godot-mcp.md](godot-mcp.md)).
- Changes to `Contracts/` follow the [controlled contract change procedure](contracts/conventions.md#契约基线与受控变更) one at a time.
- Parallel validation shares one machine. A check that times out while other pairs are validating is rerun alone before it counts as failed; reviewers and workers record both results.
- Performance measurement follows [Performance measurement](implement-sop.md#performance-measurement). Reviewers announce each job on the Linux runner `lan-linux-nightly`; let one run at a time and tell the others to wait. Schedule local quiet windows yourself: tell every reviewer the UTC start and end and whether the window is strict, and run one measurement task per window.

## 0. Dispatch — coordinator

1. Verify the candidate's Acceptance criteria are clear enough to start. If not, comment the gap, add the question to the progress record, and pick another candidate.
2. Create the workspace with Paseo `create_workspace`: `isolation: worktree`, `mode: branch-off`, `branchName: issue-<n>-<slug>`, `baseBranch: origin/master`, `title: #<n> <module>`. Verify the branch and baseline SHA in the checkout. If the branch tracks `origin/master`, run `git branch --unset-upstream` so a bare `git push` cannot target master.
3. If the Issue needs Godot, start the worktree's editor on its port and verify that `get_project_info` returns this worktree as `project_path` ([godot-mcp.md](godot-mcp.md)). If it needs Blender, start a Blender instance on its port ([blender-mcp.md](blender-mcp.md)). Point the worktree's `.mcp.json`, `.codex/config.toml` and `.cursor/mcp.json` at these ports and run `git update-index --skip-worktree .mcp.json .codex/config.toml .cursor/mcp.json`. Cursor loads a project MCP server only after approval, so also run `cursor-agent mcp enable godot` in the worktree. CodeBuddy also needs approval: write `.codebuddy/settings.local.json` containing `{"enabledMcpjsonServers": ["godot"]}` (the path is listed in the shared `.git/info/exclude`, so it never shows as untracked). Do this before creating the reviewer, because clients connect at session start.
4. Post the dispatch comment: worktree path, task branch, baseline SHA, PR base `master`, module, decision note ID when the Issue has one, contract baseline, MCP ports (Godot with its verified path, Blender) or not applicable, the Paseo workspace ID, and your own Paseo agent ID for the reviewer's report.
5. Create the reviewer with `create_agent` in that workspace (`provider: claude/claude-opus-5-5`, `settings: { modeId: bypassPermissions, thinkingOptionId: xhigh }`, title `review #<n>`) and this initial prompt:

   > You are the reviewer for Issue #\<n\> in this worktree. Follow docs/review-sop.md. The dispatch comment on the Issue has your worktree, branch and MCP assignment. Create the worker, run the plan and delivery reviews, end your final message with a `PAIR-RESULT:` line, and send that line to the coordinator agent named in the dispatch comment.

6. Add the Paseo workspace and reviewer agent IDs to the dispatch comment and update the [progress record](#progress-record).

**Complete when:** the Issue has a workspace, a dispatch comment and a running reviewer.

## 1. While the pair works — coordinator

Do not prompt the worker or join the review. Act only on:

- **A `PAIR-RESULT:` line from a reviewer.** The reviewer sends it to you with `send_agent_prompt`; finish notifications are not enough, because Paseo notifies you only about turns you started. `accepted` goes to [step 3](#3-merge-and-close--coordinator); `blocked` goes to [step 2](#2-blocked-pairs--coordinator). Notifications without that line are intermediate turns; ignore them.
- **A Linux runner job announced by a reviewer** (see Shared resources).
- **The heartbeat.** Create one Paseo heartbeat for yourself, about every 30 minutes. On each heartbeat:
  - A reviewer that stopped with a model-capacity or transient error gets a short continue prompt on the same model.
  - A pair where both agents are idle and no `PAIR-RESULT:` arrived gets a prompt asking the reviewer to continue or report.
  - Fill free slots under [Selecting tasks](#selecting-tasks).
  - Retry any pending workspace cleanup.
  - Check that the runner `lan-linux-nightly` is online (`gh api repos/odradekk/train/actions/runners`) and that the latest nightly run is under 36 hours old. The runner runs as a systemd service; when it is offline, ask the user to check the runner service.

## 2. Blocked pairs — coordinator

Read the reviewer's blocking comment on the Issue, then:

| Reason | Action |
| --- | --- |
| A decision the docs and notes cannot settle, a conflict with an accepted decision note, or a scope or Acceptance-criteria change | Add the question to the progress record and tell the user; plan a change to an accepted decision with the user through the `decision-notes` skill. The pair stays in its workspace but releases its slot. When the user answers, record the decision in an Issue comment, then send it to the reviewer once a slot is free; it takes priority over new candidates of that module. |
| Review-round limit reached | If the reviewer says the Issue is too large, split it into smaller Issues with native dependencies and archive the pair. Otherwise archive both agents and start a fresh reviewer in the same workspace once. A second failure goes to the user. |
| Missing prerequisite or Issue defect | Record the dependency or the defect on the Issue and release the slot. |

## 3. Merge and close — coordinator

After an `accepted` result:

1. Check that the PR head equals the accepted SHA, the reviewer's acceptance comment names that SHA, and all Acceptance criteria not left for the user are checked.
2. Check that the head contains the current `origin/master` (`git merge-base --is-ancestor origin/master <head>`). If master has moved and GitHub reports the PR as mergeable, run `gh pr update-branch <pr>`: GitHub merges master into the task branch without a rebase. The acceptance carries over to the new head because the update adds only master's commits. If the update conflicts, or the Windows checks fail on the new head, ask the reviewer to have the worker merge master, fix and accept again; wait for a new `PAIR-RESULT:`.
3. Wait for the Windows checks on the head you are about to merge, which is the accepted SHA or your update merge. Merge with `gh pr merge <pr> --merge --match-head-commit <head>` and verify the remote merged state and merge commit.
4. Stop the task's Godot editor and Blender instance if it has them. Run `archive_workspace`, then check `git worktree list` and that the directory is gone. If Windows locks keep the directory, record it and retry on the next heartbeat; do not block other work. Delete the remote task branch once its head is in master.
5. If the acceptance comment lists criteria waiting for the user, label the Issue `awaiting-user`, keep it open, add those criteria to the progress record, and tell the user what to check, including where an art Issue's screenshots are. When the user confirms them, check them and close the Issue; when the user reports a problem, record it on the Issue, remove the label and dispatch the Issue again. Otherwise post a closure comment linking the PR, merge commit and acceptance comment, and close the Issue as completed.
6. Update the progress record and fill the freed slot.

## Progress record

The progress record is [comment 6362784666 on #297](https://github.com/odradekk/train/issues/297#issuecomment-6362784666). Edit it in place after each dispatch, merge or block. It lists, with links: Issues in progress (module, workspace, review round when known), merged Issues, `awaiting-user` Issues with the criteria the user must confirm, blocked Issues with reasons, questions waiting for the user, and the next candidates per module.

## Stage integration

The full Demo ran in [six stages](design/00-product-and-core-loop.md#开发阶段), each closed by a stage acceptance Issue. All six passed by 2026-10-23 (version 0.6.0), and no stage acceptance Issue is open; accepted Issues merge into master without waiting for a stage. If the user opens a new stage acceptance Issue:

1. When its other native blockers are closed or `awaiting-user`, dispatch it as a pair on the current `origin/master` commit. The worker gathers the evidence that [stage acceptance](contracts/testing.md#阶段性联调的验收) requires on that commit, writes the playtest checklist, and copies the export to `D:\Code\train-builds\stage-<k>-<sha>\`.
2. Each failure, from the pair or from the user's playtest, becomes a fix Issue in the owning module that blocks the stage acceptance Issue; after the fixes merge, run step 1 again.
3. Give the user the build path, tested commit, playtest checklist, evidence comment and known gaps. Only the user's confirmation that the stage passed lets you close the stage acceptance Issue and its `awaiting-user` Issues, archive the stage workspace and update the version.

### Version

Tasks and fixes do not change the version. A passed stage increases MINOR by 1 and resets PATCH to 0; setting `1.0.0` requires the user's explicit decision. Update `project.godot` `config/version`, `export_presets.cfg` `application/product_version` and `application/file_version` (`X.Y.Z.0`), and the current-version line in `AGENTS.md`. Commit as `chore: bump version to X.Y.Z` on master, push, and record the old and new version and the commit on the closed stage Issue.

## Recovery

After an interruption, read the progress record, open Issues with dispatch comments, Paseo `list_workspaces` and `list_agents`, `git worktree list`, open PRs, and running Godot editors and Blender instances. Resume each pair at its first incomplete step: send a continue prompt to an idle reviewer, rather than creating a second workspace, PR or merge. Restart a stopped Godot editor or Blender instance on the same port and tell the reviewer that the worker must reconnect. A PR head that changed after acceptance needs a new `accepted` result before merging, unless the only change is your own `gh pr update-branch` merge from step 3.
