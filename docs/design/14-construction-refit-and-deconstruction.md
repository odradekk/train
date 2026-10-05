# 14 Composite 建造、改装与拆解

> **所属层**：核心模拟层。
>
> **修订中**：“BOM”“Goods 实体化”“Refit Job”“Deconstruction Job”中依赖实例体素布局和零件身份的部分由[列车与建筑在核心模拟层的表示](https://github.com/odradekk/train/issues/201)改写；“Construction Job”中的放置校验和“Spatial Reservation”由[聚居地的经营、建设与场景](https://github.com/odradekk/train/issues/206)改写。用已完成货物建造、不递归展开配方、三类 Job 耗时且原子提交等规则继续有效。改写完成前，上述规则不能作为实现依据；其余规则继续有效。

> 决策出处：资源转录 Q252–Q254、Q256–Q260、Q263–Q265、Q268、Q336–Q342、Q347–Q356、Q398–Q405、Q420–Q422、Q425（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)）；Demo 只为 Deconstruction 提供主动 Pause / Resume、Construction / Refit 主动暂停延后，依据 [#85](https://github.com/odradekk/train/issues/85)。资源与生产系统的总体边界见 [10](10-resources-and-extraction.md)。

## 边界

本文定义 Composite 如何使用完成的 Voxel Goods 进行耗时 Construction、Refit 与 Deconstruction：BOM 的来源、Job 的启动、Reservation、Work、Snapshot、锁定状态、失败与暂停。Composite 的结构合法性、Voxel Identity 保留与 Refit 的原子事务原则见 [03](03-composite-and-behavior.md)；Construction Capability 与 Work / Power / Labor 模型见 [13](13-production-runtime-and-facilities.md)；Inventory、Stack 与 ItemInstance 见 [12](12-inventory-and-storage.md)；Recovery 见 [11](11-goods-and-production-network.md)、[13](13-production-runtime-and-facilities.md)。

## 不变量

- Composite 的 BOM 由最终实际 Voxel Layout 唯一派生，不维护第二份手写 construction cost。
- Composite Construction 消费完成的 Voxel Goods，不递归展开其上游 Production Recipe。
- Blueprint / Construction BOM 要求精确 VoxelDefinitionId，Tag 不提供隐式替代。
- 所有真实安装在 Composite 中的 Voxel 都首先是可制造、可库存的 Voxel Good。
- Construction、Refit、Deconstruction 都是显式耗时 Job：中间过程不暴露半成品结构，最终结构修改原子提交。
- 拆卸不等于 Recovery：拆卸返回原有 Voxel / Item，Recovery 才进行物质转换。

## 规则

### BOM

- Composite BOM 完全从最终 Voxel Layout 派生。例如 Locomotive Layout 含 Structural Block ×120、Engine X ×2、Storage Equipment ×4，BOM 即这些实际 VoxelDefinitionId 与 Quantity；不另设 `construction_cost` 作为第二份权威数据。
- Rail Contact、Size class、Archetype、Connector metadata 等非 Voxel 元数据不产生物料成本。
- 官方预置型号与玩家自定义设计严格使用同一成本规则：预置型号只是官方提供的 Blueprint / Definition，不享受隐藏折扣或手写成本；玩家修改官方型号几格后，成本随实际 Voxel 变化（资源转录 Q265）。
- 列车与建筑共用同一套 Composite Construction 规则与 BOM 派生，不存在分别的 Train / Building Construction System；差别只在设计粒度：玩家可逐 Voxel 设计车辆 Composite，聚居地层面主要放置预置的 Building Composite 与装饰，建筑底层仍是真实 Voxel。以后开放建筑自定义只是开放 Blueprint 编辑能力，不改变资源规则（资源转录 Q254）。
- BOM 要求确切 VoxelDefinitionId，Tag 不允许自动替代：Basic Engine ≠ Advanced Engine。想换另一种设备必须修改 Blueprint / Refit Plan。
- Construction 消费完成 Goods：Mechanical Components → 生产 Engine → Engine 进入 Inventory → Construction 消费 Engine。不允许缺少 Engine 时自动展开 Engine 配方并直接消费上游材料。这是核心经济边界之一。

### Goods 实体化

- 库存 Goods 只有在显式 Placement、Construction、Refit add / replacement 时实体化为 VoxelInstance。
- 新实例初始化为 DefinitionId、CurrentDurability = 当前 MaxDurability、default Behavior Runtime State、以及需要时的 RuntimeId。
- 允许安装显式选定的二手 / 受损 ItemInstance：它以当前 Durability 与 Runtime State 进入 Composite，系统不自动修复。Construction Validity 只看结构合法性，Operational Condition 另行派生（[03](03-composite-and-behavior.md)，资源转录 Q263）。

### Construction Job

- 正式 Gameplay 的 Composite Construction 是耗时 Job。Construction 期间不存在半成品 Composite；进度到 100% 时原子 instantiate。视觉层以后可以表现脚手架、施工动画等，但不是权威 Composite。
- 启动时：validate Blueprint / Definition → validate final placement → derive exact BOM → validate accessible inventories → validate Construction Capability → validate Site constraints → 原子消费完整 BOM → reserve final spatial area → create ConstructionJob。BOM 在启动时完整消耗；Demo 不提供主动 Cancel，也不提供 Cancel 后退款。
- 在玩家自己的聚居地施工时，可以同时使用当前玩家 Settlement Inventory 与当前所在地可访问的玩家 Train Inventory；它们仍是两个独立 Inventory。这是 [12 的显式 Transfer 规则](12-inventory-and-storage.md#地点库存) 的限定例外，仅供 Construction / Refit 启动时消费 BOM，不赋予生产、回收或发电设施直接读取列车货舱的权限。Construction start transaction 必须记录实际消费来源，并采用稳定顺序或显式选择；Demo 默认 Settlement Inventory first → Train Inventory，未来 UI 可以允许指定来源。
- Stack Good 可自动消费；ItemInstance 必须显式选定，规则见 [12](12-inventory-and-storage.md)。

### Spatial Reservation

- Construction Job 必须预留最终 Composite 所需空间。Reservation 不是 Voxel occupant，不创建半成品 Collision，但阻止其他 Placement / Construction 占用目标区域；它属于权威 Runtime State，必须存档。
- 若外围世界变化导致最终 Placement / Site 约束失效：Construction Job fails → 已消费 BOM 丢失 → Progress 丢弃 → Reservation 释放。
- Reservation 只保护直接施工区域，不冻结附近世界。

### Construction Capability 与 Work

- 实际施工能力来自 Construction Equipment → ConstructionBehavior → ConstructionCapability，不由 Building 类型凭空产生。Construction Work 使用 [13](13-production-runtime-and-facilities.md) 的统一 Work / Power / Labor 模型；一个 Construction Capability 同时最多 1 个 Construction / Refit / Deconstruction Job。
- Demo 使用 `ConstructionWork = VoxelCount × BaseConstructionWorkPerVoxel`，不为每种 VoxelDefinition 增加 AssemblyWork。复杂部件的制造复杂度已经体现在其 Production DAG 中，Construction 只模拟安装完成部件。

### Construction Snapshot

- Job 启动时冻结 Blueprint / Layout snapshot、VoxelDefinitionId 列表、Orientation、target placement、spatial reservation；之后修改 Blueprint 不改变已经运行的 Job。
- Snapshot 不嵌入所有外部 VoxelDefinition 的旧规格。完成时按 VoxelDefinitionId 解析当前安装的 Definition，新实例采用当前规格，与 [02](02-definitions-and-content.md) 的 current-definition 规则一致。
- 若热更新删除 Job 必需的 DefinitionId，则拒绝内容发布。

### Refit Job

- 世界编辑器与控制台 `composite` 改装命令提供瞬时 Atomic Refit，用于创建和验证结构修改；它不代表正式 Gameplay 零耗时。正式 Gameplay：Refit Planning → validate final layout → derive delta BOM → consume added / replacement Goods → reserve Storage Capacity for removed / replaced parts → Refit Job → atomic final commit。[03](03-composite-and-behavior.md) 的原子事务原则继续有效：中间非法结构不暴露给 Simulation，最终结构修改原子提交，Voxel Identity 保留。
- Delta BOM 的库存可访问范围、来源记录及默认消费顺序沿用上文 Construction 启动事务的限定例外。Delta BOM 只消费新增或替换进去的 Goods。例如从 100 Structural Block + 2 Engine 改为 105 Structural Block + 2 同款 Engine + 1 Storage Equipment，需要 Structural Block ×5 与 Storage Equipment ×1。移动、旋转已有 Voxel 不消费新 Goods。
- Move / Rotate 保留原 Voxel identity 与 Runtime State。Replace 表示旧 Voxel 移除、新 Good 实体化；旧件按 Stackability 进入 Stack 或 ItemInstance（[12](12-inventory-and-storage.md)）。
- Remove 与 Replace 拆下的旧件在完成时进入执行该 Job 的 Construction Facility 所在聚居地的 Settlement Inventory。Refit 启动时须在该库存中为全部预计返还的旧件预留 Storage Capacity，容量不足则不启动；理由与下文 Deconstruction 的 Storage Reservation 相同：返还内容在启动前已知，不在完成后制造临时 Buffer（2026-10-01 设计讨论）。
- Demo 使用 `RefitWork = ChangedVoxelCount × BaseRefitWorkPerChangedVoxel`；Add、Remove、Move、Rotate、Replace 各算一个修改单位，不进一步为不同操作设置权重。
- Refit 期间目标 Composite 进入 UnderRefit 锁定状态：列车不能正常运行、不能继续贸易运营、不能同时启动第二个 Refit、不能同时 Deconstruct；完成时才原子切换最终 Layout。
- Job 启动时冻结 target Composite identity、expected source Voxel identity / layout、final validated layout、move / rotate / add / remove / replace mapping、selected ItemInstances、ProgressWork；Blueprint 后续修改不影响当前 Job。
- 若 Refit 期间 target 的 Voxel layout / identity set 被外部事件改变：Refit Job fails → Progress 丢弃 → 已消费的 added / replacement Goods 丢失 → Storage reservation 释放 → target 保持当前真实状态。若只是 Durability 改变而 Voxel 仍存在，Job 可继续，完成时保留其最新 Runtime State。

### Deconstruction Job

- 禁止 Locomotive Composite → Recovery → materials；Composite 必须先显式 Deconstruction。
- Composite → Deconstruction Job → atomic completion → Composite removed → actual Voxels returned to Settlement Inventory。使用 Construction Capability，不新增 Deconstruction Equipment。
- 0–99% 时原 Composite 完整存在、Occupancy 不变、不逐 Voxel 消失；100% 时原子移除 Composite 并把实际 Voxels 转入 Settlement Inventory。
- 期间 Composite 进入 UnderDeconstruction：不可正常运营、Building 不执行 Production、Train 不运行、不允许 Refit、不允许第二个 Deconstruction。
- 返还部件进入执行该 Job 的 Construction Facility 所在聚居地的 Settlement Inventory（2026-10-01 设计讨论）。启动前必须在该库存中预留所有预计返回部件所需 Storage Capacity。与 Production OutputBuffer 不同，Deconstruction 的输出在开始前完全已知，因此使用 Storage Reservation，不允许完成后制造巨大临时 Buffer。
- 列车 Composite 开始 Deconstruction 前，所属列车的 Train Inventory 必须为空，否则拒绝启动；货物须先经显式 Transfer 移出，不随拆解并入 Settlement Inventory（[12](12-inventory-and-storage.md#地点库存)，2026-10-01 设计讨论）。
- 返回原来的 Voxel 及其身份与 Runtime State（例如 Engine #812、Durability = 63、Behavior State），仍然是同一设备；符合 Stackability 时折叠 Stack，否则返回 ItemInstance。Deconstruction 不自动修复部件。
- Demo 使用 `DeconstructionWork = VoxelCount × BaseDeconstructionWorkPerVoxel`。
- Active Deconstruction Job 不允许主动取消，只允许暂停。
- 若目标 Voxel Layout / Identity Set 发生结构变化：Deconstruction Job fails → Progress 丢弃 → Storage reservation 释放 → Composite 保持事件后状态。普通 Durability 变化若未删除 Voxel，不令 Job 失败，最终返回最新状态。

### 失败与暂停汇总

- Demo 中 Construction / Refit / Deconstruction 均不提供主动 Cancel；失败结算不等于玩家取消。已明确的主动 Pause / Resume 仅覆盖 Deconstruction：暂停不累计 Work，保留 Progress、Storage Reservation 与 UnderDeconstruction 锁定，恢复后按当前执行条件继续。Construction / Refit 的主动暂停入口未纳入 Demo；资源或能力不足导致的进度保留不表示存在主动 Pause 命令。
- Construction：Power / Labor / Capability 不足时 Progress 保留、BOM 不返还、Reservation 保留。Site / placement validity 失效，或 Construction Facility destroyed / 失去 Job 执行资格时 Job fail、已投入 Goods 不返还、Progress 丢失、Reservation 释放。
- Refit：结构身份集合变化时 Job fail、已投入新增 / 替换 Goods 不返还、Storage reservation 释放、现有 Composite 保持真实事件后状态。
- Deconstruction：结构身份集合变化时 Job fail、Progress 丢失、Storage reservation 释放、Composite 保持事件后状态。
- Facility 存在 ActiveBatch（包括 Recovery Batch）、非空 OutputBuffer 或 ExtractionJob 时不允许直接进入 Refit / Deconstruction，见 [13](13-production-runtime-and-facilities.md)。

### 存档

ConstructionJob、RefitJob、DeconstructionJob、Spatial Reservations、Storage Reservations、selected ItemInstances / identity mapping、Deconstruction 主动暂停状态与 ProgressWork 都是权威 Runtime State，进入 Runtime Delta；完整清单见 [09](09-persistence.md)。

## Demo 范围

从 Layout 派生 BOM、精确 DefinitionId、Goods 实体化、耗时 Construction Job 与 Spatial Reservation、耗时 Refit Job、Delta BOM 与旧件返还的 Storage Reservation、Deconstruction Job 与 Storage Reservation、列车拆解前须清空 Train Inventory、UnderRefit / UnderDeconstruction 锁定、Snapshot 与失败语义、Deconstruction 主动 Pause / Resume（保留进度、预留与锁定）；玩家使用预置 Blueprint 建造车辆并对其 Refit。范围归属见 [10](10-resources-and-extraction.md)；世界编辑器与控制台 `composite` 改装命令的瞬时 Atomic Refit 继续保留（[03](03-composite-and-behavior.md)）。

## Demo 之后

完整的自由车辆设计器；Construction / Refit / Deconstruction 主动 Cancel / Refund；Construction / Refit 主动 Pause / Resume；Construction accident salvage；Composite 直接 Recovery；施工来源的 UI 指定；脚手架与施工动画等表现。

## 开放问题

- 地图迷雾（不立票）：未冻结参数：ConstructionWorkPerVoxel、RefitWorkPerChangedVoxel、DeconstructionWorkPerVoxel、Construction Equipment base capability。
- 地图迷雾（不立票）：Construction / Refit 的主动 Pause / Resume，以及三类 Job 的主动 Cancel / Refund 尚待后续设计；Demo 仅采用上文已明确的暂停与失败结算规则。

按[分期决议](https://github.com/odradekk/train/issues/145#issuecomment-5896477710)，资源生产不属于本阶段；相关实现切片被选入当前或下一切片、且需要明确决策才能推进时再立票。
