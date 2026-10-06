# 设计文档集：列车放置经营游戏

本目录是项目唯一且权威的设计契约。每条决定的出处以"转录 Qn"引用 `docs/research/gdd-grilling-transcript.md`（Q1–Q180），以"资源转录 Qn"引用资源与生产系统 grilling 的 `docs/research/resources-and-production-grilling-transcript.md`（Q180–Q428）；两份转录的编号在 Q180 重叠，引用必须带前缀。[00](00-product-and-core-loop.md)–[09](09-persistence.md) 由 v0.1 原稿拆分而来，原稿只作留底（提交 `6576bf6`），不再作为依据；[10](10-resources-and-extraction.md)–[14](14-construction-refit-and-deconstruction.md) 由资源与生产系统设计稿拆分而来。2026-10 的架构调整由地图[体素引擎改作局部场景：设计文档与 open issue 全面修订](https://github.com/odradekk/train/issues/193)推进，此后的决定引用该地图中对应票的名称和链接；仍然有效的旧规则保留原来的转录出处。被废弃的规则直接从文档删除，历史只保留在 git、已关闭的票和 `docs/research` 的转录中；ADR 不删除，被取代时在末尾注明。

**文档内明确为规则的内容是当前设计基线。** 未定项在各篇“开放问题”小节标明状态，已登记的候选方向与未冻结参数不构成实施承诺；基于实现验证修订设计时，同步受影响章节。

## 开放问题与立票

- **近期决策（有票）**：只有需要产出明确决策、且阻塞当前或下一切片的未定项才立票，并在对应开放问题条目链接。尚在收敛阶段切片规格时，阻塞这些规格形成的必要决策也属于此类；条目说明要决定什么，不把整章未来细节打包立票。
- **地图迷雾（不立票）**：尚未进入当前或下一切片的方向、待实测调优参数、未形成具体决策问题的实现细节，保留简短描述及重新评估条件，不为完整目录提前建票。它们成为近期阻塞时再升级为决策票。
- 优先复用已有决策票，不重复立票；是否属于某张 wayfinder 地图取决于当前工作，不指定某张地图为唯一入口。本阶段的范围与验收见 [00 的本阶段范围](00-product-and-core-loop.md#本阶段范围)，由地图[体素引擎改作局部场景：设计文档与 open issue 全面修订](https://github.com/odradekk/train/issues/193)确定；实施工单由代码重构地图建立；后续疑问仅在阻塞所选切片且能明确提出问题时立票。
- 决策完成后，由解决该票的会话回写对应规则，移除或改写开放问题；已关闭票可以保留为历史出处，不能继续充当未解决的阻塞。无待决事项可写“无”，已经明确的规则留在规则章节。

## 文档目标

本项目为独立开发项目，设计文档不追求在开发前穷举实现细节。设计遵循：

1. 优先明确稳定的系统边界和不变量。
2. 具体数值尽量通过实现、Benchmark、Profiling 和 Playtest 确定。
3. 本阶段只实现首个切片“宏观贸易”所需的系统，并按 [00 的本阶段验收](00-product-and-core-loop.md#本阶段验收)验收；之后按切片逐步扩展到 Demo。
4. 对明确会在未来加入的重要功能，架构上避免封死扩展空间，但不提前实现。
5. 组合优于继承。
6. 领域模型与 Godot SceneTree、渲染、UI 等表现层严格分离。
7. Deterministic Simulation 是整个项目的基础架构原则。

Demo 范围见 [00](00-product-and-core-loop.md#demo-范围)：经济、建设、战斗三个并列的主循环，包括面向玩家的设计编辑器、战斗和聚居地取得；市场与价格、人口的具体规则待定，战斗与征服见 [17](17-combat.md)。资源与生产系统已在 [10](10-resources-and-extraction.md)–[14](14-construction-refit-and-deconstruction.md) 定义，其中依赖体素世界的部分随架构调整改写，实现随后续切片安排。AI 势力、自动贸易策略和复杂调度在 Demo 之后扩充。

## 文档地图

每篇开头标明所属的层。以后修订时，受影响的文档开头用“修订中”说明列出失效的章节和负责改写的工单；改写完成前，这些章节不能作为实现依据。

| 层 | 文件 | 内容 |
| --- | --- | --- |
| 产品 | [00-product-and-core-loop.md](00-product-and-core-loop.md) | 产品定位、核心循环、Demo 范围与本阶段目标 |
| 核心模拟层 | [04-deterministic-simulation.md](04-deterministic-simulation.md) | 时间、命令与确定性 |
| 核心模拟层 | [05-world-graph-and-generation.md](05-world-graph-and-generation.md) | 世界图与世界生成 |
| 核心模拟层 | [16-settlements.md](16-settlements.md) | 聚居地 |
| 核心模拟层 | [06-railway.md](06-railway.md) | 铁路与列车运行 |
| 核心模拟层 | [17-combat.md](17-combat.md) | 战斗、世界生物与征服 |
| 核心模拟层 | [03-vehicles-buildings-and-designs.md](03-vehicles-buildings-and-designs.md) | 车辆、建筑与设计属性 |
| 核心模拟层 | [09-persistence.md](09-persistence.md) | 存档 |
| 核心模拟层 | [10-resources-and-extraction.md](10-resources-and-extraction.md) | 资源源、开采与资源更新 |
| 核心模拟层 | [11-goods-and-production-network.md](11-goods-and-production-network.md) | 货物目录、生产网络与回收网络 |
| 核心模拟层 | [12-inventory-and-storage.md](12-inventory-and-storage.md) | 库存与仓储 |
| 核心模拟层 | [13-production-runtime-and-facilities.md](13-production-runtime-and-facilities.md) | Process、生产运行时、设施与能力 |
| 核心模拟层 | [14-construction-refit-and-deconstruction.md](14-construction-refit-and-deconstruction.md) | 车辆与建筑的建造、改装、修理与拆解 |
| 内容 | [02-definitions-and-content.md](02-definitions-and-content.md) | Definition、Revision、Instance 与内容来源 |
| 体素引擎层 | [01-voxel-engine-and-scenes.md](01-voxel-engine-and-scenes.md) | 体素引擎与局部场景 |
| 体素引擎层、渲染层 | [07-rendering.md](07-rendering.md) | 渲染 |
| 渲染层 | [08-window-and-ui.md](08-window-and-ui.md) | 窗口与界面 |
| 工具 | [15-design-editor-and-tools.md](15-design-editor-and-tools.md) | 设计编辑器、创作模式与调试模式 |

用于讨论和参数取舍的独立 HTML 原型及原始测量统一保留在[设计原型归档](../../archive/prototypes/README.md)，不作为正式游戏实现或最终验收结果。

每份文档的固定小节：边界、不变量、规则、Demo 范围、Demo 之后、开放问题。Demo 指最终游戏完整可玩的最小集合；本阶段是 Demo 之前的开发里程碑，其范围只在 [00 的本阶段范围](00-product-and-core-loop.md#本阶段范围)中描述，详细分期见各实施 Issue。

术语以仓库根目录 `CONTEXT.md` 为准。VoxelDefinition、VoxelDefinitionId 分别对应体素定义、体素定义标识，Value 即基本货币价值；Train Assembly 对应列车编组。Voxel、Revision，以及 Raw Good、Extraction、Process、Batch、OutputBuffer、Stack 等尚未进入术语表的概念沿用英文，待对应票落定后再收入。文中的 Simulation Tick 指模拟步进，与术语表的市场刻不是同一概念。

## 总体架构

```text
                核心模拟层
   权威游戏状态；确定性；不涉及体素；不依赖 Godot
        │ 只读查询、领域事件              ▲ 命令
        ▼                                │
   体素引擎层：局部场景与设计编辑器          │
        │ 场景数据                         │
        ▼                                │
   渲染层：Godot（渲染、相机、输入、音频、窗口、界面）
```

- **核心模拟层是唯一的权威来源。** 它在抽象世界图上以同一精度模拟整个世界：节点、线路与铁轨、聚居地、列车、经济、战斗、世界生物、时钟与命令。存档只保存核心状态，世界生成只产生初始核心状态。它使用普通 C# 类型，不依赖 Godot、SceneTree 和体素引擎层。
- **体素引擎层只构建局部场景。** 玩家进入某个地点时，体素引擎层构建线路场景或节点场景，离开时销毁；设计编辑器也由它构建。场景只由该地点的核心状态快照、内容定义，以及由 WorldSeed 和地点身份派生的无状态随机键生成，所以同一地点每次生成的结果相同。场景不保存状态，也不影响任何结果。同一时间只显示一个场景；跟随列车时最多在后台预建一个下一场景，使切换无缝。
- **渲染层是 Godot。** 它负责渲染、相机、输入、音频、窗口和界面。宏观地图和管理界面通过核心模拟层的只读查询读取数据，不经过体素引擎层。Godot 节点、网格、碰撞形状和界面都不是游戏事实。
- **接口与信息隐藏。** 依赖方向是渲染层 → 体素引擎层 → 核心模拟层，下层不知道上层的存在。各层只通过窄接口交互：核心模拟层对外只有命令入口、只读查询和供表现使用的领域事件；体素引擎层对外只有构建、更新、销毁场景，拾取，以及交给渲染层的场景数据。玩家在任何界面或场景中的操作，都作为命令提交给核心模拟层。
- **内容是共用的只读数据。** 体素定义、蓝图等内容同时供核心模拟层和体素引擎层读取，核心模拟层只读取由设计派生出的属性。定义不直接依赖 Godot Resource，而是使用 AssetId，由渲染层在需要时解析。
- **领域事件与 Godot Signal。** 核心模拟层使用普通 C# 领域事件；适配层可以把它们转换为 Godot Signal，领域类型不为此继承 GodotObject。

这一决定记录为 [ADR-0008](../adr/0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md)，它取代了 [ADR-0004](../adr/0004-keep-the-world-model-as-the-single-source-of-truth-and-treat-godot-as-a-derived-adapter.md) 对游戏状态范围的描述。出处为[分层架构与局部场景的权威边界](https://github.com/odradekk/train/issues/198)；原架构的出处为转录 Q17、Q34、Q49、Q50、Q147。

## 核心不变量汇总

实现过程中优先保护以下规则。

### 核心模拟层

1. 核心模拟层是唯一的权威游戏状态；局部场景、渲染、碰撞和界面都是派生结果。
2. 核心模拟层不依赖 Godot、SceneTree 和体素引擎层；三层只通过公开接口交互。
3. 模拟结果与帧率、相机、窗口状态、倍速和暂停时机无关，也与局部场景是否打开、打开的是哪一个无关；核心模拟层对整个世界使用同一精度，从不等待玩家，只有玩家暂停和内部故障能让时钟停下。
4. 局部场景不保存状态，不影响模拟结果；场景中的操作只能通过命令改变世界。每条命令在确定的模拟位置执行一次，作为一个事务原子结算；存档中没有待执行命令。
5. 列车在世界图上的位置是权威位置，场景中的位置由它派生；同一条铁轨上同一时间只有一个方向的列车；除断轨和线路上的战斗外，列车只在节点等待。
6. 位置不是持久身份。
7. 注册顺序、容器枚举顺序、线程完成顺序不得决定 Gameplay。
8. 所有 Gameplay Random 最终源于 WorldSeed。
9. 本阶段全部使用 Stateless Random；有状态随机流在后续玩法确有需求时加入。
10. 后续引入 Stateful RNG 时必须持久化完整状态。
11. Price 属于 Market Context，不属于 Voxel。
12. 影响未来模拟的长生命周期 Job、Buffer、Reservation 与设备 Runtime State 必须持久化。
13. 性能优化必须服从 Profiling，而不是提前猜测。
14. 世界图的节点和线路集合、节点位置、线路长度、地貌概况和铁轨上限在生成后固定；之后只有占据者、铁轨数、断点和资源会变化；节点资源的储量只由开采流程减少，由原地恢复或再生事件增加。
15. 车辆和建筑实例引用固定的蓝图版本，只保存部件耐久、结构耐久等实例状态；设计属性由内容层从蓝图版本派生。每座建筑占据聚居地的一个地块。
16. 建造、改装、修理和损伤按事务原子结算；战斗按模拟步结算，同一步的全部命中作为一个伤害事件统一施加。
17. 车辆和建筑只能由显式的建造、委托或世界生成产生。

### 内容

18. Definition 是不可变数据。
19. Runtime State 不写回 Definition。
20. Semantic Revision 与 Presentation Asset Revision 分离。
21. Capability 必须来自真实的 Behavior；Facility Tag 只表达兼容性。
22. BOM 由蓝图版本的体素布局派生；建造消费完成的货物，不递归展开配方。
23. 每种货物都是一个体素定义；车辆和建筑不是货物。核心模拟层只读取体素定义中的核心字段。
24. 库存中的货物只按种类和数量计数，没有个体身份或个体状态。

### 体素引擎层（只在局部场景和设计编辑器中成立）

25. 一个 Voxel = 一个 Cell；一个 Cell 最多一个 Voxel。
26. Voxel Local Coordinate 永远为整数；Voxel Orientation 永远为正交方向。
27. Voxel 同一时间只有一个 Owner。
28. 新建及正常改装的设计要求六面连通。

### 已废弃的条目

原第 6 条后半（受损静态建筑保留多个分离部分）、第 12、13 条（物理铁路）、第 24、26、27 条（货物、库存与资源源）已由[地图](https://github.com/odradekk/train/issues/193)中的对应票改写或删除，见各篇的决策出处。

## 设计指导思想

本项目不追求建立理论上最通用的 Voxel Engine、ECS、铁路模拟器或物理模拟框架。核心目标是：**用少量稳定、直观且可组合的基础规则，支持一个由核心模拟层持续运行、可长期扩展的列车经营世界，并用体素局部场景给玩家带来精美、沉浸的画面。**

对未来需求：简单模型已能满足玩法就继续使用；Profiling 证明性能不足再优化数据布局；真实 Gameplay 证明某个抽象不足再增加新系统；不因为"未来可能需要"就提前实现复杂框架。尤其避免：一个 Voxel 一个 Node、一种物品一个 C# 子类、万能 Composite 递归树、万能状态机、完整 ECS Framework、完整 Event Sourcing、提前实现跨平台 bit-perfect determinism、提前实现结构力学、提前实现完整铁路联锁。

长期稳定性来自：清晰边界 + 少量强不变量 + 数据驱动 + 组合 + 确定性 Simulation + 可替换的 Presentation Optimization，而不是过度抽象。

一句话总结：**游戏世界是一张由确定性核心模拟层持续运行的抽象世界图，经济、建设和战斗都在其上结算；体素只用于玩家关注的局部场景和设计编辑器，场景按需构建、只作表现；所有层都不依赖 Godot 承担游戏状态，Godot 只负责表现与平台适配。**
