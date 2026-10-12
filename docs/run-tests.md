# 统一测试入口

`scripts/test.ps1` 是 PowerShell 7 测试入口。[Testing standard](implement-sop.md#testing-standard)决定改动需要哪些证据；[Windows CI](ci.md)使用同一入口。四模块的目标验证责任见[独立构建与契约验证](contracts/testing.md)，当前普通 C# 测试不证明真实工厂、Godot 隔离装配或人工验收通过。

## 选择

```powershell
# 首次运行或工具清单更新后
dotnet tool restore
# 默认受影响项目的快速层
pwsh -NoProfile -File scripts/test.ps1
# 交付前和常规 CI：受影响项目
pwsh -NoProfile -File scripts/test.ps1 -Layer fast,integration
# 全部项目及独立检查的快速、集成层
pwsh -NoProfile -File scripts/test.ps1 -All -Layer fast,integration
# 全部正确性检查，含引擎、不含导出
pwsh -NoProfile -File scripts/test.ps1 -All -Layer all
# 阶段结构、公开面与 Core/Content 隔离（始终执行）
pwsh -NoProfile -File scripts/test.ps1 -Layer stage
# 只比较当前工作区改动
pwsh -NoProfile -File scripts/test.ps1 -BaseRef HEAD
# 只选择 MSTest 项目
pwsh -NoProfile -File scripts/test.ps1 -Layer fast,integration -Project core,content
# 引擎层与两项 Windows 导出运行检查
pwsh -NoProfile -File scripts/test.ps1 -Layer engine -ExportWindows
```

`-Layer` 接受 `fast`、`integration`、`engine`、`stage` 的一个或多个值；`all` 仍等于 fast、integration、engine，默认是 `fast`。逗号多选和 PowerShell 数组都可用，例如 `& ./scripts/test.ps1 -Layer @('fast', 'integration')`。`-All` 强制全部项目和独立检查，仍只执行所选层；它与 `-Project` 冲突。`-Project` 接受 `core`、`content`、`godot`、`contract-support`、`contract-checks` 的一个或多个值，显式选择 MSTest 项目并跳过改动分析、契约结构与公开面、隔离、内容 Schema、引擎和阶段检查。`-ExportWindows` 仅能与未指定 `-Project` 的引擎层一起使用。

无待运行项目或检查的层在摘要中记为 `selected: false`；所有层均未选择时明确报告、写摘要并成功退出，不构建。已选中的层实际执行零个用例、无效参数、必需工具缺失或最低用例数不足仍判失败。最低用例数表位于 `scripts/test.ps1` 的 `$projects`；删除用例或调整分层时必须同步更新。最低数为零的项目与层组合仍执行，只允许该组合没有用例。

快速层运行未标记 `integration` 的 MSTest；集成层运行真实文件读写和子进程用例，并按下述规则选择独立检查。Schema 需要 Python 3 与 `jsonschema`（CI 固定为 Python 3.11、`jsonschema==4.25.1`）；它不验证跨文件引用或生产加载器行为。

引擎层经 `scripts/checks/run-app-check.ps1` 执行 logging、content、main-menu 和 simulation-host 四项当前 App 检查，需要 `GODOT`。[#405](https://github.com/odradekk/train/issues/405)将改用引擎内运行器，命令行保持不变。显式导出选择另外运行 content-export、mods-export，需要 Windows 和匹配的导出模板。Benchmark、长测、额外平台和人工观察不在 `all` 中。

## 按改动选择

默认基准 `-BaseRef origin/master` 必须在本地存在。入口以 `git merge-base <BaseRef> HEAD` 为比较起点，包含该点之后的提交及暂存、未暂存、未跟踪改动；文件列表禁用重命名识别，移动前后路径都参与规则匹配。入口不自动 fetch。dotnet-affected 7.0.1 以 `Train.sln` 限定项目图，沿 MSBuild 项目依赖选出受影响项目，再从五个已登记 MSTest 项目中选择；归档和调用样例不进入这张项目图。显式引擎层和阶段层照常执行；单独选择 stage 时完全跳过改动分析。

以下共享文件改动选择全部 MSTest 项目：`Contracts/**`、任意层级的 `Directory.Build.*`、`global.json`、`docs/content/schemas/**`、`Tests/ContractSupport/**`、`Train.sln`、`.config/dotnet-tools.json`、`scripts/test.ps1`、`scripts/checks/**`。工作流 YAML 不在共享列表中。

集成层的契约结构、公开面和调用样例检查，在 `Contracts/**`、任意 `*.csproj`、任意层级的 `Directory.Build.*`、`Train.sln`、`prototypes/**`、`Tests/ContractChecks/**`、`scripts/checks/project-boundaries.json` 或 `docs/contracts/public-api.txt` 改动时进入默认选择。内容示例 Schema 检查只在 `docs/content/**` 改动时进入默认选择。隔离重建在任意 `*.csproj`、任意层级 `Directory.Build.*`、`Train.sln`、`scripts/checks/project-boundaries.json` 或 `Tests/ContractChecks/**` 改动时进入默认选择。`-All` 选择结构、公开面、隔离和 Schema 独立检查。

dotnet-affected 退出码 166 表示空项目集合；其他非零退出码、120 秒分析超时或进程清理失败均使入口失败。工具输出写入本次运行目录。仓库路径保留 Git 返回的大小写，再用 `Path.GetFullPath` 转为原生完整路径与分隔符，避免工具的路径前缀匹配漏选。

摘要的 `selection` 保存基准引用、共同祖先 SHA、改动文件、共享规则、工具输出项目集合和最终选择。每个文件的 `project` 是包含它的最近项目目录对应的项目，嵌套项目优先；仅当该项目也在工具输出集合中时记录，否则为 `null`。此对应只作解释，选择仍由工具结果及共享规则决定。

## 执行与边界

选中 MSTest 项目、契约或隔离检查、引擎层或阶段层时，入口构建一次 `Train.sln`，MSTest 使用 Microsoft.Testing.Platform 和 `--no-build`。单独的 Schema 检查只调用 Python，不构建。C# 工具随解决方案构建；structure 串行处理四组调用样例，每组在同一个 `msbuild -restore -t:Build` 进程中构建并求值，使用 `BuildProjectReferences=false` 和 `RestoreRecursive=false` 避免写入共享契约产物；样例仍不加入解决方案。verify 检查公开面和类型归属，isolate 按[矩阵选择规则](contracts/testing.md#实际隔离与结构检查)重新构建隔离输入并运行用例。阶段层始终依次执行这三个命令；混选集成层和阶段层时，各自执行所需检查。

各层测试执行预算为快速 30 秒、集成 180 秒、引擎 300 秒。集成层选中的 MSTest 项目和 structure、verify、isolate、Schema 共用一个任务队列与 180 秒截止时间，通过已有的 `ForEach-Object -Parallel` 执行，最多 4 个任务并发，较重任务排在前面；汇总与清理仍由父流程完成。快速层保持原有 MSTest 并行执行方式，每个项目保留各自的最低用例数、TRX、超时和挂起转储。阶段层不设总预算，串行执行 structure、verify、isolate，子进程上限分别为 300、120、300 秒；引擎检查仍串行执行。构建准备上限为 300 秒，Godot 导入、导出各为 120 秒。超时导致失败并保留可用转储，不通过放宽预算掩盖超时。必要的 Godot 导入只做一次；两项导出检查共享一次 Windows 发布导出。同一工作树不允许并发运行入口。

content 检查自己在输出目录准备 `content-input/initial`、`content-input/added`，复制粮食体素和共用图片，生成市场表、聚居地类型及新增 `smoke-ore` 探针，验证同一重载器的新增与移除。导出过滤包含内容 JSON 与 PNG；PNG 使用 `importer=keep` 保留原始字节。通过必须同时满足退出码 0、行为标记和 `APP_CHECK_PASSED <名称>`，不以帧数或超时退出证明通过。

检查输入、临时文件、运行日志、导出产物和测试用户数据写入运行目录，`TEMP`、`TMP`、`TMPDIR` 指向其中的 `tmp`。Godot 原生日志通过 `--log-file` 定向；导入和导出用 `--recovery-mode` 跳过编辑器插件。导入和 App 检查将 Windows 的 `APPDATA`、`LOCALAPPDATA`，或非 Windows 的 `XDG_CONFIG_HOME`、`XDG_DATA_HOME`、`XDG_CACHE_HOME` 指向运行目录；只有导出步骤保留真实 APPDATA 以访问模板。导出是隔离的例外：Godot 仍可能改写真实 `Godot/editor_settings-4.7.tres` 和 `Godot/mono/build_logs/`；不能由导出通过推断真实 Godot 目录未变化。每项 App 检查使用独立用户目录。入口比较运行前后的快照：在 `bin/`、`obj/`、`.godot/`、`.git/` 和本次运行目录之外新增或删除文件、Git 状态或 HEAD 变化、真实 `Godot/app_userdata/Train` 下文件变化，均导致整次失败。运行期间不要修改工作树；保留已有 `override.cfg`。

## 结果与保留

每次运行目录为 `TestResults/<选择层>-<yyyyMMddTHHmmssfff>-<7位提交>/`，目录名中的时间为 UTC，只产生一份 `summary.json` 摘要。摘要记录提交、工作区状态、选择、构建、各项目与各层的执行／通过／失败／跳过数、用时和结论。跳过数记入摘要，整次运行仍可以通过，但跳过的用例不算已验证；脏工作区结果不能证明所记录的干净提交通过。

隔离工具在所有结局下删除自己拥有的隔离树，删除失败判失败，保留清单和日志。通过后只保留摘要；失败保留 TRX、日志、输入和转储等全部证据。入口只保留最近 10 个符合命名规则的运行目录；入口持有该工作树的命名互斥锁时，没有 `summary.json` 的运行目录一律视为崩溃遗留并清理。未取得互斥锁则拒绝同工作树新运行。`TestResults/` 下其他目录不清理。结果只有通过或失败；必需检查失败、缺少环境或未完成时返回退出码 1；人工、目标硬件和尚未实现的验证另行报告。
