# Windows CI

四模块契约验证的目标增量见[CI 与交付时机](contracts/testing.md#ci-与交付时机)：常规 CI 增加结构检查和三个无引擎模块的隔离契约测试，显式较重检查增加真实 Godot 隔离测试。这些扩展尚未实现；以下仍描述当前工作流的实际选择。

`.github/workflows/windows-checks.yml` 在发往 `master` 的 PR 创建、更新和重新打开时运行固定轻量集合：

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -BuildGame
```

统一入口负责发现、选择、测试工程构建和结果判定。`-BuildGame` 复用入口的游戏构建准备步骤编译 `Train.csproj`，只需要 SDK 和 NuGet；游戏构建失败使整次运行非成功，独立 unit 仍可执行并留下 TRX。常规运行不启动 Godot。YAML 不读取 TRX 或复制游戏断言，不推断路径影响。CI 不改变仓库设置、required checks 或分支保护。

常规集合包含全部 `unit` 测试，并另外构建游戏工程。具体用例数和耗时随当前核心实现变化，以每次统一入口摘要为准；这些结果用于反馈和证据记录，不是性能门槛或全仓正确性证明。

## 显式较重运行

合并前可在 PR 上添加 `ci:correctness` 标签，请求对**添加标签时的 PR head**执行一次：

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Correctness
```

标签只响应 `labeled` 事件。保留标签后继续推送仍只跑轻量集合；要重新请求，先移除再添加。仓库已有该标签；若被删除，可先用下面的 `gh label create` 重建，再请求运行：

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

`correctness` 包含全部已登记 MSTest，以及 App 的日志、内容、启动到主菜单三项真实 Godot 检查，入口复用一次游戏构建和导入并串行运行。两项 Windows 导出运行时检查须另外显式选择；Benchmark、长测、人工和额外平台也不属于此选择。

GitHub 要求 `workflow_dispatch` 文件已存在于默认分支，所以新工作流合并前使用 PR 标签取得远端证据。见 [GitHub 触发文档](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch)。

两种执行选择分别生成 `Windows routine`、`Windows correctness` 检查。其他标签的 run 名为 `Windows unrelated-label`，job 在分配 runner 前跳过；GitHub 的 Checks API 会保留该 job 名的未求值表达式，与已执行的常规或正确性检查不同名。

## 环境与证据

- runner 为 `windows-2025` x64；系统镜像会更新，实际 ImageOS／ImageVersion 保留在产物中。
- SDK 由 `global.json` 固定为 10.0.112、禁止 roll-forward；`setup-dotnet` 在干净 runner 安装它。
- 测试使用 PowerShell 7.6.6 官方便携构建。bootstrap 使用 runner 自带 PowerShell，将便携版本置于后续步骤 PATH 首位；测试子进程显式调用便携可执行文件。
- 较重选择下载 Godot 4.7.2 stable .NET Windows x64 官方 ZIP，设置 `GODOT` 为 console 可执行文件。两个工具 ZIP 的 SHA256 固定在工作流；actions 固定到 commit SHA。不安装无关导出模板，不缓存跨运行 Godot／包／结果。
- PR 显式检出 head SHA；手动运行检出所选 ref 的 SHA。统一摘要记录实际提交、dirty 状态、选择、环境、准备和每组耗时；`TestResults/ci/environment.txt` 补充 Actions run URL、attempt 和 runner 镜像。引用 Core/Scenes 工程的 MSTest 不证明部署的 Godot 装配，真实引擎证据另列。

入口使用唯一 `TestResults/run-tests/<id>/`，保留 `summary.json`、`report.md`、TRX、准备日志、引擎日志和隔离用户数据。报告追加到 job summary，整个 `TestResults/` 上传为 `windows-<selection>-<run_id>-<attempt>`，保留 14 天，重跑使用新的 attempt 名称。`include-hidden-files` 保留验证产物中的隐藏文件。

报告和上传步骤使用 `always()`；失败后仍执行，取消时尽可能收集已产生文件。强制终止 runner 或上传服务不可用时不能保证上传。入口非零状态直接传递给步骤，后续上传成功不会把失败 job 变绿。工具准备失败仍尝试入口，让所选检查报告缺环境；若连 PowerShell 都未准备好，只保留已产生的环境记录与 Actions 原始日志，不宣称执行通过。

job 上限为 30 分钟，为工具下载、构建与检查预留时间，并结束卡住的任务；当前检查的实际耗时以各次统一入口报告为准。

私有仓库的 Windows 托管运行消耗付费 Actions 分钟，较重路径要求显式请求。测试证据与复用规则见 [Testing standard](implement-sop.md#testing-standard)。
