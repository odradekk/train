# 四模块公开契约

本目录记录完整 Demo 的模块接口，不记录模块内部类图。[四模块的依赖、公开类型归属与契约变更规则](https://github.com/odradekk/train/issues/279)已确认以下规范，取舍见 [ADR-0016](../adr/0016-separate-module-contracts-and-limit-implementation-access-to-composition.md)。接口的公共语义和变更流程见[公共约定](conventions.md)，玩法规则仍以[设计文档](../design/README.md)为准。

## 状态与后续交付

当前已确定模块职责、类型归属、目标项目布局和契约管理规则。契约程序集尚未创建，生产代码尚未迁移，独立构建和依赖检查尚未实现；本页不是这些检查已经通过的证据，也不是整组契约基线。

四份模块文档按下表归档；目前尚未创建，不用空壳文档冒充成熟接口。各票补齐可编译声明、调用样例和本模块约束，再由契约原型验证。

| 模块 | 预定文档 | 负责决策 |
| --- | --- | --- |
| 核心模拟层 | `core.md` | [核心模拟层公开契约：命令、查询、事件与复现](https://github.com/odradekk/train/issues/287) |
| 内容层 | `content.md` | [内容层公开契约：加载、投影、设计计算与发布](https://github.com/odradekk/train/issues/288) |
| 体素引擎层 | `scenes.md` | [体素引擎层公开契约：场景、拾取与编辑会话](https://github.com/odradekk/train/issues/289) |
| Godot 层 | `godot.md` | [Godot 层公开契约：宿主适配、资源与交互接入](https://github.com/odradekk/train/issues/290) |

验证的组织与工具由[四模块独立构建、测试替身与契约一致性检查](https://github.com/odradekk/train/issues/291)确定；[契约原型](https://github.com/odradekk/train/issues/292)验证独立调用与替换；[并行开发与最终联调流程](https://github.com/odradekk/train/issues/293)确定交付流程及必要 SOP 调整。实际生产代码迁移留给[工单重组](https://github.com/odradekk/train/issues/294)后的实施任务。

## 目标项目布局

路径相对仓库根目录。`Contracts/` 只是集中存放契约的目录，不是程序集，也不是第五个业务模块；每份契约由对应模块负责。

| 目录 | 项目 | 用途 |
| --- | --- | --- |
| `Contracts/Core/` | `Train.Core.Contracts` | 核心公开契约 |
| `Contracts/Content/` | `Train.Content.Contracts` | 内容公开契约 |
| `Contracts/Scenes/` | `Train.Scenes.Contracts` | 体素公开契约 |
| `Core/` | `Train.Core` | 核心实现 |
| `Content/` | `Train.Content` | 内容实现 |
| `Scenes/` | `Train.Scenes` | 体素实现 |
| 根目录 | `Train` | Godot 项目；源码仍按 `App/`、`Rendering/`、`Ui/` 划分 |

契约项目沿用 .NET 10、C# 12，不引用 Godot。命名空间沿用“项目名.项目内目录名”。新增项目时隔离源码编译范围，不能让根项目或实现项目再次编译契约源码。测试项目的拆分由独立构建与测试票确定。

## 依赖与装配

下表是第一方项目引用的允许范围，不要求为了对称而添加不使用的引用。实际程序集依赖也受同一方向约束；不能通过间接引用绕过。

| 项目 | 允许引用的第一方项目 |
| --- | --- |
| `Train.Core.Contracts` | 无 |
| `Train.Content.Contracts` | `Train.Core.Contracts` |
| `Train.Scenes.Contracts` | `Train.Core.Contracts`、`Train.Content.Contracts` |
| `Train.Core` | `Train.Core.Contracts` |
| `Train.Content` | `Train.Content.Contracts`、`Train.Core.Contracts` |
| `Train.Scenes` | `Train.Scenes.Contracts`、`Train.Content.Contracts`、`Train.Core.Contracts` |
| `Train` | 三个契约项目；真实实现项目只用于下述装配例外 |

核心在编译时看不到布局、体素朝向、表现资产和显示文本。内容按读取方提供投影；体素可以读取核心查询和内容契约，宏观地图及管理界面可以直接使用核心查询，不需要经过体素。

各实现类型默认 `internal`，实现程序集只额外公开少量创建工厂。工厂的参数和返回值使用公开契约，不泄露实现类型；工厂是受控的装配入口，同样纳入文档、公开面检查和变更基线。既有 `public` 类型不自动成为新契约，不承诺保留通用模拟框架的全部构造和扩展入口。

Godot 只有 `App/Composition/` 可以调用真实创建工厂；其他宿主流程、渲染和界面接收装配好的契约接口。独立测试替换装配入口并注入替身，仍执行同一份 Godot 生产代码。暂不引入依赖注入容器、插件扫描或反射装配框架。

不为 Godot 对称地增设第四个契约程序集。下层需要的文件读取、PNG 解码、资源提交等宿主接口由需要它的模块定义，Godot 实现；下层不引用 Godot 类型。Godot 的接口文档仍须说明这些适配、装配、交互和资源责任。

## 跨模块类型归属

每种跨模块类型只声明一次，消费者引用其契约，不复制同形类型。类型的所有者不一定是值的计算者；强类型身份可供多层使用，但不因此携带布局或加载实现。

| 所有者 | 类型范围与限制 |
| --- | --- |
| 核心契约 | 世界对象身份、模拟位置、命令及结果、查询与领域事件；核心需要的定义标识、设计哈希和构成哈希的值类型、部件编号、设计属性、能力、BOM 等输入类型 |
| 内容契约 | 内容包与来源、内容世代、资产修订、完整设计与布局、显示资料、加载与设计校验结果 |
| 体素契约 | 场景和编辑会话、拾取结果、场景更新及交给渲染层的数据 |
| Godot 内部 | Godot 节点、纹理、材质、输入事件、窗口与界面状态，不进入前三者的契约 |

设计属性的声明归核心契约，计算归内容实现。设计哈希的值类型供核心引用，哈希算法、布局遍历、部件编号生成及设计合法性计算只由内容实现负责；核心把哈希和编号当标识，不读取布局。具体规则引用[设计与部件](../design/03-vehicles-buildings-and-designs.md#部件与结构)，不在契约项目重做一份计算。

内容世代归内容契约，是发布序号，不是持久身份，也不能替代语义指纹。设计哈希、构成哈希、语义修订与资产修订继续承担各自的身份职责，见[内容版本与哈希](../design/02-definitions-and-content.md#三种内容版本)。核心只接收自己需要的业务投影，不接收完整内容目录；跨世代配对与会话切换由[跨层事务票](https://github.com/odradekk/train/issues/284)确定。

## 跨层工作的责任

| 工作 | 责任 |
| --- | --- |
| 核心状态与确定性 | 核心捕获、编码、校验和恢复核心状态，记录命令、生成检查点、执行确定性重放；重放计算可在无 Godot 的测试中运行 |
| 内嵌内容 | 内容捕获和恢复设计、蓝图库、草稿与模板，解析外部定义并提供各读取方的投影 |
| 完整存档与复现包 | Godot 宿主组合完整容器、访问文件、调度自动存档、组装复现包，在候选会话成功后整体切换；不拆开核心状态自行编码，也不重新计算设计属性 |
| 场景生命周期 | 体素不加入权威存档；会话切换后重新构建场景 |
| 诊断日志 | Godot 宿主负责输出、文件保留和展示；其他模块通过自己的结果、事件或必要的诊断接口报告，不依赖日志实现 |
| 控制台 | Godot 宿主解析文本并分发已解析命令，与界面、自动测试使用同一核心命令入口；核心命令记录、确定性摘要和稳定故障信息仍归核心 |

宿主内部的纯 .NET 代码可以独立测试；涉及引擎的行为仍用真实 Godot 验证。日志搬迁不等于立即重写实现。存档、录制和内容发布的准备、提交、失败回滚及版本配对由[跨层事务票](https://github.com/odradekk/train/issues/284)具体设计，数据和玩法规则仍见[存档](../design/09-persistence.md)、[录制与复现](../design/04-deterministic-simulation.md#录制与复现)。
