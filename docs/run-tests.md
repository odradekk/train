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
# 阶段检查（始终执行，Windows 另含导出与截图）
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

`-Project cli -Layer fast,integration` 验证命令行工具：快速层检查 PNG 结构、GLB 实际顶点与层级变换、表面及不支持特性的事实；集成层启动真实 CLI 进程，加载已提交的 `builtin/core`，比较重复运行及保存退出后恢复的检查点，并严格重放本次录制中的接受／拒绝命令。经济短配方覆盖租赁、装卸、自动买入及具体命令回执，核对恢复后的查询样本与连续运行后缀；有限现车短配方核对报价扣款、待售减少及推进后不补车。委托分支配方让真实起始车队按起步贸易的货物和地点满载采购后，连续提交两次空来源货车委托，验证首次受理扣款与工钱、再次 `insufficient_goods` 回执的完整缺货身份，以及同一步拒绝前后的钱包、库存、任务和工钱查询不变，并验证本次录制的严格重放。基础贯通、容量紧张、委托分支、撤退和受损现车交易的已提交示例检查，以及临时副本篡改用例以 `OSCondition` 限定 Windows；配方计数期望用临时配方副本验证达成时制作成功、达不到时 `make` 失败且不写存档、未知计数键在读取时拒绝；同机跨进程检查在所有平台运行。比较前只显式核对 `CoreInputId`，不同报需要重新制作；种子、生成器版本、生成内容修订和配置交给检查点比较报告字段。示例测试比较检查点摘要并确认提交文件未变，不要求重制存档字节一致。四游戏小时起步贸易与一天租约到期的长配方不进入日常测试层，交付前以 Release 分别执行 replay、save／resume／compare、make／check；同一存档用同种构建配置制作和检查。标准世界 6 游戏小时配方 `standard-world-long` 直接用 CLI `run` 诊断，用法见[示例手册](../examples/README.md#标准世界长测)。这些检查不重跑内容制备脚本，也不提供 Godot、Scenes 或四模块阶段验收证据。工具、输出和并行长验证用法见[示例存档与配方](../examples/README.md)。

无待运行项目或检查的层在摘要中记为 `selected: false`；所有层均未选择时明确报告、写摘要并成功退出，不构建。已选中的层实际执行零个用例、无效参数、必需工具缺失或最低用例数不足仍判失败。最低用例数表位于 `scripts/test.ps1` 的 `$projects`，引擎层使用其中的 `godot` 行；删除用例或调整分层时必须同步更新。最低数为零的项目与层组合仍执行，只允许该组合没有用例。

Scenes 快速层验证会话生命周期、不可变帧、立方体网格及其贴图方向与跨格平铺、拾取、只读预览、线路与节点生成规则、列车摆放和线路—节点切换。#326 的模块用例覆盖模板原坐标与地块号联结、站点槽位及贯通线、核心站台号映射与数量不符诊断、建筑身份与未变网格复用及迟到结果拒收、两条停车线的排序和容量上限、资源数量变化与独立对象身份、缺失内容来源。Core／Content 使用固定读视图，几何使用生产构造和独立采样，会话时序使用受控后台门控；部分摆放夹具省略地形。资源对象网格复用或静态布局输入不变的断言本身不证明真实地形重建行为。生产线路 A → 空节点 → 线路 B 的跟随、停留与首帧坐标交接在进程内异步验证，并检查空节点没有站台或混凝土读取。快速层不提供大型真实样例的首建和重绑定成本、真实四模块联调或 Godot 纹理与画面证据；core-city 的完整内容和真实引擎表现、#299 阶段验收仍需后续验证。Scenes 已接入实际隔离入口，单次是否通过以运行日志与摘要为准；实现范围见[体素契约](contracts/scenes.md)。

快速层运行未标记 `integration` 或 `stage` 的 MSTest；集成层运行真实文件读写和子进程用例，并按下述规则选择独立检查。Scenes 集成层最低 1 个用例：宿主测试进程与一个独立子进程各使用同一完整的同面双入口站点夹具，先串行构建参考，再共享同一份准备数据，以每批两个任务真实重建全部地形区块与轨段；首批地形与轨段门控同时入场，各进程内按语义键逐部件比较网格、材质、姿态、高度、拾取和诊断。后续使用并行结果逐列核对生产地形的接缝道床、站内道床和站线轨床和排队引入线地面，并保留原有跨进程的部件键、帧内对象身份与顺序比较。子进程结果和日志写入本次运行目录下的临时目录，成功后删除该临时目录，失败时保留诊断。Schema 需要 Python 3 与 `jsonschema`（CI 固定为 Python 3.11、`jsonschema==4.25.1`）；示例 Schema 不验证跨文件引用；正式经济检查读取关联 JSON，但不替代生产加载器测试。

正式经济检查还要求燃料生产每批产出的燃料能量大于该流程及直接上游开采的耗电；同时报告模板本地吞吐与满速燃料能量收支。仓库规模预算仅由生成器推导，不在检查器复算。静态关系通过不证明新局可交易或能持续生产，这些结论需要真实核心的市场查询与推进证据。

引擎层在真实 Godot 中运行 `Tests/Engine/Train.Engine.Tests.csproj` 的当前 App 集成用例，需要 `GODOT`。独立测试程序集固定使用 Chickensoft.GoDotTest 2.0.47 和 GodotTestDriver 3.1.83；根项目仅在 Debug 编译 `Tests/Engine/Bootstrap/Runner.tscn` 的启动代码，加载外部测试 DLL 并生成 JUnit；当前无界面引擎最低用例数由脚本中的项目表维护。Release、ExportDebug 和 ExportRelease 排除测试代码、启动场景和测试依赖。这些用例不证明目标四模块的隔离装配。显式导出选择另用 `scripts/checks/run-export-smoke.ps1` 执行一个导出冒烟检查，对同一次 Windows 导出运行两次 `--smoke`，需要 Windows 和匹配的导出模板。Benchmark、长测、额外平台和人工观察不在 `all` 中。

`-ExportWindows` 还运行资产导出探针：从空构建目录链接 `App/Assets/` 同份生产源码及 `GlbFixture.cs`，编译探针入口，仅引用 Core／Content 契约，记录实际编译输入、引用和加载程序集，并在导出程序中核对 PCK／Mod 的原始字节、Keep File 原名及失败注入。探针同时核对真实游戏 PCK 的内置 PNG 字节，并包含隐藏项扫描本次 PCK：出现 `.mcp.json`、`global.json`、`addons/` 下任何条目或 `App/ArtCapture/` 时判失败；原有 `art-source`／`.blend` 扫描保留，仓库没有这类源文件输入夹具，这次内容扫描不能证明源文件排除规则有效。行为断言集中在 `Tests/AssetExport/ProbeRunner.cs`，脚本只编排构建、导出和运行；真实解码、线程与缓存用例仍由现有引擎 runner 执行。探针在 `asset-export/staging/` 独立构建，导出到 `asset-export/export/asset-probe.exe`，运行时检查自己的 PCK 和真实游戏 PCK。它不替代独立宿主隔离或完整四模块联调。接入和所有权见[资产端口](godot-assets.md)。

Windows 主游戏导出后，独立的 `export-optimizations` 检查（阶段层为 `stage-export-optimizations`）读取全部托管 DLL 的元数据，跳过原生 DLL；任何托管 DLL 带 `DisableOptimizations`，或缺少 Train／Core／Content／Scenes 程序集，均判失败。

Debug 有窗口显示帧预览复用生产 `App._Process`，以固定内容和有限情景替身检查渲染、输入和最小化接线。先构建并导入，再启动预览：

```powershell
dotnet build Tests/Engine/Train.Engine.Tests.csproj -c Debug
```

导入和启动预览均使用[隔离子进程片段](local-environment.md#agent-手动启动-godot)，Godot 参数分别为 `--headless --path . --editor --import` 和 `--path . --scene res://Tests/Engine/Bootstrap/ScenePreview.tscn`；保留片段中的 `--log-file`，每次使用新的运行目录。

入口先显示预览选择，选择“显示帧预览”使用上述固定显示帧。“进入”打开跟随场景；“下一情景”依次切换原点移动、部分就绪与空间变更；“切换车辆”在同列车的两个跟随目标间切换；“节点”和“线路”用于比较中键平移范围；“退出”或 Escape 返回验证地图。“最小化”设置真实窗口模式，从任务栏恢复。状态栏显示模拟步数、Present／Observe 次数、Session／Request、对象根与雾组实例 ID、根节点退出树次数及 Camera／Environment 激活状态，可检查连续帧、目标切换、稳定节点身份与最小化期间模拟推进。这是人工观察入口，不生成 JUnit，也不代表真实体素工厂或完整玩法验收。

选择“检查光照预览”启动 `InspectionLightingPreview`。该手动预览通过真实 `ScenesFactory.Create()` 生成一条线路和一列小型列车，复用生产 App、世界视图、相机和渲染；Core/Content 使用 ContractSupport 的固定读视图与明确的线路、车辆及素材输入，并保持暂停。按反引号打开控制台，执行 [`lighting`](console/lighting.md) 切换三种检查光照或恢复；“线路 / 重新进入”支持线路平移及重入，“跟随列车”切换跟随目标。它提供真实 Scenes 生成、真实 Godot 渲染的窗口检查入口，不生成截图或 JUnit，不证明完整四模块联调或 Godot 完整隔离。自动截图使用[美术检查截图入口](art/08-asset-pipeline-and-review.md#检查人与记录)，从普通存档恢复真实编组，证据放在工作树 `.godot/validation/<名称>/`。预览代码仅在 Debug 可用，导出构建不包含此入口。

选择“节点列车帧序列”启动 `NodeTransitionPreview`，以固定 Core/Content 读视图和真实 Scenes 工厂，覆盖双轨线路从 2 号轨进站、W 等候与获站台、停靠后从 1／2 号轨出站、途经、同边异轨返回和同侧回转。它核对真实车辆 Node3D 与 FindPose 的姿态，并对配对边界关键帧断言位置、方向和高度连续；记录进出轨号、Origin、车辆姿态、相机、边界误差和加载状态，连同 PNG 输出至 `.godot/validation/node-transition-preview-<时间戳>/`。受控关键帧不能替代生产 Core 时序、连续行驶或窗口性能证据。

选择“生产 App 逐帧取证”启动 `ProductionNodeCapture`，通过正常 UI 创建或载入游戏并安排计划，选择列车后点击“跟随并记录”；seed=40 的 Demo 可点“Demo 停靠 / 途经计划”，由既有列车命令解析与提交路径设置停靠 13、途经 8、停靠 5 的计划并启动列车，再用正常模拟速度按钮开始运行。它使用生产装配，在每个实际绘制帧后记录核心位置、车辆显示姿态、FindPose、相机、Origin 和加载状态，阶段改变时截图，输出至 `.godot/validation/production-node-capture-<时间戳>/`。验收时需检查相邻帧相机与车辆位移，并结合边界两侧 Origin 区分坐标平移与真实跳动；该入口本身不生成 JUnit 或自动验收结论。

选择“开发检查预览”启动 `DeveloperToolsPreview`，仍使用同份生产 App、开发窗口、协调器和文件流程；Core、Content、Scenes 为界面列明的有限契约替身。普通会话只支持运行到步 2，重放验证顺序为单步到 `1:0`、推进到 `2:0`、导出普通存档、通过存档面板导入并读取副本。预览启动时在 `TestResults/developer-tools-preview/` 写入固定复现包，窗口或辅助按钮可执行这些操作；场景图层开关请求替身提供彩色线。它不验证真实 Core 重放、生产 Scenes 图层生成、完整 Godot 隔离或四模块联调。

选择“玩家编辑器预览”启动 `EditorPreview`，使用同份生产 App、编辑协调、界面和渲染，Core／Content／Scenes 为明确的有限情景替身。引擎 `EditorUiTests` 检查同帧拾取、当前转动线框、嵌套输入坐标、笔刷整笔应用、大范围修改确认、过期预览拒绝、Enter 拒绝和失败后手动重试；设置 `TRAIN_UI_CAPTURE_DIR` 后有窗口运行该类会保存 `player-editor.png` 与渲染身份。它不验证尚未接入的真实编辑会话、用户库生产装配或阶段 3 的完整设计与委托闭环。

开发创作的宿主集成用例位于 `Tests/Godot/Authoring/`：同份纯 .NET 写入器以真实文件验证来源冲突、显式删除、Keep File 追加文本和部分完成；测试程序集的 `--package-write-process` 子进程在第 N 项发布完成后发信号并阻塞，父进程收到信号后终止，核对已写文件与未列入补丁的普通包文件，不依赖随机时机。这里保留的 `draft.json` 是普通包文件，不是 `user://authoring-drafts/packages/…` 下的草稿。协调用例用列明情景替身验证关闭保护、候选不激活与失败协调；`AuthoringUiTests` 在真实 Godot 中检查原始输入脏标记和逐类型字段。

`Tests/Engine/AuthoringIntegrationTests` 使用真实 Core／Content／Scenes 工厂和隔离用户目录下的临时本地 Mod，验证网格编辑、真实定义草稿编码、写前叠加校验、逐文件写入、普通重载激活，以及来源冲突、非网格表单写回和真实内容拒绝。新建模板写入显示文本并经普通重载进入内容；错误类别的库存蓝图在写文件前被普通加载拒绝。未完成生物聚落字段的 `invalid_field` 仍按普通诊断路径显示字段。`PackageRestartTests` 的 `--package-restart-child` 子进程执行真实 `PackageWritePlan` 与生产写入器，首项完成后发信号阻塞，父进程确定性终止；另一子进程普通加载，断言真实内容给出缺失资产诊断且磁盘不自动恢复。`AuthoringPreviewTests` 接入真实模板帧、重复地块编号的数组下标拾取与刷新相机；设置 `TRAIN_UI_CAPTURE_DIR` 后有窗口运行保留地表轮廓截图供视觉检查。自动用例不写仓库 `builtin/core`；手工 `--authoring-core-probe` 入口使用编辑器二进制验证源码 core 的打开、网格编辑、草稿、写包与普通重载，并在 `finally` 恢复原始字节，另记录运行前后 Git 状态。完整 #300 联调、保存／恢复／严格重放仍需阶段验收。Keep File 导入器本身沿用既有资产导出证据，本票只验证追加项文本。

## 远端检查

推送自己的任务分支后，可把完整回归交给 Linux runner。工作流选择、事件白名单与 job 上限见[CI](ci.md#linux-夜间运行)；长示例的平台分工见[示例手册](../examples/README.md#重制与长验证)。

```powershell
# 分支全量检查：全部正确性层和 Linux 阶段检查
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=nightly
# 长示例各自触发；可选名称见示例手册
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=long-example -f example=<示例>
# 正式测量：先与协调者确认 runner 时段和主机条件
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=measure -f measure_scenarios=scene-node,scene-edge -f measure_input=examples/standard-world/save.json -f measure_repeat=3 -f measure_seconds=0
# 测试机器上的正式窗口长测；先与协调者确认 runner 时段和主机条件
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=window-soak -f soak_seconds=7200
# 阶段验收：工作流使用 master，代码使用固定的完整 40 位提交 SHA
gh workflow run windows-checks.yml --repo odradekk/train --ref master -f selection=long-example -f example=lease-expiry -f commit=<完整 SHA>
# 显式请求 Windows 常规或完整正确性检查
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=routine
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=correctness

# 找到本次运行，核对 headSha、事件、选择及时间
gh run list --repo odradekk/train --workflow windows-checks.yml --branch <分支> --limit 10 --json databaseId,headSha,event,displayTitle,status,conclusion
gh run watch <run_id> --repo odradekk/train --exit-status
gh run view <run_id> --repo odradekk/train
# 下载该次所有 artifact；使用新的本地证据目录
gh run download <run_id> --repo odradekk/train --dir TestResults/remote-<run_id>
```

Actions 页面和 job 摘要也可查看结果、逐条命令退出码与耗时，并下载 artifact。排队和运行时间不表示通过；必须核对结论、被测提交和摘要中的实际检查。失败或未完成的命令不能作为成功证据。工作流取自 `--ref`，可选 `commit` 指定被测代码；留空时二者使用触发的 ref。核对 artifact 中 `ci/environment.txt` 的 `head=`（被测提交）、`workflow=`（工作流版本），以及摘要中的 commit；Actions 的 `headSha` 对应工作流版本，不能代替实际被测提交。编排和测量脚本取自工作流版本，CLI、配方、SDK 配置及全量检查入口取自被测提交。工作流文件已在默认分支，手动 dispatch 可使用任务分支上的版本。

Linux 全量结果可承担同份代码的完整回归，但不替代真实窗口交互／截图、目标实机性能、Windows 导出及导出后运行、已提交 Windows 示例的重制比较，以及 Windows 固定重放语料等平台限定检查。上述检查仍在相应本机环境运行，按[Testing standard](implement-sop.md#testing-standard)判断证据是否适用；最终 PR head 的 Windows routine CI 仍须通过。Linux CLI 长示例不提供 Godot、Scenes 或完整阶段联调证据。

## 观察无关检查与窗口测量

`ObservationIndependenceTests` 由引擎层执行，使用默认 App 的真实 Core／Content／Scenes 装配和 `examples/basic-through/recipe.json`。参考运行使用 60 Hz、1×、不打开局部场景；五组变体覆盖 30／120 Hz、不规则间隔、2×／4×／8×、运行中切换倍速、暂停中连续命令，以及线路／节点／跟随场景的打开、切换和关闭。间隔由测试注入生产帧驱动，不依赖机器实际 FPS；在 `BoundaryCompleted` 请求完整边界暂停。每条命令前后及目标位置先核对位置、种子、运行身份和 `CoreInputId`，再委托核心比较检查点并定位首个字段差异。它提供真实模块的观察无关证据，不提供窗口性能或完整 Demo 验收。

同存档变体先生成测试自己的世界，经生产 `SaveService.Save` 发布临时存档，再由三个独立 App 经 `SaveService.Load` 读取同一文件，分别以 1×、固定 8× 和不规则帧间隔 8× 执行相同配方，复用上述完整边界检查点比较，并核对起始存档字节未变。

标准世界变体读取 `examples/standard-world/save.json`，那里节点战斗、线路战斗、施工、生产和站台排队都在进行。参考结果不经过宿主帧驱动：同一进程直接用核心和内容工厂恢复同一存档，每 100 步推进并捕获检查点，共 600 步。三个 App 经生产 `SaveService.StartExample` 打开同一份文档，分别以 1×／60 Hz、8× 不规则帧间隔加暂停帧、8× 下依次打开节点战斗场景、线路战斗场景和跟随场景再返回地图，在相同位置与参考结果逐个比较；所有宿主变体共有的驱动错误也会因此暴露。

`scripts/measure/export-measurement.ps1` 生成并保留 Windows／Linux Release 导出，环境要求见[本地 Windows 验证](local-environment.md#windows-export-and-runtime-validation)。它拒绝脏工作树，结束时核对 HEAD 未变且仍干净。用户目录隔离为 Windows 的 `APPDATA`／`LOCALAPPDATA` 或 Linux 的 XDG 目录；只复制 Release 模板，结束时删除副本。默认用已安装模板，`-TemplatePath` 可指定已核对的模板；Linux 使用 `Linux` 预设，输出 `train.x86_64`。构建和测量的输出均须为新目录或空目录；仓库内须被 Git 忽略，推荐 `TestResults/`。统一测试入口会删除自己的导出二进制，测量须保留完整导出。

测量导出同样执行共享的程序集优化检查，结果记入 `export-summary.json` 的 `optimizations` 步骤；检查失败时拒绝该构建。

`scripts/measure/measure.ps1` 复用这个已有导出，不重新构建。保留完整导出目录中的 EXE、旁置 PCK 和唯一的 `Train.dll`；输入为普通存档：

```powershell
# 构建一次，后续测量复用此固定二进制；保留完整目录与构建日志
pwsh -NoProfile -File scripts/measure/export-measurement.ps1 -OutputDirectory TestResults/measurement-build
# 默认运行 scene-route-1x 和 scene-route-8x；两者都不需要参考文件
pwsh -NoProfile -File scripts/measure/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/standard-world/save.json -OutputDirectory TestResults/measure-route
# 单独复测一组场景，仍执行三个独立进程
pwsh -NoProfile -File scripts/measure/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/standard-world/save.json -OutputDirectory TestResults/measure-route-8x -Scenario scene-route-8x
# 在 PowerShell 中多选；不把逗号列表作为 -File 的单个参数传入
& ./scripts/measure/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/standard-world/save.json -OutputDirectory TestResults/measure-selected -Scenario @('scene-route-1x', 'scene-route-8x')
```

报告从已加载程序集的 `AssemblyInformationalVersion` 读取 `identity.game_build`／`identity.build_commit`，从本地构建提交的 `git rev-parse <commit>:docs/contracts/public-api.txt` 读取 `identity.public_api_git_blob`。脚本记录 EXE／PCK／`Train.dll` 与输入哈希，二进制来源不取当前 HEAD。

场景经生产 `--measure=<场景>` 运行，输入存档不变。报告核对该程序集的 SHA-256 与构建提交，记录窗口／viewport、GPU／驱动、录制、自动存档和遇敌自动降速设置。`identity.encounter_action` 记录实际值 `value` 和产品默认值 `default_value`，仅测量主动关闭时带 `reason`。`timing.frames` 记录每个 `FramePostDraw` 间的单调时间、绝对完成步数、请求倍速、暂停状态和模拟工作时间，保留卡顿帧。单位为秒和字节；分位数用 nearest-rank，不插值；实际倍速为完成步数除以 60 和采样秒数。模拟工作时间含帧驱动推进及边界消费者，不含候选准备和渲染提交；触及 5 ms 预算的次数不作失败阈值。

报告判定见[说明](../scripts/measure/README.md#逐项性能判定)。

使用 1920×1080 真实窗口。外部每秒记录总 CPU 占用及 Godot／dotnet 进程。正式测量用 [Linux runner](ci.md#linux-夜间运行)；开发机在协调者安排的安静窗口串行交替对照，记录电源与外部负载。OS 文件缓存和 Windows 驱动着色器缓存不清空；Linux 每轮隔离 `XDG_CACHE_HOME`，Mesa 着色器缓存初始为空。截图另跑，回读与 PNG 编码不作性能证据。

### 场景打开、离开节点与规模测量

```powershell
# 同一 HEAD、同一输入先生成参考；参考运行不计入性能样本
pwsh -NoProfile -File scripts/measure/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/starting-trade/save.json -OutputDirectory TestResults/scene-reference -Scenario scene-reference -Repeat 1
# 常态负载预演；正式重复次数由测量计划指定
pwsh -NoProfile -File scripts/measure/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/starting-trade/save.json -OutputDirectory TestResults/scene-node-preview -Scenario scene-node -ReferencePath TestResults/scene-reference/scene-reference-1/scene-reference.json -Repeat 2
# 内存循环的短预演；默认 DurationSeconds=0 时运行两小时
pwsh -NoProfile -File scripts/measure/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/starting-trade/save.json -OutputDirectory TestResults/scene-memory-preview -Scenario scene-memory -ReferencePath TestResults/scene-reference/scene-reference-1/scene-reference.json -Repeat 2 -DurationSeconds 30
```

| 情景 | 实际路径 |
| --- | --- |
| `scene-reference` | 独立参考运行，暂停至初始 Ready 后恢复仿真，再观察 10 秒；各空间首次 Ready 及焦点再移动 16 m 后的 Ready 帧记录公开对象和地形，输出 `scene-reference.json`，不作性能样本。世界里有节点 3 和边 15 时一并记录，供 `scene-soak` 使用 |
| `scene-map` | 同存档的新进程地图内存基线，不打开场景 |
| `scene-node` | 地图首次打开节点 2、返回地图后重开、节点→线路 11、线路→节点、读档回地图后立即再开节点 |
| `scene-node-first` | 只测地图首次打开节点 2，用于规模点 |
| `scene-edge` | 地图首次打开线路 11、返回地图后重开、读档回地图后立即再开线路 |
| `scene-follow` | 地图首次点击列车 1，跟随其参考中心所在的车辆 |
| `scene-follow-8x` | 经同一 UI 路径跟随，遇敌自动降速设为 `None`；8× 预热 5 秒后采样 120 秒，可用 `DurationSeconds` 改为短预演 |
| `scene-route-1x`、`scene-route-2x`、`scene-route-4x`、`scene-route-8x` | seed 40 起步贸易存档，跟随列车 1 完成线路→节点→线路；终点节点显示帧定位到车头（按编组朝向取首车或末车）后，补齐交接后的车身轨迹。记录 Scenes／实际显示的前方覆盖、车身覆盖、边界截断和未知；倍率取自原生帧循环。仅 `scene-route-8x` 将遇敌自动降速设为 `None` |
| `scene-route-capture-1x`、`scene-route-capture-8x` | 同一路线的独立截图运行，截图开销不计作计时证据 |
| `scene-memory` | 重复打开节点并返回地图；每 10 轮及结束后执行完整 GC；默认两小时，不使用其时延作正式性能证据 |
| `scene-soak` | 正式窗口长测，输入为 `examples/standard-world/save.json`，见下文[窗口长测](#窗口长测) |
| `scene-capture` | 单独记录部分就绪、Ready 画面及雾区拾取；截图回读与编码使该进程不能作时延或内存性能证据 |
| `scene-editor` | 地图稳态 5 秒并强制 GC 后，经蓝图库「复制到本局…」打开泰坦样机 `core:titan-armed-vehicle`，记录点击到会话就绪和首个 Ready 编辑帧绘制的时间、样机格数、部件数与编辑帧几何量；保持编辑器 30 秒采样帧时间与内存（`DurationSeconds` 可改为 1–120 秒）；再执行与 `Tests/Engine/TitanEditingTests.cs` 相同的编辑序列：样机内一块空长方体整块加入 5,000 格（确认），49 次单格删除凑满 50 步，连续撤销 50 步、重做 50 步，每步等新修订实际绘制后才做下一步，`editor-step` 记录调用耗时、调用到新修订绘制的时延、新修订首次 `session.Present` 耗时（含几何构建与帧差分，并非纯几何）、首个 Present 至首次提交的 process 帧数（两端包含，同帧提交为 1）与该帧区间的实际绘制最长帧（含 Present 阻塞帧）、行文本生成及 `ShowElements` 列表更新耗时；编辑器自身 `SceneDisplay` 的准备与提交耗时沿用 `scene-resource-frames.jsonl`，格数、步数或撤销重做结果不符时测量失败；最后经「退出」「放弃本次修改」回到地图，地图稳态 5 秒后再强制 GC |

`-TrainId`、`-NodeId`（应用参数 `--measure-train`、`--measure-node`）分别替换上表的列车 1 和节点 2，线路仍为 11；跟随对象与地图点击相同，是该列车参考中心所在的车辆（见[设计 01](design/01-voxel-engine-and-scenes.md#跟随列车时的场景切换)）。目标列车不存在或编组为空、目标节点不存在时测量失败。参考运行按同样的目标打开，因此每个列车或节点目标各需一份参考。`ready-geometry` 的 `followed_train` 按成员记录已发布车体及其子对象的网格、顶点和三角形数；`active-load` 的列车状态带编组车辆数和位置。负载计数还记录战斗（地点和参战方数）、生物群（地点、活动、数量和存活数）及生物总数；打开记录在点击时（`load_at_click`）和观察结束时（`load`）各记一次，`scene-follow-8x` 在采样窗口开始和结束各写一条 `sample-load`。

`Repeat` 控制每情景的新进程数量，默认是 3；`DurationSeconds=0` 使用情景默认时长。`scene-follow-8x` 的 `DurationSeconds`（应用参数 `--measure-duration`）只能缩短默认的 120 秒采样窗口。输入规模及测量、探针和判定方法见[场景预算验证报告](research/scene-budget-verification.md)。

除 `scene-reference`、`scene-map`、`scene-editor` 和 `scene-route-*` 外，所有 `scene-*` 情景必须传 `-ReferencePath`，指向同一构建提交、同一输入 SHA 的 `scene-reference.json`；参考运行不计入性能样本。整路测量使用默认 `DurationSeconds=0`，非零值只作提前结束的预演。

整路首次打开仅对初始空间单列 `route-analysis.json.startup`：从首个可操作帧起，以显示帧 `displayed_readiness` 在 5 秒内达到 Ready 判定。状态为 `passed`、`failed`、`unknown` 或 `not_observed`；历史记录缺少 readiness 为未知，初始空间从未可操作为未观察。初始空间在 Ready 前切走时，下一空间的 Ready 不能补算成功。初始空间这 5 秒内的判定帧以 `excluded_reason=initial_startup` 排除出 criterion 2 始终覆盖统计，逐帧原始覆盖仍保留；恰好第 5 秒起恢复统计，期间发生的跟随空间切换也立即恢复统计。该分类不改变 D18 near32 首次 Partial 的语义。

整路前方距离沿后续实际车头轨迹计算。Core 在列车参考中心过界时切换位置，因此空间边界估计为跨空间拼接轨迹中新空间首帧的车头位置，沿弧长回退半列车总长；容差取 2 米与跨界帧行驶距离的较大者。到达估计边界即截断，边界容差内发现的缺口记 `boundary_limited`；其他缺口仍计失败，轨迹不足或不能拼接仍记未知。车身覆盖独立检查，不因前方边界截断而豁免。

输出目录保留 `summary.json`，每个进程的子目录保留以下文件：

| 文件 | 内容 |
| --- | --- |
| `identity.json`、`report.json` | 构建／输入身份、游戏报告；编排不改写报告。`timing`：`scene-follow-8x` 为 8× 采样窗口，`scene-memory` 为初始地图窗口，其余非参考情景为最后地图保持窗口；打开过程看操作记录 |
| `process.json`、`process.log`、`godot.log` | 结果与日志。Linux：`exit_code` 为游戏退出码（信号终止 `-N`，未知 `null`），`signal` 含名称和编号，`launcher_exit_code` 为外层退出码。`process_diagnostics` 分别保留日志的 WASAPI／AUDCLNT_E_DEVICE_INVALIDATED 原始行 |
| `game-process.json` | 仅 Linux；gamescope 内的 Python 记录器写出的游戏退出码及信号 |
| `external-load.jsonl` | 外部系统负载采样 |
| `scene-frames.jsonl`、`scene-operations.jsonl`、`scene-collections.jsonl`、`scene-probe-costs.jsonl` | 逐帧状态、打开操作、GC 和全过程探针耗时记录（含读档）；路径由报告 `identity.scene_budget` 引用 |
| `scene-reference.json` | 仅参考情景生成的参考数据 |
| `route-frames.jsonl`、`route-ranges.jsonl`、`route-analysis.json` | 整路逐帧轨迹、范围变化及离线覆盖结果；不能把未知或观察不足当作通过 |
| 截图 | 对应截图情景生成的画面 |

场景内存写在 `scene-frames.jsonl`、操作记录及 `identity.scene_budget` 的稳态值中。设计 00「私有字节」在 Windows 上读取 `PrivateUsage`，峰值读取 `PeakPagefileUsage`；Linux 上读取 `/proc/self/status` 的 `RssAnon + VmSwap`（缺少 `VmSwap` 时为 0），私有峰值取 0.2 秒采样的最大值。所有场景共用这一处读法；两个平台的计数并非完全相同。工作集及峰值工作集分别使用 Windows 的 `WorkingSet`／`PeakWorkingSet` 和 Linux 的 `VmRSS`／`VmHWM`，单位都是字节。

### 流式宿主记录

`scene-route-*` 的流式记录默认关闭。原有路线入口不传新参数时，镜头、采样与原有输出保持原行为。单组可用 `-StreamingDiagnostics -RouteCamera default|farthest`；`Scenario` 和 `RouteCamera` 均支持多值，以下组合生成完整矩阵：

```powershell
& ./scripts/measure/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/starting-trade/save.json -OutputDirectory TestResults/streaming-matrix -Scenario scene-route-1x,scene-route-8x -StreamingDiagnostics -RouteCamera default,farthest
```

矩阵使用 `examples/starting-trade/save.json` 作为输入，默认目标为列车 1，复用 `scene-route-1x`、`scene-route-8x` 各自的完整线路→节点→线路路线。输入身份保留在每个进程的 `identity.json.input.sha256`，构建身份仍由原有入口校验。每种速度分别使用默认和最远镜头，各运行 3 个新进程；目录例如 `scene-route-8x-farthest-2`。`-Repeat 1 -DurationSeconds 30` 只作预演，完整基线使用默认 `DurationSeconds=0`。显式指定 `RouteCamera` 需要启用 `StreamingDiagnostics`。


#742 的低俯仰覆盖测量在同一入口增加 `-RouteCamera default-low-orbit,farthest-low-orbit`。路线情景仍须带 `-StreamingDiagnostics`，原 `default`／`farthest` 行为保持不变。两个新增档位通过现有相机输入请求最低俯仰 0.15，分别请求默认 48 格和最远距离；路线环绕周期为 30 秒除以请求倍率（1×为30秒、2×为15秒、4×为7.5秒、8×为3.75秒），自由镜头周期为30秒；实际角度与距离仍受地形避让影响，以记录为准。仅新增环绕档位生成 `route-orbit.jsonl`，以5Hz记录相对环绕偏航/俯仰状态、请求周期与连续累计请求环绕角；实际相机和显示空间读取 streaming-frames.jsonl，通过绘制帧号关联；核实各空间完整一圈时不能只使用混入列车转向的世界偏航。

自由镜头使用 `-Scenario scene-node,scene-edge -RouteCamera default-low-orbit,farthest-low-orbit -Repeat 1 -ReferencePath <scene-reference.json>`，不带 `-StreamingDiagnostics`。每个组合仅执行原情景的首次打开操作，Ready 后持续观察 60 秒，不再重复打开；节点自由镜头最远 96 格，线路最远 240 格，使用 1×。`scene-operations.jsonl` 的 `low-orbit-sample` 每秒最多五次记录实际相机、环绕角状态、核心步数、速度与覆盖，`open.low_orbit` 保留观察起止。固定观察窗口不证明全域远景已补齐。

内存对照使用 `-NodeReadyHold`（应用开关 `--measure-node-ready-hold`），仅扩长 `NodeId` 指定节点操作的 Ready 后保持时间至 60 秒；支持 `scene-node-first` 和 `scene-reference`，参考中的其他节点及线路操作保持原时长。省略时行为不变。`scene-node-first` 增加 55 秒；`scene-reference` 的目标节点参考增加 50 秒。每次打开的 `ready_hold_seconds_requested` 记录保持时间，报告保留实际观察起止；基线和新版须使用相同测量入口改动与参数。

应用参数为 `--measure-streaming` 和 `--measure-route-camera default|farthest`。记录开启时窗口与视口仍固定为 1920×1080，使用玩家默认跟随相机：偏航跟随车体，保留默认俯仰与按地形抬高镜头，仅通过现有 `ShakeEnabled` 关闭镜头震动。默认缩放距离保持 48 格；最远档通过现有 `Zoom` 拉到 `ZoomCeiling`，跟随时线路和节点均为 240 格，96 格只适用于节点自由平移。每个绘制帧记录实际相机位置、焦点、距离和角度，包含地形避让造成的变化；距离单位为格（1 格 = 0.5 米）。

窗口标签按核心列车参考中心的有向里程划分，单位是**核心米**，只用于分组，不用于推算场景速度：

| 标签 | 半开区间与条件 |
| --- | --- |
| `node-entry` | 距即将进入／当前节点入口边界 `[-128, +128)` 米，边界前为负 |
| `node-exit` | 距当前／刚离开节点出口边界 `[-128, +128)` 米，边界前为负 |
| `line-middle` | 在线路上，沿当前行驶方向距两端均至少 128 米 |
| `outside-windows` | 上述标签均不匹配 |

线路逆行时先换算有向进度。节点全程长度为核心查询的进站、站台、出站长度之和。标签可以重叠（包括恰好距线路终点 128 米同时属于中段和入口窗口）；排序后用 `|` 连接为分析的 `window` 值，各组统计不相加。

宿主按 `Admitted.Objects` 关联部件与对象，并在锁定准备帧 C 时冻结映射。仅保留 D 和 C 的覆盖查询引用，候选替换或取消后释放旧引用。null 覆盖表示布局未知，空结果表示预期地面域外；两者均不表示已需求未显示。

宿主记录文件如下。所有 `at`、`started`、`ended` 和覆盖 `seconds` 均为相对于本次路线起点的单调真实秒；probe 开始／结束也使用同一起点。

场景事件与部件时刻的统计截止于最后一次路线绘制采样；路线结束后、写文件期间到达的后台构建事件不计入。截止时仍未观测完成的阶段保留 null。

| 文件／字段 | 含义 |
| --- | --- |
| `streaming-frames.jsonl` | 每绘制帧的 `draw`、`at`、`steps`、`coordinates`、`displayed`、显示车辆的 `displayed_position`、空间交接 `transition`、`window`、实际 `camera` 和最近覆盖样本 `coverage_draw` |
| `coordinates` | 显示车辆位置当前采用的场景坐标身份；车辆位置已加该帧原点，故同空间浮动原点变化不会制造位移 |
| `camera` | 镜头在相机支架坐标中的 `position`、`focus`，实际 `distance_cells`、世界偏航／俯仰（rad），`near_cells`／`far_cells` 是相机视深裁剪值 |
| `streaming-scenes.jsonl` | Scenes 回调仅在调用时读取时钟并入队；每个 streaming-draw 内批量回放事件，回放成本包含在该 probe 内。原始事件含 `at`、`kind` 和相关身份；Frame 仅存 `scene`、`stamp`、`parts`，不序列化 Coverage 或保留逐点数据 |
| `streaming-parts.jsonl` | 按 `attempt` 记录 Needed、Queued、Started、Built/CacheHit、Admitted、宿主资源准备及实际 SceneDisplay.Commit 结束的时刻；`outcome` 为 committed/cancelled/failed/unfinished。未观测阶段为 null，不能推定零时延；缓存外圈部件可能先提交后进入需求，此时保留原始时间，already_visible_before_demand=true，needed_to_committed_seconds=null |
| `missing_parts` | 仅统计实际已提交帧 D 所属场景中，当前 Needed 且尚未显示的部件，按最新尝试阶段计数：needed_not_queued、queued、started、built_waiting_admission、admitted_waiting_resources、prepared_waiting_commit、cancelled、failed。DemandEnded/Closed 的历史部件不计入；视野内未进入需求的缺口由覆盖分类 not_needed 报告 |
| `preview_parts` | 预览场景按 Candidate／Successor role 分别统计阶段，避免将尚未成为 D 的预览需求计入当前显示缺口 |
| `rebuilding_parts` | 部件旧 attempt 仍在 D 显示、最新 attempt 尚未显示时，按新 attempt 阶段单列重建数量，不计入 missing_parts |
| `streaming-host.jsonl` | 每条含 `display`、`at` 和 `value`；`value` 含候选序号 `candidate`、完整 `frame`、可空 `object`、`stage` 和可空 `resource_objects`。`display` 区分重新创建的宿主显示实例 |
| `ResourcesPrepared` | 仅当此对象至少一个视觉资源不能通过 `CanReuseVisual` 复用、需要新准备时记录；在该对象全部视觉资源就绪后发出，不等待整个候选。全复用的姿态更新、仅删除视觉与无视觉对象不记录 |
| `FramePreparationStarted` / `FramePrepared` / `FrameCommitted` | 内部帧级通知，`object=null`；分别在冻结候选、完整 PreparedFrame 生成及实际 Commit 结束时发出。开始通知的 `resource_objects` 列出至少一个视觉资源需要新准备的对象，其余通知为 null。全复用和无视觉候选也发，不替代逐对象资源事件。逐部件资源准备时间取同一成功候选中、与 Admitted.Objects 匹配的 ResourcesPrepared 最大值；尚未提交时可取截止时仍有效且已完成该部件准备的候选。已取消候选不参与，无新视觉对象为 null |
| `HostCancelled` | 宿主放弃此候选，`object=null`；同一帧重试分配新的 `candidate`，不沿用被取消候选的准备时间 |
| `streaming-coverage.jsonl` | 每次实际覆盖采样的 `seconds`、D 的完整 `frame`、`window`、`draw` 和 `sample`；无已提交帧时 `sample=null` |
| `sample` | 仅保留 `eligible`、`terrain`、`simplified_terrain`、`fog_only`、`neither`、三类排除计数和缺地形／无雾缺口比例；空分母的比例为 null，不能当作 0。不保留逐点坐标；仅对 fog_only／neither 点调用绑定 D 帧布局的 `FindTerrainParts` 在线归属。`part_categories` 按 category 聚合，只保存 category、fog_only、neither，不保存完整 PartId 或 terrain 计数。分类为 not_needed、needed_<stage>、outside_terrain_domain、layout_unknown；重建时仍显示旧 attempt 的部件归 displayed。同一点对应多个部件时，每个不同类别各计一次，总计可能超过缺地形点数 |
| `streaming-probes.jsonl` | 覆盖查询和整个流式绘制回调的实际起止、耗时，包含事件批量回放与覆盖归属查询成本。`coverage` 嵌套于 `streaming-draw`，后者嵌套于既有 `route-observer`，不能相加重复计算 |
| `streaming-analysis.json` | 逐区间实际倍率／场景速度、窗口汇总、覆盖缺口区间与分位数。`coverage_by_window` 按窗口汇总 category 的 fog_only／neither 计数；窗口 `sample_intervals` 只使用该窗口内的样本，缺口分析仍保留完整序列的窗口边界删失。`already_visible_before_demand` 报告先显示后进入需求的尝试数，这类样本排除 Needed→Commit 延时分布；`part_outcomes` 汇总部件尝试结果，`part_latency_seconds` 报告已观测阶段差值的 n/p50/p95/p99/max，包括 Needed→Queued 和 Admitted→ResourcesPrepared；n=0 时分位数为 null。`parts_by_window_and_kind` 按窗口和 PartKind 给出同样的分布、尝试结果、取消和未完成数；窗口用原始 Frame.Stamp/Parts 与实际绘制帧 Displayed 精确关联，取该尝试首次实际显示时的窗口，未显示尝试归 not-observed；部件 committed 时间来自实际 FrameCommitted 回调，以完整帧身份联结 Parts；逐帧是否已显示仍仅使用实际 D 的 Parts |

覆盖只查询已提交帧 `D = SceneDisplay.DisplayedFrame`，不查询最新但尚未提交的帧。对屏幕均匀分布的 32×18 个点投射射线，经实际显示根的逆变换转入 D 根空间，与稳定场景 `Y=0`（D 局部 `Y=-D.Origin.Y`）求交。没有正向交点、交点在近裁剪面之前或远裁剪面之后，不计入分母。相机裁剪值是视深；入口按每条射线的夹角换算有效距离，不把远裁剪面误作以相机为中心的球面。

有效点优先调用 `D.TerrainHeight`：有值记为有地形；否则检查 D 已提交的构建雾盒，点的 XZ 与基准高度 Y 均落在盒子范围内记为只有雾；否则调用 `D.DisplayTerrainHeight`，Found 记为远景；仍无值则记为无地形无雾。雾下的远景不计作远景覆盖。雾使用玩家实际看到的盒子，不使用 Scenes 内部弧段规则。线路地形成员关系由 `TerrainHeight` 判断，不使用网格 AABB 并集估计弯道覆盖。这是**视野地面基准面投影覆盖率**，不衡量山坡、水面、隧道及建筑遮挡后的真实地形像素可见性。

线路部件归属按生成器实际体素列判断，查询开销计入 coverage probe。

覆盖以 5 Hz 为目标频率，显示空间或窗口改变时补采；记录真实采样间隔。未采样的绘制帧只引用最近样本的 draw，不能当作该帧新测得的覆盖。缺地形比例 `>0` 和 `neither` 比例 `>0` 分别形成连续缺口，以左保持估计持续时间，报告 nearest-rank p50／p99、最大值、总时长和原始区间。遇到未知、空间或窗口改变即截断，开放首尾标记 `left_censored`／`right_censored`；结束时使用真实最后绘制帧时刻截断尾区间。低频样本无法排除采样间的短缺口。

场景速度使用实际显示车辆的连续三维轨迹距离，格数乘 0.5 后除以真实秒。同空间绝对坐标消除浮动原点影响；跨空间使用记录的交接原点差对齐，缺少交接、缺少姿态或多轨边界不能确认连续对应时，速度为 null 并记 `unknown`。实际模拟倍率为完成步数增量除以 `SimulationTime.StepsPerSecond` 再除真实秒；窗口均值按区间实际持续时间加权，场景均值仅包含已知区间并另报未知数量。

记录关闭的帧时间对照使用原有 `scene-route-8x`，master 和本分支各至少 3 个独立进程，比较差值与 master 自身运行波动；不设置新的通过阈值，由 reviewer 验收。原始主线构建与新增记录构建分别保留真实 SHA，不能将记录构建的身份改写成主线 SHA。

### 窗口长测

`scene-soak` 在默认录制和测量设定的 5 分钟自动存档下按段循环，直到 `DurationSeconds`（默认 7200 秒）用完，再跑完当前一段。产品默认间隔为 1 小时，长测为在段内观察到自动存档轮换而缩短。每段：

1. 把标准世界存档复制回用户目录并经存档面板读取，暂停后核对节点 2 是玩家聚居地、节点 3 与边 15 各有玩家武装参战的战斗、列车 1 在运行，任一不成立即失败；随后强制 GC，作为相同地图状态下的存活堆读数，再打开边 15 采样线路战斗。
2. 主体至少 12 分钟、不读档，让自动存档槽和录制起点在同一会话里轮换。每轮依次：地图、地图→节点 2、节点→边 15、边 15→节点 3、返回地图、地图→边 15、跟随列车 1、设计编辑器（打开列车 1 首车，上色一格、撤销、重做后放弃）。每个视图取 10 秒 8× 样本，每段首轮另取 1× 样本；编辑器打开时宿主暂停模拟。
3. 段末 8 次保存并读取玩家自己的存档，读档后首个打开的场景在节点 2 与边 15 之间交替。

`scene-soak` 也将遇敌自动降速设为 `None`。负载按保存点的配方期望计数，后台线程每秒最多计数一次；每帧按最近结果分为 `full`、`all-but-line-battle` 和 `below-recipe`，计数耗时写入 `scene-probe-costs.jsonl`。自动存档按槽文件头的保存时间计数；录制裁剪要求同一会话前后都有可用起点，按最早起点前移计数。玩家存档读档被拒绝时，记录操作、保存位置、状态和问题字段，在原对局继续，不取读档后首显样本，并将 `round_trips.evidence` 记为 `failed`；标准世界重置失败仍终止运行。每段主体按首轮实测的每真实秒步数定长，让同一会话积累足够起点并发生裁剪（`body_sizing`）。`identity.scene_budget.soak` 汇总各视图与倍速的帧耗时分位数、超过 16.7 ms 的帧比例、实际步数／秒与负载分类比例、操作的时延分布、前后三分之一中位数、每小时斜率、相对前一地图稳态的峰值增量、内存斜率和每段存活堆。Godot 显存读数在集成显卡上是共享系统内存。

测量窗口和视口必须在整段运行中保持 1920×1080，适配器不能是软件渲染，否则立即失败。Linux 上 `measure.ps1` 总在 gamescope 嵌套合成器中启动游戏，找不到 gamescope 时失败。进程首次打开节点、线路和首次跟随需要新进程，不在长测范围内。测试机器上的触发方式见[远端检查](#远端检查)和 [CI](ci.md#linux-夜间运行)。

### 内容加载与设计求值成本

`Tools/Cli` 的 `measure-content` 用真实 Core／Content 工厂、磁盘内容端口和纯 .NET 资产检查器测量正式包 `builtin/core` 的内容成本，不经过 Godot。每组用一个新的 Release 进程运行，进程峰值只对应该组：

```powershell
dotnet build Tools/Cli/Train.Cli.csproj -c Release
New-Item -ItemType Directory -Force TestResults/content-cost | Out-Null
foreach ($group in 'load', 'evaluate', 'history', 'capture', 'restore') {
    dotnet Tools/Cli/bin/Release/net10.0/Train.Cli.dll measure-content . examples/starting-trade/save.json "TestResults/content-cost/$group.json" $group
}
```

| 组 | 操作 |
| --- | --- |
| `load` | `Discover`、`PrepareLoad(Initial)`；初始视图的驻留内存 |
| `evaluate` | `EvaluateDesign`：`titan-armed-vehicle`、`core-city-hub`、`bastion-fortress` |
| `history` | 初始视图与恢复视图的驻留内存；无变化重载；4 次有效重载（只在内存中改一份体素的显示名）和 4 次草稿保存，每次之后的历史驻留增量 |
| `capture` | `Capture`、`Encode`、`DecodeCapture`；1 个、草稿保存后 5 个和有效重载后 5 个历史视图的 `CaptureReplay` 与 `Encode` |
| `restore` | `DecodeState`（核心，单独计时，不计入内容成本）、`DecodeCapture`、`PrepareLoad(Restore)`；上述三种复现载荷的 `DecodeReplay` |

单项操作先运行一次记为冷启动，再运行 5 次热运行；每次运行前强制 GC。JSON 记录每次运行的耗时、分配字节、GC 次数，加载类操作另记文件读取和资产检查的耗时，载荷记录 UTF-8 字节、SHA-256 和各段大小。另记强制 GC 后的驻留增量、进程峰值工作集和峰值私有提交量，以及构建提交、输入文件的数量、字节和 SHA-256、运行时与 GC 模式。工具不写阈值或达标判断，也不采集外部负载；同机负载由测量人在每批前后另行记录。任何诊断错误或异常都使命令返回非零，且不留下结果文件。

## 阶段检查与候选基准

`stage` 在所有平台始终运行 structure、verify、Core/Content/Scenes/Godot 隔离重建、两项生产 Core 确定性用例。确定性用例通过生产命令日志与回放接口，在新进程逐检查点比较回放，以及每步写盘、读盘、恢复后与连续运行比较。它们属于阶段层，快速层和集成层不包含这些用例。

Windows 阶段层还执行 Windows 导出、空用户目录与有效 Mod 的导出后运行、资产导出探针、有窗口的主菜单 GPU 截图比较。非 Windows 的导出、导出后运行、资产探针和截图在摘要中记为未执行，不记为通过。当前 Linux 检查不需要渲染上下文，不使用 xvfb；无界面 Godot 使用 Dummy 音频。

发布导出冒烟保留生产内容、PNG 和有效 Mod 检查，同时从 PCK 的 `res://examples/basic-through/save.json` 经过正常恢复流程打开示例，不执行配方。它检查原身份与新身份均为有效且不同的 UUID、位置 200:0、暂停、未保存，以及玩家菜单隐藏示例入口；不保存副本或读回。副本保存到新 UUID 目录且原件不变由宿主测试在临时目录使用真实文件 I/O 验证。开发编辑器的可见入口由引擎测试覆盖；调试导出另做人工验证，命令、日志和截图记录在 Issue 交付评论，不加入 `test.ps1`。这些检查不替代 #298 四模块联调验收。

截图复用引擎测试程序集、Bootstrap 和 JUnit，仅在 Windows 有窗口运行中执行。`stage-screenshot` 设置 `TRAIN_ENGINE_SCREENSHOT=1`，一起执行 `ScreenshotTests` 和 `SceneMaterialPixelTests`，最低 7 例：主菜单截图、5 个材质像素用例和模型实例绘制调用测量。后者分别采样 1 与 64 实例的 5 个已完成帧，要求调用数稳定、非零且相等，原始数值输出到 Godot 日志。普通 headless engine 在执行前排除这两个窗口套件，不把它们计为通过或跳过。输入为 [windows-main-menu.png](../Tests/Engine/Baselines/windows-main-menu.png) 与 [windows-main-menu.json](../Tests/Engine/Baselines/windows-main-menu.json)，通过物理路径读取；`Tests/.gdignore` 阻止编辑器为测试 PNG 生成导入文件。比较先转为 RGBA8。候选值为 `MaxChannelDelta=32`、`MaxDifferentPixelFraction=0.00002`：任一通道差大于 32 的像素计为明显差异，明显差异像素数除以总像素数大于 0.00002 才失败。`comparison.json` 每次记录任意差异像素数、明显差异像素数及比例、最大通道差；`difference.png` 用红色区分明显差异、黄色区分轻微差异，通过时也保留。尺寸不一致、缺少基准或容差均失败。

Benchmark 仅供手动诊断，不属于阶段检查，不设阶段吞吐阈值。`core-train-loop-v2` 测量固定列车循环运行负载下的 Core 吞吐，结果写入 `throughput.json`，BDN 日志和报告保存原始测量；这些结果不提供帧耗时或完整游戏 8× 运行证据。

手动采集吞吐时，显式构建不在 `Train.sln` 中的 Benchmark 项目，再从项目目录启动。基准的构建工具链直接指定本次编译源码所在的 `Train.Benchmarks.csproj`，并在日志中写出路径，避免 BenchmarkDotNet 默认从解决方案根目录递归搜索时扫到 `TestResults` 中保留的旧隔离副本，无需删除证据：

```powershell
dotnet build Tests/Benchmarks/Train.Benchmarks.csproj -c Release
Push-Location Tests/Benchmarks
try {
    dotnet run --project Train.Benchmarks.csproj -c Release --no-build -- --artifacts <本次运行目录的绝对路径>
} finally {
    Pop-Location
}
```

首次确认启动方式时，在上述运行命令末尾添加 `--job Dry`，只做一次最短作业，验证构建、执行和 `throughput.json` 写出。结果的 `dryRun` 为 `true`、`sampleCount` 为 1；这只证明运行链路可用，不能作为吞吐依据。正式测量使用完全相同的启动命令，仅去掉 `--job Dry`，并为每次运行指定独立目录。

提交的 PNG 和容差为**等待用户阶段验收确认的候选基准**，只能用于检查机制，不能据此宣布阶段或产品性能验收通过。更新截图时，在目标 Windows 图形配置下至少用三个独立 Godot 进程和独立用户目录采集，核对菜单内容并比较各图的通道差和明显差异像素比例，再替换 PNG 与容差 JSON。更换 GPU、驱动或 Godot 版本后重新核对。候选值及其替换需用户阶段验收确认，替换后重跑完整阶段检查。原始截图、差异、设备信息和测量结果只保留在忽略的运行目录，长期证据引用写入 Issue 评论，不提交运行报告。

`core-train-loop-v2` 复用的铁路恢复夹具采用 120／720 步的人口消费／统计间隔，与内置规则一致；共享基础夹具仍用 1／10 步缩短人口行为用例。基准在计时外创建会话，不移除人口执行。比较其他提交时，两边应使用同一份基准和世界／铁路夹具源码，使列车、地图、内容及人口间隔一致。

## 按改动选择

默认基准 `-BaseRef origin/master` 必须在本地存在。入口以 `git merge-base <BaseRef> HEAD` 为比较起点，包含该点之后的提交及暂存、未暂存、未跟踪改动；文件列表禁用重命名识别，移动前后路径都参与规则匹配。入口不自动 fetch。dotnet-affected 7.0.1 以 `Train.sln` 限定项目图，沿 MSBuild 项目依赖选出受影响项目，再从七个已登记 MSTest 项目中选择；归档和调用样例不进入这张项目图。引擎层复用同一次改动分析：dotnet-affected 输出包含 `Tests/Engine/Train.Engine.Tests.csproj`，或改动命中下述引擎路径规则时选中；该项目引用根 `Train.csproj`，所以根项目及其依赖的影响沿同一张项目图传播。选中后执行当前全部引擎用例。`-All` 和 `-ExportWindows` 强制引擎，不依赖改动结果。单独选择 `stage` 时完全跳过改动分析，不要求基准引用存在，始终执行[阶段检查](#阶段检查与候选基准)；`-Layer stage,engine` 不强制引擎，阶段全量检查使用 `-All -Layer all,stage`。

以下共享文件改动选择全部 MSTest 项目：`Contracts/**`、任意层级的 `Directory.Build.*`、`global.json`、`docs/content/schemas/**`、`Tests/ContractSupport/**`、`Train.sln`、`.config/dotnet-tools.json`、`scripts/test.ps1`、`scripts/checks/**`。工作流 YAML 不在共享列表中。

CLI 及其项目依赖的源码改动由 dotnet-affected 沿引用图选择。`examples/**`、`builtin/**` 和 `project.godot` 是 CLI 的额外输入，改动时也选择 CLI 测试；摘要逐文件记录这三组补充规则 `cliRules`。

引擎路径规则复用上述整体构建与测试入口条目，但不包含 `docs/content/schemas/**` 和 `Tests/ContractSupport/**`；另外包含 `Tests/Engine/**`、`project.godot`、`export_presets.cfg`、`builtin/**`、`addons/**`，以及 `archive/`、`docs/`、`prototypes/`、`Tests/` 之外的 `*.tscn`、`*.tres`、`*.uid`、`*.import`。`Tests/Engine/` 内的资源已由该目录规则覆盖。普通宿主 MSTest 被选中不表示引擎也被选中。

集成层的契约结构、公开面和调用样例检查，在 `Contracts/**`、任意 `*.csproj`、任意层级的 `Directory.Build.*`、`Train.sln`、`prototypes/**`、`Tests/ContractChecks/**`、`scripts/checks/project-boundaries.json` 或 `docs/contracts/public-api.txt` 改动时进入默认选择；`builtin/**` PNG／GLB 及其 `.import` 改动也选择该检查，由 C# 工具要求全部内置资产使用 Keep File。内容 Schema 检查在 `docs/content/**`、`builtin/core/**` 或 `scripts/content/**` 改动时进入默认选择；同一任务依次检查示例 Schema、正式数据可重制性与 Schema，以及正式经济关系、静态供电估算、UI 符号和战斗效果资产一致性。隔离重建在任意 `*.csproj`、任意层级 `Directory.Build.*`、`Train.sln`、`scripts/checks/project-boundaries.json` 或 `Tests/ContractChecks/**` 改动时进入默认选择。`-All` 选择结构、公开面、隔离和 Schema 独立检查。

dotnet-affected 退出码 166 表示空项目集合；其他非零退出码、120 秒分析超时或进程清理失败均使入口失败。工具输出写入本次运行目录。仓库路径保留 Git 返回的大小写，再用 `Path.GetFullPath` 转为原生完整路径与分隔符，避免工具的路径前缀匹配漏选。

执行改动分析时，摘要的 `selection` 保存基准引用、共同祖先 SHA、改动文件、共享规则、工具输出项目集合和最终选择。无论请求哪些层，这些运行的 `selection.engine` 都记录引擎是否受影响；逐文件的 `engineRules` 记录命中的引擎路径规则，`affectedProjects` 中的引擎测试项目解释依赖图触发，`mode: all` 或 `exportWindows: true` 解释强制选择。每个文件的 `project` 是包含它的最近项目目录对应的项目，嵌套项目优先；仅当该项目也在工具输出集合中时记录，否则为 `null`。此对应只作解释，选择仍由工具结果及共享规则决定。单独选择 `stage` 时摘要记录 `mode: stage`，不记录改动分析或引擎选择。

## 执行与边界

选中 MSTest 项目、契约或隔离检查、引擎层或阶段层时，入口构建一次 `Train.sln`，MSTest 使用 SDK 的 `dotnet test --test-modules <相对 DLL 路径> --root-directory <根目录>` 运行已构建程序集，不重复构建。单独的 Schema 任务只调用 Python，不构建；正式经济检查也可独立运行 `python scripts/content/check_economy.py [content-root]`，默认读取 `builtin/core`，标准输出为 JSON 报告。C# 工具随解决方案构建；structure 通过工具生成的遍历项目，在一个 MSBuild 进程内采集全部登记项目的引用、编译项、友元和输出路径。四组调用样例在该进程内恢复、重新求值并构建，使用 `BuildProjectReferences=false` 和 `RestoreRecursive=false` 避免重复构建共享契约；样例仍不加入解决方案。引用采集、样例构建、隔离构建与工具自测的夹具构建均使用 `-maxCpuCount:1` 和 `UseSharedCompilation=false`，限制并发任务内的 MSBuild 节点数并禁用共享编译服务器。verify 检查公开面和类型归属，isolate 按[矩阵选择规则](contracts/testing.md#实际隔离与结构检查)选择模块，串行复制独立输入、冷构建、独立查询 MSBuild 事实并运行各自非阶段用例，首个失败后停止；已构建的隔离测试 DLL 同样由 SDK 的 `dotnet test --test-modules` 运行，引用判定与 structure 共用。隔离重建排除 `stage` 类别，由阶段入口单独运行这些用例。阶段层始终依次执行这三个命令，再执行确定性用例；混选集成层和阶段层时，各自执行所需检查。

各隔离测试会话的进程上限 N 为 660 秒，由 `Isolation.SessionTimeoutSeconds` 定义；挂起转储在 N−10 秒触发，MTP 在 N−5 秒中止会话，外层 ToolProcess 在 N 秒终止进程。三者任一结束会话时，隔离失败信息均标明超时、模块、实际耗时、上限 N 和日志路径。

各层测试执行预算为快速 450 秒、集成 2070 秒、引擎 1710 秒。集成层选中的 MSTest 项目和 structure、verify、Schema 共用一个任务队列与 2070 秒截止时间，通过已有的 `ForEach-Object -Parallel` 执行，最多 4 个任务并发，较重任务排在前面；汇总与清理仍由父流程完成。队列正常结束或检查失败后，先停止集成层计时并保存用例计数，再独立运行选中的 isolate，使用 1290 秒上限，不占用队列或集成层预算。收到停止请求时，不再启动尚未开始的独立隔离检查。快速层保持原有 MSTest 并行执行方式，每个项目保留各自的最低用例数、TRX、超时和挂起转储。阶段层不设总预算，串行执行 structure、verify、isolate，子进程上限分别为 300、120、1290 秒；确定性阶段用例上限为 180 秒，窗口截图为 120 秒，导出冒烟的两次运行共用 180 秒（外层检查上限 185 秒）；引擎检查仍串行执行。构建准备上限为 300 秒，Godot 导入、导出各为 120 秒。诊断区分已启动后超时（`TimedOut`）与截止时尚未启动（`NotStarted`），点名对应检查并记录逐项秒数；缺少 TRX 不覆盖超时诊断。超时用于发现挂起的任务，导致失败并保留可用转储；正常执行耗时增长时，按[预算调整原则](contracts/testing.md#预算调整原则)增加预算，不以延长预算掩盖挂起。必要的 Godot 导入只做一次；导出冒烟的两次运行共享一次 Windows 发布导出。同一工作树不允许并发运行入口。

预算合并依据：[#589](https://github.com/odradekk/train/pull/589) 的本机多 pair engine 层为 471.5293269 秒、同 head Windows CI 为 378.3510036 秒，按较慢值乘 1.5 并向上取整到 30 秒得到 720 秒；[#387](https://github.com/odradekk/train/issues/387) 含导出冒烟和 PCK 探针的本机 engine 层为 510.6451746 秒，同法得到 780 秒。当时两边取较大值，engine 使用 780 秒。继续合并 #390 后，协调者提前释放测量窗口、恢复本机常态工作后，完整 184 项 engine 层实测 539.3363178 秒，同法得到 810 秒，因此最终 engine 使用 810 秒。合并 #640 后，本机多 pair 常态负载下完整 247 项 engine 层实测 897.1310593 秒（`engine-20261019T074034914-bd2caa5`），同法得到 1350 秒；并入 70c82916 后同一负载下实测 972.795 秒（`engine-20261019T084230810-4fd010b`），同法得到 1470 秒，当时 engine 使用 1470 秒。#360 合并 #661 后，本机多 pair 常态负载下含导出冒烟和 PCK 探针的完整 engine 层实测 985.574866 秒（268 项真实 Godot 用例、导出冒烟和资源探针全部通过），同法得到 1500 秒，因此 engine 最终使用 1500 秒。#360 之后本机多 pair 常态负载下完整 269 项 engine 层实测 1124.5181922 秒（`engine-20261020T145552361-3d6dee5`，269/269 通过），乘 1.5 得 1686.7772883，向上取整到 30 秒得到 1710 秒，因此 engine 使用 1710 秒。

引擎单用例默认上限依据：2026-10-19 本机多 pair 常态负载下完整 247 项 engine 层实测最慢未单独设限用例 35.576 秒，结合 #301 同负载下记录的 42.301 秒，按较慢值乘 1.5 并向上取整到 30 秒得到 90 秒，因此默认上限使用 90 秒；慢用例继续使用各自的 `[Timeout]` 单独放宽。

引擎内容准备等待上限依据：2026-10-18 本机多 pair 常态负载归档运行（`issue-336-20261018T193314/TestResults/engine-20261018T094221738-9f19386/engine-godot-native.log`）中已完成的最坏启动内容准备为 26.7 秒，26.7 × 1.5 = 40.05 → 60 秒；#360 在 `f723b1a8` 首跑另有 2 次等待在 30 秒截断（真实值未知），30 × 1.5 = 45 也取 60 秒。`Real_form_draft...` 的 75.1 秒是第 65 行等待失败、后续未执行的下界。因此 `FramesUntil` 的内容准备等待上限为 60 秒，其他等待保持 30 秒。同一代码的测量运行 `engine-20261020T143836111` 的 26 次真实启动内容准备最小 6.087、中位 9.076、最大 13.690 秒，重载/写包上界最大 19.604 秒；干净重跑 `engine-20261020T145552361` 269/269 通过。单用例上限按 75.1 × 1.5 = 112.65 → 120 秒，`Real_form_draft...` 由默认 90 秒提高到 120 秒，其余现有上限足够。

快速预算依据：#352 在本机多 pair 常态负载下 fast 层 1454 项实测 90.3 秒，按较慢值乘 1.5 并向上取整到 30 秒得到 150 秒，当时 fast 使用 150 秒。#360 在本机多 pair 常态负载下运行完整快速层，Scenes 的 672 项全部通过，测试本体耗时 144.529 秒，但进程在 149.2 秒尚未完成收尾时达到预算。包含收尾后的 1.5 倍余量向上取整到 30 秒，将 fast 提高到 240 秒；合并 #661 后，本机完整 3168 项 fast 层通过，实测 166.0641564 秒；乘 1.5 并向上取整到 30 秒得到 270 秒，当时 fast 使用 270 秒。继续合并 #574、#664 后，本机多 pair 常态负载下完整 3169 项 fast 层通过，实测 181.2759565 秒；乘 1.5 并向上取整到 30 秒得到 300 秒，因此 fast 使用 300 秒。原始与合并后实测记录见 #360。#675 在本机多 pair 常态负载下（开始时其他 dotnet 进程 7 个、Godot 进程 16 个）运行完整快速层，目录 `fast-integration-20261021T001442302-65dd6fb` 的层用时 135.4428751 秒、fast-scenes 134.977 秒；1.5 倍向上取整为 210 秒，低于按 #303 Scenes 进程 288 秒得到的 450 秒，因此 fast 使用 450 秒。

集成预算依据：#387 此前完整集成层最坏实测为 353.2119539 秒，同法得到 540 秒；#589 同步后的 master 集成预算仍为 450 秒。同步后的新一轮完整集成层共 175 项，实测 388.6998585 秒，同法得到 600 秒。此后合并 #599 的完整 175 项集成层在本机多 pair 常态负载下实测 408.0123188 秒，同法得到 630 秒。继续合并 #390 后，完整集成层共 176 项，本机多 pair 常态负载下实测 470.1861206 秒，同法得到 720 秒，当时集成使用 720 秒。#360 在本机多 pair 常态负载下的 CLI 集成检查于 719.293 秒被预算终止，已完成 14 项通过、1 项 Linux 专用跳过，最后一次正常完成在 11 分 51 秒；仍有两条真实状态差异检查未完成，未见断言失败。提高到 1200 秒后的完整集成队列实测为 987.6055585 秒（191 项通过、1 项 Linux 专用跳过；其中 CLI 进程 884.8 秒），按较慢值乘 1.5 并向上取整到 30 秒得到 1500 秒，当时集成使用 1500 秒。继续合并 #574、#664 后，本机多 pair 常态负载下完整集成层通过，实测 1367.9973824 秒（191 项通过、1 项 Linux 专用跳过；其中 CLI 进程 1231.372 秒），乘 1.5 并向上取整到 30 秒得到 2070 秒，因此集成使用 2070 秒。原始失败与完整重跑记录见 #360。#675 在本机多 pair 常态负载下（开始时其他 dotnet 进程 7 个、Godot 进程 16 个）运行完整集成层，目录 `fast-integration-20261021T001442302-65dd6fb` 的层用时 732.2043286 秒、integration-cli 673.444 秒；乘 1.5 并向上取整到 30 秒得到 1110 秒，低于现行 2070 秒，因此集成仍使用 2070 秒。

隔离预算依据：isolate 与 stage-isolate 运行同一条 `Train.ContractChecks isolate` 命令，重建并运行相同的 Core/Content/Scenes/Godot 模块，因此共用同一上限。#675 在本机多 pair 常态负载下（开始时其他 dotnet 进程 7 个、Godot 进程 16 个）运行完整集成层，目录 `fast-integration-20261021T001442302-65dd6fb` 的 isolate 为 676.728 秒；#303 联调目录 `fast-integration-engine-stage-20261020T130517557-d815ce7` 的 isolate 为 583.739 秒，目录 `engine-stage-20261020T134125186-d815ce7` 的 stage-isolate 为 681.053 秒。Linux runner `lan-linux-nightly` 的 post-merge（run 39526193091）与 nightly（run 39513620788）实测 isolate 为 226.924／229.325 秒、stage-isolate 为 230.29／227.63 秒，均低于本机。按所有样本最坏的 681.053 × 1.5 = 1021.5795 向上取整到 30 秒得到 1050 秒，因此两项共用 1050 秒。本轮（#679）在本机多 pair 常态负载下运行 `-All -Layer integration`（目录 `integration-20261021T014200336-786f3d2`，开始时 dotnet 进程共 39 个、Godot 进程 13 个）的 isolate 为 671.989 秒，运行 `-Layer stage`（目录 `stage-20261021T023218471-786f3d2`，开始时 dotnet 进程共 18 个、Godot 进程 15 个）的 stage-isolate 为 607.008 秒，两次隔离检查均通过；最坏值仍是 681.053 秒，1050 秒不变。单会话样本：`stage-20261021T022536706-786f3d2` 的 `Train.Core.Tests` 会话在 268.22 秒触发当时的 270 秒上限（1485 项全部通过，测试本体 4m27.742s），stage-isolate 因此失败；`stage-20261021T023218471-786f3d2` 的 Core 会话 230.7 秒、Scenes 会话 175.6 秒，均在 270 秒内通过。268.22 × 1.5 = 402.33 向上取整到 30 秒得到 420 秒，因此 `Isolation.SessionTimeoutSeconds` 使用 420 秒；外层两项上限保持 1050 秒。#692 合并 master 后，`stage-20261021T074952135-e0a3285` 的 Core 会话在 420.78 秒被外层终止，TRX 记录 1491 项全部通过、测试本体 410.58 秒，属负载下变慢；同提交稍后的 `stage-20261021T080135927-e0a3285` 完整 stage-isolate 用时 856.745 秒（开始时 dotnet 18 个、Godot 10 个），会话 Core 308.037、Content 59.977、Scenes 253.248、Godot 19.972 秒。420.78 × 1.5 = 631.17 向上取整到 30 秒得到 660 秒，`Isolation.SessionTimeoutSeconds` 使用 660 秒；856.745 × 1.5 = 1285.12 向上取整到 30 秒得到 1290 秒，isolate 与 stage-isolate 共用。本轮（#700）在 `6363e69` 上串行运行两次：`-Layer stage`（目录 `stage-20261021T094358218-6363e69`，开始时 dotnet 10 个、Godot 6 个）的 stage-isolate 为 650.43 秒，会话 Core 231.724、Content 47.843、Scenes 190.116、Godot 13.680 秒；`-All -Layer integration`（目录 `integration-20261021T100212423-6363e69`，开始时 dotnet 17 个、Godot 6 个）的 isolate 为 663.298 秒，两次均通过。集成入口通过后按保留规则只留摘要，因此另以同一隔离命令直接运行一次（开始时 dotnet 1 个、Godot 5 个）记录逐会话用时：总 698.34 秒，Core 284.299、Content 47.151、Scenes 187.913、Godot 14.102 秒；660 秒与 1290 秒不变。

导出冒烟预算依据：`stage-export-runtime` 的两次运行共用一个 `-TimeoutSeconds`，外层检查上限比它多 5 秒清理时间。预算取整个 pwsh 进程的 `checks[].seconds`，并与 #679 被截断的样本比较，取最大值乘 1.5 后向上取整到 30 秒。#679 在 `786f3d20` 的 `stage-20261021T023218471-786f3d2` 中，空目录 10:46:43 启动、10:47:00 完成 `content_reload_summary`、10:47:19 写出 `application_smoke_summary` 后退出；Mod 10:47:20 启动、10:47:32 完成重载、10:47:41 被共用的 60 秒终止，未写出冒烟摘要；检查用时 61.503 秒。本轮（#692）在本机多 pair 常态负载下运行 `-Layer stage` 三次：目录 `stage-20261021T052552847-4f7bd39`（临时上限 180 秒，工作树含未提交的临时改动，整体因 `userDataChanged` 判失败；开始时 dotnet 进程 15 个、Godot 进程 16 个）的空目录 13:41:47 启动、13:41:58 完成重载、13:42:13 写出冒烟摘要并退出，用时 26.3 秒，Mod 13:42:14 启动、13:42:25 完成重载、13:42:38 写出摘要并退出，用时 24.3 秒，检查用时 52.251 秒；目录 `stage-20261021T054515243-109d276`（上限 120 秒，整体因 `userDataChanged` 判失败）的检查用时 54.740 秒；目录 `stage-20261021T060806855-109d276`（上限 120 秒，通过）的空目录 14:25:44 启动、14:26:06 完成重载、14:26:32 写出摘要、14:26:33 退出，用时 49.2 秒，Mod 14:26:34 启动、14:26:55 完成重载、14:27:29 写出摘要、14:27:29 退出，用时 55.4 秒，检查用时 111.617 秒，距 120 秒上限只剩 8.4 秒。合并 master（`e0a32858`，含 #364 等内容改动）后，目录 `stage-20261021T080135927-e0a3285`（上限 180 秒，开始时 dotnet 进程 18 个、Godot 进程 10 个）的空目录 16:19:14 启动、16:19:28 完成重载、16:19:44 写出摘要、16:19:44 退出，用时 30.5 秒，Mod 16:19:46 启动、16:20:00 完成重载、16:20:15 写出摘要、16:20:16 退出，用时 29.7 秒，检查用时 63.743 秒，整层通过；该样本低于 111.617 秒，180 秒不变。按最大样本 111.617 × 1.5 = 167.4255 向上取整到 30 秒得到 180 秒，因此 `stage-export-runtime` 使用 180 秒，外层上限 185 秒，`run-export-smoke.ps1` 的默认值同为 180 秒。引擎层 `-Layer engine -ExportWindows` 的导出冒烟使用引擎层剩余预算，不读这个固定值。

各 job 按 workflow 实际执行的入口计算上限合计：

| job／入口 | 算式（秒） | 合计 |
| --- | --- | --- |
| Windows routine：`-Layer fast,integration` | `改动分析 120 + 构建 300 + 快速 450 + 集成 2070 + 独立隔离 1290` | 4230（70.5 分钟） |
| Windows routine：`-Layer engine` | `改动分析 120 + 构建 300 + Godot 导入 120 + 引擎用例 1710` | 2250（37.5 分钟） |
| Windows correctness：`-All -Layer all` | `构建 300 + 快速 450 + 集成 2070 + 独立隔离 1290 + Godot 导入 120 + 引擎用例 1710` | 5940（99 分钟） |
| Linux 全量：`-All -Layer all,stage` | `构建 300 + 快速 450 + 集成 2070 + 独立隔离 1290 + Godot 导入 120 + 引擎用例 1710 + structure 300 + verify 120 + 阶段隔离 1290 + 阶段 Core 用例 180` | 7830（130.5 分钟） |

改动分析的 120 秒只出现在非 `-All` 入口。Godot 导入的 120 秒在引擎计时之外，不占用 1710 秒引擎预算。routine 的第一个入口失败后仍会执行 engine 入口，因此两个入口相加，最坏 6480 秒（108 分钟）。

准备与收尾（检出、还原、工具准备、下载和上传）实测约 1 分钟；`Invoke-HiddenProcess` 超时后最多再等 5 秒清理进程。因此 Windows job 上限设为 115 分钟，Linux 全量 job 上限设为 138 分钟。以后调整任何入口上限时，按表中算式重算合计，再为准备收尾和超时后的进程清理留出约 7 分钟。

`AppTests` 和 `ContentTests` 使用生产默认装配，验证内置内容就绪、新局、真实保存后读档、控制台与输入重载，覆盖业务更新、纯表现／无变化及失败保留核心与内容配对；日志用例观测 Godot 输出、控制台缓冲、后台队列、JSONL 和退出释放。菜单用例验证退出请求。`HostSimulationTests` 通过注入宿主及真实帧验证控制台会话控制、步进、检查点、重绑定、故障冻结和诊断。引擎结果要求退出码、JUnit 结果及最低用例数均满足入口规则；实际执行、通过、失败和跳过数写入摘要，不以帧数或超时退出证明通过。

`RecordingTests` 在真实 App 场景中注入有限情景的核心／内容契约替身，验证初始起点自动落盘、报告面板状态、从内存窗口导出、故障文件及冻结后导出。路径由测试直接给定，不自动化原生 FileDialog；`recording-artifacts/` 写在本次 JUnit 报告目录。它执行同一份生产宿主和文件代码，但不证明完整 Godot 隔离或默认真实工厂装配。纯宿主录制测试归 `Tests/Godot/Recording/`；真实 ZIP 与发布失败用例属于 integration。

导出冒烟的两次运行分别使用空用户目录和有效 Mod 用户目录，都必须退出 0 并提供 JSONL 证据。生产 `--smoke` 等待真实内容准备完成，验证 `core:staple-food`、PNG 解码和已发布包，不接收测试专用预期参数。脚本核对 Info 事件 `content_reload_summary` 的状态为 `Committed` 或 `CommittedWithWarnings`，并读取 `application_smoke_summary` 的 `passed`、`packages`、`voxels`。Mod 夹具采用 manifest v2、voxel v3；Mod 运行必须包含包 `550e8400-e29b-41d4-a716-446655440000` 及定义 `550e8400-e29b-41d4-a716-446655440000:smoke-food`，包数比空目录运行增加 1。

检查输入、临时文件、运行日志、导出产物和测试用户数据写入运行目录，`TEMP`、`TMP`、`TMPDIR` 指向其中的 `tmp`。Godot 原生日志通过 `--log-file` 定向；主项目导入用 `--recovery-mode` 跳过编辑器插件，导出不使用该参数，让移除 MCP autoload 的导出钩子执行。发布冒烟的空用户目录与有效 Mod 两次正常运行中，原生日志出现任何引擎或脚本错误均判失败。导入、引擎测试、主游戏和资产探针的导出及导出冒烟将 Windows 的 `APPDATA`、`LOCALAPPDATA`，或非 Windows 的 `XDG_CONFIG_HOME`、`XDG_DATA_HOME`、`XDG_CACHE_HOME` 指向运行目录。两个 Windows 发布导出只从真实模板目录复制 `4.7.2.stable.mono/windows_release_x86_64.exe` 到各自隔离环境，不创建模板 junction；真实模板目录只读，复制的模板在导出 `finally` 中删除，前序步骤失败时也清理。引擎测试与导出冒烟的两次运行使用隔离用户目录，运行结束后清理目录；JSONL 日志先复制到运行证据目录。入口比较运行前后的快照：在 `bin/`、`obj/`、`.godot/`、`.git/` 和本次运行目录之外新增或删除文件、Git 状态或 HEAD 变化、真实 `Godot/app_userdata/Train` 下文件变化，均导致整次失败。真实 `Godot/app_userdata/Train` 的前后比较仍会被本机其他 Godot 进程的改动触发。运行期间不要修改工作树；保留已有 `override.cfg`。

非 Windows 的 MTP Unix socket 路径有 103 字节上限。入口在 `/tmp` 创建指向本次 `tmp` 的唯一短符号链接，通过 [MTP 的 `TESTINGPLATFORM_PIPE_DIRECTORY`](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-config#testingplatform_pipe_directory-environment-variable) 使用它；管道文件仍位于运行目录。结束时只删除短链接并恢复环境变量，摘要记录链接清理结果；删除失败使整次失败。

## 结果与保留

Core 集成层的回放语料位于 `Tests/Core/Sessions/ReplayCorpus/`：这组固定基准仅在 Windows 执行（`OSCondition`）；Linux 将其记为跳过，跨进程确定性由阶段层的生产自回放用例验证。提交的 JSONL 命令日志经公开 `PrepareReplay`／`ICoreReplay` 重放，每两步保存生产检查点，与 Verify 31.26.0 管理的 `*.verified.txt` 比较。每个检查点一行，格式为 `<步数>:<序号> <SHA-256> <规范文本>`；`.gitattributes` 固定基准为 UTF-8、LF，Windows 的 `core.autocrlf=true` 检出也保持该格式。运行身份固定，位置、规范检查点文本和 SHA-256 都参与基准比较；首个字段分歧由 `CompareCheckpoints` 报告。反向用例只在内存中篡改中间基准，不改提交文件。

更新基准时先运行 `pwsh -NoProfile -File scripts/test.ps1 -Project core -Layer integration`。Verify 已关闭 DiffEngine 弹窗；失败信息给出 `TEMP` 下本次接收目录和基准输出副本路径。检查 `*.received.txt` 的每个变化与预期一致后，将其复制到源码语料目录并改名为同名 `*.verified.txt`，再运行集成层。首次新增语料没有已接受基准，因此首次失败是初始化流程；已有基准的失败仍须先诊断。接收文件和旧基准副本都位于本次运行目录的 `tmp` 内，测试通过会删除临时语料目录，统一入口通过后只保留摘要。直接运行测试时遵循系统 `TEMP`；不要将运行目录中的旧基准副本当成源码更新目标。

每次运行目录为 `TestResults/<选择层>-<yyyyMMddTHHmmssfff>-<7位提交>/`，目录名中的时间为 UTC，只产生一份 `summary.json` 摘要。摘要记录提交、工作区状态、选择、构建、各项目与各层的执行／通过／失败／跳过数（引擎用例来自 JUnit）、用时和结论。独立隔离检查在 `summary.checks` 中以 `name=isolate`、`layer=isolation` 记录 `seconds`、`timedOut`、`notStarted` 和 `cleanupSucceeded`，其耗时不计入 `layers.integration`；不增加 `-Layer` 选项。跳过数记入摘要，整次运行仍可以通过，但跳过的用例不算已验证；脏工作区结果不能证明所记录的干净提交通过。

隔离工具在所有结局下删除自己拥有的隔离树，删除失败判失败，保留清单和日志。普通运行通过后只保留摘要；Windows 阶段运行另保留 `screenshot/` 下的实际 PNG、渲染身份和差异 JSON，以及 `stage-screenshot.junit.xml`。Windows 阶段或 `-ExportWindows` 另保留日志、`*.process.json`、探针耗时记录和 `asset-export/probe-data/`（含 `build-inputs/`），以及本次隔离的 `inputs.json`、`first-party-assemblies.json` 和 `isolation-summary.json`，供复核断言结果、输入清单、实际解析程序集和总耗时。导出二进制、`asset-export/staging/` 及隔离环境目录删除。失败保留 TRX、JUnit、日志、输入和转储等全部证据，其中引擎结果为 `engine-godot.junit.xml`，原生日志为 `engine-godot-native.log`。入口只保留最近 10 个符合命名规则的运行目录；入口持有该工作树的命名互斥锁时，没有 `summary.json` 的运行目录一律视为崩溃遗留并清理。未取得互斥锁则拒绝同工作树新运行。`TestResults/` 下其他目录不清理。结果只有通过或失败；必需检查失败、缺少环境或未完成时返回退出码 1；人工、目标硬件和尚未实现的验证另行报告。

定位单个引擎用例时，先构建 `dotnet build Tests/Engine/Train.Engine.Tests.csproj -c Debug` 并按[隔离子进程片段](local-environment.md#agent-手动启动-godot)完成资源导入。在运行用例的隔离子进程内设置 `TRAIN_ENGINE_TEST_ASSEMBLY` 为 `.godot/mono/temp/bin/Debug/Train.Engine.Tests.dll` 的绝对路径，`TRAIN_ENGINE_REPORT` 为本次目录内 JUnit 文件的绝对路径，并设置 `TRAIN_ENGINE_SCREENSHOT=0`。Godot 参数使用 `--headless --path . --scene res://Tests/Engine/Bootstrap/Runner.tscn --log-file <本次原生日志> -- --run-tests=<类>[.<方法>] --quit-on-finish`，将选择表达式替换为类名或类名加方法名；此手动运行有用户目录隔离，但不执行统一入口的最低用例数与快照检查。

单独重新采集截图时复用上述 Bootstrap 和[隔离子进程片段](local-environment.md#agent-手动启动-godot)：在子进程内设置 `TRAIN_ENGINE_SCREENSHOT=1`、`TRAIN_SCREENSHOT_OUTPUT` 为证据目录，并去掉 `--headless`。这会选中截图与材质窗口套件；只重新采集主菜单时可用 `--run-tests=ScreenshotTests` 限定选择。运行截图套件后从该目录取得 `actual.png`、`rendering.json` 和已有基准下的 `comparison.json`；缺少基准时仍保存实际 PNG，然后按预期失败。每次采集使用新的进程和用户目录，采集后只清理自己创建的用户目录；调用终端的环境保持原值。普通无界面用例使用 `TRAIN_ENGINE_SCREENSHOT=0`。
