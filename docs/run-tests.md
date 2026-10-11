# Unified test execution contract

This document specifies the usage and behavior of `scripts/run-tests.ps1`. Which checks a change needs is decided by the [Testing standard](implement-sop.md#testing-standard); [Windows CI](ci.md) calls the same entry.

四模块测试与隔离验证的目标见[独立构建与契约验证](contracts/testing.md)。模块项目拆分、多项目选择、Core/Content 实际隔离构建和项目依赖检查已接入。真实工厂契约与 Godot 隔离装配仍待生产入口建立，不能由现有普通 C# 测试推断通过。

`scripts/run-tests.ps1` (PowerShell 7) is the single entry for discovery, selection, prerequisites, runner invocation and reporting. It is a thin dispatcher: assertions stay in their test implementations, and runners remain independently callable. Without arguments it prints help and the discovered groups; running requires an explicit selection such as `-Family`, `-Scope`, `-Test` or `-Correctness`.

默认从 `Tests/` 下发现引用 `MSTest.TestAdapter` 或 `MSTest` 的测试项目：Core、Content、Godot 宿主和 ContractSupport 自检。未指定模块的 `-Correctness` 还要求 Core、Content、Godot 三个现有入口都可发现，缺失时报告阻塞。`-Module core|content|godot` 在构建和发现前限定项目，重复参数取并集；`scenes` 入口尚不存在，明确选择会报阻塞。模块选择包含相应的契约支持自检；模块正确性选择还包含 `contracts.structure`。Godot 隔离装配不存在时，`-Module godot -Correctness` 将待实现项加入所选用例并明确报告阻塞；`-Family` 和 `-Test` 继续对它筛选。当前引擎检查仍分别执行并报告。`-Project <路径>` 用于显式运行一个 MSTest 项目，与 `-Module` 互斥。`module` 表示责任归属；Core/Content 的 `-Correctness` 在系统临时目录按允许依赖闭包重建并运行，未来的 Scenes 项目也按相同规则处理。临时目录在运行结束后清理，输入及引用清单留在本次报告目录；`TestResults/.gdignore` 阻止 Godot 扫描报告。完整现有 MSTest 可用 `-Correctness -RunnerCatalog none` 运行，结构与公开面检查可用 `-Test contracts.structure` 运行。

`-Test content.examples-schema` 调用现有 JSON Schema 校验器检查 `docs/content/examples/` 的全部当前格式示例及 Schema 自身；需要 Python 和 `jsonschema` 包。示例为空、格式不符或工具缺失都不能通过。此项不验证跨文件引用或生产加载器行为。

MSTest cases and classification come from test metadata. Other runners are registered once in `scripts/runner-catalog.json` with only the invocation information they need; the same file records the retained support harnesses and tools. `scripts/` 顶层保留统一入口、入口测试和登记表；具体检查、检查测试及其专用进程助手放在 `scripts/checks/`，MSTest 元数据读取器放在 `scripts/metadata-reader/`。当前 App 的引擎检查使用 `scripts/checks/run-app-check.ps1`：

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.logging-smoke
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.content-smoke
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.main-menu
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.simulation-host-smoke
# 同次调用共享一次 Windows 导出，分别验证内容和空 Mod 根的真实运行行为。
pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.content-export-smoke -Test godot.mods-export-smoke
```

`-Correctness` 选择 MSTest、结构与公开面、内容示例 Schema 和已登记的非导出引擎检查；Schema 检查需要 Python 3 与 `jsonschema`（CI 为 Python 3.11、`jsonschema==4.25.1`），引擎检查需要 `GODOT` 和游戏工程构建、资源导入。带 `export` 要求的检查必须显式选择；它们需要 Windows 和匹配的导出模板。一次调用只构建、导入一次，选择导出检查时再共享一次 Windows 发布导出。导出准备保留模板所在的 APPDATA；随后每项检查都有独立 APPDATA，并通过 `--check-user-data` 将应用日志及外部内容限制在该项的输出目录。

content 的输入由运行器提前复制到输出目录的 `content-input/initial` 与 `content-input/added`，两份都只包含 `voxels/grain.voxels.json`、四个绑定共用的 `voxels/grain/placeholder.png`，以及用于烟测的市场表和聚居地类型 JSON；`added` 另外生成 `smoke-ore` 体素探针。图片经物理内容根读取，不需要复制 Godot 导入配置。检查切换两份读取来源，在同一个重载器上验证新增、移除；不写项目资源或输入文件。检查选择使用 `-- --check=<名称>`，不读取旧 TRAIN_* 检查开关。通过必须同时满足进程退出 0、行为标记和 `APP_CHECK_PASSED <名称>`，不以帧数或超时退出证明通过。

导出过滤显式包含 `*.voxels.json`、`*.composites.json`、`*.market-tables.json`、`*.settlement-types.json` 和 `*.png`。内置 PNG 的 `.import` 使用 `importer=keep`，保证导出包中的原始字节可读。content 检查必须报告 `CONTENT_GRAIN_LOADED`、`CONTENT_PNG_DECODED`、`CONTENT_MARKET_SETTLEMENT_READY` 等初始标记，导出和空 Mod 根检查分别报告 `CONTENT_EXPORT_GRAIN_LOADED` 与 `MODS_EXPORT_GRAIN_LOADED`；PNG 标记还验证四个表现绑定共用同一张实际解码的非空图片。当前导出运行验证最小粮食内容，完整预置内容在 [#236](https://github.com/odradekk/train/issues/236) 接入后补充相应资源证据。

Windows 上覆盖 APPDATA 不会改变 Godot 原生的默认 `user://` 路径。运行器为引擎日志传入运行目录内的 `--log-file`，导入和导出准备使用 `--recovery-mode` 跳过编辑器插件，防止插件设置写入正常用户目录。App 检查正常启动主场景；项目 MCP 在独立的编辑器验证中核验。正常用户目录的前后快照不一致时，整次运行以非零退出。

统一入口不写 `override.cfg`，已有文件保留并在摘要中记录 SHA256。运行结果、输入、日志和导出文件归本次运行目录所有，正常玩家目录保持不变。

## Classification and selection

Classify tests by family (`unit`, `headless`, `benchmark`) and stable functional scope such as content, console, logging, saves, simulation or voxels, and record actual requirements such as Godot, separate processes, export or a graphical session. Speed follows measurements, not a blanket assumption that C# tests are unit tests. Use stable case/group names rather than permanent entry points for individual Issues.

Show the selected cases and prerequisites before execution; combine family/scope filters and deduplicate selected cases. Invalid arguments, unknown names and an explicit selection matching no cases are errors. An unavailable required environment is reported as unverified with a non-success result, never by silently shrinking the selection. Full regression includes the registered correctness checks; unimplemented Benchmark, long-run, manual or platform groups are pending requirements, not empty groups that pass. Publish the actual selected coverage so a partial migration or small CI selection cannot be presented as all repository checks, and list manual observation separately from automated passes.

## Execution and isolation

Each run gets a fresh result directory for its reports and owned outputs. Core/Content `-Correctness` projects are rebuilt and run in temporary isolation; Scenes will follow the same rule when its test project exists. Other MSTest selections, including explicit `-Project`, build and run in the working tree and provide no module isolation evidence. Godot checks isolate application logs, external content, and user data, but do not yet isolate the game assembly from other implementation modules. Child processes get controlled test settings without leaking task-specific settings into other runs. Only necessary builds/imports are prepared and are reused within the invocation. Checks that need Godot, builds or shared resources run serially; there is no cross-run caching or automatic parallel scheduling.

Process checks use measured, bounded timeouts. Cancellation or timeout preserves available evidence and terminates only this run's owned process tree; if cleanup cannot establish isolation, dispatch stops and the blocker is reported. Other worktrees' processes and ordinary game saves are left untouched.

After an ordinary assertion failure, other independent, safe groups continue and all results are kept. A test assembly build or metadata read needed for discovery stops the run. A game build, import, or export failure after discovery blocks its dependent groups while independent groups continue. Cancellation stops dispatch, and serious state corruption stops that case at the first evidence. The exit is non-success whenever a selected required check fails, is blocked or does not finish. The report distinguishes failed, blocked, cancelled and not-executed outcomes; later passes never overwrite earlier failures.

## Results and evidence

Each run produces one machine-readable summary and one concise Markdown report beside the original evidence, in a unique directory under one Git-ignored hierarchy that never overwrites earlier reports. Both record the selection, actual execution, per-check outcome, preparation/test durations, tested commit and working-tree dirty state, relevant build/content/environment identities and artifact locations. Native TRX and Godot logs are preserved rather than copied into a second system. Results from a dirty tree do not establish that the recorded clean commit passed. Exact summary fields, nonzero exit codes and helper-file splits are implementation choices within these contracts; keep them minimal and documented.
