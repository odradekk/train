# Unified test execution contract

This document specifies the usage and behavior of `scripts/run-tests.ps1`. Which checks a change needs is decided by the [Testing standard](implement-sop.md#testing-standard); [Windows CI](ci.md) calls the same entry.

`scripts/run-tests.ps1` (PowerShell 7) is the single entry for discovery, selection, prerequisites, runner invocation and reporting. It is a thin dispatcher: assertions stay in their test implementations, and runners remain independently callable. Without arguments it prints help and the discovered groups; running requires an explicit selection such as `-Family`, `-Scope`, `-Test` or `-Correctness`.

MSTest cases and classification come from test metadata. Other runners are registered once in `scripts/runner-catalog.json` with only the invocation information they need; the same file records the retained support harnesses and tools. 当前 App 的引擎检查使用 `scripts/checks/run-app-check.ps1`：

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.logging-smoke
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.content-smoke
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.main-menu
# 同次调用共享一次 Windows 导出，分别验证内容和空 Mod 根的真实运行行为。
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.content-export-smoke -Test godot.mods-export-smoke
```

`-Correctness` 选择 MSTest 与日志、内容、主菜单三项引擎检查，需要 `GODOT` 和游戏工程构建、资源导入。带 `export` 要求的检查必须显式选择；它们需要 Windows 和匹配的导出模板。一次调用只构建、导入一次，选择导出检查时再共享一次 Windows 发布导出。导出准备保留模板所在的 APPDATA；随后每项检查都有独立 APPDATA，并通过 `--check-user-data` 将应用日志及外部内容限制在该项的输出目录。

content 的输入由运行器提前复制到输出目录的 `content-input/initial` 与 `content-input/added`，只包含必要定义和图片。检查切换两份读取来源，在同一个重载器上验证新增、移除；不写项目资源或输入文件。检查选择使用 `-- --check=<名称>`，不读取旧 TRAIN_* 检查开关。通过必须同时满足进程退出 0、行为标记和 `APP_CHECK_PASSED <名称>`，不以帧数或超时退出证明通过。

Windows 上覆盖 APPDATA 不会改变 Godot 原生的默认 `user://` 路径。运行器为引擎日志传入运行目录内的 `--log-file`，导入和导出准备使用 `--recovery-mode` 跳过编辑器插件，防止插件设置写入正常用户目录。App 检查正常启动主场景；项目 MCP 在独立的编辑器验证中核验。正常用户目录的前后快照不一致时，整次运行以非零退出。

统一入口不写 `override.cfg`，已有文件保留并在摘要中记录 SHA256。运行结果、输入、日志和导出文件归本次运行目录所有，正常玩家目录保持不变。

## Classification and selection

Classify tests by family (unit, headless automation, Benchmark) and stable functional scope such as voxels, buildings, railway, persistence or content, and record actual requirements such as Godot, separate processes, export or a graphical session. Speed follows measurements, not a blanket assumption that C# tests are unit tests. Use stable case/group names rather than permanent entry points for individual Issues.

Show the selected cases and prerequisites before execution; combine family/scope filters and deduplicate selected cases. Invalid arguments, unknown names and an explicit selection matching no cases are errors. An unavailable required environment is reported as unverified with a non-success result, never by silently shrinking the selection. Full regression includes the registered correctness checks; unimplemented Benchmark, long-run, manual or platform groups are pending requirements, not empty groups that pass. Publish the actual selected coverage so a partial migration or small CI selection cannot be presented as all repository checks, and list manual observation separately from automated passes.

## Execution and isolation

Each run gets a fresh result directory with isolated saves, mutable content and user data. Child processes get controlled test settings without leaking task-specific settings into other runs. Only necessary builds/imports are prepared and are reused within the invocation. Checks that need Godot, builds or shared resources run serially; there is no cross-run caching or automatic parallel scheduling.

Process checks use measured, bounded timeouts. Cancellation or timeout preserves available evidence and terminates only this run's owned process tree; if cleanup cannot establish isolation, dispatch stops and the blocker is reported. Other worktrees' processes and ordinary game saves are left untouched.

After an ordinary assertion failure, other independent, safe groups continue and all results are kept. A failed build/import blocks its dependent groups, cancellation stops dispatch, and serious state corruption stops that case at the first evidence. The exit is non-success whenever a selected required check fails, is blocked or does not finish. The report distinguishes failed, blocked, cancelled and not-executed outcomes; later passes never overwrite earlier failures.

## Results and evidence

Each run produces one machine-readable summary and one concise Markdown report beside the original evidence, in a unique directory under one Git-ignored hierarchy that never overwrites earlier reports. Both record the selection, actual execution, per-check outcome, preparation/test durations, tested commit and working-tree dirty state, relevant build/content/environment identities and artifact locations. Native TRX, Godot logs and checkpoints are preserved rather than copied into a second system. Results from a dirty tree do not establish that the recorded clean commit passed. Exact summary fields, nonzero exit codes and helper-file splits are implementation choices within these contracts; keep them minimal and documented.
