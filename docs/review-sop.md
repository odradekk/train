# Review Standard Operating Procedure

Use this procedure as the reviewer of one Issue. The reviewer owns the Issue's quality. It creates the worker, approves the worker's plan, reviews each delivery, verifies the Acceptance criteria, and reports to the coordinator only when the Issue is accepted or blocked. Scheduling, merging and closure belong to the coordinator under [dispatch-sop.md](dispatch-sop.md). The worker follows [implement-sop.md](implement-sop.md).

## Scope and authority

Work in the assigned worktree and Issue. Do not edit product code, tests or docs, and do not commit, push, merge or close Issues. You may run builds, tests and Godot checks in the worktree, post Issue comments, and check satisfied Acceptance-criteria boxes. You share the worktree with the worker, so run checks only while the worker is idle. Ignored leftovers inside the worktree, such as old build outputs, go away with the worktree; neither agent deletes them.

You decide design choices the docs leave open when they are reversible and change no design doc, contract or Acceptance criterion. Record each such decision with its reason in the [approved-plan comment](#2-review-the-plan); for a decision made after approval, edit that comment and add it. A choice that would change a design doc, a contract, the scope or an Acceptance criterion belongs to the user; [report it as blocked](#blocked).

## 0. Prepare

Read the coordinator's dispatch comment, the Issue body and all comments, `AGENTS.md`, and the design docs, ADRs, contracts and `CONTEXT.md` terms the Issue references. Verify that the worktree path, branch and MCP assignment match the dispatch comment.

## 1. Create the worker

Create the worker with Paseo `create_agent` in the same workspace, title `work #<n>`. Since the user's 2026-10-23 decision, GPT is the default worker because DeepSeek and Grok quota is nearly used up:

| Issue | Worker `provider` and `settings` |
| --- | --- |
| All Issues | `codex/gpt-6.1-sol`, `{ modeId: full-access, thinkingOptionId: xhigh }`. Do not turn on Fast (`service_tier: priority`). Art Issues must use GPT, because only GPT can generate images. |
| Fallback when GPT is unavailable, not for art | `codebuddy-code/deepseek-v4.1-flash`, `{ modeId: fullAccess, thinkingOptionId: max }`, or `cursor/grok-4.7`, `{ modeId: agent, thinkingOptionId: xhigh, features: { auto_accept: true } }`. |

Do not use Claude Opus as a worker. DeepSeek and Grok are somewhat weaker than GPT 6.1 Sol. Give them more guidance: name the files, the existing code to reuse and the checks to run; approve plans in small steps; review every delivery line by line and run more of the risk-relevant checks yourself. If a worker's model keeps failing or runs out of quota, replace the worker with another allowed model and hand over through the approved-plan comment.

Each client reads its own MCP config: Codex `.codex/config.toml`, Cursor `.cursor/mcp.json`, Claude and CodeBuddy `.mcp.json`. Once the worker starts, confirm it sees the `godot` tools when the Issue needs them; if not, ask the coordinator to fix the port or reload the worker instead of retrying.

Use this initial prompt:

> You are the worker for Issue #\<n\> in this worktree. Follow docs/implement-sop.md. The dispatch comment on the Issue has your branch, MCP assignment and stage. Your first deliverable is the plan; do not write code before I approve it. End every reply with a `WORKER:` status line.

The worker is your subagent, so you are notified when each of its turns ends. Read its reply from the notification or with `get_agent_activity`, answer with `send_agent_prompt`, then end your turn and wait for the next notification, including after you answer a question. Do not poll. If a question still arrives as an input request instead of a `WORKER: question` reply, answer it there (answers are keyed by each question's `header`) and remind the worker to use `WORKER: question`. If the worker stops with a model-capacity or transient error, send it a short continue prompt on the same model.

## 2. Review the plan

Before any code, the worker sends a plan ending with `WORKER: plan`. Check:

- **Direction:** the approach follows the design docs, ADRs, contracts, `CONTEXT.md` terms and the current stage scope.
- **Scope:** it covers every Acceptance criterion and adds nothing the Issue and recorded decisions do not ask for, even if it looks useful.
- **Code taste:** it uses the fewest files, types and concepts, reuses existing code, the standard library or Godot features, and adds no speculative abstraction, setting or extension point, per `AGENTS.md` code taste.
- **Tests:** the selected checks match the changed risks under the [Testing standard](implement-sop.md#testing-standard).

Approve it, or send concrete corrections and ask for a revised plan. Answer the worker's open questions or decide them under [Scope and authority](#scope-and-authority).

On approval, post the approved-plan comment on the Issue: the approach, the step order, the selected checks, and each design decision with its reason. Direction review, a replacement pair and recovery all work from this comment, not from chat history.

## 3. Review the delivery

When the worker reports `WORKER: delivered <PR> <SHA>`, read its completion comment and the full diff from the PR base to that head. Review three areas:

| Area | What to check |
| --- | --- |
| Correctness | Every Acceptance criterion is implemented as specified. Logic, edge cases, error handling, state and determinism are right. The tests can detect realistic defects and claim only the boundaries they exercise. |
| Direction | The change matches the design docs, ADRs, contracts and `CONTEXT.md`; it stays within the Issue, the recorded decisions and the approved plan; module boundaries and dependency directions follow `AGENTS.md` source layout and the [contract index](contracts/README.md). |
| Code taste | The code is plain and readable without comments; it has no unneeded helpers, layers, interfaces, settings or compatibility code; comments appear only where the code cannot show the reason. |

You may run each area in its own subagent so that one area's findings do not narrow attention on another. Run the key risk-appropriate checks yourself rather than in subagents, following [Review tests and evidence](implement-sop.md#review-tests-and-evidence). For Godot changes, inspect saved scene and resource diffs and runtime evidence, not tool success messages. For art Issues, review the screenshots against docs/art 00–07 and the [inspection checklist](art/08-asset-pipeline-and-review.md#检查清单); you may use Blender MCP to inspect models.

Verify each finding against the code or a reproduction before sending it, and drop claims that do not hold. Post one Issue comment per review round naming the PR head SHA and, for each finding, its severity, the violated requirement or doc, the file and line or reproduction, observed and expected results, and the requested correction. Send the same findings to the worker. A finding blocks acceptance when it breaks an Acceptance criterion, a design doc or contract, or adds code that costs maintenance. Low-severity findings that change no behavior, such as doc wording, go into the same round as blocking findings or into the acceptance comment as non-blocking risks; they alone do not start a new round.

Re-review each new delivery: the fixed findings, the areas the fixes touch, and anything else the new commits change. After four delivery rounds with findings still open, [report blocked](#blocked).

## 4. Accept

When no findings remain:

1. Fetch and check whether `origin/master` is an ancestor of the PR head. If master has moved, have the worker merge it into the branch (no rebase), rerun the affected checks and push; review the merge and the behavior it touches, and accept the new head. The coordinator merges only heads that contain master.
2. Map every Acceptance criterion to the inspected implementation and its evidence. Check only satisfied criteria; read the saved Issue body back to confirm. Do not change the criteria's wording. Criteria that need the user's confirmation, such as visual approval, thresholds, budgets or values, stay unchecked and do not block acceptance.
3. Post the acceptance comment: PR URL and head SHA, evidence per criterion, the checks you ran and the worker's results you relied on, resolved findings, and remaining non-blocking risks. List the criteria waiting for the user, each with the evidence the user needs, such as screenshot paths or measurements.
4. End your final message with `PAIR-RESULT: accepted <PR URL> <head SHA>` and [send it to the coordinator](#report-to-the-coordinator).

If master moves again before the coordinator merges, the coordinator updates the task branch with `gh pr update-branch` and merges once the Windows checks pass, without a new acceptance. It comes back to you only when that update conflicts or its checks fail. Then have the worker pull the branch, merge `origin/master` (no rebase), fix, rerun the affected checks and push. Review the merge and the affected behavior, post a new acceptance comment for the new head, and report `accepted` again.

## Blocked

When the Issue cannot be accepted — a decision belongs to the user, the round limit is reached, a prerequisite is missing, or the Issue itself is wrong — tell the worker to stop. Post an Issue comment with the reason, the evidence, and for a decision the options with your recommendation. If the Issue is too large, say how to split it. End your final message with `PAIR-RESULT: blocked <comment URL>` and [send it to the coordinator](#report-to-the-coordinator). When the coordinator later sends the user's answer, which it has recorded on the Issue, add it to the approved-plan comment and continue from where the pair stopped.

## Report to the coordinator

Paseo notifies the coordinator only about turns it started, and most of your turns are started by the worker. So send every `PAIR-RESULT:` line to the coordinator with `send_agent_prompt`, using the coordinator agent ID from the dispatch comment, `background: true` and `notifyOnFinish: false`.

## Stage acceptance pairs

For a stage acceptance Issue, there is no PR. The worker produces evidence on the tested master commit under [Stage integration](dispatch-sop.md#stage-integration). Verify that evidence against the stage's Acceptance criteria and [stage acceptance](contracts/testing.md#阶段性联调的验收), and check only the technical criteria. Leave the user's confirmation to the user. Report `PAIR-RESULT: accepted <evidence comment URL> <tested SHA>`, or `blocked` with every failing path listed.

## Recovery

After an interruption, read the Issue comments, the PR head and the worktree's Git state, and check the worker's status with `get_agent_status`. Resume the first incomplete step with the existing worker. Create a new worker only if the old one is gone, and point it at the Issue comments.
