# 14 建造、改装、修理与拆解

> **所属层**：核心模拟层。


> 决策出处：资源转录 Q252–Q254、Q256–Q260、Q263–Q265、Q268、Q336–Q342、Q347–Q356、Q398–Q405、Q420–Q422、Q425（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)）；Demo 只为 Deconstruction 提供主动 Pause / Resume、Construction / Refit 主动暂停延后，依据 [#85](https://github.com/odradekk/train/issues/85)。实例引用固定的蓝图版本、部件不在实例之外保留个体身份、新增修理任务，由[列车与建筑在核心模拟层的表示](https://github.com/odradekk/train/issues/201)于 2026-10-05 确认。资源与生产系统的总体边界见 [10](10-resources-and-extraction.md)。实例有自己的设计、蓝图只是模板、改装草稿与本局蓝图库，由[实例与蓝图：实例保存自己的设计，模板的存放与改装目标](https://github.com/odradekk/train/issues/260)于 2026-10-09 确认，见 ADR-0012。复合体嵌套与能入库的复合体作为货物，由[复合体嵌套：大型设备装到车辆和建筑上](https://github.com/odradekk/train/issues/249)于 2026-10-09 确认，见 ADR-0013。

## 边界

本文定义车辆和建筑怎样使用完成的货物，进行耗时的建造、改装、修理与拆解，包括：BOM 的来源、任务的启动、预留、工作量、快照、锁定状态、失败与暂停。

相关内容见：
- 实例保存的内容、部件与结构、建造与改装的含义：[03](03-vehicles-buildings-and-designs.md)
- Construction Capability 与 Work、Power、Labor 模型：[13](13-production-runtime-and-facilities.md)
- 库存：[12](12-inventory-and-storage.md)
- Recovery：[11](11-goods-and-production-network.md)、[13](13-production-runtime-and-facilities.md)

## 不变量

- 车辆和建筑的 BOM 只由设计的体素布局派生，不维护第二份手写的建造成本。
- 建造消费完成的货物，不递归展开上游生产配方。
- 蓝图和建造 BOM 要求精确的 VoxelDefinitionId，Tag 不提供隐式替代。
- 设计中的每个体素都对应一种可制造、可库存的货物。
- 建造、改装、修理、拆解都是显式的耗时任务：中间过程不暴露半成品，最终修改原子提交。
- 拆解不等于 Recovery：拆解返还的是原有的货物，Recovery 才进行物质转换。

## 规则

### BOM

- BOM 完全从设计的体素布局派生。例如某车头的布局含 Structural Block ×120、Engine X ×2、Storage Equipment ×4，BOM 就是这些实际的 VoxelDefinitionId 和数量；不另设 `construction_cost` 作为第二份权威数据。
- 设计中装着大型设备时，BOM 列出整件设备（按去掉涂装的设计），不展开成其中的体素，与“不自动展开配方”一致（[复合体嵌套：大型设备装到车辆和建筑上](https://github.com/odradekk/train/issues/249)，ADR-0013）。
- 档位等非体素信息不产生物料成本。
- 官方预置型号与玩家自定义设计严格使用同一套成本规则。预置型号只是官方提供的蓝图，不享受隐藏折扣或手写成本；玩家把官方型号改动几格，成本随实际体素变化（资源转录 Q265）。
- 车辆和建筑共用同一套建造规则与 BOM 派生，不分别设置车辆建造系统和建筑建造系统。玩家可以设计车辆和建筑（[00](00-product-and-core-loop.md#demo-范围)），资源规则相同（资源转录 Q254）。
- BOM 要求确切的 VoxelDefinitionId，Tag 不允许自动替代：Basic Engine ≠ Advanced Engine。想换另一种设备，必须修改蓝图。
- 建造消费完成的货物：Mechanical Components → 生产 Engine → Engine 进入库存 → 建造消费 Engine。缺少 Engine 时，不允许自动展开 Engine 的配方去直接消费上游材料。这是核心经济边界之一。
- 安装进实例的部件一律是新件，耐久为当前定义的上限。不安装二手或受损的部件。

### Construction Job

- 正式玩法中的建造是耗时任务。建造期间不存在半成品实例，进度到 100% 时原子地生成实例。表现层以后可以加脚手架、施工动画等，但它们不是权威状态。
- 启动时依次：从选定的蓝图复制设计并校验 → 校验目标（建筑校验目标地块空闲、尺寸合适、建筑种类允许；车辆校验车辆建造设施支持的档位，见 [16](16-settlements.md)）→ 派生精确的 BOM → 校验可访问的库存 → 校验 Construction Capability → 原子消费完整 BOM → 建筑的目标地块标记为工地 → 创建 ConstructionJob。完工的车辆停放在该聚居地。大型设备也按蓝图建造，完工后放进该聚居地的库存，不生成实例，不占地块。BOM 在启动时一次消耗完；Demo 不提供主动取消，也不提供取消后的退款。
- 在玩家自己的聚居地施工时，可以同时使用该聚居地的库存和当地可访问的玩家列车库存；二者仍是两个独立的库存。这是 [12 的显式转移规则](12-inventory-and-storage.md#地点库存)的限定例外，只供建造、改装、修理启动时消费 BOM，不让生产、回收或发电设施直接读取列车货舱。启动事务必须记录实际的消费来源，按稳定顺序或显式选择消费；Demo 默认先用聚居地库存，再用列车库存，以后的界面可以让玩家指定来源。

### 地块预留

- 建造建筑时，目标地块在施工期间标记为工地，不能另作他用；工地不是建筑实例，没有部件和耐久。它属于权威运行状态，必须存档（[聚居地的经营、建设与场景](https://github.com/odradekk/train/issues/206)，2026-10-05）。
- 地块预留随任务完成或失败解除。Demo 中不会出现施工期间聚居地易主，见 [17](17-combat.md#聚居地关系与征服)。
- 委托非敌对聚居地施工时的材料来源、费用和排队，见 [16 的委托](16-settlements.md#委托)。

### Construction Capability 与 Work

- 实际施工能力来自 Construction Equipment → ConstructionBehavior → ConstructionCapability，不由建筑类型凭空产生。施工使用 [13](13-production-runtime-and-facilities.md) 统一的 Work、Power、Labor 模型。一个 Construction Capability 同时最多执行 1 个建造、改装、修理或拆解任务。
- Demo 使用 `ConstructionWork = VoxelCount × BaseConstructionWorkPerVoxel`，不为每种 VoxelDefinition 单独设置装配工作量。复杂部件的制造复杂度已经体现在它的生产网络中，建造只模拟安装完成的部件。

### Construction Snapshot

- 任务启动时冻结目标设计和目标地块。之后修改蓝图，不改变已经在运行的任务。
- 快照不嵌入外部体素定义的旧规格。完成时按 VoxelDefinitionId 解析当前安装的定义，新实例采用当前规格，与 [02](02-definitions-and-content.md) 的当前定义规则一致。
- 如果热更新删除了任务必需的 DefinitionId，拒绝这次内容发布。

### Refit Job

- 调试模式和控制台的改装命令（[15](15-design-editor-and-tools.md#调试命令)）提供瞬时的原子改装，用于创建和验证修改，不代表正式玩法零耗时。正式玩法中，改装是把实例从当前设计切换到目标设计的耗时任务：校验目标设计 → 派生差额 BOM → 消费新增的货物 → 为返还的货物预留库存容量 → 创建 RefitJob → 完成时原子提交。中间的非法结构不暴露给模拟。
- 差额 BOM 按体素位置和定义对比两份设计：
  - 位置和定义都不变的体素保持原样，部件保留耐久，不消耗也不返还货物。
  - 装上、拆下、更换大型设备都按整件计算：装上消耗库存中的这件设备，拆下时先修好、再整件返还入库。
  - 新增或替换进去的体素消耗对应的货物。例如从 100 Structural Block + 2 Engine 改为 105 Structural Block + 2 同款 Engine + 1 Storage Equipment，需要 Structural Block ×5 和 Storage Equipment ×1。
  - 拆下的完好部件返还为普通货物。拆下的受损部件先修好再返还，修理的工作量计入本次任务。被毁的部件不返还。
  - 拆下的结构体素按结构耐久比例返还：每种结构体素返还“拆下数量 × 当前结构耐久比例”向下取整。
- 库存的可访问范围、来源记录和默认消费顺序，沿用上文建造启动事务的限定例外。
- 返还的货物在完成时进入执行任务的设施所在聚居地的库存。启动时须为全部预计返还的货物预留库存容量，容量不足则不启动；返还内容在启动前已经知道，不在完成后制造临时缓冲区（2026-10-01 设计讨论）。
- Demo 使用 `RefitWork = ChangedVoxelCount × BaseRefitWorkPerChangedVoxel + 受损部件的修理工作量`。新增、移除、替换各算一个修改单位，不为不同操作设权重。
- 改装期间实例处于 UnderRefit 锁定状态：列车不能运行和贸易，不能同时启动第二个改装、修理或拆解；完成时才原子切换到目标版本。
- 任务期间实例受到损伤：如果实例被摧毁，任务失败，进度丢弃，已消费的货物丢失，库存预留释放；其他损伤不影响任务，完成时保留最新的部件耐久和结构耐久。

### Repair Job

- 修理把实例恢复到它自己的设计：补回被毁的部件，把受损部件的耐久和结构耐久恢复到当前定义的上限。
- 消耗：被毁部件对应的货物；以及按损失比例折算的结构体素货物，即每种结构体素消耗“数量 × 结构耐久损失比例”向上取整。受损但未被毁的部件只消耗工作量。
- 工作量：`RepairWork = 被毁部件数 × BaseConstructionWorkPerVoxel + 受损部件修理工作量 + 折算的结构体素数 × BaseConstructionWorkPerVoxel`。
- 修理在自己聚居地的设施中进行，或委托非敌对聚居地进行。期间实例处于 UnderRepair 锁定状态，规则与 UnderRefit 相同。实例被摧毁则任务失败；其他损伤不影响任务，完成时一并恢复。

### Deconstruction Job

- 禁止 Locomotive → Recovery → materials；车辆和建筑必须先显式拆解。
- 实例 → 拆解任务 → 原子完成 → 实例移除 → 返还货物进入聚居地库存。拆解使用 Construction Capability，不新增拆解设备。
- 进度在 0–99% 时，原实例完整存在、不逐个体素消失；到 100% 时原子地移除实例并返还货物。
- 期间实例处于 UnderDeconstruction：不能正常运营，建筑不执行生产，列车不运行，不能改装、修理，也不能开始第二个拆解。
- 返还规则与改装的拆下规则相同：完好部件返还为普通货物，受损部件修好后返还（工作量计入），被毁部件不返还；结构体素按当前结构耐久比例返还，向下取整。
- 返还的货物进入执行任务的设施所在聚居地的库存（2026-10-01 设计讨论）。启动前必须在该库存中预留全部预计返还货物所需的容量。与生产的 OutputBuffer 不同，拆解的输出在开始前就完全知道，所以使用库存预留，不在完成后制造巨大的临时缓冲区。
- 列车开始拆解前，其列车库存必须为空，否则拒绝启动；货物须先经显式转移移出，不随拆解并入聚居地库存（[12](12-inventory-and-storage.md#地点库存)，2026-10-01 设计讨论）。
- Demo 使用 `DeconstructionWork = VoxelCount × BaseDeconstructionWorkPerVoxel + 受损部件修理工作量`。
- 进行中的拆解不允许主动取消，只允许暂停。
- 实例在拆解期间被摧毁时，任务失败，进度丢弃，库存预留释放。其他损伤不影响任务，完成时按最新状态计算返还。

### 失败与暂停汇总

- Demo 中建造、改装、修理、拆解都不提供主动取消；失败结算不等于玩家取消。主动 Pause / Resume 只覆盖拆解：暂停期间不累计工作量，保留进度、库存预留和 UnderDeconstruction 锁定，恢复后按当前条件继续。建造、改装、修理的主动暂停未纳入 Demo；资源或能力不足时进度保留，不表示存在主动暂停命令。
- 建造：Power、Labor 或 Capability 不足时，进度保留，BOM 不返还，预留保留。施工设施被毁或失去执行资格时，任务失败，已投入的货物不返还，进度丢失，地块预留解除。
- 改装、修理：实例被摧毁时任务失败，已投入的货物不返还，库存预留释放。
- 拆解：实例被摧毁时任务失败，进度丢失，库存预留释放。
- 设施存在 ActiveBatch（包括 Recovery Batch）或非空 OutputBuffer 时，不允许直接进入改装、修理或拆解，见 [13](13-production-runtime-and-facilities.md)。

### 存档

ConstructionJob、RefitJob、RepairJob、DeconstructionJob、地块预留、库存预留、拆解的主动暂停状态和 ProgressWork 都是权威运行状态，必须存档；完整清单见 [09](09-persistence.md)。

## Demo 范围

从设计派生 BOM；精确的 DefinitionId；耗时的建造任务与地块预留；耗时的改装任务、差额 BOM 与返还货物的库存预留；修理任务；拆解任务与库存预留；列车拆解前须清空列车库存；UnderRefit、UnderRepair、UnderDeconstruction 锁定；快照与失败语义；拆解的主动 Pause / Resume。玩家使用自己设计或预置的蓝图建造、改装和修理车辆与建筑。调试模式和控制台的瞬时原子改装继续保留（[15](15-design-editor-and-tools.md#调试命令)）。

## Demo 之后

建造、改装、修理、拆解的主动取消与退款；建造、改装、修理的主动 Pause / Resume；施工事故的残骸回收；车辆和建筑的直接 Recovery；施工来源的界面指定；脚手架与施工动画等表现。

## 开放问题

- 地图迷雾（不立票）：未冻结的参数，包括 ConstructionWorkPerVoxel、RefitWorkPerChangedVoxel、DeconstructionWorkPerVoxel、受损部件的修理工作量，以及 Construction Equipment 的基础能力。
- 地图迷雾（不立票）：建造、改装、修理的主动 Pause / Resume，以及四类任务的主动取消与退款，有待后续设计；Demo 只采用上文已明确的暂停与失败结算规则。

资源与生产在后续切片实现，见 [00 的后续切片](00-product-and-core-loop.md#后续切片暂定)。
