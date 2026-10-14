# 统一测试入口

`scripts/test.ps1` 是 PowerShell 7 测试入口。[Testing standard](implement-sop.md#testing-standard)决定改动需要哪些证据；[CI](ci.md)使用同一入口。四模块的目标验证责任见[独立构建与契约验证](contracts/testing.md)，普通 C# 测试不替代 Godot 隔离装配或人工验收。

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
# 阶段检查（始终执行，Windows 另含导出、截图与吞吐阈值）
pwsh -NoProfile -File scripts/test.ps1 -Layer stage
# 只比较当前工作区改动
pwsh -NoProfile -File scripts/test.ps1 -BaseRef HEAD
# 只选择 MSTest 项目
pwsh -NoProfile -File scripts/test.ps1 -Layer fast,integration -Project core,content
# 引擎层、Windows 导出冒烟与资产导出验证
pwsh -NoProfile -File scripts/test.ps1 -Layer engine -ExportWindows
# 全仓阶段验收，包含全部正确性层及阶段检查
pwsh -NoProfile -File scripts/test.ps1 -All -Layer all,stage
```

`-Layer` 接受 `fast`、`integration`、`engine`、`stage` 的一个或多个值；`all` 仍等于 fast、integration、engine，默认是 `fast`。逗号多选和 PowerShell 数组都可用，例如 `& ./scripts/test.ps1 -Layer @('fast', 'integration')`。`-All` 强制全部项目和独立检查，含所请求的引擎层，仍只执行所选层；它与 `-Project` 冲突。`-Project` 接受 `core`、`content`、`scenes`、`godot`、`contract-support`、`contract-checks`、`cli` 的一个或多个值，显式选择 MSTest 项目并跳过改动分析、契约结构与公开面、隔离、内容 Schema、引擎和阶段检查。`-ExportWindows` 仅能与未指定 `-Project` 的引擎层一起使用，并强制运行引擎层及导出冒烟。单独 `-Layer engine` 仍按改动选择。

`-Project cli -Layer fast,integration` 验证命令行工具：快速层检查 PNG 结构与明确不支持的 GLB；集成层启动真实 CLI 进程，加载已提交的 `builtin/core`，比较重复运行及保存退出后恢复的检查点，并严格重放本次录制中的接受／拒绝命令。已提交示例的重新制作比较及临时副本篡改用例以 `OSCondition` 限定 Windows；同机跨进程检查在所有平台运行。比较前只显式核对 `CoreInputId`，不同报需要重新制作；种子、生成器版本、生成内容修订和配置交给检查点比较报告字段。示例测试比较检查点摘要并确认提交文件未变，不要求重制存档字节一致。这些检查不重跑内容制备脚本，也不提供 Godot、Scenes 或四模块阶段验收证据。工具与配方用法见[基础示例](../examples/README.md)。

无待运行项目或检查的层在摘要中记为 `selected: false`；所有层均未选择时明确报告、写摘要并成功退出，不构建。已选中的层实际执行零个用例、无效参数、必需工具缺失或最低用例数不足仍判失败。最低用例数表位于 `scripts/test.ps1` 的 `$projects`，引擎层使用其中的 `godot` 行；删除用例或调整分层时必须同步更新。最低数为零的项目与层组合仍执行，只允许该组合没有用例。

Scenes 快速层验证会话生命周期、不可变帧、立方体网格及其贴图方向与跨格平铺、拾取、只读预览、线路与节点生成规则、平台涂装、列车摆放和线路—节点切换；Core／Content 为配置情景的固定读视图，几何使用生产构造和独立采样检查，会话时序另用受控后台门控验证，摆放使用省略地形的构建夹具；生产线路 A → 空节点 → 线路 B 的跟随、停留与首帧坐标交接在进程内异步验证，并断言空节点只有地形与交汇轨道、无站台或混凝土读取。快速层不提供大型真实样例的首建和重绑定成本证据。它们不提供真实四模块联调证据；立方体 UV 与黄色体素的纯 .NET 检查不替代 #298 的真实 Godot 纹理、涂装和画面联调。Scenes 已接入实际隔离入口，单次是否通过以运行日志与摘要为准；后续实现范围见[体素契约](contracts/scenes.md)。

快速层运行未标记 `integration` 或 `stage` 的 MSTest；集成层运行真实文件读写和子进程用例，并按下述规则选择独立检查。Scenes 集成层最低 1 个用例：宿主测试进程与一个独立子进程各构建同面双入口节点，逐列核对生产地形的接缝道床、站内道床和站台地面，并比较部件键与帧内对象身份、顺序。子进程结果和日志写入本次运行目录下的临时目录，成功后删除该临时目录，失败时保留诊断。Schema 需要 Python 3 与 `jsonschema`（CI 固定为 Python 3.11、`jsonschema==4.25.1`）；它不验证跨文件引用或生产加载器行为。

引擎层在真实 Godot 中运行 `Tests/Engine/Train.Engine.Tests.csproj` 的当前 App 集成用例，需要 `GODOT`。独立测试程序集固定使用 Chickensoft.GoDotTest 2.0.47 和 GodotTestDriver 3.1.83；根项目仅在 Debug 编译 `Tests/Engine/Bootstrap/Runner.tscn` 的启动代码，加载外部测试 DLL 并生成 JUnit；当前无界面引擎最低用例数由脚本中的项目表维护。Release、ExportDebug 和 ExportRelease 排除测试代码、启动场景和测试依赖。这些用例不证明目标四模块的隔离装配。显式导出选择另用 `scripts/checks/run-export-smoke.ps1` 执行一个导出冒烟检查，对同一次 Windows 导出运行两次 `--smoke`，需要 Windows 和匹配的导出模板。Benchmark、长测、额外平台和人工观察不在 `all` 中。

`-ExportWindows` 还运行资产导出探针：从空构建目录链接 `App/Assets/` 同份生产源码及 `GlbFixture.cs`，编译探针入口，仅引用 Core／Content 契约，记录实际编译输入、引用和加载程序集，并在导出程序中核对 PCK／Mod 的原始字节、Keep File 原名及失败注入。探针同时核对真实游戏 PCK 的内置 PNG 字节，并包含隐藏项扫描本次 PCK：出现 `.mcp.json`、`global.json`、`addons/` 下任何条目或 `App/ArtCapture/` 时判失败；原有 `art-source`／`.blend` 扫描保留，仓库没有这类源文件输入夹具，这次内容扫描不能证明源文件排除规则有效。行为断言集中在 `Tests/AssetExport/ProbeRunner.cs`，脚本只编排构建、导出和运行；真实解码、线程与缓存用例仍由现有引擎 runner 执行。探针在 `asset-export/staging/` 独立构建，导出到 `asset-export/export/asset-probe.exe`，运行时检查自己的 PCK 和真实游戏 PCK。它不替代独立宿主隔离或完整四模块联调。接入和所有权见[资产端口](godot-assets.md)。

Debug 有窗口显示帧预览复用生产 `App._Process`，以固定内容和有限情景替身检查渲染、输入和最小化接线。先构建并导入，再启动预览：

```powershell
dotnet build Tests/Engine/Train.Engine.Tests.csproj -c Debug
& $env:GODOT --headless --path . --editor --import
& $env:GODOT --path . --scene res://Tests/Engine/Bootstrap/ScenePreview.tscn
```

入口先显示预览选择，选择“显示帧预览”使用上述固定显示帧。“进入”打开跟随场景；“下一情景”依次切换原点移动、部分就绪与空间变更；“切换车辆”在同列车的两个跟随目标间切换；“节点”和“线路”用于比较中键平移范围；“退出”或 Escape 返回验证地图。“最小化”设置真实窗口模式，从任务栏恢复。状态栏显示模拟步数、Present／Observe 次数、Session／Request、对象根与雾组实例 ID、根节点退出树次数及 Camera／Environment 激活状态，可检查连续帧、目标切换、稳定节点身份与最小化期间模拟推进。这是人工观察入口，不生成 JUnit，也不代表真实体素工厂或完整玩法验收。

选择“检查光照预览”启动 `InspectionLightingPreview`。该手动预览通过真实 `ScenesFactory.Create()` 生成一条线路和一列小型列车，复用生产 App、世界视图、相机和渲染；Core/Content 使用 ContractSupport 的固定读视图与明确的线路、车辆及素材输入，并保持暂停。按反引号打开控制台，执行 [`lighting`](console/lighting.md) 切换三种检查光照或恢复；“线路 / 重新进入”支持线路平移及重入，“跟随列车”切换跟随目标。它提供真实 Scenes 生成、真实 Godot 渲染的窗口检查入口，不生成截图或 JUnit，不证明完整四模块联调或 Godot 完整隔离；后两者分别归 #298 和 #424。自动截图使用[美术检查截图入口](art/08-asset-pipeline-and-review.md#检查人与记录)，从普通存档恢复真实编组，证据放在工作树 `.godot/validation/<名称>/`。预览代码仅在 Debug 可用，导出构建不包含此入口。

选择“开发检查预览”启动 `DeveloperToolsPreview`，仍使用同份生产 App、开发窗口、协调器和文件流程；Core、Content、Scenes 为界面列明的有限契约替身。普通会话只支持运行到步 2，重放验证顺序为单步到 `1:0`、推进到 `2:0`、导出普通存档、通过存档面板导入并读取副本。预览启动时在 `TestResults/developer-tools-preview/` 写入固定复现包，窗口或辅助按钮可执行这些操作；场景图层开关请求替身提供彩色线。它不验证真实 Core 重放、生产 Scenes 图层生成、完整 Godot 隔离或四模块联调。

## 阶段检查与候选基准

`stage` 在所有平台始终运行 structure、verify、Core/Content/Scenes/Godot 隔离重建、两项生产 Core 确定性用例和独立 Release Benchmark。确定性用例通过生产命令日志与回放接口，在新进程逐检查点比较回放，以及每步写盘、读盘、恢复后与连续运行比较。它们属于阶段层，快速层和集成层不包含这些用例。

Windows 阶段层还执行 Windows 导出、空用户目录与有效 Mod 的导出后运行、资产导出探针、有窗口的主菜单 GPU 截图比较，以及吞吐阈值判定。非 Windows 仍运行 Benchmark 并保存结果；导出、导出后运行、资产探针、截图和阈值判定在摘要中记为未执行，不记为通过。当前 Linux 检查不需要渲染上下文，不使用 xvfb；无界面 Godot 使用 Dummy 音频。

发布导出冒烟保留生产内容、PNG 和有效 Mod 检查，同时从 PCK 的 `res://examples/basic-through/save.json` 经过正常恢复流程打开示例，不执行配方。它检查原身份与新身份均为有效且不同的 UUID、位置 200:0、暂停、未保存，以及玩家菜单隐藏示例入口；不保存副本或读回。副本保存到新 UUID 目录且原件不变由宿主测试在临时目录使用真实文件 I/O 验证。开发编辑器的可见入口由引擎测试覆盖；调试导出另做人工验证，命令、日志和截图记录在 Issue 交付评论，不加入 `test.ps1`。这些检查不替代 #298 四模块联调验收。

截图复用引擎测试程序集、Bootstrap 和 JUnit，仅在 Windows 有窗口运行中执行。普通 headless engine 在执行前排除截图套件，不把它计为跳过。输入为 [windows-main-menu.png](../Tests/Engine/Baselines/windows-main-menu.png) 与 [windows-main-menu.json](../Tests/Engine/Baselines/windows-main-menu.json)，通过物理路径读取；`Tests/.gdignore` 阻止编辑器为测试 PNG 生成导入文件。比较先转为 RGBA8。候选值为 `MaxChannelDelta=32`、`MaxDifferentPixelFraction=0.00002`：任一通道差大于 32 的像素计为明显差异，明显差异像素数除以总像素数大于 0.00002 才失败。`comparison.json` 每次记录任意差异像素数、明显差异像素数及比例、最大通道差；`difference.png` 用红色区分明显差异、黄色区分轻微差异，通过时也保留。尺寸不一致、缺少基准或容差均失败。

吞吐输入为 [windows-core-train-loop.json](../Tests/Benchmarks/Baselines/windows-core-train-loop.json)。BenchmarkDotNet 以独立 Release 构建测量 `core-train-loop-v1`：复用 Core 世界和铁路恢复夹具，通过真实 `CoreFactory` 创建 `ICoreSession`，一辆列车持续循环经过三个车站，每批 65,536 个完整模拟步。每次迭代在计时外重建会话并设置循环计划，计时仅覆盖 `ICoreSession.Advance`；结束时在计时外核对实际完成步数、无故障、循环计划仍活动，以及每个计划车站至少两次真实到达，并释放会话。`throughput.json` 保存负载身份、列车数、批次步数、每步耗时、样本数和步/秒，BDN 日志和报告保留原始测量。它只测量该固定列车运行负载的核心吞吐，不提供帧耗时或完整游戏 8× 运行证据。Windows 候选规则使用 `baselineStepsPerSecond=190000` 和 `maximumSlowdown=3`（三次独立完整测量中的最低吞吐向下取整至每秒一万步，等待用户确认），仅当实测步/秒乘以 3 小于候选基线时失败；摘要记录实测值、基线和实测除以基线的比值。缺少基准、负载身份不符或无效数字仍失败。Linux 只记录结果，不应用 Windows 阈值。

需要单独重新采集吞吐时，显式构建并运行不在 `Train.sln` 中的 Benchmark 项目：

```powershell
dotnet build Tests/Benchmarks/Train.Benchmarks.csproj -c Release
dotnet run --project Tests/Benchmarks/Train.Benchmarks.csproj -c Release --no-build -- --artifacts <本次运行目录>
```

提交的 PNG、容差和吞吐基线均为**等待用户阶段验收确认的候选基准**，只能用于检查机制，不能据此宣布阶段或产品性能验收通过。更新截图时，在目标 Windows 图形配置下至少用三个独立 Godot 进程和独立用户目录采集，核对菜单内容并比较各图的通道差和明显差异像素比例，再替换 PNG 与容差 JSON。更换 GPU、驱动或 Godot 版本后重新核对。需要重新采集吞吐时，使用上面的 BenchmarkDotNet 命令做多次独立完整测量，记录外部负载和波动，以实测结果提出基线；不要用单次较快结果掩盖波动。候选值及其替换需用户阶段验收确认，替换后重跑完整阶段检查。原始截图、差异、设备信息和测量结果只保留在忽略的运行目录，长期证据引用写入 Issue 评论，不提交运行报告。

## 按改动选择

默认基准 `-BaseRef origin/master` 必须在本地存在。入口以 `git merge-base <BaseRef> HEAD` 为比较起点，包含该点之后的提交及暂存、未暂存、未跟踪改动；文件列表禁用重命名识别，移动前后路径都参与规则匹配。入口不自动 fetch。dotnet-affected 7.0.1 以 `Train.sln` 限定项目图，沿 MSBuild 项目依赖选出受影响项目，再从六个已登记 MSTest 项目中选择；归档和调用样例不进入这张项目图。引擎层复用同一次改动分析：dotnet-affected 输出包含 `Tests/Engine/Train.Engine.Tests.csproj`，或改动命中下述引擎路径规则时选中；该项目引用根 `Train.csproj`，所以根项目及其依赖的影响沿同一张项目图传播。选中后执行当前全部引擎用例。`-All` 和 `-ExportWindows` 强制引擎，不依赖改动结果。单独选择 `stage` 时完全跳过改动分析，不要求基准引用存在，始终执行[阶段检查](#阶段检查与候选基准)；`-Layer stage,engine` 不强制引擎，阶段全量检查使用 `-All -Layer all,stage`。

以下共享文件改动选择全部 MSTest 项目：`Contracts/**`、任意层级的 `Directory.Build.*`、`global.json`、`docs/content/schemas/**`、`Tests/ContractSupport/**`、`Train.sln`、`.config/dotnet-tools.json`、`scripts/test.ps1`、`scripts/checks/**`。工作流 YAML 不在共享列表中。

CLI 及其项目依赖的源码改动由 dotnet-affected 沿引用图选择。`examples/**`、`builtin/**` 和 `project.godot` 是 CLI 的额外输入，改动时也选择 CLI 测试；摘要逐文件记录这三组补充规则 `cliRules`。

引擎路径规则复用上述整体构建与测试入口条目，但不包含 `docs/content/schemas/**` 和 `Tests/ContractSupport/**`；另外包含 `Tests/Engine/**`、`project.godot`、`export_presets.cfg`、`builtin/**`、`addons/**`，以及 `archive/`、`docs/`、`prototypes/`、`Tests/` 之外的 `*.tscn`、`*.tres`、`*.uid`、`*.import`。`Tests/Engine/` 内的资源已由该目录规则覆盖。普通宿主 MSTest 被选中不表示引擎也被选中。

集成层的契约结构、公开面和调用样例检查，在 `Contracts/**`、任意 `*.csproj`、任意层级的 `Directory.Build.*`、`Train.sln`、`prototypes/**`、`Tests/ContractChecks/**`、`scripts/checks/project-boundaries.json` 或 `docs/contracts/public-api.txt` 改动时进入默认选择；`builtin/**` PNG／GLB 及其 `.import` 改动也选择该检查，由 C# 工具要求全部内置资产使用 Keep File。内容示例 Schema 检查只在 `docs/content/**` 改动时进入默认选择。隔离重建在任意 `*.csproj`、任意层级 `Directory.Build.*`、`Train.sln`、`scripts/checks/project-boundaries.json` 或 `Tests/ContractChecks/**` 改动时进入默认选择。`-All` 选择结构、公开面、隔离和 Schema 独立检查。

dotnet-affected 退出码 166 表示空项目集合；其他非零退出码、120 秒分析超时或进程清理失败均使入口失败。工具输出写入本次运行目录。仓库路径保留 Git 返回的大小写，再用 `Path.GetFullPath` 转为原生完整路径与分隔符，避免工具的路径前缀匹配漏选。

执行改动分析时，摘要的 `selection` 保存基准引用、共同祖先 SHA、改动文件、共享规则、工具输出项目集合和最终选择。无论请求哪些层，这些运行的 `selection.engine` 都记录引擎是否受影响；逐文件的 `engineRules` 记录命中的引擎路径规则，`affectedProjects` 中的引擎测试项目解释依赖图触发，`mode: all` 或 `exportWindows: true` 解释强制选择。每个文件的 `project` 是包含它的最近项目目录对应的项目，嵌套项目优先；仅当该项目也在工具输出集合中时记录，否则为 `null`。此对应只作解释，选择仍由工具结果及共享规则决定。单独选择 `stage` 时摘要记录 `mode: stage`，不记录改动分析或引擎选择。

## 执行与边界

选中 MSTest 项目、契约或隔离检查、引擎层或阶段层时，入口构建一次 `Train.sln`，MSTest 使用 SDK 的 `dotnet test --test-modules <相对 DLL 路径> --root-directory <根目录>` 运行已构建程序集，不重复构建。单独的 Schema 检查只调用 Python，不构建。C# 工具随解决方案构建；structure 通过工具生成的遍历项目，在一个 MSBuild 进程内采集全部登记项目的引用、编译项、友元和输出路径。四组调用样例在该进程内恢复、重新求值并构建，使用 `BuildProjectReferences=false` 和 `RestoreRecursive=false` 避免重复构建共享契约；样例仍不加入解决方案。引用采集、样例构建、隔离构建与工具自测的夹具构建均使用 `-maxCpuCount:1` 和 `UseSharedCompilation=false`，限制并发任务内的 MSBuild 节点数并禁用共享编译服务器。verify 检查公开面和类型归属，isolate 按[矩阵选择规则](contracts/testing.md#实际隔离与结构检查)选择模块，串行复制独立输入、冷构建、独立查询 MSBuild 事实并运行各自非阶段用例，首个失败后停止；已构建的隔离测试 DLL 同样由 SDK 的 `dotnet test --test-modules` 运行，引用判定与 structure 共用。隔离重建排除 `stage` 类别，由阶段入口单独运行这些用例。阶段层始终依次执行这三个命令，再执行确定性用例和 Benchmark；混选集成层和阶段层时，各自执行所需检查。

各层测试执行预算为快速 90 秒、集成 330 秒、引擎 300 秒。集成层选中的 MSTest 项目和 structure、verify、Schema 共用一个任务队列与 330 秒截止时间，通过已有的 `ForEach-Object -Parallel` 执行，最多 4 个任务并发，较重任务排在前面；汇总与清理仍由父流程完成。队列正常结束或检查失败后，先停止集成层计时并保存用例计数，再独立运行选中的 isolate，使用 450 秒上限，不占用队列或集成层预算。收到停止请求时，不再启动尚未开始的独立隔离检查。快速层保持原有 MSTest 并行执行方式，每个项目保留各自的最低用例数、TRX、超时和挂起转储。阶段层不设总预算，串行执行 structure、verify、isolate，子进程上限分别为 300、120、450 秒；确定性阶段用例上限为 180 秒，Benchmark 为 600 秒，窗口截图为 120 秒；引擎检查仍串行执行。构建准备上限为 300 秒，Godot 导入、导出各为 120 秒。诊断区分已启动后超时（`TimedOut`）与截止时尚未启动（`NotStarted`），点名对应检查并记录逐项秒数；缺少 TRX 不覆盖超时诊断。超时用于发现挂起的任务，导致失败并保留可用转储；正常执行耗时增长时，按[预算调整原则](contracts/testing.md#预算调整原则)增加预算，不以延长预算掩盖挂起。必要的 Godot 导入只做一次；导出冒烟的两次运行共享一次 Windows 发布导出。同一工作树不允许并发运行入口。

`AppTests` 和 `ContentTests` 使用生产默认装配，验证内置内容就绪、新局、真实保存后读档、控制台与输入重载，覆盖业务更新、纯表现／无变化及失败保留核心与内容配对；日志用例观测 Godot 输出、控制台缓冲、后台队列、JSONL 和退出释放。菜单用例验证退出请求。`HostSimulationTests` 通过注入宿主及真实帧验证控制台会话控制、步进、检查点、重绑定、故障冻结和诊断。引擎结果要求退出码、JUnit 结果及最低用例数均满足入口规则；实际执行、通过、失败和跳过数写入摘要，不以帧数或超时退出证明通过。

`RecordingTests` 在真实 App 场景中注入有限情景的核心／内容契约替身，验证初始起点自动落盘、报告面板状态、从内存窗口导出、故障文件及冻结后导出。路径由测试直接给定，不自动化原生 FileDialog；`recording-artifacts/` 写在本次 JUnit 报告目录。它执行同一份生产宿主和文件代码，但不证明完整 Godot 隔离或默认真实工厂装配。纯宿主录制测试归 `Tests/Godot/Recording/`；真实 ZIP 与发布失败用例属于 integration。

导出冒烟的两次运行分别使用空用户目录和有效 Mod 用户目录，都必须退出 0 并提供 JSONL 证据。生产 `--smoke` 等待真实内容准备完成，验证 `core:staple-food`、PNG 解码和已发布包，不接收测试专用预期参数。脚本核对 Info 事件 `content_reload_summary` 的状态为 `Committed` 或 `CommittedWithWarnings`，并读取 `application_smoke_summary` 的 `passed`、`packages`、`voxels`。Mod 夹具采用 manifest v2、voxel v3；Mod 运行必须包含包 `550e8400-e29b-41d4-a716-446655440000` 及定义 `550e8400-e29b-41d4-a716-446655440000:smoke-food`，包数比空目录运行增加 1。

检查输入、临时文件、运行日志、导出产物和测试用户数据写入运行目录，`TEMP`、`TMP`、`TMPDIR` 指向其中的 `tmp`。Godot 原生日志通过 `--log-file` 定向；主项目导入用 `--recovery-mode` 跳过编辑器插件，导出不使用该参数，让移除 MCP autoload 的导出钩子执行。发布冒烟的空用户目录与有效 Mod 两次正常运行中，原生日志出现任何引擎或脚本错误均判失败。导入、引擎测试、主游戏和资产探针的导出及导出冒烟将 Windows 的 `APPDATA`、`LOCALAPPDATA`，或非 Windows 的 `XDG_CONFIG_HOME`、`XDG_DATA_HOME`、`XDG_CACHE_HOME` 指向运行目录。两个 Windows 发布导出只从真实模板目录复制 `4.7.2.stable.mono/windows_release_x86_64.exe` 到各自隔离环境，不创建模板 junction；真实模板目录只读，复制的模板在导出 `finally` 中删除，前序步骤失败时也清理。引擎测试与导出冒烟的两次运行使用隔离用户目录，运行结束后清理目录；JSONL 日志先复制到运行证据目录。入口比较运行前后的快照：在 `bin/`、`obj/`、`.godot/`、`.git/` 和本次运行目录之外新增或删除文件、Git 状态或 HEAD 变化、真实 `Godot/app_userdata/Train` 下文件变化，均导致整次失败。真实 `Godot/app_userdata/Train` 的前后比较仍会被本机其他 Godot 进程的改动触发。运行期间不要修改工作树；保留已有 `override.cfg`。

非 Windows 的 MTP Unix socket 路径有 103 字节上限。入口在 `/tmp` 创建指向本次 `tmp` 的唯一短符号链接，通过 [MTP 的 `TESTINGPLATFORM_PIPE_DIRECTORY`](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-config#testingplatform_pipe_directory-environment-variable) 使用它；管道文件仍位于运行目录。结束时只删除短链接并恢复环境变量，摘要记录链接清理结果；删除失败使整次失败。

## 结果与保留

Core 集成层的回放语料位于 `Tests/Core/Sessions/ReplayCorpus/`：这组固定基准仅在 Windows 执行（`OSCondition`）；Linux 将其记为跳过，跨进程确定性由阶段层的生产自回放用例验证。提交的 JSONL 命令日志经公开 `PrepareReplay`／`ICoreReplay` 重放，每两步保存生产检查点，与 Verify 31.26.0 管理的 `*.verified.txt` 比较。每个检查点一行，格式为 `<步数>:<序号> <SHA-256> <规范文本>`；`.gitattributes` 固定基准为 UTF-8、LF，Windows 的 `core.autocrlf=true` 检出也保持该格式。运行身份固定，位置、规范检查点文本和 SHA-256 都参与基准比较；首个字段分歧由 `CompareCheckpoints` 报告。反向用例只在内存中篡改中间基准，不改提交文件。

更新基准时先运行 `pwsh -NoProfile -File scripts/test.ps1 -Project core -Layer integration`。Verify 已关闭 DiffEngine 弹窗；失败信息给出 `TEMP` 下本次接收目录和基准输出副本路径。检查 `*.received.txt` 的每个变化与预期一致后，将其复制到源码语料目录并改名为同名 `*.verified.txt`，再运行集成层。首次新增语料没有已接受基准，因此首次失败是初始化流程；已有基准的失败仍须先诊断。接收文件和旧基准副本都位于本次运行目录的 `tmp` 内，测试通过会删除临时语料目录，统一入口通过后只保留摘要。直接运行测试时遵循系统 `TEMP`；不要将运行目录中的旧基准副本当成源码更新目标。

每次运行目录为 `TestResults/<选择层>-<yyyyMMddTHHmmssfff>-<7位提交>/`，目录名中的时间为 UTC，只产生一份 `summary.json` 摘要。摘要记录提交、工作区状态、选择、构建、各项目与各层的执行／通过／失败／跳过数（引擎用例来自 JUnit）、用时和结论。独立隔离检查在 `summary.checks` 中以 `name=isolate`、`layer=isolation` 记录 `seconds`、`timedOut`、`notStarted` 和 `cleanupSucceeded`，其耗时不计入 `layers.integration`；不增加 `-Layer` 选项。跳过数记入摘要，整次运行仍可以通过，但跳过的用例不算已验证；脏工作区结果不能证明所记录的干净提交通过。

隔离工具在所有结局下删除自己拥有的隔离树，删除失败判失败，保留清单和日志。普通运行通过后只保留摘要；阶段运行还保留 `benchmark/` 下的吞吐 JSON、BDN 日志和报告，Windows 另保留 `screenshot/` 下的实际 PNG、渲染身份和差异 JSON，以及 `stage-screenshot.junit.xml`。Windows 阶段或 `-ExportWindows` 另保留日志、`*.process.json`、探针耗时记录和 `asset-export/probe-data/`（含 `build-inputs/`），以及本次隔离的 `inputs.json`、`first-party-assemblies.json` 和 `isolation-summary.json`，供复核断言结果、输入清单、实际解析程序集和总耗时。导出二进制、`asset-export/staging/` 及隔离环境目录删除。失败保留 TRX、JUnit、日志、输入和转储等全部证据，其中引擎结果为 `engine-godot.junit.xml`，原生日志为 `engine-godot-native.log`。入口只保留最近 10 个符合命名规则的运行目录；入口持有该工作树的命名互斥锁时，没有 `summary.json` 的运行目录一律视为崩溃遗留并清理。未取得互斥锁则拒绝同工作树新运行。`TestResults/` 下其他目录不清理。结果只有通过或失败；必需检查失败、缺少环境或未完成时返回退出码 1；人工、目标硬件和尚未实现的验证另行报告。

定位单个引擎用例时，先构建 `dotnet build Tests/Engine/Train.Engine.Tests.csproj -c Debug` 并完成资源导入；设置 `TRAIN_ENGINE_TEST_ASSEMBLY` 为 `.godot/mono/temp/bin/Debug/Train.Engine.Tests.dll` 的绝对路径，`TRAIN_ENGINE_REPORT` 为要保存的 JUnit 文件绝对路径。然后运行 `& $env:GODOT --headless --path . --scene res://Tests/Engine/Bootstrap/Runner.tscn -- --run-tests=<类>[.<方法>] --quit-on-finish`，将选择表达式替换为类名或类名加方法名；此直接运行不执行统一入口的最低用例数、目录隔离与快照检查。

单独重新采集截图时复用上述 Bootstrap：先将 `APPDATA`、`LOCALAPPDATA` 指向本次采集的独立目录，设置 `TRAIN_ENGINE_SCREENSHOT=1`、`TRAIN_SCREENSHOT_OUTPUT` 为证据目录，并去掉 `--headless`。运行截图套件后从该目录取得 `actual.png`、`rendering.json` 和已有基准下的 `comparison.json`；缺少基准时仍保存实际 PNG，然后按预期失败。每次采集使用新的进程和用户目录；采集结束恢复环境变量并清理自己创建的用户目录。普通无界面用例使用 `TRAIN_ENGINE_SCREENSHOT=0`。
