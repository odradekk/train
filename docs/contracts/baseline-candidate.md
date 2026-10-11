# 初始契约基线候选（#302）

本页是 [#302](https://github.com/odradekk/train/issues/302) 交给 [#304](https://github.com/odradekk/train/issues/304) 的候选清单，不是正式基线或生产行为通过记录。规划依据为派发基线提交 `c8a959923e0d12172a2513d7ed11496827137777` 及 [#302 范围决定](https://github.com/odradekk/train/issues/302#issuecomment-6105295990)。候选的确切源码版本由交付时的 PR head 提交标识，检查命令与结果记在交付评论中；本文件的版本随该提交固定。

## 候选范围

| 内容 | 本次纳入的路径 | 当前状态 |
| --- | --- | --- |
| 可编译声明 | `Contracts/Core/`、`Contracts/Content/`、`Contracts/Scenes/` 各自的 `.csproj` 与 `.cs` | 三个项目已存在；仅声明公开接口和值，不包含实现 |
| 接口语义 | 本目录的 `README.md`、`conventions.md`、`core.md`、`content.md`、`scenes.md`、`godot.md`、`transactions.md`、`editing.md`、`testing.md` | 约定调用方须知道的责任；玩法规则仍以设计文档为准 |
| 可编译调用样例 | `prototypes/core-contract/`、`prototypes/content-contract/`、`prototypes/scenes-contract/`、`prototypes/godot-contract/` 的项目与源码 | 四组现有样例继续原位；编译不证明行为或真实引擎接入 |
| 共享情景与有限替身 | `Tests/ContractSupport/` | 按 Core、Content、Scenes 接口所有者组织；当前只引用三个契约项目，不引用任何生产实现 |

目标产品布局是三个契约项目、三个实现项目，加根目录 Godot 项目；当前缺少有真实行为的 `Content/Train.Content.csproj`。`Train.sln` 收录已有的三个契约、`Core/`、`Scenes/`、根项目和候选测试项目；四组调用样例按其项目文件分别编译。根项目排除契约、样例、测试支持及其他项目的源码，避免重复编译。目标允许依赖以[契约索引](README.md#依赖与装配)为准；现有实现间引用属于下述待迁移状态，不计为符合目标隔离。

## 真实工厂与装配

| 模块 | 已声明的工厂接口 | 目标创建入口 | 本次真实公开入口 |
| --- | --- | --- | --- |
| 核心 | `Contracts/Core/Lifecycle.cs` 的 `ICoreFactory` | `Train.Core.CoreFactory.Create()` | 尚无 |
| 内容 | `Contracts/Content/Lifecycle.cs` 的 `IContentFactory` | `Train.Content.ContentFactory.Create()` | 尚无 |
| 体素 | `Contracts/Scenes/World.cs` 的 `IScenesFactory` | `Train.Scenes.ScenesFactory.Create()` | 尚无 |

目标入口名称见各模块接口文档。旧实现的其他 `public` 类型不自动成为新契约。真实创建入口随对应实现迁移接入，届时由 `App/Composition/` 调用，并把其实际公开面加入受控基线；本票不建空项目或假工厂。

## 共享情景和证据边界

接口所属模块维护输入、操作顺序、预期结果、共享断言与有限替身。`Tests/ContractSupport/` 目前提供三个可执行情景；它们的断言接收公开接口，替身只回放列明的输入，未配置调用明确失败。当前入口是 `pwsh -NoProfile -File scripts/run-tests.ps1 -Project Tests/ContractSupport/Train.ContractSupport.Tests.csproj -RunnerCatalog none -Family unit`；按模块选择和真实实现的独立入口交给 #304。情景通过只能证明这些断言与替身可用，不能代替缺失的真实生产者或消费方验证。

| 所属接口 | 本次可执行的固定情景 | 尚需真实验证的责任 |
| --- | --- | --- |
| 核心 | 保存捕获后推进一次，旧捕获的输入身份、引用和嵌套模板地块在推进与会话关闭后保留 | 核心计算、规范编码、真实保存与严格重放 |
| 内容 | 同一路径的完整字节读取、超限失败、再读及旧字节保留；截断成功的反例会被断言发现 | Godot 文件端口的真实 I/O、内容解析、投影和资产规则 |
| 体素 | 同一会话切换观察请求后，旧命中返回 `stale_request` | 真实场景生成、拾取、帧更新与 Godot 显示 |

[验证矩阵](testing.md#最小调用原型的验证矩阵)还列出内容投影到核心、核心结果到体素、场景数据到 Godot、编辑草稿与设计校验、读档/发布/会话切换、异步结果返回六条跨层路径。现有四组样例只提供其中部分接口的可编译调用；未在上表执行的共享行为断言、真实生产者及消费方检查保持**待验证**。#304 为适用的情景接入检查入口；后续模块实施补齐其真实行为及阶段联调。宿主能力接口的共享断言仍由定义接口的模块维护，真实 Godot/I/O 由宿主验证。

## 旧代码迁移边界

| 现状 | 后续迁移需一起处理 |
| --- | --- |
| `Core/Content/` 尚在 `Train.Core`；`Core/Saves/FileSaveStore.cs` 使用其中的 `ExternalPath` | 内容项目成立时消除核心对内容实现的反向依赖，并保持文件路径行为等效 |
| 内容加载/重载依赖现有 `Core/Logging/`；App 和内容测试使用旧 `Train.Core.Content` 类型 | 按目标宿主日志与内容接口迁移，随迁移同步 App、测试与必要调用点 |
| `Scenes/Train.Scenes.csproj` 引用 `Train.Core`；根项目引用现有实现，尚无 `App/Composition/` | 真实体素与宿主装配建立时改为允许的契约方向，并验证实际程序集依赖及工厂调用位置 |
| `Train.Tests` 仍为聚合测试入口 | #304 接模块选择和实际隔离；旧用例随生产迁移归位，在替代入口证明等效前不退役 |

这些条目是迁移记录，不授权在 #302 搬迁旧实现或改写玩法。

## 移交 #304 的检查机制

#302 可构建声明、样例与有限情景，并继续运行现有游戏与测试入口；尚未具备 `-Module` 选择、临时目录实际隔离、求值后及实际程序集依赖检查、公开面差异检查、真实工厂符号调用位置检查、故意违规检出、真实 Godot 隔离装配及统一 CI 汇总。#304 对适用范围实现这些机制，区分机制通过与生产行为待验，再提交正式整组基线供用户确认。旧 `Train.Tests` 和游戏构建继续保留，直到替代入口证明等效。
