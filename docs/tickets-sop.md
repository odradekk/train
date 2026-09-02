# Ticket Dispatch Standard Operating Procedure

Use this procedure to hand a fully specified GitHub Issue to a terminal agent in an independent Orca worktree.

The default mode is a **full handoff**: once the task is delivered and the agent has started working, the dispatcher stops monitoring. Use Orca supervised orchestration instead when the user asks to wait for results, coordinate multiple tasks, manage decision gates, or automatically advance dependencies.

## Dispatch Gate

Dispatch a ticket only when all of these conditions are true:

- the GitHub Issue is open;
- it has the `ready-for-agent` label;
- it has no open blockers;
- it has not been superseded by another Issue;
- its specification supports routine implementation without further decisions;
- all required code and documentation exist on the target Git baseline;
- the requested agent command is available;
- the worktree setup hook can complete successfully.

Uncommitted changes in the main workspace are absent from an independent worktree created from the default baseline. If the ticket depends on those changes, first publish them to a reachable baseline or ask the user to decide. Do not create the worktree yet.

## Standard Procedure

### 1. Validate the ticket

Read the body, comments, state, labels, and native GitHub dependencies. Follow `docs/agents/issue-tracker.md` for GitHub operations.

```powershell
gh issue view <issue> --repo odradekk/train --comments
gh api repos/odradekk/train/issues/<issue>/dependencies/blocked_by
```

If the ticket is closed, superseded, or blocked, show the facts and ask the user to choose. Recommend the current unblocked dispatch frontier; never silently dispatch a different ticket.

**Completion criterion:** one open, executable, unblocked ticket has been confirmed.

### 2. Confirm a self-contained baseline

Confirm that the target baseline contains the ticket’s required code, specification, domain documentation, and ADRs.

By default, create an independent top-level worktree with no parent and let Orca use the repository’s default base ref. Set a parent or base branch only when the user explicitly requests stacked work, continuation from the current branch, or a specific baseline.

**Completion criterion:** the new worktree needs no implicit state from the dispatcher’s current directory.

### 3. Initialize Orca

Use the `orca-cli` skill to select one CLI executable, load the version-matched guide from that binary, and confirm that the runtime is ready:

```text
<orca-command> skills get orca-cli
<orca-command> status --json
```

Use the same executable for every later command. If it fails, report the original error; do not switch to another Orca build.

**Completion criterion:** the runtime is ready and the current command surface is known.

### 4. Inspect repository setup

Read the live repository configuration rather than caching its setup command in this document:

```text
<orca-command> repo show --repo path:<repo-path> --json
```

Confirm that the setup script, default run policy, and agent startup policy fit the ticket. When the agent depends on restored packages or generated files, setup must finish before the agent starts.

**Completion criterion:** setup is known and ordered before any dependent agent work.

### 5. Create the worktree

For a custom agent command, use a two-step launch. Create the worktree first:

```text
<orca-command> worktree create --repo id:<repo-id> --name issue-<number>-<short-name> --issue <number> --no-parent --setup run --json
```

Name it `issue-<number>-<english-kebab-case>`. Save the complete worktree ID returned by Orca:

```text
<repo-id>::<absolute-worktree-path>
```

A bare repository ID is not a worktree ID.

**Completion criterion:** the worktree is linked to the correct Issue, uses the intended baseline, and has no unintended parent.

### 6. Verify setup

List the new worktree’s terminals and inspect the setup terminal’s real output:

```text
<orca-command> terminal list --worktree id:<full-worktree-id> --include-visual-layouts --json
```

Use explicit success output or a returned prompt as evidence. Do not guess with a sleep. If setup fails, keep the worktree, stop before launching the agent, and report the error.

Setup tabs and default terminals created by repository configuration are intentional surfaces. Keep them unless a terminal has been confirmed as an unused shell and removal is necessary.

**Completion criterion:** setup has explicitly succeeded.

### 7. Launch the agent

When `worktree create --agent <id> --prompt ...` fully expresses the requested launch, prefer that agent-first path.

When the command includes custom arguments, create a dedicated terminal after setup succeeds:

```text
<orca-command> terminal create --worktree id:<full-worktree-id> --title "<Agent> #<issue>" --command "<agent-command>" --json
```

Add permission-expanding flags such as `--yolo` only when the user explicitly requests them.

Save the returned terminal handle. If it becomes stale, list the worktree’s terminals again and use only the replacement handle.

**Completion criterion:** exactly one target agent instance and one valid terminal handle exist.

### 8. Clear startup gates

Wait for the TUI to become idle. If it does not, or Orca reports a blocked reason, read the terminal:

```text
<orca-command> terminal wait --terminal <handle> --for tui-idle --timeout-ms 60000 --json
<orca-command> terminal read --terminal <handle> --json
```

Accept Workspace Trust only when the displayed path exactly matches the new worktree and the user has authorized that agent to execute code there. Stop for user action when login, permissions, or paths are unexpected.

**Completion criterion:** the agent’s main interface accepts a task and no startup prompt remains unresolved.

### 9. Send one implementation brief

The brief must start exactly with `/implement `, including one trailing space and with no preceding text. This prefix triggers the implementation skill. The remainder of the brief must include:

- ticket number, title, and URL;
- ownership of the independent worktree;
- the Issue, `AGENTS.md`, domain documentation, and ADRs to read;
- the ticket’s scope and adjacent-ticket boundaries;
- test, build, and diff-review requirements;
- a Conventional Commit requirement;
- worktree comment update requirements;
- permission boundaries for PRs, Issue state, and other external writes;
- how to report a genuine blocker.

Send it once through the terminal handle and append Enter:

```text
<orca-command> terminal send --terminal <handle> --text "<brief>" --enter --json
```

`accepted` proves only that input was written. Read the terminal afterward to confirm that the agent received the task and started working. TUI animation or repeated rendering is not evidence that the brief needs to be sent again.

**Completion criterion:** one task delivery has been received and is being processed.

### 10. Update Orca metadata

```text
<orca-command> worktree set --worktree id:<full-worktree-id> --comment "<Agent> implementing #<issue> <short scope>" --workspace-status in-progress --json
```

Keep the comment current and concise; do not copy the ticket body.

**Completion criterion:** the Orca card shows the correct Issue, status, agent, and short scope.

### 11. Complete the handoff

A full handoff is complete when all of this evidence exists:

- the ticket is valid and unblocked;
- the worktree baseline is self-contained;
- setup succeeded;
- the target agent started;
- startup gates were cleared;
- the brief began with `/implement ` and was sent exactly once;
- the agent is working;
- worktree metadata was updated.

Stop reading, waiting, or polling after this point. Report the worktree, branch, path, baseline, Issue, setup result, agent command, terminal handle, scope, and that monitoring has stopped.

## Implementation Brief Template

```text
/implement GitHub Issue #<number> for odradekk/train: <issue-url>

You own this independent Orca worktree and branch. Read the full issue body and repository instructions before changing code. Implement only #<number>. Respect AGENTS.md and the current domain docs/ADRs. Use the smallest correct diff and keep archive/mvp read-only.

Required workflow:
1. Inspect the repo and fetch the issue with gh using --repo odradekk/train when no remote is configured.
2. Implement the complete observable contract, including tests at the specified behavior boundary.
3. Run the relevant tests and practical build/Godot validation.
4. Review the diff for scope and regressions.
5. Commit all intended changes with a Conventional Commit message.
6. Update the Orca worktree comment at meaningful checkpoints and leave a concise final validation result.

Use the decisions already specified in the issue. Report genuine missing-fact blockers in the worktree comment. Keep dependent or adjacent tickets unchanged. Create no PR unless explicitly requested.
```

Adapt the language and validation commands to the ticket while preserving the scope, validation, commit, and external-write boundaries.

## Recovering a Dispatch

After a parent-session restart, interrupted output, or lost tool state, **rediscover rather than recreate**:

1. reload the version-matched Orca guide and confirm status;
2. find the worktree with `worktree list/show`;
3. find the current handle with `terminal list --worktree ...`;
4. read the terminal to identify setup, startup-gate, idle, or working state;
5. continue from the observed state.

During recovery, do not create a duplicate worktree, launch a second copy of the agent, resend the brief, or assume that an old terminal handle remains valid.

## Exception Handling

| Condition | Action |
| --- | --- |
| Ticket is closed or superseded | Show the facts and ask the user to select the current ticket |
| Ticket has an open blocker | Recommend the unblocked dispatch frontier and stop |
| Required context exists only as uncommitted changes | Publish a reachable baseline or request a decision |
| Orca is not running | Start it with the selected CLI and check status again |
| Setup fails | Keep the worktree, do not launch the agent, and report the error |
| Agent command is unavailable | Report the exact error; do not substitute another agent |
| Workspace Trust path differs | Do not accept it; ask the user to confirm |
| Terminal handle is stale | List terminals again and use only the new handle |
| Parent session restarts | Rediscover the existing dispatch; do not recreate or resend |
| Agent is already working | Complete the full handoff and stop monitoring |

## Dispatch Report Template

```text
Dispatched #<issue>: <title>

- Worktree: <name>
- Branch: <branch>
- Path: <absolute path>
- Baseline: <base ref>
- Parent worktree: none / <parent>
- Setup: succeeded
- Agent command: <command>
- Terminal: <title> / <handle>
- Orca status: in-progress
- Scope: implement only #<issue>; exclude <adjacent tickets>
- External writes: <PR/Issue permissions>
- Monitoring: task delivered and started; full handoff monitoring stopped
```

## Rules Proven by This Dispatch

Issue #17 was closed and superseded by the dependency chain `#18 → #19 → #20`, so the dispatcher first selected the only unblocked frontier ticket, #18. The custom command `cursor-agent --yolo` used the two-step path: create the worktree and finish setup, then create a dedicated terminal. Workspace Trust was accepted only after matching the displayed path. When the parent session restarted, Orca rediscovered the existing worktree and terminal; it did not recreate the dispatch or resend the task.

The three core safeguards are: **dispatch the frontier, require a self-contained baseline, and rediscover rather than recreate.**
