# Godot / Windows MVP 技术基线与人周砍线

研究票：[确定 Godot 与 Windows 技术基线及人周砍线](https://github.com/odradekk/train/issues/9)

研究日期：2026-08-23  
修订：用户明确选择 C#；此前“标准版 Godot + GDScript”结论作废。

## 结论

采用以下技术基线：

- **引擎**：Godot `4.7.x` 稳定分支的最新补丁；研究时最新补丁为 `4.7.2-stable`。
- **编辑器**：Godot **.NET 版**编辑器。
- **语言与运行时**：C#，使用 `global.json` 固定 64 位稳定 .NET 9 SDK；本机已安装 `9.0.316`，满足 Godot 4.7 文档的 .NET 8+ 要求。
- **目标**：Windows Desktop `x86_64`，键盘鼠标，窗口化桌面构建。
- **渲染器**：Compatibility。
- **经济模拟**：不使用 Godot 物理；采用普通 C# 类、整数状态、显式事件顺序和固定整数市场刻。
- **静态数据**：C# 自定义 `Resource` 与 `.tres`，通过 `[GlobalClass]`、`[Export]` 和 Inspector 编辑 12 站点、3 种货物、铁路边和初始市场。
- **运行状态**：独立的内存 `SimulationState`；每次重置从不可变场景定义重新创建，不做通用存档。
- **界面**：Godot `Control` 与容器节点实现地图优先编辑页、平衡分屏运行页和圈后报告；C# 脚本作为场景适配层。
- **简化 3D**：`Node3D`、`Path3D`/`PathFollow3D`、内建 `BoxMesh`、纯色材质和固定相机；不引入正式体素资产、GridMap/CSG 内容管线、物理碰撞或自由相机。
- **试玩记录**：默认不在游戏内持久化；按已确认的个人试玩闸门在游戏外手记一行。若 4 人周内有余量，才向 `user://playtest.jsonl` 追加一行 JSON。
- **构建**：提交 `.sln`、`.csproj`、`export_presets.cfg` 和 C# 源码；忽略 `.godot/`；安装匹配引擎版本的 **.NET export templates**；先 `dotnet build`，再执行 Godot Windows export。

这套基线覆盖已经决定的 MVP。在假设实现者熟悉 C#、具备一般软件开发能力、Godot/.NET 集成经验为初级到中级、复用仓库内三个已验证 HTML 原型、全部资产使用占位几何的前提下，预计 **3–4 人周**。若实现者同时不熟悉 C# 和 Godot，则 4 人周不可可靠承诺。

## 事实、结论和建议

### 版本与支持

**观察**：Godot 官方 GitHub Release API 显示 `4.7.2-stable` 发布于 2026-08-18，是研究时最新的 Godot 4 稳定补丁；发布说明称维护版本兼容之前版本并推荐采用。[Godot 4.7.2 release](https://github.com/godotengine/godot/releases/tag/4.7.2-stable)

**观察**：Godot 4.7 文档将 4.7 列为受支持稳定分支；补丁版本向后兼容，官方强烈建议同一稳定分支用户跟进最新补丁。官方也建议新项目使用 Godot 4.x。[Godot release policy](https://github.com/godotengine/godot-docs/blob/4.7/about/release_policy.rst)

**结论**：使用 Godot 4.7.x .NET 稳定版，不使用并行维护的 Godot 3.6 或 4.8 开发版。

**建议**：约束写成“Godot 4.7.x 最新稳定补丁”。同分支 patch 可在 smoke 后采用；升级到 4.8 等新 minor 必须单独决定。

### C# / .NET 工具链

**观察**：Godot 支持 C#，但标准编辑器不包含 C# 支持；必须下载 .NET 版编辑器。[Godot C# overview](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/c_sharp/index.rst)

**观察**：Godot 不自带构建 C# 项目所需的 MSBuild 和编译器；必须另行安装 .NET SDK。4.7 分支文档要求 .NET 8 或更高版本，并建议安装最新稳定 SDK。Godot 内置 C# 编辑体验有限，官方建议使用 Visual Studio、VS Code 或 Rider 等外部 IDE。[C# prerequisites and editors](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/c_sharp/c_sharp_basics.rst)

**观察**：Godot 4.2 起 C# 支持 Windows、Linux、macOS 桌面导出；当前 MVP 仅目标 Windows。[C# platform support](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/c_sharp/index.rst#c-platform-support)

**观察**：创建首个 C# 脚本后 Godot 会生成 `.sln` 和 `.csproj`；这些应提交，`.godot/mono` 和整个 `.godot/` 可忽略。[C# project setup](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/c_sharp/c_sharp_basics.rst#project-setup-and-workflow)

**观察**：4.7.2 官方 Release 资产同时提供 .NET Windows 编辑器和 .NET export templates。[Godot 4.7.2 assets](https://github.com/godotengine/godot/releases/tag/4.7.2-stable)

**结论**：C# 是本项目的固定实现约束。额外成本集中在首次环境、项目构建和 Godot API 适配，不是语言选择讨论。

**建议**：

- 安装 Godot 4.7.x .NET `win64`、匹配的 .NET export templates。
- 使用项目根目录 `global.json` 固定本机已安装的稳定 .NET SDK `9.0.316`，避免默认使用 `10.0.400-preview`；Godot 4.7 文档最低要求为 .NET 8。
- 使用用户熟悉的 C# IDE；不把更换 IDE 或学习 GDScript 放进 MVP。
- `dotnet build` 必须在首日 smoke 与每次导出前通过。
- 提交 `global.json`、`.sln`、`.csproj`；`.gitignore` 增加 `/.godot/` 和构建输出目录。

### 确定性模拟

**观察**：Godot `_Process()` 的频率取决于帧率；`_PhysicsProcess()` 默认按固定频率运行。[Idle and Physics Processing](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/idle_and_physics_processing.rst)

**观察**：Godot 发布政策明确写明 Godot 物理引擎不是确定性的。[Godot release policy — compatibility criteria](https://github.com/godotengine/godot-docs/blob/4.7/about/release_policy.rst#what-are-the-criteria-for-compatibility-across-engine-versions)

**结论**：经济状态不能依赖物理节点、碰撞、渲染帧或浮点 `delta` 累计。

**建议**：

- `SimulationEngine`、`SimulationState`、`TradePlan`、`RunReport` 是不继承 `GodotObject`/`Node` 的普通 C# 类或 record。
- 所有供给、需求、资金、货物、市场刻、铁路耗时和价格使用整数。
- 用 `RunMarketTicks(int count)`、`TradeAt(StationId)`、`RunNextEvent()` 显式推进。
- 1×/4×/16×、跳站、快速完成只改变表现等待，不改变事件结果。
- Godot Node 脚本通过窄接口调用领域层；3D 插值绝不反向驱动模拟。
- 纯领域层应能由 `dotnet test` 或一个小型 console smoke 独立运行；本 MVP 只保留覆盖失败圈、修正圈、部分成交和重置的少量测试，不建立大型测试框架。

### 数据定义

**观察**：Godot `Resource` 是数据容器，支持 Inspector 编辑和 `.tres` 文本序列化。[Resources](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/resources.rst)

**观察**：C# 自定义全局类使用 `[GlobalClass]`；自定义 `Resource` 可用 `[Export]` 暴露属性。官方要求文件名与 GlobalClass 类名大小写一致。C# Resource 必须提供无参构造函数，否则 Inspector 创建和编辑会出问题。[C# global classes](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/c_sharp/c_sharp_global_classes.rst) [Resource C# example](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/resources.rst#creating-your-own-resources)

**结论**：12 站点固定矩阵不需要数据库、表格导入管线或自定义编辑器。

**建议**：建立以下 `partial class ... : Resource`：

- `GoodDefinition`
- `StationGoodDefinition`
- `StationDefinition`
- `RailEdgeDefinition`
- `ScenarioDefinition`

每类使用 `[GlobalClass]`、`[Export]` 和显式无参构造函数。`ScenarioDefinition` 引用其他定义并包含起始资金、货舱和预置贸易方案。不要建立通用经济 DSL、热加载、Spreadsheet importer 或 EditorPlugin。

### UI

**观察**：Godot GUI 使用 `Control` 节点，提供 Button、Label、LineEdit/TextEdit 以及 BoxContainer、MarginContainer、ScrollContainer、TabContainer、Popup 等节点。[Godot UI](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/ui/index.rst)

**结论**：地图优先编辑页、平衡分屏运行页和滚动报告都可用内建 UI 节点和 C# 脚本完成，无需第三方 UI 框架。

**建议**：

- `PlanningScreen.cs`：地图自绘 Control/Node2D 与右侧 Inspector。
- `RunScreen.cs`：`HSplitContainer`，左侧 3D，右侧实时面板。
- `ReportPanel.cs`：复用右侧区域，使用 ScrollContainer 与表格状容器。
- C# 场景脚本只协调 UI 和领域服务；市场公式不复制到 UI。
- 只做最小 Theme：字体、字号、间距和状态色。

### 简化 3D 与渲染器

**观察**：Godot 4 提供 Forward+、Mobile 和 Compatibility；Compatibility 支持核心 3D、面向低端桌面和最广硬件。[Renderer overview](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/rendering/renderers.rst)

**观察**：Compatibility 的简单导出项目最低 GPU 基线是 OpenGL 3.3 或 Windows Direct3D 11；Forward+/Mobile 需要 Vulkan、Direct3D 12 或 Metal。[System requirements](https://github.com/godotengine/godot-docs/blob/4.7/about/system_requirements.rst#exported-godot-project)

**观察**：Godot 提供 `BoxMesh`、`Path3D` 和 `PathFollow3D`，足以构造占位列车并沿曲线移动。[BoxMesh](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_boxmesh.rst) [Path3D](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_path3d.rst) [PathFollow3D](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_pathfollow3d.rst)

**结论**：本 MVP 不需要 Forward+、正式体素工具链、Blender、GridMap 内容生产、复杂 CSG 或物理列车。

**建议**：

- Compatibility 渲染器。
- 2–4 个 `MeshInstance3D` + `BoxMesh` 构成列车。
- 只制作西仓—中枢—南市的可见走廊；其他站点只存在规划地图和数据中。
- 固定 Camera3D、一个 DirectionalLight3D、纯色 StandardMaterial3D。
- `RunPresenter.cs` 根据模拟事件设置 `PathFollow3D.Progress`；跳站与快速完成可直接设置事件终点。
- 不使用碰撞、Navigation、阴影烘焙、全局光照、粒子、后处理或自由相机。

### 会话重置与试玩记录

**观察**：`user://` 在导出项目中保证可写；Windows 默认位置是 `%APPDATA%\Godot\app_userdata\[project_name]`。[Data paths](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/io/data_paths.rst)

**观察**：Godot C# 可用 `FileAccess` 和 `Json.Stringify()` 写可读文本；JSON 适合简单且易调试的数据。[Saving games](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/io/saving_games.rst)

**结论**：本 MVP 不需要通用存档。个人试玩闸门已经规定“每次只手记一行”，不要求应用内日志。

**建议**：

- 重置：重新从 `ScenarioDefinition` 构造内存状态。
- 默认：测试者在游戏外记录 `改了什么 / 为什么 / 结果怎样`。
- 有余量时才实现 `PlaytestLog.cs`，每圈向 `user://playtest.jsonl` 追加一行；它是第一砍线项。

### Windows 导出与许可

**观察**：Godot Windows 默认导出架构为 x86_64；导出需要匹配的 export templates。[Exporting for Windows](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/export/exporting_for_windows.rst)

**观察**：`export_presets.cfg` 通常可安全提交，`.godot/export_credentials.cfg` 含机密信息，不应提交。Godot 支持命令行 export。[Exporting projects](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/export/exporting_projects.rst)

**观察**：Godot 命令行支持 `--build-solutions`、`--headless` 和 `--export-release`。[Command line tutorial](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/editor/command_line_tutorial.rst)

**观察**：Godot Engine 使用 MIT 许可证，允许使用、修改与分发，但软件副本或实质部分需包含版权与许可声明。[Godot LICENSE](https://github.com/godotengine/godot/blob/4.7.2-stable/LICENSE.txt)

**结论**：个人 MVP 不需要自编译引擎、代码签名、安装器或 CI。

**建议**：

- Windows Desktop x86_64 preset，使用 .NET export templates。
- 构建顺序：`dotnet restore` → `dotnet build` → Godot `--build-solutions`（作为 Godot 集成检查）→ `--export-release`。
- 日常 debug 构建；内部闸门前至少一次 release 导出。
- 不做签名、安装器、商店集成或 ARM64。
- 导出包附 Godot MIT 许可文本；占位资产全部自制。

## 模块边界

```text
mvp/godot/
├── global.json
├── TrainMvp.csproj
├── TrainMvp.sln
├── project.godot
├── export_presets.cfg
├── App/
│   ├── AppState.cs
│   └── Main.tscn
├── Domain/
│   ├── Definitions/                 # C# Resource 类型
│   ├── Scenario/ScenarioDefinition.tres
│   ├── TradePlan.cs                 # 普通 C# 值对象
│   ├── SimulationState.cs           # 普通 C# 可变整数状态
│   ├── SimulationEngine.cs          # 普通 C# 纯事件模拟
│   └── RunReport.cs                 # 普通 C# 报告数据
├── Planning/
│   ├── PlanningScreen.tscn
│   ├── PlanningScreen.cs
│   ├── MapView.cs
│   └── RuleInspector.cs
├── Run/
│   ├── RunScreen.tscn
│   ├── RunScreen.cs
│   ├── TrainView.tscn
│   └── RunPresenter.cs
├── Report/
│   ├── ReportPanel.tscn
│   └── ReportPanel.cs
└── Playtest/
    └── PlaytestLog.cs               # 可选，有余量才实现
```

关键约束：

- `Domain/` 普通 C# 模拟类不引用 Godot Node、Planning、Run 或 Report。
- Resource 定义可依赖 Godot，但先转换成普通领域对象，再交给模拟层。
- UI 不自行重算市场；只调用 `SimulationEngine`。
- 3D 动画不生成经济事件；事件先决定，动画只表现。
- 全项目只有一个固定场景定义、一个玩家列车和一个运行状态。

## C# 4 人周基线估算

估算单位为一名实现者专注工作日，`1 人周 = 5 日`。这不是官方事实。前提：

- 实现者熟悉 C# 与常规 .NET 项目，但 Godot/.NET 集成经验为初级到中级。
- 可直接参考仓库内三个已经人工确认的 HTML 原型。
- 不做正式美术、音频、教程、存档、外部测试或发布。
- 只支持 Windows x86_64、键盘鼠标和一个固定分辨率范围。

| 模块 | 估算 | 完成定义 |
|---|---:|---|
| .NET 环境、项目骨架、Windows export smoke | 1–1.5 日 | .NET 编辑器、SDK、.NET templates、`dotnet build`、空项目 debug/release 导出 |
| C# Resource 数据与 12 站矩阵 | 1–1.5 日 | `[GlobalClass]`/`[Export]` 可编辑，单场景可加载 |
| 普通 C# 确定性模拟与少量 smoke tests | 3–4 日 | 路线、市场刻、价格、规则顺序、部分成交、重置、失败/修正圈一致 |
| 地图优先 PlanningScreen | 3–4 日 | 选站、排序、规则编辑、校验、路径/耗时/容量提示 |
| 平衡分屏 RunScreen 与控制 | 2.5–3.5 日 | 简化 3D、暂停、1×/4×/16×、跳站、快速完成、自动停靠 |
| 圈后 ReportPanel 与上一圈对比 | 1.5–2 日 | 已冻结报告字段与下一步提示 |
| 集成、缺陷修复、Windows 构建 | 2.5–3.5 日 | 连续完成至少 3 次策略迭代，无阻断缺陷 |
| **合计** | **14.5–20 日** | **约 2.9–4 人周** |

可选 `PlaytestLog.cs` 另需 `0.5–1 日`，不计入 4 人周批准基线。

### 估算判断

- **4 人周基线可批准**，但必须保持内建几何、单场景、无应用内日志和无正式资产。
- 熟悉 C# 能避免语言学习成本；Godot Node 生命周期、Signal、Resource、Inspector 和导出仍有集成风险。
- 如果第 2 日结束时 `.NET editor → dotnet build → Godot run → Windows export` 仍未贯通，立即采用 2 人周砍线，不继续扩建 3D。
- 若实现者也不熟悉 C#，额外增加约 `3–5 日`，4 人周不再可靠。

## 2 人周降级基线

按以下顺序砍线，保护“最多 30 分钟内自然完成至少 3 次策略迭代”：

1. 不实现 `PlaytestLog.cs`；继续在游戏外手记一行。
2. 3D 改为 BoxMesh 列车在起点/中转/目的地三个位置间直线 Tween，不建 Path3D 铁路和环境。
3. 运行中只显示最近一条规则结果；完整明细保留在圈后报告。
4. 上一圈只比较利润、卖出量和压舱货物，删除市场细项对比。
5. 1× 只做粗略移动；保留暂停、4×、16×、跳站和快速完成。

“只开放 6 个站”会改变已经确认的 12 站任意成环范围，不得自动进入最终规格；只能作为单独批准的技术 spike。

### 推荐 10 日安排

| 日 | 工作 |
|---|---|
| 1 | 建立正式 C# 项目；复用已验证的 SDK/build/headless/export smoke；完成项目骨架 |
| 2–3 | C# Resource 矩阵 + 普通 C# 模拟 + 失败/修正圈 smoke |
| 4–6 | 地图优先编辑页 |
| 7 | 圈后报告 |
| 8 | 直线 BoxMesh 列车 + 运行控制 |
| 9 | 编辑→运行→报告→编辑集成 |
| 10 | 个人试玩、修阻断缺陷、release 导出 |

## 首日风险闸门

本机已确认存在 Godot 4.7.2 .NET 版：`D:/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64.exe`，版本输出为 `4.7.2.stable.mono.official.ed1daf0bf`。已观察到稳定 .NET SDK `9.0.316` 和预览 SDK `10.0.400-preview`；临时项目的 `global.json` 成功固定 `9.0.316`。

本机 C# Godot 工具链已经完整验证：

- `Godot.NET.Sdk/4.7.2` NuGet 恢复；
- `global.json` 固定稳定 SDK `9.0.316`；
- `dotnet build`；
- Godot `--build-solutions`；
- Godot headless C# 场景运行；
- 官方 `.NET export templates` 安装到 `%APPDATA%/Godot/export_templates/4.7.2.stable.mono`；
- Windows Desktop x86_64 release export；
- 导出的约 `104.31 MB` C# `.exe` 实际运行并输出 `TRAIN_CSHARP_EXPORTED_SMOKE_OK`，退出码 `0`。

Godot C# 导出要求项目存在与 `.csproj` 同名的 solution；缺少 `.sln` 时 ExportPlugin 会报错。本项目必须提交 `TrainMvp.sln`。

实施首日仍应在正式项目中重复以下 smoke，防止项目配置偏离已验证基线：

```powershell
dotnet --info
# 应显示 global.json 选定的 9.0.316，而不是 10.0.400-preview
dotnet restore mvp/godot/TrainMvp.csproj
dotnet build mvp/godot/TrainMvp.sln
godot-mono --headless --path mvp/godot --build-solutions --quit
godot-mono --headless --path mvp/godot --export-release "Windows Desktop" builds/train-mvp.exe
```

若正式项目不能重复 C# 构建、导出和产物启动，不继续建设 UI/3D；先修复环境或项目骨架。

## 不采用

- Godot 3.6 或 Godot 4.8 开发版。
- 标准版 Godot/GDScript 基线：与用户已确定的 C# 技术约束冲突。
- Forward+ 默认基线。
- 完整体素/GridMap/Blender/CSG 内容管线。
- 物理驱动列车或市场。
- 通用存档、数据库、遥测服务。
- 第三方正式资产包、代码签名和安装器。

## 剩余不确定性

- Godot 4.7.2 .NET 编辑器、稳定 .NET 9 SDK、官方 .NET export templates、C# restore/build、Godot solution build、headless 场景运行、Windows x86_64 release export 和导出产物启动均已在本机验证。
- 没有低端 Windows 机器实测 Compatibility。
- 最终 Godot C# UI 尚未在 1366×768 下复刻验证。
- 本估算假设实现者熟悉 C#；如果该假设不成立，工期必须重新估算。
