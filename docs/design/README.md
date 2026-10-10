# 设计文档集：列车放置经营游戏

本目录是玩法与系统规则的权威来源，模块调用契约见[四模块契约规范](../contracts/README.md)，各种资料的权威范围见[公共约定](../contracts/conventions.md#各种资料的权威范围)。每条决定的出处以"转录 Qn"引用 `docs/research/gdd-grilling-transcript.md`（Q1–Q180），以"资源转录 Qn"引用资源与生产系统 grilling 的 `docs/research/resources-and-production-grilling-transcript.md`（Q180–Q428）；两份转录的编号在 Q180 重叠，引用必须带前缀。[00](00-product-and-core-loop.md)–[09](09-persistence.md) 由 v0.1 原稿拆分而来，原稿只作留底（提交 `6576bf6`），不再作为依据；[10](10-resources-and-extraction.md)–[14](14-construction-refit-and-deconstruction.md) 由资源与生产系统设计稿拆分而来。2026-10 的架构调整由地图[体素引擎改作局部场景：设计文档与 open issue 全面修订](https://github.com/odradekk/train/issues/193)推进，此后的决定引用该地图中对应票的名称和链接；仍然有效的旧规则保留原来的转录出处。被废弃的规则直接从文档删除，历史只保留在 git、已关闭的票和 `docs/research` 的转录中；ADR 不删除，被取代时在末尾注明。

**文档内明确为规则的内容是当前设计基线。** 未定项在各篇“开放问题”小节标明状态，已登记的候选方向与未冻结参数不构成实施承诺；基于实现验证修订设计时，同步受影响章节。

**当前里程碑是完整 Demo。** 地图[完整 Demo：四模块公开接口、契约验证与并行开发规划](https://github.com/odradekk/train/issues/277)先补齐阻塞接口的规则、交付并验证四模块契约，再重组实施工单。[00](00-product-and-core-loop.md)已确定开放沙盒、默认开局、标准混合负载与完整 Demo 验收。受影响旧票仍暂停；其他设计、内容和美术文档中的首切片分期待对应决策及[按四模块重组现有工单并补齐 Demo 实施工单](https://github.com/odradekk/train/issues/294)更新，不能把旧分期和临时规则直接当作 Demo 实施依据。既定的系统规则仍是基线。

## 开放问题与立票

- **近期决策（有票）**：只有需要产出明确决策、且阻塞当前里程碑规划、接口或实施的未定项才立票，并在对应开放问题条目链接；条目说明要决定什么，不把整章未来细节打包立票。
- **地图迷雾（不立票）**：尚未形成具体决策问题的方向和实现细节，保留简短描述及重新评估条件，不为完整目录提前建票。待实测调优参数先安排测量责任，得到证据后再确定数值；明确属于 Demo 之后的工作不混入本次地图迷雾。
- 优先复用已有决策票，不重复立票；是否属于某张 wayfinder 地图取决于当前工作，不指定某张地图为唯一入口。当前 Demo 的范围与验收由[完整 Demo 的开局、目标、结束与验收](https://github.com/odradekk/train/issues/278)确认，统一记在 [00](00-product-and-core-loop.md)；接口验证后由[按四模块重组现有工单并补齐 Demo 实施工单](https://github.com/odradekk/train/issues/294)修订实施计划。
- 决策完成后，由解决该票的会话回写对应规则，移除或改写开放问题；已关闭票可以保留为历史出处，不能继续充当未解决的阻塞。无待决事项可写“无”，已经明确的规则留在规则章节。

## 文档目标

本项目为独立开发项目，设计文档不追求在开发前穷举实现细节。设计遵循：

1. 优先明确稳定的系统边界和不变量。
2. 具体数值尽量通过实现、Benchmark、Profiling 和 Playtest 确定。
3. 本阶段目标是完整 Demo：先确定并验证四模块公开契约，再按模块并行开发、各自独立测试；开发期持续检查契约兼容性，四模块完成后执行完整联调、回归和人工验收。各模块内部仍以小任务迭代。
4. 对明确会在未来加入的重要功能，架构上避免封死扩展空间，但不提前实现。
5. 组合优于继承。
6. 领域模型与 Godot SceneTree、渲染、UI 等表现层严格分离。
7. Deterministic Simulation 是整个项目的基础架构原则。

Demo 范围见 [00](00-product-and-core-loop.md#demo-范围)：经济、建设、战斗三个并列的主循环，包括面向玩家的设计编辑器、战斗和聚居地取得；战斗与征服见 [17](17-combat.md)，人口、消费与劳动力见 [18](18-population-and-labor.md)，市场、显式自动交易规则与运行报告见 [19](19-markets-and-trading.md)。资源与生产系统已在 [10](10-resources-and-extraction.md)–[14](14-construction-refit-and-deconstruction.md) 定义，其中依赖体素世界的部分随架构调整改写，实施任务纳入本次 Demo 工单重组。AI 势力、自主制定贸易策略和复杂调度在 Demo 之后扩充，不包括 Demo 已有的玩家规则自动执行。

## 文档地图

每篇开头标明所属的层。以后修订时，受影响的文档开头用“修订中”说明列出失效的章节和负责改写的工单；改写完成前，这些章节不能作为实现依据。

| 层 | 文件 | 内容 |
| --- | --- | --- |
| 产品 | [00-product-and-core-loop.md](00-product-and-core-loop.md) | 产品定位、核心循环、Demo 范围与本阶段目标 |
| 核心模拟层 | [04-deterministic-simulation.md](04-deterministic-simulation.md) | 时间、命令与确定性 |
| 核心模拟层 | [05-world-graph-and-generation.md](05-world-graph-and-generation.md) | 世界图与世界生成 |
| 核心模拟层 | [16-settlements.md](16-settlements.md) | 聚居地 |
| 核心模拟层 | [18-population-and-labor.md](18-population-and-labor.md) | 人口变化、居民消费与劳动力 |
| 核心模拟层 | [19-markets-and-trading.md](19-markets-and-trading.md) | 真实库存市场、精确定价、现车、自动交易与运行报告 |
| 核心模拟层 | [06-railway.md](06-railway.md) | 铁路与列车运行 |
| 核心模拟层 | [17-combat.md](17-combat.md) | 战斗、世界生物与征服 |
| 核心模拟层 | [03-vehicles-buildings-and-designs.md](03-vehicles-buildings-and-designs.md) | 车辆、建筑与设计属性 |
| 核心模拟层、内容、Godot 宿主 | [09-persistence.md](09-persistence.md) | 存档 |
| 核心模拟层 | [10-resources-and-extraction.md](10-resources-and-extraction.md) | 节点资源、开采与资源更新 |
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

美术风格和资产制作规则见[美术设计文档](../art/README.md)。它在本目录的技术规则之内规定画法和规格；美术规则需要改动技术规则时，同时修改本目录的对应章节。

每份文档的固定小节：边界、不变量、规则、Demo 范围、Demo 之后、开放问题。Demo 指最终游戏完整可玩的最小集合，也是当前开发里程碑；范围与验收统一记在 [00](00-product-and-core-loop.md)，接口确定后由各实施 Issue 描述模块内的小任务和联调要求。

术语以仓库根目录 `CONTEXT.md` 为准。VoxelDefinition、VoxelDefinitionId、Voxel、Cell 分别对应体素定义、体素定义标识、体素、体素格，Value 即基本货币价值；Train Assembly 对应列车编组。文中剩余的 Voxel、Cell 在各篇下次修订时改为中文。Revision，以及 Raw Good、Extraction、Process、Batch、OutputBuffer、Stack 等尚未进入术语表的概念沿用英文，待对应票落定后再收入。文中的 Simulation Tick 指模拟步进；Demo 不再有恢复供需的市场刻。

## 总体架构

四个逻辑模块不等于四个程序集。核心、内容、体素分别拆分契约与实现，Godot 负责装配和适配；目标项目布局与引用允许范围统一见[契约索引](../contracts/README.md#依赖与装配)。这仍是目标结构，生产代码尚未迁移。

- **核心模拟层是唯一的权威来源。** 它在抽象世界图上以同一精度模拟整个世界：节点、线路与铁轨、聚居地、列车、经济、战斗、世界生物、时钟与命令。运行状态只保存核心状态；完整存档另含内嵌内容，不保存场景和界面状态。世界生成只产生初始核心状态。它使用普通 C# 类型，不依赖 Godot、SceneTree 和体素引擎层。
- **体素引擎层只构建局部场景。** 玩家进入某个地点时，体素引擎层构建线路场景或节点场景，离开时销毁；设计编辑器也由它构建。场景只由该地点的核心状态快照、内容定义，以及由 WorldSeed 和地点身份派生的无状态随机键生成，所以同一地点每次生成的结果相同。场景不保存状态，也不影响任何结果。同一时间只显示一个场景；跟随列车时最多在后台预建一个下一场景，使切换无缝。
- **Godot 负责表现与宿主服务。** 它负责渲染、相机、输入、音频、窗口和界面，以及装配、诊断日志、控制台文本分发、完整存档和复现包组织。宏观地图和管理界面直接使用核心查询，不经过体素引擎层；宿主只通过公开契约组合工作，不复制核心或内容规则。Godot 节点、网格、碰撞形状和界面都不是游戏事实。
- **接口与信息隐藏。** 各模块依赖其他模块的契约，不引用其实现。Godot 仅在 `App/Composition/` 调用真实创建工厂，其他代码接收契约接口；下层所需的宿主接口由下层声明，Godot 实现。玩家和调试工具改变核心状态的操作都作为核心命令提交，查询、场景和编辑会话的其他工作按各自公开接口执行。
- **内容层按读取方投影。** 内容层解析定义，检查引用和资产，算出设计属性和建造合法性，再按读取方拆开交给各层；核心只得到 `simulation` 一节和设计属性（ADR-0011）。设计属性的类型归核心契约，计算仍归内容实现。定义不直接依赖 Godot Resource，而是使用资产身份，由宿主按公开契约接入。
- **领域事件与 Godot Signal。** 核心模拟层使用普通 C# 领域事件；适配层可以把它们转换为 Godot Signal，领域类型不为此继承 GodotObject。

权威状态和场景职责记录为 [ADR-0008](../adr/0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md)，它取代了 [ADR-0004](../adr/0004-keep-the-world-model-as-the-single-source-of-truth-and-treat-godot-as-a-derived-adapter.md) 对游戏状态范围的描述，出处为[分层架构与局部场景的权威边界](https://github.com/odradekk/train/issues/198)。契约拆分与宿主职责由 [ADR-0016](../adr/0016-separate-module-contracts-and-limit-implementation-access-to-composition.md)修订；原架构的出处为转录 Q17、Q34、Q49、Q50、Q147。

## 核心不变量汇总

实现过程中优先保护以下规则。

### 核心模拟层

1. 核心模拟层是唯一的权威游戏状态；局部场景、渲染、碰撞和界面都是派生结果。
2. 核心模拟层不依赖 Godot、SceneTree 和体素引擎层；四模块只通过公开契约交互。
3. 模拟结果与帧率、相机、窗口状态、倍速和暂停时机无关，也与局部场景是否打开、打开的是哪一个无关；核心模拟层对整个世界使用同一精度，不因玩法决策等待玩家。玩家暂停、内部故障及[技术操作锁](../contracts/transactions.md#技术操作锁与线程)可以停止驱动，普通场景构建不能停止模拟。
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
15. 车辆和建筑实例有自己的设计，只保存设计引用、部件耐久、结构耐久等实例状态；设计属性由内容层从设计派生；蓝图只是模板。每座建筑占据聚居地的一个地块。
16. 建造、改装、修理和损伤按事务原子结算；战斗按模拟步结算，同一步的全部命中作为一个伤害事件统一施加。
17. 车辆和建筑只能由显式的建造、委托或世界生成产生；现车买卖只转移原实例，建筑不单独交易。

### 内容

18. Definition 是不可变数据。
19. Runtime State 不写回 Definition。
20. Semantic Revision 与 Presentation Asset Revision 分离。
21. Capability 必须来自真实的 Behavior；Facility Tag 只表达兼容性。
22. BOM 由设计派生：自己的体素逐格计数，被嵌套的大型设备按整件计数；建造消费完成的货物，不递归展开配方。
23. 货物是带 `simulation` 一节的体素定义或能入库的复合体；车辆和建筑不是货物。核心模拟层只读取体素定义中的 `simulation` 一节。
24. 库存中的货物只按种类和数量计数，没有个体身份或个体状态。

### 体素引擎层（只在局部场景和设计编辑器中成立）

25. 一个 Voxel = 一个 Cell；一个 Cell 最多一个 Voxel。
26. Voxel Local Coordinate 永远为整数；Voxel Orientation 永远为正交方向。
27. Voxel 同一时间只有一个 Owner。
28. 车辆、建筑和大型设备要求全部体素和被嵌套的复合体六面连成一体；场景模型和生物不要求。

### 已废弃的条目

原第 6 条后半（受损静态建筑保留多个分离部分）、第 12、13 条（物理铁路）、第 24、26、27 条（货物、库存与资源源）已由[地图](https://github.com/odradekk/train/issues/193)中的对应票改写或删除，见各篇的决策出处。

## 设计指导思想

本项目不追求建立理论上最通用的 Voxel Engine、ECS、铁路模拟器或物理模拟框架。核心目标是：**用少量稳定、直观且可组合的基础规则，支持一个由核心模拟层持续运行、可长期扩展的列车经营世界，并用体素局部场景给玩家带来精美、沉浸的画面。**

对未来需求：简单模型已能满足玩法就继续使用；Profiling 证明性能不足再优化数据布局；真实 Gameplay 证明某个抽象不足再增加新系统；不因为"未来可能需要"就提前实现复杂框架。尤其避免：一个 Voxel 一个 Node、一种物品一个 C# 子类、万能 Composite 递归树、万能状态机、完整 ECS Framework、完整 Event Sourcing、提前实现跨平台 bit-perfect determinism、提前实现结构力学、提前实现完整铁路联锁。

长期稳定性来自：清晰边界 + 少量强不变量 + 数据驱动 + 组合 + 确定性 Simulation + 可替换的 Presentation Optimization，而不是过度抽象。

一句话总结：**游戏世界是一张由确定性核心模拟层持续运行的抽象世界图，经济、建设和战斗都在其上结算；体素只用于玩家关注的局部场景和设计编辑器，场景按需构建、只作表现；所有层都不依赖 Godot 承担游戏状态，Godot 只负责表现与平台适配。**
