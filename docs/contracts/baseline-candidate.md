# 初始契约基线候选（#302）

本页是 [#302](https://github.com/odradekk/train/issues/302) 交给 [#304](https://github.com/odradekk/train/issues/304) 的候选清单，不是正式基线或生产行为通过记录。规划依据是提交 `c8a959923e0d12172a2513d7ed11496827137777` 及 [#302 范围决定](https://github.com/odradekk/train/issues/302#issuecomment-6105295990)。候选以验收时的 PR head 为准，提交号和检查结果记在 Issue 评论中。

## 候选范围

| 内容 | 本次纳入的路径 | 当前状态 |
| --- | --- | --- |
| 可编译声明 | `Contracts/Core/`、`Contracts/Content/`、`Contracts/Scenes/` 各自的 `.csproj` 与 `.cs` | 三个项目已存在；仅声明公开接口和值，不包含实现 |
| 接口语义 | 本目录的 `README.md`、`conventions.md`、`core.md`、`content.md`、`scenes.md`、`godot.md`、`transactions.md`、`editing.md`、`testing.md` | 约定调用方须知道的责任；玩法规则仍以设计文档为准 |
| 可编译调用样例 | `prototypes/core-contract/`、`prototypes/content-contract/`、`prototypes/scenes-contract/`、`prototypes/godot-contract/` 的项目与源码 | 四组现有样例继续原位；编译不证明行为或真实引擎接入 |
| 共享情景与有限情景替身 | `Tests/ContractSupport/` | 按 Core、Content、Scenes 接口所有者拆成三个普通支持类库，自检测试项目引用它们；不引用任何生产实现 |

目标产品布局是三个契约项目、三个实现项目，加根目录 Godot 项目；当前没有 `Content/Train.Content.csproj`，随内容代码迁移建立；`Train.Scenes` 目前只有项目文件。`Train.sln` 收录已有的三个契约、`Core/`、`Scenes/`、根项目、`Train.Tests` 及共享支持和自检项目；四组调用样例不在解决方案中，按其项目文件分别编译。根项目排除契约、样例、测试支持及其他项目的源码，避免重复编译。目标允许依赖以[契约索引](README.md#依赖与装配)为准；现有实现间引用属于下述待迁移状态，不计为符合目标隔离。

## 真实工厂与装配

| 模块 | 已声明的工厂接口 | 目标创建入口 | 本次真实公开入口 |
| --- | --- | --- | --- |
| 核心 | `Contracts/Core/Lifecycle.cs` 的 `ICoreFactory` | `Train.Core.CoreFactory.Create()` | 尚无 |
| 内容 | `Contracts/Content/Lifecycle.cs` 的 `IContentFactory` | `Train.Content.ContentFactory.Create()` | 尚无 |
| 体素 | `Contracts/Scenes/World.cs` 的 `IScenesFactory` | `Train.Scenes.ScenesFactory.Create()` | 尚无 |

目标入口名称见各模块接口文档。旧实现的其他 `public` 类型不自动成为新契约。真实创建入口随对应实现迁移接入，届时由 `App/Composition/` 调用，并把其实际公开面加入受控基线；#302 不建空项目或假工厂。

## 目标模块测试入口

以下项目位置是 #302 确定的目标；三个 MSTest 项目和模块选择、隔离 runner 尚未建立，由 #304 接入。入口语义及独立验证责任以 [模块测试的组织与入口](testing.md#模块测试的组织与入口)为准。

| 模块 | 目标测试项目或 runner 位置 | 允许的契约 | 真实实现共享情景与当前状态 |
| --- | --- | --- | --- |
| `core` | `Tests/Core/Train.Core.Tests.csproj` | 核心 | 在真实核心会话上运行捕获情景；项目与真实工厂均未接入 |
| `content` | `Tests/Content/Train.Content.Tests.csproj` | 内容、核心 | 使用文件端口替身验证真实内容加载；真实内容的解析、投影及设计计算情景随实施补齐。文件端口的真实读取由 Godot 侧验证；项目与真实实现均不存在 |
| `scenes` | `Tests/Scenes/Train.Scenes.Tests.csproj` | 体素、内容、核心 | 在真实体素会话上运行观察请求失效情景；项目与真实工厂均未接入 |
| `godot` | 根 `Train.csproj` 的隔离装配；`scripts/runner-catalog.json` 登记真实引擎 runner | 三个契约 | 测试专用程序集或隔离装配对真实文件适配器运行共享断言，并验证自身引擎/I/O 责任；共享支持和 MSTest 不编入生产 `App/Checks/` 或游戏导出；目标入口尚未建立 |

目标命令均以 `pwsh -NoProfile -File scripts/run-tests.ps1` 开头，参数如下；模块选择当前不可执行，不能当作通过证据。

| 模块 | 日常单元测试参数 | 正确性检查参数 |
| --- | --- | --- |
| `core` | `-Module core -Family unit` | `-Module core -Correctness` |
| `content` | `-Module content -Family unit` | `-Module content -Correctness` |
| `scenes` | `-Module scenes -Family unit` | `-Module scenes -Correctness` |
| `godot` | `-Module godot -Family unit` | `-Module godot -Correctness` |

日常入口只运行已登记的单元测试；正确性入口承担模块正确性与隔离检查。Godot 的正确性检查使用真实引擎，单元测试不能替代它。

共享支持分别位于 `Tests/ContractSupport/Core/Train.Core.ContractSupport.csproj`、`Tests/ContractSupport/Content/Train.Content.ContractSupport.csproj`、`Tests/ContractSupport/Scenes/Train.Scenes.ContractSupport.csproj`，允许的契约分别与上表对应模块一致。模块测试最多引用本模块的一个真实实现，并按需要引用允许的支持库。这些普通类库不含测试，测试入口不会从中发现或执行用例。`Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj` 只负责支持库自检，可引用全部三个支持库，不作为独立模块验证入口。

## 共享情景和证据边界

接口所属模块维护输入、操作顺序、预期结果、共享断言与有限情景替身。`Tests/ContractSupport/` 目前提供三个可执行情景；它们的断言接收公开接口，替身只保存情景所需的状态、返回固定结果，未配置调用明确失败。当前自检入口是 `pwsh -NoProfile -File scripts/run-tests.ps1 -Project Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj -RunnerCatalog none -Family unit`。该项目尚未纳入默认入口、CI 或 VS Code 的 `test` 任务，必须显式传 `-Project`。报告仍写死“Train.Tests tests Core and Scenes through project references”，不适用于此自检项目；其实际边界是三个支持库及允许的契约，不是生产实现，#304 须修正报告。情景通过只能证明这些断言与替身可用，不能代替缺失的真实生产者或消费方验证。

| 所属接口 | 本次可执行的固定情景 | 尚需真实验证的责任 |
| --- | --- | --- |
| 核心 | 保存捕获后推进一次，旧捕获的输入身份与引用清单在推进及会话释放后保留；有限情景替身的默认后续捕获新增一条带 `FrozenRevision` 的流程引用，表示推进中启动的生产批次所需的冻结规格来源。捕获若错误地读取会话当前引用，共享断言会发现；只有两份预期引用确实不同时，才证明引用变化后的旧值保留 | 核心计算、规范编码、真实保存与严格重放 |
| 内容 | 两个不同路径、等长且不同的固定字节读取，超限失败及旧字节保留；截断成功和同长度复用缓冲区的反例会被断言发现 | Godot 文件端口的真实 I/O、内容解析、投影和资产规则 |
| 体素 | 同一会话发出新观察请求（包括目标相同）后，旧命中返回 `stale_request` | 真实场景生成、拾取、帧更新与 Godot 显示 |

[验证矩阵](testing.md#最小调用原型的验证矩阵)还列出“内容投影 → 核心输入”、“核心命令、查询、事件 → 体素场景”、“场景数据 → Godot”、“编辑草稿 → 设计校验与提交”、“读档、内容发布、会话切换”、“异步结果返回”六条跨层路径。现有四组样例只提供其中部分接口的可编译调用；未在上表执行的共享行为断言、真实生产者及消费方检查保持**待验证**。剩余共享断言由接口所属模块在对应真实实施任务中补齐；#304 接入已有及届时适用情景的检查入口，不承担提前实现后续模块行为的责任。后续阶段任务验证真实模块联调。宿主能力接口的共享断言仍由定义接口的模块维护，真实 Godot/I/O 由宿主验证。

## 旧代码迁移边界

| 现状 | 后续迁移需一起处理 |
| --- | --- |
| `Core/Content/` 尚在 `Train.Core`；`Core/Saves/FileSaveStore.cs` 使用其中的 `ExternalPath` | 内容项目成立时消除核心对内容实现的反向依赖，并保持文件路径行为等效 |
| 内容加载/重载依赖现有 `Core/Logging/`；App 和内容测试使用旧 `Train.Core.Content` 类型 | 按目标宿主日志与内容接口迁移，随迁移同步 App、测试与必要调用点 |
| `Core/Logging/`、`Core/Console/` 仍编入 `Train.Core`，控制台分发依赖日志实现 | 日志输出与文本解析、分发迁到 Godot 宿主，并同步调用方和测试；核心命令记录、确定性摘要与稳定故障信息仍留在核心 |
| `App/App.cs`、`App/SimulationConsoleCommands.cs`、`App/Checks/SimulationCheck.cs` 直接使用 `Train.Core.Simulation`、`Train.Core.Console` 或 `Train.Core.Logging`；内容/日志/启动检查及 `GodotContentSource`、`GodotLogOutput` 也使用旧日志类型 | 迁移为核心契约和宿主日志、控制台接口，经 `App/Composition/` 装配；同步检查与适配器调用方 |
| `Ui/GameConsole.cs` 使用 `Train.Core.Console` 和 `Train.Core.Logging` | 界面接收宿主控制台与日志接口，经 `App/Composition/` 装配，消除对核心实现类型的直接依赖 |
| `Scenes/Train.Scenes.csproj` 引用 `Train.Core`；根项目引用现有实现，尚无 `App/Composition/` | 真实体素与宿主装配建立时改为允许的契约方向，并验证实际程序集依赖及工厂调用位置 |
| `Train.Tests` 仍为聚合测试入口 | #304 接模块选择和实际隔离；旧用例随生产迁移归位，在替代入口证明等效前不退役 |
| `Core/Train.Core.csproj` 授予 `Train.Tests` 的 `InternalsVisibleTo` | 用例拆分时逐项处理现有内部访问；共享支持通过公开契约准备数据，不把聚合入口的友元权限复制给支持库或其他模块测试 |

这些条目是迁移记录，不授权在 #302 搬迁旧实现或改写玩法。

## 移交 #304 的检查机制

#302 可构建声明和样例、运行有限情景替身自检，并继续运行现有游戏与测试入口。#304 的完整责任以[自动检查要求](conventions.md#自动检查要求)、[结构检查表](testing.md#实际隔离与结构检查)及其验收条件为准；当前尚缺：

- 按模块选择目标项目，并在临时目录隔离构建和运行；Godot 使用隔离装配及真实引擎 runner，将共享断言失败报告为检查失败。
- 检查求值后的项目引用和实际程序集依赖，报告公开面差异及签名泄露的实现类型，并按真实工厂符号核对调用位置。
- 检查测试支持是否引入对方实现、反射构造、友元访问或链接对方生产源码，并通过故意破坏验证这些检查能发现问题。
- 契约、四组调用样例、支持库自检及目标模块检查的统一 CI 汇总；四组样例当前也未纳入 CI。
- 按实际项目和 runner 修正报告的测试边界描述，并保持默认入口和 VS Code 任务与已接入范围一致。

#304 分开记录检查机制已通过的部分和生产行为仍待验证的部分，再提交正式整组基线供用户确认。旧 `Train.Tests` 和游戏构建继续保留，直到替代入口证明等效。
