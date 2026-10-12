# Windows CI

四模块契约验证的目标增量见[CI 与交付时机](contracts/testing.md#ci-与交付时机)。以下描述工作流的实际选择；各次结构检查结果以统一入口的摘要为准。

`.github/workflows/windows-checks.yml` 在发往 `master` 的 PR 创建、更新和重新打开时运行受影响项目的快速与集成层，并在其后独立运行非阻断引擎层：

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Layer fast,integration
pwsh -NoProfile -File scripts/test.ps1 -Layer engine
```

工作流检出完整历史并执行 `dotnet tool restore`，恢复固定的 dotnet-affected。入口按默认基准 `origin/master` 的共同祖先及实际改动选择 MSTest 项目和独立检查，规则见[按改动选择](run-tests.md#按改动选择)。选中 MSTest 或契约检查时构建一次 `Train.sln`，使用 Microsoft.Testing.Platform 运行所选测试项目；Schema 单独运行时不构建。Schema 由工作流准备 Python 3.11 与 `jsonschema==4.25.1`。常规两个步骤分别生成运行目录和摘要。引擎步骤构建一次解决方案并运行当前 App 的真实 Godot 集成用例，使用 `continue-on-error: true`，暂不阻断 PR；本地失败仍返回 1，摘要保留实际 JUnit 用例计数。常规 PR 不按路径筛选引擎用例；稳定后转为阻断另行实施，不预设数值门槛。Core/Content 隔离重建已暂停，由 #406 恢复。普通 .NET 宿主用例不提供真实引擎证据。

YAML 只传递明确选择并发布入口摘要，不复制游戏断言或推断路径影响。CI 不改变仓库设置、required checks 或分支保护。具体用例数、跳过数和耗时以每次摘要为准；CI 通过不表示人工、性能或全仓目标责任都已验证。

## 显式较重运行

合并前可在 PR 上添加 `ci:correctness` 标签，请求对**添加标签时的 PR head**执行一次：

```powershell
pwsh -NoProfile -File scripts/test.ps1 -All -Layer all
```

标签只响应 `labeled` 事件。保留标签后继续推送仍跑常规快速、集成和非阻断引擎步骤；要重新请求，先移除再添加。仓库已有该标签；若被删除，可先用下面的 `gh label create` 重建，再请求运行：

```powershell
gh label create ci:correctness --repo odradekk/train --description '显式请求一次 Windows 正确性回归' --color 1d76db
gh pr edit <PR号> --repo odradekk/train --add-label ci:correctness
# 同一 PR 再次请求：先 --remove-label ci:correctness，再 --add-label ci:correctness
```

合并后可从 Actions 页面选择 Windows checks → Run workflow，选择 ref 和 `routine` 或 `correctness`，也可用：

```powershell
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=routine
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=correctness
```

`correctness` 运行四个现有 MSTest 项目、结构与公开面、内容示例 Schema 及当前 App 的非导出真实 Godot 集成用例。入口复用一次游戏构建和导入并运行独立引擎测试程序集；同层 MSTest 项目并行运行。导出冒烟对同一次 Windows 导出运行两次，用 `-Layer engine -ExportWindows` 另外显式选择；Benchmark、长测、人工和额外平台也不属于此选择。

GitHub 要求 `workflow_dispatch` 文件已存在于默认分支，所以新工作流合并前使用 PR 标签取得远端证据。见 [GitHub 触发文档](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch)。

两种执行选择分别生成 `Windows routine`、`Windows correctness` 检查。其他标签的 run 名为 `Windows unrelated-label`，job 在分配 runner 前跳过；GitHub 的 Checks API 会保留该 job 名的未求值表达式，与已执行的常规或正确性检查不同名。

在 `master` 上手动运行 `routine` 时，默认基准与 HEAD 相同，快速与集成步骤得到空选择并成功退出，不构建；独立引擎步骤仍构建并运行。需要全仓回归时选择 `correctness`。

## 环境与证据

- runner 为 `windows-2025` x64；系统镜像会更新，实际 ImageOS／ImageVersion 保留在产物中。
- SDK 由 `global.json` 固定为 10.0.112、禁止 roll-forward；`setup-dotnet` 在干净 runner 安装它。
- 测试使用 PowerShell 7.6.6 官方便携构建。bootstrap 使用 runner 自带 PowerShell，将便携版本置于后续步骤 PATH 首位；测试子进程显式调用便携可执行文件。
- 两种选择都下载 Godot 4.7.2 stable .NET Windows x64 官方 ZIP，设置 `GODOT` 为 console 可执行文件。两个工具 ZIP 的 SHA256 固定在工作流；actions 固定到 commit SHA。常规选择的准备步骤同样使用 `continue-on-error`。不安装无关导出模板，不缓存跨运行 Godot／包／结果。
- PR 显式检出 head SHA；手动运行检出所选 ref 的 SHA。统一摘要记录实际提交、工作区状态、选择、构建、项目与层的用例数（含跳过）、耗时和结论；`TestResults/ci/environment.txt` 补充 Actions run URL、attempt 和 runner 镜像。引用 Core/Scenes 工程的 MSTest 不证明部署的 Godot 装配，真实引擎证据另列。

入口使用唯一 `TestResults/<选择层>-<yyyyMMddTHHmmssfff>-<7位提交>/`。通过只保留一份 `summary.json`，失败保留 TRX、JUnit、日志、转储等全部证据。CI 读取该一级运行目录的摘要追加到 job summary，不生成另一份测试摘要。整个 `TestResults/` 上传为 `windows-<selection>-<run_id>-<attempt>`，保留 14 天，重跑使用新的 attempt 名称。`include-hidden-files` 保留验证产物中的隐藏文件。

摘要发布和上传步骤使用 `always()`；失败后仍执行，取消时尽可能收集已产生文件。强制终止 runner 或上传服务不可用时不能保证上传。入口非零状态直接传递给步骤；快速、集成和显式 correctness 失败会令 job 失败，常规选择的 Godot 准备和独立引擎步骤均暂用 `continue-on-error`。Godot 下载失败时，这两个步骤的失败可见；若其他必需步骤成功，常规 job 仍为绿色。后续上传不改变这些规则。工具准备失败仍尝试入口，让所选检查报告缺环境；若连 PowerShell 都未准备好，只保留已产生的环境记录与 Actions 原始日志，不宣称执行通过。

job 上限为 30 分钟，为工具下载、构建与检查预留时间，并结束卡住的任务；当前检查的实际耗时以各次统一入口摘要为准。

私有仓库的 Windows 托管运行消耗付费 Actions 分钟，包含全层的 correctness 路径要求显式请求。测试证据与复用规则见 [Testing standard](implement-sop.md#testing-standard)。
