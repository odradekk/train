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
# 阶段结构、公开面与 Core/Content/Scenes 隔离（始终执行）
pwsh -NoProfile -File scripts/test.ps1 -Layer stage
# 只比较当前工作区改动
pwsh -NoProfile -File scripts/test.ps1 -BaseRef HEAD
# 只选择 MSTest 项目
pwsh -NoProfile -File scripts/test.ps1 -Layer fast,integration -Project core,content
# 引擎层、Windows 导出冒烟与资产导出验证
pwsh -NoProfile -File scripts/test.ps1 -Layer engine -ExportWindows
# 全仓验收，包含全部层及 Windows 导出冒烟
pwsh -NoProfile -File scripts/test.ps1 -All -Layer all -ExportWindows
```

`-Layer` 接受 `fast`、`integration`、`engine`、`stage` 的一个或多个值；`all` 仍等于 fast、integration、engine，默认是 `fast`。逗号多选和 PowerShell 数组都可用，例如 `& ./scripts/test.ps1 -Layer @('fast', 'integration')`。`-All` 强制全部项目和独立检查，含所请求的引擎层，仍只执行所选层；它与 `-Project` 冲突。`-Project` 接受 `core`、`content`、`scenes`、`godot`、`contract-support`、`contract-checks` 的一个或多个值，显式选择 MSTest 项目并跳过改动分析、契约结构与公开面、隔离、内容 Schema、引擎和阶段检查。`-ExportWindows` 仅能与未指定 `-Project` 的引擎层一起使用，并强制运行引擎层及导出冒烟。单独 `-Layer engine` 仍按改动选择。

无待运行项目或检查的层在摘要中记为 `selected: false`；所有层均未选择时明确报告、写摘要并成功退出，不构建。已选中的层实际执行零个用例、无效参数、必需工具缺失或最低用例数不足仍判失败。最低用例数表位于 `scripts/test.ps1` 的 `$projects`，引擎层使用其中的 `godot` 行；删除用例或调整分层时必须同步更新。最低数为零的项目与层组合仍执行，只允许该组合没有用例。

Scenes 快速层验证会话生命周期、不可变帧、立方体网格及其贴图方向与跨格平铺、拾取和只读预览；Core／Content 为配置情景的固定读视图，世界生成由测试门控，不能作为真实线路或节点生成证据；立方体 UV 的纯 .NET 检查不替代 #298 的真实 Godot 纹理采样联调。Scenes 已接入实际隔离入口，单次是否通过以运行日志与摘要为准；后续实现范围见[体素契约](contracts/scenes.md)。

快速层运行未标记 `integration` 的 MSTest；集成层运行真实文件读写和子进程用例，并按下述规则选择独立检查。Schema 需要 Python 3 与 `jsonschema`（CI 固定为 Python 3.11、`jsonschema==4.25.1`）；它不验证跨文件引用或生产加载器行为。

引擎层在真实 Godot 中运行 `Tests/Engine/Train.Engine.Tests.csproj` 的当前 App 集成用例，需要 `GODOT`。独立测试程序集固定使用 Chickensoft.GoDotTest 2.0.47 和 GodotTestDriver 3.1.83；根项目仅在 Debug 编译 `Tests/Engine/Bootstrap/Runner.tscn` 的启动代码，加载外部测试 DLL 并生成 JUnit；当前引擎最低用例数为 23。Release、ExportDebug 和 ExportRelease 排除测试代码、启动场景和测试依赖。这些用例不证明目标四模块的隔离装配。显式导出选择另用 `scripts/checks/run-export-smoke.ps1` 执行一个导出冒烟检查，对同一次 Windows 导出运行两次 `--smoke`，需要 Windows 和匹配的导出模板。Benchmark、长测、额外平台和人工观察不在 `all` 中。

`-ExportWindows` 还运行资产导出探针：从空构建目录链接 `App/Assets/` 同份生产源码及 `GlbFixture.cs`，编译探针入口，仅引用 Core／Content 契约，记录实际编译输入、引用和加载程序集，并在导出程序中核对 PCK／Mod 的原始字节、Keep File 原名及失败注入。探针同时核对真实游戏 PCK 的内置 PNG 字节，并扫描本次 PCK 中的 `art-source`／`.blend`；仓库没有这类源文件输入夹具，这次内容扫描不能证明源文件排除规则有效。行为断言集中在 `Tests/AssetExport/ProbeRunner.cs`，脚本只编排构建、导出和运行；真实解码、线程与缓存用例仍由现有引擎 runner 执行。探针在 `asset-export/staging/` 独立构建，导出到 `asset-export/export/asset-probe.exe`，运行时检查自己的 PCK 和真实游戏 PCK。它不替代完整宿主隔离、四模块联调或生产资源装配。接入和所有权见[资产端口](godot-assets.md)。

## 按改动选择

默认基准 `-BaseRef origin/master` 必须在本地存在。入口以 `git merge-base <BaseRef> HEAD` 为比较起点，包含该点之后的提交及暂存、未暂存、未跟踪改动；文件列表禁用重命名识别，移动前后路径都参与规则匹配。入口不自动 fetch。dotnet-affected 7.0.1 以 `Train.sln` 限定项目图，沿 MSBuild 项目依赖选出受影响项目，再从六个已登记 MSTest 项目中选择；归档和调用样例不进入这张项目图。引擎层复用同一次改动分析：dotnet-affected 输出包含 `Tests/Engine/Train.Engine.Tests.csproj`，或改动命中下述引擎路径规则时选中；该项目引用根 `Train.csproj`，所以根项目及其依赖的影响沿同一张项目图传播。选中后执行当前全部引擎用例。`-All` 和 `-ExportWindows` 强制引擎，不依赖改动结果。单独选择 `stage` 时完全跳过改动分析，不要求基准引用存在，始终执行结构、公开面与 Core/Content/Scenes 隔离三项检查；`-Layer stage,engine` 不强制引擎，阶段全量正确性使用 `-All -Layer all`。

以下共享文件改动选择全部 MSTest 项目：`Contracts/**`、任意层级的 `Directory.Build.*`、`global.json`、`docs/content/schemas/**`、`Tests/ContractSupport/**`、`Train.sln`、`.config/dotnet-tools.json`、`scripts/test.ps1`、`scripts/checks/**`。工作流 YAML 不在共享列表中。

引擎路径规则复用上述整体构建与测试入口条目，但不包含 `docs/content/schemas/**` 和 `Tests/ContractSupport/**`；另外包含 `Tests/Engine/**`、`project.godot`、`export_presets.cfg`、`builtin/**`、`addons/**`，以及 `archive/`、`docs/`、`prototypes/`、`Tests/` 之外的 `*.tscn`、`*.tres`、`*.uid`、`*.import`。`Tests/Engine/` 内的资源已由该目录规则覆盖。普通宿主 MSTest 被选中不表示引擎也被选中。

集成层的契约结构、公开面和调用样例检查，在 `Contracts/**`、任意 `*.csproj`、任意层级的 `Directory.Build.*`、`Train.sln`、`prototypes/**`、`Tests/ContractChecks/**`、`scripts/checks/project-boundaries.json` 或 `docs/contracts/public-api.txt` 改动时进入默认选择；`builtin/**` PNG／GLB 及其 `.import` 改动也选择该检查，由 C# 工具要求全部内置资产使用 Keep File。内容示例 Schema 检查只在 `docs/content/**` 改动时进入默认选择。隔离重建在任意 `*.csproj`、任意层级 `Directory.Build.*`、`Train.sln`、`scripts/checks/project-boundaries.json` 或 `Tests/ContractChecks/**` 改动时进入默认选择。`-All` 选择结构、公开面、隔离和 Schema 独立检查。

dotnet-affected 退出码 166 表示空项目集合；其他非零退出码、120 秒分析超时或进程清理失败均使入口失败。工具输出写入本次运行目录。仓库路径保留 Git 返回的大小写，再用 `Path.GetFullPath` 转为原生完整路径与分隔符，避免工具的路径前缀匹配漏选。

执行改动分析时，摘要的 `selection` 保存基准引用、共同祖先 SHA、改动文件、共享规则、工具输出项目集合和最终选择。无论请求哪些层，这些运行的 `selection.engine` 都记录引擎是否受影响；逐文件的 `engineRules` 记录命中的引擎路径规则，`affectedProjects` 中的引擎测试项目解释依赖图触发，`mode: all` 或 `exportWindows: true` 解释强制选择。每个文件的 `project` 是包含它的最近项目目录对应的项目，嵌套项目优先；仅当该项目也在工具输出集合中时记录，否则为 `null`。此对应只作解释，选择仍由工具结果及共享规则决定。单独选择 `stage` 时摘要记录 `mode: stage`，不记录改动分析或引擎选择。

## 执行与边界

选中 MSTest 项目、契约或隔离检查、引擎层或阶段层时，入口构建一次 `Train.sln`，MSTest 使用 Microsoft.Testing.Platform 和 `--no-build`。单独的 Schema 检查只调用 Python，不构建。C# 工具随解决方案构建；structure 通过工具生成的遍历项目，在一个 MSBuild 进程内采集全部登记项目的引用、编译项、友元和输出路径。四组调用样例在该进程内恢复、重新求值并构建，使用 `BuildProjectReferences=false` 和 `RestoreRecursive=false` 避免重复构建共享契约；样例仍不加入解决方案。引用采集、样例构建、隔离构建与工具自测的夹具构建均使用 `-maxCpuCount:1`，限制并发任务内的 MSBuild 节点数。verify 检查公开面和类型归属，isolate 按[矩阵选择规则](contracts/testing.md#实际隔离与结构检查)重新构建隔离输入并运行用例，引用判定与 structure 共用。阶段层始终依次执行这三个命令；混选集成层和阶段层时，各自执行所需检查。

各层测试执行预算为快速 30 秒、集成 180 秒、引擎 300 秒。集成层选中的 MSTest 项目和 structure、verify、isolate、Schema 共用一个任务队列与 180 秒截止时间，通过已有的 `ForEach-Object -Parallel` 执行，最多 4 个任务并发，较重任务排在前面；汇总与清理仍由父流程完成。快速层保持原有 MSTest 并行执行方式，每个项目保留各自的最低用例数、TRX、超时和挂起转储。阶段层不设总预算，串行执行 structure、verify、isolate，子进程上限分别为 300、120、300 秒；引擎检查仍串行执行。构建准备上限为 300 秒，Godot 导入、导出各为 120 秒。超时导致失败并保留可用转储，不通过放宽预算掩盖超时。必要的 Godot 导入只做一次；导出冒烟的两次运行共享一次 Windows 发布导出。同一工作树不允许并发运行入口。

`AppTests` 验证 App 启动时通过 Godot 资源加载内置内容，以及重载输入发布新内容代次；日志用例通过 `OS.AddLogger` 注册的 `Godot.Logger` 回调观测真实 Godot 输出，同时检查控制台缓冲、后台事件队列、JSONL 和退出时的日志释放；菜单用例发送退出按钮信号，以替换的退出操作断言 App 请求退出。`ContentTests` 使用真实内容源和物理夹具验证重载新增、移除及旧快照保留。`SimulationTests` 推进真实帧并通过控制台操作验证会话控制，另检查命令、步进、检查点、重绑定，以及故障回滚、冻结和诊断。引擎结果要求退出码、JUnit 结果及最低用例数均满足入口规则；实际执行、通过、失败和跳过数写入摘要，不以帧数或超时退出证明通过。

导出冒烟的两次运行分别使用空用户目录和有效 Mod 用户目录，都必须退出 0 并提供 JSONL 证据。生产 `--smoke` 验证内容重载提交、核心粮食和 PNG 加载，不接收测试专用预期参数。脚本核对 Mod 运行的 Info 事件：`content_mods_scan_summary` 的 `accepted_count=1`、`isolated_count=0`；`content_reload_summary` 的状态为 `Committed` 或 `CommittedWithWarnings`，accepted 数比空目录运行增加 1。

检查输入、临时文件、运行日志、导出产物和测试用户数据写入运行目录，`TEMP`、`TMP`、`TMPDIR` 指向其中的 `tmp`。Godot 原生日志通过 `--log-file` 定向；导入和导出用 `--recovery-mode` 跳过编辑器插件。导入、引擎测试和导出冒烟将 Windows 的 `APPDATA`、`LOCALAPPDATA`，或非 Windows 的 `XDG_CONFIG_HOME`、`XDG_DATA_HOME`、`XDG_CACHE_HOME` 指向运行目录；只有真实游戏导出步骤保留真实 APPDATA 以访问模板。资产探针的构建、导出和运行始终使用隔离 APPDATA；导出时在其中创建指向真实 `Godot/export_templates` 目录的 junction，结束后只删除本次创建的链接本身。真实游戏导出是环境隔离的例外：Godot 仍可能改写真实 `Godot/editor_settings-4.7.tres` 和 `Godot/mono/build_logs/`；不能由导出通过推断真实 Godot 目录未变化。引擎测试与导出冒烟的两次运行使用隔离用户目录，运行结束后清理目录；JSONL 日志先复制到运行证据目录。入口比较运行前后的快照：在 `bin/`、`obj/`、`.godot/`、`.git/` 和本次运行目录之外新增或删除文件、Git 状态或 HEAD 变化、真实 `Godot/app_userdata/Train` 下文件变化，均导致整次失败。运行期间不要修改工作树；保留已有 `override.cfg`。

## 结果与保留

Core 集成层的回放语料位于 `Tests/Core/Sessions/ReplayCorpus/`：提交的 JSONL 命令日志经公开 `PrepareReplay`／`ICoreReplay` 重放，每两步保存生产检查点，与 Verify 31.26.0 管理的 `*.verified.txt` 比较。每个检查点一行，格式为 `<步数>:<序号> <SHA-256> <规范文本>`；`.gitattributes` 固定基准为 UTF-8、LF，Windows 的 `core.autocrlf=true` 检出也保持该格式。运行身份固定，位置、规范检查点文本和 SHA-256 都参与基准比较；首个字段分歧由 `CompareCheckpoints` 报告。反向用例只在内存中篡改中间基准，不改提交文件。

更新基准时先运行 `pwsh -NoProfile -File scripts/test.ps1 -Project core -Layer integration`。Verify 已关闭 DiffEngine 弹窗；失败信息给出 `TEMP` 下本次接收目录和基准输出副本路径。检查 `*.received.txt` 的每个变化与预期一致后，将其复制到源码语料目录并改名为同名 `*.verified.txt`，再运行集成层。首次新增语料没有已接受基准，因此首次失败是初始化流程；已有基准的失败仍须先诊断。接收文件和旧基准副本都位于本次运行目录的 `tmp` 内，测试通过会删除临时语料目录，统一入口通过后只保留摘要。直接运行测试时遵循系统 `TEMP`；不要将运行目录中的旧基准副本当成源码更新目标。

每次运行目录为 `TestResults/<选择层>-<yyyyMMddTHHmmssfff>-<7位提交>/`，目录名中的时间为 UTC，只产生一份 `summary.json` 摘要。摘要记录提交、工作区状态、选择、构建、各项目与各层的执行／通过／失败／跳过数（引擎用例来自 JUnit）、用时和结论。跳过数记入摘要，整次运行仍可以通过，但跳过的用例不算已验证；脏工作区结果不能证明所记录的干净提交通过。

隔离工具在所有结局下删除自己拥有的隔离树，删除失败判失败，保留清单和日志。通过后只保留摘要；`-ExportWindows` 另保留日志、`*.process.json`、探针耗时记录和 `asset-export/probe-data/`（含 `build-inputs/`），供复核断言结果与隔离输入。导出二进制、`asset-export/staging/` 及隔离环境目录删除。失败保留 TRX、JUnit、日志、输入和转储等全部证据，其中引擎结果为 `engine-godot.junit.xml`，原生日志为 `engine-godot-native.log`。入口只保留最近 10 个符合命名规则的运行目录；入口持有该工作树的命名互斥锁时，没有 `summary.json` 的运行目录一律视为崩溃遗留并清理。未取得互斥锁则拒绝同工作树新运行。`TestResults/` 下其他目录不清理。结果只有通过或失败；必需检查失败、缺少环境或未完成时返回退出码 1；人工、目标硬件和尚未实现的验证另行报告。

定位单个引擎用例时，先构建 `dotnet build Tests/Engine/Train.Engine.Tests.csproj -c Debug` 并完成资源导入；设置 `TRAIN_ENGINE_TEST_ASSEMBLY` 为 `.godot/mono/temp/bin/Debug/Train.Engine.Tests.dll` 的绝对路径，`TRAIN_ENGINE_REPORT` 为要保存的 JUnit 文件绝对路径。然后运行 `& $env:GODOT --headless --path . --scene res://Tests/Engine/Bootstrap/Runner.tscn -- --run-tests=<类>[.<方法>] --quit-on-finish`，将选择表达式替换为类名或类名加方法名；此直接运行不执行统一入口的最低用例数、目录隔离与快照检查。
