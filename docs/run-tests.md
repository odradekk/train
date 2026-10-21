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

`-Project cli -Layer fast,integration` 验证命令行工具：快速层检查 PNG 结构、GLB 实际顶点与层级变换、表面及不支持特性的事实；集成层启动真实 CLI 进程，加载已提交的 `builtin/core`，比较重复运行及保存退出后恢复的检查点，并严格重放本次录制中的接受／拒绝命令。经济短配方覆盖租赁、装卸、自动买入及具体命令回执，核对恢复后的查询样本与连续运行后缀；有限现车短配方核对报价扣款、待售减少及推进后不补车。委托分支配方让真实起始车队按起步贸易的货物和地点满载采购后，连续提交两次空来源货车委托，验证首次受理扣款与工钱、再次 `insufficient_goods` 回执的完整缺货身份，以及同一步拒绝前后的钱包、库存、任务和工钱查询不变，并验证本次录制的严格重放。基础贯通、容量紧张与委托分支的已提交示例重制比较，以及临时副本篡改用例以 `OSCondition` 限定 Windows；同机跨进程检查在所有平台运行。比较前只显式核对 `CoreInputId`，不同报需要重新制作；种子、生成器版本、生成内容修订和配置交给检查点比较报告字段。示例测试比较检查点摘要并确认提交文件未变，不要求重制存档字节一致。四游戏小时起步贸易与一天租约到期的长配方不进入日常测试层，交付前以 Release 分别执行 replay、save／resume／compare、make／check；同一存档用同种构建配置制作和检查。这些检查不重跑内容制备脚本，也不提供 Godot、Scenes 或四模块阶段验收证据。工具、输出和并行长验证用法见[示例存档与配方](../examples/README.md)。

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

选择“检查光照预览”启动 `InspectionLightingPreview`。该手动预览通过真实 `ScenesFactory.Create()` 生成一条线路和一列小型列车，复用生产 App、世界视图、相机和渲染；Core/Content 使用 ContractSupport 的固定读视图与明确的线路、车辆及素材输入，并保持暂停。按反引号打开控制台，执行 [`lighting`](console/lighting.md) 切换三种检查光照或恢复；“线路 / 重新进入”支持线路平移及重入，“跟随列车”切换跟随目标。它提供真实 Scenes 生成、真实 Godot 渲染的窗口检查入口，不生成截图或 JUnit，不证明完整四模块联调或 Godot 完整隔离；后两者分别归 #298 和 #424。自动截图使用[美术检查截图入口](art/08-asset-pipeline-and-review.md#检查人与记录)，从普通存档恢复真实编组，证据放在工作树 `.godot/validation/<名称>/`。预览代码仅在 Debug 可用，导出构建不包含此入口。

选择“节点列车帧序列”启动 `NodeTransitionPreview`，以固定 Core/Content 读视图和真实 Scenes 工厂，覆盖双轨线路从 2 号轨进站、W 等候与获站台、停靠后从 1／2 号轨出站、途经、同边异轨返回和同侧回转。它核对真实车辆 Node3D 与 FindPose 的姿态，并对配对边界关键帧断言位置、方向和高度连续；记录进出轨号、Origin、车辆姿态、相机、边界误差和加载状态，连同 PNG 输出至 `.godot/validation/node-transition-preview-<时间戳>/`。受控关键帧不能替代生产 Core 时序、连续行驶或窗口性能证据。

选择“生产 App 逐帧取证”启动 `ProductionNodeCapture`，通过正常 UI 创建或载入游戏并安排计划，选择列车后点击“跟随并记录”；seed=40 的 Demo 可点“Demo 停靠 / 途经计划”，由既有列车命令解析与提交路径设置停靠 13、途经 8、停靠 5 的计划并启动列车，再用正常模拟速度按钮开始运行。它使用生产装配，在每个实际绘制帧后记录核心位置、车辆显示姿态、FindPose、相机、Origin 和加载状态，阶段改变时截图，输出至 `.godot/validation/production-node-capture-<时间戳>/`。验收时需检查相邻帧相机与车辆位移，并结合边界两侧 Origin 区分坐标平移与真实跳动；该入口本身不生成 JUnit 或自动验收结论。

选择“开发检查预览”启动 `DeveloperToolsPreview`，仍使用同份生产 App、开发窗口、协调器和文件流程；Core、Content、Scenes 为界面列明的有限契约替身。普通会话只支持运行到步 2，重放验证顺序为单步到 `1:0`、推进到 `2:0`、导出普通存档、通过存档面板导入并读取副本。预览启动时在 `TestResults/developer-tools-preview/` 写入固定复现包，窗口或辅助按钮可执行这些操作；场景图层开关请求替身提供彩色线。它不验证真实 Core 重放、生产 Scenes 图层生成、完整 Godot 隔离或四模块联调。

选择“玩家编辑器预览”启动 `EditorPreview`，使用同份生产 App、编辑协调、界面和渲染，Core／Content／Scenes 为明确的有限情景替身。引擎 `EditorUiTests` 检查同帧拾取、当前转动线框、嵌套输入坐标、笔刷整笔应用、大范围修改确认、过期预览拒绝、Enter 拒绝和失败后手动重试；设置 `TRAIN_UI_CAPTURE_DIR` 后有窗口运行该类会保存 `player-editor.png` 与渲染身份。它不验证尚未接入的真实编辑会话、用户库生产装配或阶段 3 的完整设计与委托闭环。

开发创作的宿主集成用例位于 `Tests/Godot/Authoring/`：同份纯 .NET 写入器以真实文件验证来源冲突、显式删除、Keep File 追加文本和部分完成；测试程序集的 `--package-write-process` 子进程在第 N 项发布完成后发信号并阻塞，父进程收到信号后终止，核对已写文件与未列入补丁的普通包文件，不依赖随机时机。这里保留的 `draft.json` 是普通包文件，不是 `user://authoring-drafts/packages/…` 下的草稿。协调用例用列明情景替身验证关闭保护、候选不激活与失败协调；`AuthoringUiTests` 在真实 Godot 中检查原始输入脏标记和逐类型字段。

`Tests/Engine/AuthoringIntegrationTests` 使用真实 Core／Content／Scenes 工厂和隔离用户目录下的临时本地 Mod，验证网格编辑、真实定义草稿编码、写前叠加校验、逐文件写入、普通重载激活，以及来源冲突、非网格表单写回和真实内容拒绝。新模板的 `/display` 契约缺口（#595）与未完成生物聚落字段的 `invalid_field` 验证按普通诊断路径显示字段。`PackageRestartTests` 的 `--package-restart-child` 子进程执行真实 `PackageWritePlan` 与生产写入器，首项完成后发信号阻塞，父进程确定性终止；另一子进程普通加载，断言真实内容给出缺失资产诊断且磁盘不自动恢复。`AuthoringPreviewTests` 接入真实模板帧、重复地块编号的数组下标拾取与刷新相机；设置 `TRAIN_UI_CAPTURE_DIR` 后有窗口运行保留地表轮廓截图供视觉检查。自动用例不写仓库 `builtin/core`；手工 `--authoring-core-probe` 入口使用编辑器二进制验证源码 core 的打开、网格编辑、草稿、写包与普通重载，并在 `finally` 恢复原始字节，另记录运行前后 Git 状态。完整 #300 联调、保存／恢复／严格重放仍需阶段验收。Keep File 导入器本身沿用既有资产导出证据，本票只验证追加项文本。

## 远端检查

推送自己的任务分支后，可把完整回归交给 Linux runner。工作流选择、事件白名单与 job 上限见[CI](ci.md#linux-夜间运行)；长示例的平台分工见[示例手册](../examples/README.md#重制与长验证)。

```powershell
# 分支全量检查：全部正确性层和 Linux 阶段检查
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=nightly
# 长示例各自触发，不进入日常测试层
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=long-example -f example=lease-expiry
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=long-example -f example=starting-trade
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=long-example -f example=rail-repair
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=long-example -f example=conquest
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=long-example -f example=clearance
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> -f selection=long-example -f example=operating-failure
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

Actions 页面和 job 摘要也可查看结果、逐条命令退出码与耗时，并下载 artifact。排队和运行时间不表示通过；必须核对结论、被测提交和摘要中的实际检查。失败或未完成的命令不能作为成功证据。工作流取自 `--ref`，可选 `commit` 指定被测代码；留空时二者使用触发的 ref。核对 artifact 中 `ci/environment.txt` 的 `head=`（被测提交）、`workflow=`（工作流版本），以及摘要中的 commit；Actions 的 `headSha` 对应工作流版本，不能代替实际被测提交。编排脚本取自工作流版本，CLI、配方、SDK 配置及全量检查入口取自被测提交。工作流文件已在默认分支，手动 dispatch 可使用任务分支上的版本；上述 master 命令须等新输入合并后使用。

Linux 全量结果可承担同份代码的完整回归，但不替代真实窗口交互／截图、目标实机性能、Windows 导出及导出后运行、已提交 Windows 示例的重制比较，以及 Windows 固定重放语料等平台限定检查。上述检查仍在相应本机环境运行，按[Testing standard](implement-sop.md#testing-standard)判断证据是否适用；最终 PR head 的 Windows routine CI 仍须通过。Linux CLI 长示例不提供 Godot、Scenes 或完整阶段联调证据。

## 观察无关检查与窗口测量

`ObservationIndependenceTests` 由引擎层执行，使用默认 App 的真实 Core／Content／Scenes 装配和 `examples/basic-through/recipe.json`。参考运行使用 60 Hz、1×、不打开局部场景；五组变体覆盖 30／120 Hz、不规则间隔、2×／4×／8×、运行中切换倍速、暂停中连续命令，以及线路／节点／跟随场景的打开、切换和关闭。间隔由测试注入生产帧驱动，不依赖机器实际 FPS；在 `BoundaryCompleted` 请求完整边界暂停。每条命令前后及目标位置先核对位置、种子、运行身份和 `CoreInputId`，再委托核心比较检查点并定位首个字段差异。它提供真实模块的观察无关证据，不提供窗口性能或完整 Demo 验收。

同存档变体先生成测试自己的世界，经生产 `SaveService.Save` 发布临时存档，再由三个独立 App 经 `SaveService.Load` 读取同一文件，分别以 1×、固定 8× 和不规则帧间隔 8× 执行相同配方，复用上述完整边界检查点比较，并核对起始存档字节未变。

`scripts/export-measurement.ps1` 为测量生成并保留 Windows Release 导出，先读[本地 Windows 环境要求](local-environment.md#windows-export-and-runtime-validation)。它拒绝脏工作树，导出结束后确认 HEAD 未变且工作树仍干净。它隔离用户目录，只复制已安装的 Windows Release 模板并在结束时清理该副本。构建和测量的输出都须使用新目录或空目录；仓库内构建目录须被 Git 忽略，推荐 `TestResults/`。统一测试入口会删除自己的导出二进制；测量需要另行保留完整导出。

测量导出同样执行共享的程序集优化检查，结果记入 `export-summary.json` 的 `optimizations` 步骤；检查失败时拒绝该构建。

`scripts/measure.ps1` 复用这个已有导出，不重新构建。保留完整导出目录中的 EXE、旁置 PCK 和唯一的 `Train.dll`；输入为普通存档：

```powershell
# 构建一次，后续测量复用此固定二进制；保留完整目录与构建日志
pwsh -NoProfile -File scripts/export-measurement.ps1 -OutputDirectory TestResults/measurement-build
pwsh -NoProfile -File scripts/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/basic-through/save.json -OutputDirectory TestResults/measure-small-world
# 单独复测一组情景，仍执行三个独立进程
pwsh -NoProfile -File scripts/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/basic-through/save.json -OutputDirectory TestResults/measure-follow -Scenario follow-8x
# 在 PowerShell 中多选；不把逗号列表作为 -File 的单个参数传入
& ./scripts/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/basic-through/save.json -OutputDirectory TestResults/measure-selected -Scenario @('sdfgi-off', 'sdfgi-on')
```

报告的 `identity.game_build` 和 `identity.build_commit` 来自实际加载程序集的 `AssemblyInformationalVersion`。脚本从该构建提交计算 `git rev-parse <commit>:docs/contracts/public-api.txt`，记录为 `identity.public_api_git_blob`；提交须存在于本地仓库。脚本同时记录实际 EXE／PCK／`Train.dll` 与输入哈希，不以当前工作树 HEAD 代替二进制来源。

默认情景为 `map-1x`、`map-8x`、`follow-1x`、`follow-8x`、`node-1x`、`node-8x`、`sdfgi-off`、`sdfgi-on`、`taa-off`、`taa-on`。每组串行启动三个进程，各暖机 5 秒、采样至少 30 秒。脚本将输入、报告和身份路径传给生产 `--measure=<情景>`，不使用测试 Bootstrap、控制台命令或新增 UI。测量加载普通存档、启动其第一列非空编组列车并保留已有计划；节点情景观察首个节点，其他局部情景跟随该列车首车。SDFGI／TAA 情景均以 8× 运行，只切换对应效果，其余使用当前游戏画质。

所有非 `scene-*` 情景（地图、跟随、节点及 SDFGI／TAA）都可通过 `-DurationSeconds` 指定 1–120 秒采样时长（应用参数 `--measure-duration`），例如 `-DurationSeconds 120`；省略或设为 0 时仍为 30 秒。非 scene 进程超时按采样时长加 40 秒估算启动和收尾成本，取 1.5 倍并向上取整到 30 秒，且不低于 180 秒；120 秒样本的进程超时为 240 秒。

`timing.actual_speed` 保留整段实际倍速；`speed_windows` 按从采样起点开始的连续目标秒，取每个目标秒后的首个绘制帧作为边界，记录实际耗时及完成步数，不插值、不重叠计算步数。`window_median_actual_speed` 和 `window_minimum_actual_speed` 分别报告各窗口倍速的中位数与最小值；120 秒样本形成 120 个窗口，末尾超出最后目标秒的部分仅计入整段。单帧跨越多个目标秒而无法形成非零窗口时测量失败。帧间隔 p50／p99／maximum 使用全部绘制帧，不丢弃卡顿帧。跟随情景的原始 `frames` 追加 `follow`，包含列车编号、`Edge`／`Node` 位置种类与编号、已显示帧的空间编号；这些字段读取已有状态，不额外调用核心查询。

各次运行保留 `identity.json`、`report.json`、进程日志、`external-load.jsonl` 和新的 `measured.save.json`，原输入不变。报告核对当前加载程序集的 SHA-256 与构建提交，记录实际窗口／viewport、GPU／驱动、录制和自动保存设置。`timing.frames` 保存每个 `FramePostDraw` 间的单调时间、绝对完成步数、请求倍速、暂停状态和模拟工作时间。时间单位为秒，内存单位为字节；分位数使用 nearest-rank，不插值，实际倍速按完成步数除以 60 和实际采样秒数计算。模拟工作时间覆盖帧驱动推进及边界消费者，排除候选准备和渲染提交；5 ms 预算触及次数单列，不作为失败阈值。首次进程读档、场景首个实际绘制帧、Ready 帧和保存的耗时及成败分别记录，OS 文件缓存和驱动着色器缓存未清空。

内存只在帧采样结束后读取一次，`Process.Refresh()` 与属性读取的开销不计入帧间隔。`working_set_bytes`、`private_bytes`、Godot 静态内存和显存是此时的当前值；`peak_working_set_bytes` 是从进程启动至读数时的工作集峰值，包含读档、场景准备和暖机，不是采样窗口内的峰值。Release 不可用的 Godot 静态内存或显存指标写 `unavailable`。报告的 `identity.memory_scope` 记录这个范围。

SDFGI 情景必须在采样窗口观察到原点变化，跟随／TAA 情景必须观察到车辆运动；否则明确失败。专项在采样结束后保留连续实际绘制帧 PNG，SDFGI 额外等待下一次原点变化再采图。截图回读及 PNG 编码不计入帧统计。图像仅提供复核材料，GI 重建表现和拖影的视觉判断与数值报告分开。

这些结果只对应阶段一小世界、开发机和当前占位内容。外部采样每秒记录总 CPU 占用与其他 Godot／dotnet 进程，不要求暂停同机任务；并发负载可能影响数值，不能作为可比性能基线。5 ms／8× 与尾部、内存、等待数值交用户阶段试玩确认；完整混合负载、目标实机和两项完整长测仍由后续阶段承担。

### 场景打开、离开节点与规模测量

```powershell
# 同一 HEAD、同一输入先生成参考；参考运行不计入性能样本
pwsh -NoProfile -File scripts/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/starting-trade/save.json -OutputDirectory TestResults/scene-reference -Scenario scene-reference -Repeat 1
# 常态负载预演；正式重复次数由测量计划指定
pwsh -NoProfile -File scripts/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/starting-trade/save.json -OutputDirectory TestResults/scene-node-preview -Scenario scene-node -ReferencePath TestResults/scene-reference/scene-reference-1/scene-reference.json -Repeat 2
# 内存循环的短预演；默认 DurationSeconds=0 时运行两小时
pwsh -NoProfile -File scripts/measure.ps1 -ExecutablePath TestResults/measurement-build/train.exe -InputPath examples/starting-trade/save.json -OutputDirectory TestResults/scene-memory-preview -Scenario scene-memory -ReferencePath TestResults/scene-reference/scene-reference-1/scene-reference.json -Repeat 2 -DurationSeconds 30
```

| 情景 | 实际路径 |
| --- | --- |
| `scene-reference` | 独立参考运行，暂停至初始 Ready 后恢复仿真，再观察 10 秒；各空间首次 Ready 及焦点再移动 16 m 后的 Ready 帧记录公开对象和地形，输出 `scene-reference.json`，不作性能样本 |
| `scene-map` | 同存档的新进程地图内存基线，不打开场景 |
| `scene-node` | 地图首次打开节点 2、返回地图后重开、节点→线路 11、线路→节点、读档回地图后立即再开节点 |
| `scene-node-first` | 只测地图首次打开节点 2，用于规模点 |
| `scene-edge` | 地图首次打开线路 11、返回地图后重开、读档回地图后立即再开线路 |
| `scene-follow` | 地图首次点击列车 1，跟随车辆 1 |
| `scene-follow-8x` | 经同一 UI 路径进入跟随，切 8×、预热 5 秒后采样 120 秒；可用 `DurationSeconds` 改为短预演 |
| `scene-route-1x`、`scene-route-2x`、`scene-route-4x`、`scene-route-8x` | seed 40 起步贸易存档，跟随列车 1 走完线路→节点→线路路线，再补齐交接后的车身轨迹；记录 Scenes／实际显示的前方覆盖、车身覆盖、边界截断和未知，倍率取自原生帧循环 |
| `scene-route-capture-1x`、`scene-route-capture-8x` | 同一路线的独立截图运行，截图开销不计作计时证据 |
| `scene-memory` | 重复打开节点并返回地图；每 10 轮及结束后执行完整 GC；默认两小时，不使用其时延作正式性能证据 |
| `scene-capture` | 单独记录部分就绪、Ready 画面及雾区拾取；截图回读与编码使该进程不能作时延或内存性能证据 |

`Repeat` 控制每情景的新进程数量，默认是 3；`DurationSeconds=0` 使用情景默认时长。`scene-follow-8x` 的 `DurationSeconds`（应用参数 `--measure-duration`）只能缩短默认的 120 秒采样窗口。输入规模及测量、探针和判定方法见[场景预算验证报告](research/scene-budget-verification.md)。

除 `scene-reference`、`scene-map` 和 `scene-route-*` 外，所有 `scene-*` 情景必须传 `-ReferencePath`，指向同一构建提交、同一输入 SHA 的 `scene-reference.json`；参考运行不计入性能样本。整路测量使用默认 `DurationSeconds=0`，非零值只作提前结束的预演。

整路首次打开仅对初始空间单列 `route-analysis.json.startup`：从首个可操作帧起，以显示帧 `displayed_readiness` 在 5 秒内达到 Ready 判定。状态为 `passed`、`failed`、`unknown` 或 `not_observed`；历史记录缺少 readiness 为未知，初始空间从未可操作为未观察。初始空间在 Ready 前切走时，下一空间的 Ready 不能补算成功。初始空间这 5 秒内的判定帧以 `excluded_reason=initial_startup` 排除出 criterion 2 始终覆盖统计，逐帧原始覆盖仍保留；恰好第 5 秒起恢复统计，期间发生的跟随空间切换也立即恢复统计。该分类不改变 D18 near32 首次 Partial 的语义。

整路前方距离沿后续实际车头轨迹计算。Core 在列车参考中心过界时切换位置，因此空间边界估计为跨空间拼接轨迹中新空间首帧的车头位置，沿弧长回退半列车总长；容差取 2 米与跨界帧行驶距离的较大者。到达估计边界即截断，边界容差内发现的缺口记 `boundary_limited`；其他缺口仍计失败，轨迹不足或不能拼接仍记未知。车身覆盖独立检查，不因前方边界截断而豁免。

输出目录保留 `summary.json`，每个进程的子目录保留以下文件：

| 文件 | 内容 |
| --- | --- |
| `identity.json`、`report.json` | 构建与输入身份、游戏生成的运行报告；编排脚本不改写 `report.json`。`scene-follow-8x` 的 `timing` 是 8× 采样窗口，`scene-memory` 是初始地图窗口，其他非参考情景是最后地图保持窗口；打开过程读取操作记录 |
| `process.json`、`process.log`、`godot.log` | 进程结果与原始日志；`process.json.process_diagnostics` 分别保留两个日志来源匹配 WASAPI／AUDCLNT_E_DEVICE_INVALIDATED 的原始行 |
| `external-load.jsonl` | 外部系统负载采样 |
| `scene-frames.jsonl`、`scene-operations.jsonl`、`scene-collections.jsonl`、`scene-probe-costs.jsonl` | 逐帧状态、打开操作、GC 和全过程探针耗时记录（含读档）；路径由报告 `identity.scene_budget` 引用 |
| `scene-reference.json` | 仅参考情景生成的参考数据 |
| `route-frames.jsonl`、`route-ranges.jsonl`、`route-analysis.json` | 整路逐帧轨迹、范围变化及离线覆盖结果；不能把未知或观察不足当作通过 |
| `measured.save.json`、截图 | 对应情景生成的存档副本或画面 |

## 阶段检查与候选基准

`stage` 在所有平台始终运行 structure、verify、Core/Content/Scenes/Godot 隔离重建、两项生产 Core 确定性用例和独立 Release Benchmark。确定性用例通过生产命令日志与回放接口，在新进程逐检查点比较回放，以及每步写盘、读盘、恢复后与连续运行比较。它们属于阶段层，快速层和集成层不包含这些用例。

Windows 阶段层还执行 Windows 导出、空用户目录与有效 Mod 的导出后运行、资产导出探针、有窗口的主菜单 GPU 截图比较，以及吞吐阈值判定。非 Windows 仍运行 Benchmark 并保存结果；导出、导出后运行、资产探针、截图和阈值判定在摘要中记为未执行，不记为通过。当前 Linux 检查不需要渲染上下文，不使用 xvfb；无界面 Godot 使用 Dummy 音频。

发布导出冒烟保留生产内容、PNG 和有效 Mod 检查，同时从 PCK 的 `res://examples/basic-through/save.json` 经过正常恢复流程打开示例，不执行配方。它检查原身份与新身份均为有效且不同的 UUID、位置 200:0、暂停、未保存，以及玩家菜单隐藏示例入口；不保存副本或读回。副本保存到新 UUID 目录且原件不变由宿主测试在临时目录使用真实文件 I/O 验证。开发编辑器的可见入口由引擎测试覆盖；调试导出另做人工验证，命令、日志和截图记录在 Issue 交付评论，不加入 `test.ps1`。这些检查不替代 #298 四模块联调验收。

截图复用引擎测试程序集、Bootstrap 和 JUnit，仅在 Windows 有窗口运行中执行。`stage-screenshot` 设置 `TRAIN_ENGINE_SCREENSHOT=1`，一起执行 `ScreenshotTests` 和 `SceneMaterialPixelTests`，最低 7 例：主菜单截图、5 个材质像素用例和模型实例绘制调用测量。后者分别采样 1 与 64 实例的 5 个已完成帧，要求调用数稳定、非零且相等，原始数值输出到 Godot 日志。普通 headless engine 在执行前排除这两个窗口套件，不把它们计为通过或跳过。输入为 [windows-main-menu.png](../Tests/Engine/Baselines/windows-main-menu.png) 与 [windows-main-menu.json](../Tests/Engine/Baselines/windows-main-menu.json)，通过物理路径读取；`Tests/.gdignore` 阻止编辑器为测试 PNG 生成导入文件。比较先转为 RGBA8。候选值为 `MaxChannelDelta=32`、`MaxDifferentPixelFraction=0.00002`：任一通道差大于 32 的像素计为明显差异，明显差异像素数除以总像素数大于 0.00002 才失败。`comparison.json` 每次记录任意差异像素数、明显差异像素数及比例、最大通道差；`difference.png` 用红色区分明显差异、黄色区分轻微差异，通过时也保留。尺寸不一致、缺少基准或容差均失败。

吞吐输入为 [windows-core-train-loop.json](../Tests/Benchmarks/Baselines/windows-core-train-loop.json)。BenchmarkDotNet 以独立 Release 构建测量 `core-train-loop-v2`：复用 Core 世界夹具并显式固定种子 42、生成器 `stage1-world-v1`、40×20 米世界、80 米进站段、96 米站台段、80 米出站段、15 米/秒节点限速和 0.7 米/秒²制动减速度，通过真实 `CoreFactory` 创建 `ICoreSession`，一辆牵引力 100 kN、最高速度 30 米/秒的列车持续循环经过三个车站，依次停留 4／3／2 步，每批 65,536 个完整模拟步。每次迭代在计时外重建会话并设置循环计划，计时仅覆盖 `ICoreSession.Advance`；结束时在计时外核对实际完成步数、无故障、循环计划仍活动，以及每个计划车站至少两次真实到达，并释放会话。`throughput.json` 保存负载身份、种子、生成器、世界尺寸、节点三段长度、节点限速、制动减速度、列车数、批次步数、每步耗时及标准差、样本数和步/秒，BDN 日志和报告保留原始测量。它只测量该固定列车运行负载的核心吞吐，不提供帧耗时或完整游戏 8× 运行证据。v2 的 Windows 候选规则为 `baselineStepsPerSecond=130000`、`maximumSlowdown=3`；基线取三次独立完整测量中的最低吞吐，向下取整至每秒一万步，仍等待用户确认，仅当实测步/秒乘以 3 小于候选基线时失败；摘要记录实测值、基线和实测除以基线的比值。缺少基准、负载身份不符或无效数字仍失败。Linux 只记录结果，不应用 Windows 阈值。

需要单独重新采集吞吐时，显式构建不在 `Train.sln` 中的 Benchmark 项目，再从项目目录启动。基准的构建工具链直接指定本次编译源码所在的 `Train.Benchmarks.csproj`，并在日志中写出路径，避免 BenchmarkDotNet 默认从解决方案根目录递归搜索时扫到 `TestResults` 中保留的旧隔离副本，无需删除证据：

```powershell
dotnet build Tests/Benchmarks/Train.Benchmarks.csproj -c Release
Push-Location Tests/Benchmarks
try {
    dotnet run --project Train.Benchmarks.csproj -c Release --no-build -- --artifacts <本次运行目录的绝对路径>
} finally {
    Pop-Location
}
```

首次确认启动方式时，在上述运行命令末尾添加 `--job Dry`，只做一次最短作业，验证构建、执行和 `throughput.json` 写出。结果的 `dryRun` 为 `true`、`sampleCount` 为 1；这只证明运行链路可用，不能作为吞吐或候选基线依据。正式测量使用完全相同的启动命令，仅去掉 `--job Dry`，并为每次运行指定独立目录。

提交的 PNG、容差和吞吐基线均为**等待用户阶段验收确认的候选基准**，只能用于检查机制，不能据此宣布阶段或产品性能验收通过。更新截图时，在目标 Windows 图形配置下至少用三个独立 Godot 进程和独立用户目录采集，核对菜单内容并比较各图的通道差和明显差异像素比例，再替换 PNG 与容差 JSON。更换 GPU、驱动或 Godot 版本后重新核对。需要重新采集吞吐时，使用上面的 BenchmarkDotNet 命令做多次独立完整测量，记录外部负载和波动，以实测结果提出基线；不要用单次较快结果掩盖波动。候选值及其替换需用户阶段验收确认，替换后重跑完整阶段检查。原始截图、差异、设备信息和测量结果只保留在忽略的运行目录，长期证据引用写入 Issue 评论，不提交运行报告。

`core-train-loop-v2` 复用的铁路恢复夹具采用 120／720 步的人口消费／统计间隔，与内置规则一致；共享基础夹具仍用 1／10 步缩短人口行为用例。基准在计时外创建会话，不移除人口执行。比较其他提交时，两边应使用同一份基准和世界／铁路夹具源码，使列车、地图、内容及人口间隔一致。

## 按改动选择

默认基准 `-BaseRef origin/master` 必须在本地存在。入口以 `git merge-base <BaseRef> HEAD` 为比较起点，包含该点之后的提交及暂存、未暂存、未跟踪改动；文件列表禁用重命名识别，移动前后路径都参与规则匹配。入口不自动 fetch。dotnet-affected 7.0.1 以 `Train.sln` 限定项目图，沿 MSBuild 项目依赖选出受影响项目，再从六个已登记 MSTest 项目中选择；归档和调用样例不进入这张项目图。引擎层复用同一次改动分析：dotnet-affected 输出包含 `Tests/Engine/Train.Engine.Tests.csproj`，或改动命中下述引擎路径规则时选中；该项目引用根 `Train.csproj`，所以根项目及其依赖的影响沿同一张项目图传播。选中后执行当前全部引擎用例。`-All` 和 `-ExportWindows` 强制引擎，不依赖改动结果。单独选择 `stage` 时完全跳过改动分析，不要求基准引用存在，始终执行[阶段检查](#阶段检查与候选基准)；`-Layer stage,engine` 不强制引擎，阶段全量检查使用 `-All -Layer all,stage`。

以下共享文件改动选择全部 MSTest 项目：`Contracts/**`、任意层级的 `Directory.Build.*`、`global.json`、`docs/content/schemas/**`、`Tests/ContractSupport/**`、`Train.sln`、`.config/dotnet-tools.json`、`scripts/test.ps1`、`scripts/checks/**`。工作流 YAML 不在共享列表中。

CLI 及其项目依赖的源码改动由 dotnet-affected 沿引用图选择。`examples/**`、`builtin/**` 和 `project.godot` 是 CLI 的额外输入，改动时也选择 CLI 测试；摘要逐文件记录这三组补充规则 `cliRules`。

引擎路径规则复用上述整体构建与测试入口条目，但不包含 `docs/content/schemas/**` 和 `Tests/ContractSupport/**`；另外包含 `Tests/Engine/**`、`project.godot`、`export_presets.cfg`、`builtin/**`、`addons/**`，以及 `archive/`、`docs/`、`prototypes/`、`Tests/` 之外的 `*.tscn`、`*.tres`、`*.uid`、`*.import`。`Tests/Engine/` 内的资源已由该目录规则覆盖。普通宿主 MSTest 被选中不表示引擎也被选中。

集成层的契约结构、公开面和调用样例检查，在 `Contracts/**`、任意 `*.csproj`、任意层级的 `Directory.Build.*`、`Train.sln`、`prototypes/**`、`Tests/ContractChecks/**`、`scripts/checks/project-boundaries.json` 或 `docs/contracts/public-api.txt` 改动时进入默认选择；`builtin/**` PNG／GLB 及其 `.import` 改动也选择该检查，由 C# 工具要求全部内置资产使用 Keep File。内容 Schema 检查在 `docs/content/**`、`builtin/core/**` 或 `scripts/content/**` 改动时进入默认选择；同一任务依次检查示例 Schema、正式数据可重制性与 Schema，以及正式经济关系和静态供电估算。隔离重建在任意 `*.csproj`、任意层级 `Directory.Build.*`、`Train.sln`、`scripts/checks/project-boundaries.json` 或 `Tests/ContractChecks/**` 改动时进入默认选择。`-All` 选择结构、公开面、隔离和 Schema 独立检查。

dotnet-affected 退出码 166 表示空项目集合；其他非零退出码、120 秒分析超时或进程清理失败均使入口失败。工具输出写入本次运行目录。仓库路径保留 Git 返回的大小写，再用 `Path.GetFullPath` 转为原生完整路径与分隔符，避免工具的路径前缀匹配漏选。

执行改动分析时，摘要的 `selection` 保存基准引用、共同祖先 SHA、改动文件、共享规则、工具输出项目集合和最终选择。无论请求哪些层，这些运行的 `selection.engine` 都记录引擎是否受影响；逐文件的 `engineRules` 记录命中的引擎路径规则，`affectedProjects` 中的引擎测试项目解释依赖图触发，`mode: all` 或 `exportWindows: true` 解释强制选择。每个文件的 `project` 是包含它的最近项目目录对应的项目，嵌套项目优先；仅当该项目也在工具输出集合中时记录，否则为 `null`。此对应只作解释，选择仍由工具结果及共享规则决定。单独选择 `stage` 时摘要记录 `mode: stage`，不记录改动分析或引擎选择。

## 执行与边界

选中 MSTest 项目、契约或隔离检查、引擎层或阶段层时，入口构建一次 `Train.sln`，MSTest 使用 SDK 的 `dotnet test --test-modules <相对 DLL 路径> --root-directory <根目录>` 运行已构建程序集，不重复构建。单独的 Schema 任务只调用 Python，不构建；正式经济检查也可独立运行 `python scripts/content/check_economy.py [content-root]`，默认读取 `builtin/core`，标准输出为 JSON 报告。C# 工具随解决方案构建；structure 通过工具生成的遍历项目，在一个 MSBuild 进程内采集全部登记项目的引用、编译项、友元和输出路径。四组调用样例在该进程内恢复、重新求值并构建，使用 `BuildProjectReferences=false` 和 `RestoreRecursive=false` 避免重复构建共享契约；样例仍不加入解决方案。引用采集、样例构建、隔离构建与工具自测的夹具构建均使用 `-maxCpuCount:1` 和 `UseSharedCompilation=false`，限制并发任务内的 MSBuild 节点数并禁用共享编译服务器。verify 检查公开面和类型归属，isolate 按[矩阵选择规则](contracts/testing.md#实际隔离与结构检查)选择模块，串行复制独立输入、冷构建、独立查询 MSBuild 事实并运行各自非阶段用例，首个失败后停止；已构建的隔离测试 DLL 同样由 SDK 的 `dotnet test --test-modules` 运行，引用判定与 structure 共用。隔离重建排除 `stage` 类别，由阶段入口单独运行这些用例。阶段层始终依次执行这三个命令，再执行确定性用例和 Benchmark；混选集成层和阶段层时，各自执行所需检查。

各隔离测试会话的进程上限 N 为 270 秒，由 `Isolation.SessionTimeoutSeconds` 定义；挂起转储在 N−10 秒触发，MTP 在 N−5 秒中止会话，外层 ToolProcess 在 N 秒终止进程。三者任一结束会话时，隔离失败信息均标明超时、模块、实际耗时、上限 N 和日志路径。

各层测试执行预算为快速 450 秒、集成 2070 秒、引擎 1710 秒。集成层选中的 MSTest 项目和 structure、verify、Schema 共用一个任务队列与 2070 秒截止时间，通过已有的 `ForEach-Object -Parallel` 执行，最多 4 个任务并发，较重任务排在前面；汇总与清理仍由父流程完成。队列正常结束或检查失败后，先停止集成层计时并保存用例计数，再独立运行选中的 isolate，使用 780 秒上限，不占用队列或集成层预算。收到停止请求时，不再启动尚未开始的独立隔离检查。快速层保持原有 MSTest 并行执行方式，每个项目保留各自的最低用例数、TRX、超时和挂起转储。阶段层不设总预算，串行执行 structure、verify、isolate，子进程上限分别为 300、120、780 秒；确定性阶段用例上限为 180 秒，Benchmark 为 600 秒，窗口截图为 120 秒；引擎检查仍串行执行。构建准备上限为 300 秒，Godot 导入、导出各为 120 秒。诊断区分已启动后超时（`TimedOut`）与截止时尚未启动（`NotStarted`），点名对应检查并记录逐项秒数；缺少 TRX 不覆盖超时诊断。超时用于发现挂起的任务，导致失败并保留可用转储；正常执行耗时增长时，按[预算调整原则](contracts/testing.md#预算调整原则)增加预算，不以延长预算掩盖挂起。必要的 Godot 导入只做一次；导出冒烟的两次运行共享一次 Windows 发布导出。同一工作树不允许并发运行入口。

预算合并依据：[#589](https://github.com/odradekk/train/pull/589) 的本机多 pair engine 层为 471.5293269 秒、同 head Windows CI 为 378.3510036 秒，按较慢值乘 1.5 并向上取整到 30 秒得到 720 秒；[#387](https://github.com/odradekk/train/issues/387) 含导出冒烟和 PCK 探针的本机 engine 层为 510.6451746 秒，同法得到 780 秒。当时两边取较大值，engine 使用 780 秒。继续合并 #390 后，协调者提前释放测量窗口、恢复本机常态工作后，完整 184 项 engine 层实测 539.3363178 秒，同法得到 810 秒，因此最终 engine 使用 810 秒。合并 #640 后，本机多 pair 常态负载下完整 247 项 engine 层实测 897.1310593 秒（`engine-20261019T074034914-bd2caa5`），同法得到 1350 秒；并入 70c82916 后同一负载下实测 972.795 秒（`engine-20261019T084230810-4fd010b`），同法得到 1470 秒，当时 engine 使用 1470 秒。#360 合并 #661 后，本机多 pair 常态负载下含导出冒烟和 PCK 探针的完整 engine 层实测 985.574866 秒（268 项真实 Godot 用例、导出冒烟和资源探针全部通过），同法得到 1500 秒，因此 engine 最终使用 1500 秒。#360 之后本机多 pair 常态负载下完整 269 项 engine 层实测 1124.5181922 秒（`engine-20261020T145552361-3d6dee5`，269/269 通过），乘 1.5 得 1686.7772883，向上取整到 30 秒得到 1710 秒，因此 engine 使用 1710 秒。

引擎单用例默认上限依据：2026-10-19 本机多 pair 常态负载下完整 247 项 engine 层实测最慢未单独设限用例 35.576 秒，结合 #301 同负载下记录的 42.301 秒，按较慢值乘 1.5 并向上取整到 30 秒得到 90 秒，因此默认上限使用 90 秒；慢用例继续使用各自的 `[Timeout]` 单独放宽。

引擎内容准备等待上限依据：2026-10-18 本机多 pair 常态负载归档运行（`issue-336-20261018T193314/TestResults/engine-20261018T094221738-9f19386/engine-godot-native.log`）中已完成的最坏启动内容准备为 26.7 秒，26.7 × 1.5 = 40.05 → 60 秒；#360 在 `f723b1a8` 首跑另有 2 次等待在 30 秒截断（真实值未知），30 × 1.5 = 45 也取 60 秒。`Real_form_draft...` 的 75.1 秒是第 65 行等待失败、后续未执行的下界。因此 `FramesUntil` 的内容准备等待上限为 60 秒，其他等待保持 30 秒。同一代码的测量运行 `engine-20261020T143836111` 的 26 次真实启动内容准备最小 6.087、中位 9.076、最大 13.690 秒，重载/写包上界最大 19.604 秒；干净重跑 `engine-20261020T145552361` 269/269 通过。单用例上限按 75.1 × 1.5 = 112.65 → 120 秒，`Real_form_draft...` 由默认 90 秒提高到 120 秒，其余现有上限足够。

快速预算依据：#352 在本机多 pair 常态负载下 fast 层 1454 项实测 90.3 秒，按较慢值乘 1.5 并向上取整到 30 秒得到 150 秒，当时 fast 使用 150 秒。#360 在本机多 pair 常态负载下运行完整快速层，Scenes 的 672 项全部通过，测试本体耗时 144.529 秒，但进程在 149.2 秒尚未完成收尾时达到预算。包含收尾后的 1.5 倍余量向上取整到 30 秒，将 fast 提高到 240 秒；合并 #661 后，本机完整 3168 项 fast 层通过，实测 166.0641564 秒；乘 1.5 并向上取整到 30 秒得到 270 秒，当时 fast 使用 270 秒。继续合并 #574、#664 后，本机多 pair 常态负载下完整 3169 项 fast 层通过，实测 181.2759565 秒；乘 1.5 并向上取整到 30 秒得到 300 秒，因此 fast 使用 300 秒。原始与合并后实测记录见 #360。

集成预算依据：#387 此前完整集成层最坏实测为 353.2119539 秒，同法得到 540 秒；#589 同步后的 master 集成预算仍为 450 秒。同步后的新一轮完整集成层共 175 项，实测 388.6998585 秒，同法得到 600 秒。此后合并 #599 的完整 175 项集成层在本机多 pair 常态负载下实测 408.0123188 秒，同法得到 630 秒。继续合并 #390 后，完整集成层共 176 项，本机多 pair 常态负载下实测 470.1861206 秒，同法得到 720 秒，当时集成使用 720 秒。#360 在本机多 pair 常态负载下的 CLI 集成检查于 719.293 秒被预算终止，已完成 14 项通过、1 项 Linux 专用跳过，最后一次正常完成在 11 分 51 秒；仍有两条真实状态差异检查未完成，未见断言失败。提高到 1200 秒后的完整集成队列实测为 987.6055585 秒（191 项通过、1 项 Linux 专用跳过；其中 CLI 进程 884.8 秒），按较慢值乘 1.5 并向上取整到 30 秒得到 1500 秒，当时集成使用 1500 秒。继续合并 #574、#664 后，本机多 pair 常态负载下完整集成层通过，实测 1367.9973824 秒（191 项通过、1 项 Linux 专用跳过；其中 CLI 进程 1231.372 秒），乘 1.5 并向上取整到 30 秒得到 2070 秒，因此集成使用 2070 秒。原始失败与完整重跑记录见 #360。

各 job 按 workflow 实际执行的入口计算上限合计：

| job／入口 | 算式（秒） | 合计 |
| --- | --- | --- |
| Windows routine：`-Layer fast,integration` | `改动分析 120 + 构建 300 + 快速 450 + 集成 2070 + 独立隔离 780` | 3720（62 分钟） |
| Windows routine：`-Layer engine` | `改动分析 120 + 构建 300 + Godot 导入 120 + 引擎用例 1710` | 2250（37.5 分钟） |
| Windows correctness：`-All -Layer all` | `构建 300 + 快速 450 + 集成 2070 + 独立隔离 780 + Godot 导入 120 + 引擎用例 1710` | 5430（90.5 分钟） |
| Linux 全量：`-All -Layer all,stage` | `构建 300 + 快速 450 + 集成 2070 + 独立隔离 780 + Godot 导入 120 + 引擎用例 1710 + benchmark 构建 300 + structure 300 + verify 120 + 阶段隔离 780 + 阶段 Core 用例 180 + Benchmark 600` | 7710（128.5 分钟） |

改动分析的 120 秒只出现在非 `-All` 入口。Godot 导入的 120 秒在引擎计时之外，不占用 1710 秒引擎预算。routine 的第一个入口失败后仍会执行 engine 入口，因此两个入口相加，最坏 5970 秒（99.5 分钟）。

准备与收尾（检出、还原、工具准备、下载和上传）实测约 1 分钟；`Invoke-HiddenProcess` 超时后最多再等 5 秒清理进程。因此 Windows job 上限设为 107 分钟，Linux 全量 job 上限设为 136 分钟。以后调整任何入口上限时，按表中算式重算合计，再为准备收尾和超时后的进程清理留出约 7 分钟。

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

定位单个引擎用例时，先构建 `dotnet build Tests/Engine/Train.Engine.Tests.csproj -c Debug` 并按[隔离子进程片段](local-environment.md#agent-手动启动-godot)完成资源导入。在运行用例的隔离子进程内设置 `TRAIN_ENGINE_TEST_ASSEMBLY` 为 `.godot/mono/temp/bin/Debug/Train.Engine.Tests.dll` 的绝对路径，`TRAIN_ENGINE_REPORT` 为本次目录内 JUnit 文件的绝对路径，并设置 `TRAIN_ENGINE_SCREENSHOT=0`。Godot 参数使用 `--headless --path . --scene res://Tests/Engine/Bootstrap/Runner.tscn --log-file <本次原生日志> -- --run-tests=<类>[.<方法>] --quit-on-finish`，将选择表达式替换为类名或类名加方法名；此手动运行有用户目录隔离，但不执行统一入口的最低用例数与快照检查。

单独重新采集截图时复用上述 Bootstrap 和[隔离子进程片段](local-environment.md#agent-手动启动-godot)：在子进程内设置 `TRAIN_ENGINE_SCREENSHOT=1`、`TRAIN_SCREENSHOT_OUTPUT` 为证据目录，并去掉 `--headless`。这会选中截图与材质窗口套件；只重新采集主菜单时可用 `--run-tests=ScreenshotTests` 限定选择。运行截图套件后从该目录取得 `actual.png`、`rendering.json` 和已有基准下的 `comparison.json`；缺少基准时仍保存实际 PNG，然后按预期失败。每次采集使用新的进程和用户目录，采集后只清理自己创建的用户目录；调用终端的环境保持原值。普通无界面用例使用 `TRAIN_ENGINE_SCREENSHOT=0`。
