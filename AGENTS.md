工作语言为中文。

## Current phase: core gameplay development

Current version: `0.0.5`.

- Develop with agile, playable slices: choose the smallest end-to-end change that answers the current gameplay question.
- Work in short iterations and validate each slice before expanding scope.
- Keep architecture, abstractions, content, polish, and extensibility proportional to the current slice; prefer reversible decisions while the design is still changing rapidly.
- Treat the current game, domain model, and roadmap as provisional. Features may be revised, replaced, added, or removed when new evidence changes the direction.
- Stop each iteration when there is enough evidence to choose the next small step.
- Keep future systems and content ideas in the issue tracker or roadmap until they are selected for the current slice; a proposed direction is not an implementation commitment.

## Source layout

- Develop the current game directly from the repository root. Place the Godot project and production source directories at the root when the first formal gameplay slice requires them.
- Treat `archive/mvp/` as read-only validation evidence. Reuse an archived implementation only by deliberately moving or rewriting the necessary code into the root project; do not continue product development inside the archive.
- Keep generated Godot state, build outputs, and restored packages out of the archive; regenerate them only when reproducing an MVP result.

## Local Godot validation

- On this Windows machine, Godot 4.7.2 .NET is installed at `D:\Godot_v4.7.2-stable_mono_win64\`.
- Before running Godot integration checks in the checkout under test, set `$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'` so the existing smoke scripts can find the engine and capture its output.

## Agent skills

### Issue tracker

Issues and specs are tracked in GitHub Issues for `odradekk/train`. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the five default canonical triage labels. See `docs/agents/triage-labels.md`.

### Domain docs

Use a single-context layout with `CONTEXT.md` and `docs/adr/` at the repository root. See `docs/agents/domain.md`.

### Issue task workflow

For Issue dispatch, PR delivery, review and revision, Acceptance-criteria verification, or user-approved merge, cleanup, closure, and version updates, follow `docs/tickets-sop.md`.

### Console command documentation

使用或审查控制台命令时，从 `docs/console/README.md` 查阅手册；新增、修改或移除命令时，按一命令一文件同步维护 `docs/console/` 中的对应文档及索引。
