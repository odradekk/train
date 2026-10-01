# 13 生产运行时与设施能力

> 决策出处：资源转录 Q182、Q190、Q224–Q228、Q280–Q281、Q284、Q295–Q299、Q301–Q335、Q343–Q346、Q372–Q374、Q408–Q413、Q418–Q419、Q423、Q426–Q428（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)）。资源与生产系统的总体边界见 [10](10-resources-and-extraction.md)。

## 边界

本文定义 Production Process 与 Recovery Process 的 Definition、Facility 的兼容性与 Capability 分层、Production Plan 与 ActiveBatch、Work / Power / Labor 等 Production Factors、共享资源 Resolver、Facility-local OutputBuffer、Recovery 的执行、Job 的失败与暂停、内容热更新对运行中 Job 的影响。生产网络的内容见 [11](11-goods-and-production-network.md)；Extraction Job 见 [10](10-resources-and-extraction.md)；库存见 [12](12-inventory-and-storage.md)；Construction / Refit / Deconstruction Job 见 [14](14-construction-refit-and-deconstruction.md)；Tick 流水线与 Resolver 的通用规则见 [04](04-deterministic-simulation.md)。人口如何换算 Labor、Settlement AI 与生产规划策略不在本文。

## 不变量

- Capability 必须来自真实 Behavior。Facility Tag 只负责兼容性，不能伪造 Production / Extraction / Construction / Storage / Power Capability。
- 共享资源竞争必须显式 Resolve；注册顺序、容器枚举顺序、线程完成顺序不得决定 Gameplay。
- Goods Quantity 永远是整数；Process 的 Goods Input 和 Output 也必须是整数。
- 所有长生命周期 Job、OutputBuffer、Reservation、Fuel remainder 等影响未来模拟的状态都必须持久化。
- 已启动事务的承诺不能被内容热更新重新解释：需要冻结的执行契约在 Job 启动时冻结；外部 Definition 身份仍按 DefinitionId / current-definition 规则解析。
- 所有 Job 严格遵守 Simulation Tick Barrier。
- 玩家与 NPC 聚居地使用同一套 ProcessDefinition、Facility 与 Capability 规则；差异只来自设施、产能与地域，NPC 聚居地不使用另一套简化公式凭空产出 Goods（资源转录 Q182）。

## 规则

### 术语

- **Production Process**：把 Goods 与 Production Factors 转换为 Goods 的正式内容 Definition。
- **Recovery Process**：把耐用 Goods 或 ItemInstance 有损转换为已有 Goods 的正式内容 Definition。二者使用不同的强类型 ID 和不同的 DAG，但复用同一套 Batch Runtime。
- **Production Factors**：不属于 Goods、不可库存的生产约束：Work Capacity、Power、Labor、Site / Environmental Requirements。
- **Facility**：承担 Production、Extraction、Construction 等功能的 Building Composite。职责分成两层：Composite Definition / Facility Tags 决定允许什么类型的 Process；内部真实 Voxel Behavior / Capability 决定当前拥有多少实际执行能力。

### Process Definition

- Process 是正式内容 Definition，使用强类型、namespaced ID：ProductionProcessDefinitionId、RecoveryProcessDefinitionId，例如 `core:ferrous-refining`、`core:mechanical-components`、`core:recover-power-equipment`。显示名称、文件路径不是身份。ID 与 Semantic Revision 规则沿用 [02](02-definitions-and-content.md)。
- ProductionProcessDefinition { ProcessDefinitionId, Inputs, Outputs, WorkRequired, PowerPerWork, LaborPerWork, RequiredFacilityTags, EnvironmentalRequirements }。所有 Goods Input / Output Quantity 为正整数。
- Process 可以多输入、多输出、跨所谓“加工层级”直接引用已有 Goods、与同一 Output 的其他显式替代 Process 并存。Process 不允许隐式 Tag 替代 Input，也不允许 `any metal` 之类自动配方替换；同一 Product 的两种路线必须是两个显式 ProcessDefinition。
- RecoveryProcessDefinition 使用单独 Definition 类型和 DAG，Runtime 复用 Production Batch Framework。Recovery 输入可以是 Stack Good 或指定 ItemInstance；Recovery 不从 Production Recipe 自动反推。内容覆盖范围见 [11](11-goods-and-production-network.md)。

### Facility 与 Capability 分层

- Settlement 不直接拥有 IndustrialCapacity、MiningCapacity、StorageCapacity 等无来源的抽象事实；能力来自实际 Building Composite 内的功能 Voxel。生产设施是聚居地拥有的 Building Composite，其产出进入该聚居地的 Settlement Inventory（资源转录 Q226）。
- 聚居地周边的 Resource Sources、当前 Production Capabilities 与当前 Goods Supply 是三份独立事实，分别由 Source Voxel、建筑内设备与 Settlement Inventory 派生，不互相推导：农业聚居地可以没有 Grain Source 却大量生产 Grain（资源转录 Q228，[05](05-world-generation-and-settlements.md)）。
- Building Composite Definition 保存 Facility compatibility tags，例如 metallurgy、assembly、textile、food_processing、agriculture、recovery、mining、forestry、oil_extraction。Tag 只表示“此 Facility 允许执行某类 Process”；实际执行还必须拥有来自真实 Voxel Behavior 的对应 Capability。
- 例如 Smelter `facility_tags = [metallurgy]`，其中的 Industrial Machinery 提供实际 ProductionCapability；设备全部损坏时 facility tag 仍是 metallurgy，但 ProductionCapability = 0。
- 首版通用能力设备：Industrial Machinery → ProductionCapability；Extraction Equipment → ExtractionCapability；Construction Equipment → ConstructionCapability；Storage Equipment → StorageCapability；Power Equipment → PowerCapability。Control Equipment 移入 [11 的 Future Product Families](11-goods-and-production-network.md#future-product-families)，首版不为其创建商品、配方或控制 Capability，待具体 Gameplay 确定并通过 Gameplay Difference Test 后再设计。Tag、Behavior、Capability 三层分离的通用规则见 [03](03-composite-and-behavior.md)。

### Environmental Requirements

- Site / Environmental Requirements 属于 Process，而不是硬编码在 Building 类型中。同一个 Agricultural Facility 可以兼容 Grain Farming 和 Produce Farming，但不同 Process 可以拥有不同环境要求。
- 运行时当前环境必须继续满足 Requirements。语义上是持续有效条件；实现可以通过 Construction、Site state change、Process start 等事件重算并缓存，不要求每 Tick 全地图扫描。

### Production Plan

- 底层 Production Plan 只表达 { ProcessDefinitionId, Enabled, Priority }。Enabled 时 Facility 持续尝试执行该 Process。
- 底层系统不负责自动判断最赚钱商品、自动维持目标库存、自动决定生产多少 Batch；这些属于未来 Player Policy / Settlement AI / Economy 层。
- 缺 Input 时 Enabled = true、ActiveBatch = none、WaitingForInputs；新 Input 到达后自动继续尝试。
- Plan 只保存稳定 ProcessDefinitionId。

### ActiveBatch 与 Batch Start

- ActiveBatch 是权威 Runtime State：{ ProcessDefinitionId, StartedFromRevision, FrozenExecutionSnapshot, ProgressWork, diagnostics / provenance as needed }。Goods 只在完整 Batch 上转换；Work 可以连续积累。
- Batch 启动必须 All-or-Nothing：检查全部 Inputs、Facility compatibility、当前执行资格，解决共享资源竞争，然后原子消费完整 Inputs 并创建 ActiveBatch。不允许半装料 Batch、缺一个 Input 时先扣其他 Input、或完成时才临时检查 Input。Inputs 一旦扣除，就已经进入在制品状态。
- 首版不提供 Production Batch 的主动 Cancel / Refund。

### Work 与 Production Capability

- Production 使用抽象 Work：`BatchCompletionTime = WorkRequired / actual executable Work rate`。实际 Work 受 Production Capability、Power、Labor 共同限制。
- 实际 Work Capacity 主要来自 Industrial Machinery → ProductionBehavior → ProductionCapability。首版线性叠加：N 台 Industrial Machinery → N × base capability。暂不加入邻接 Bonus、边际递减、规模经济、复杂布局效率。

### Power 与 Fuel

- Power 不是 Goods，不可库存。Settlement 使用统一 Power Pool：所有 active Power Equipment → Settlement Power Pool；不模拟内部 Cable、Substation、Grid topology。
- Power Equipment 消耗 Settlement Inventory 中的 Fuel。采用 demand-driven 发电：actual delivered Power → proportional FuelEnergy consumption；闲置 Power Equipment 不按额定功率白白消耗燃料。
- Fuel Inventory Quantity 仍是整数。Power Equipment 每次取出一个完整 Fuel unit，并保存设备 Runtime State `CurrentFuelEnergyRemaining : double`，实际发电持续扣减。该状态在 Pause、Save / Load、拆卸进入 ItemInstance、重新安装后都保留；设备被摧毁时丢失。它不是可库存 Power。

### Labor

- Labor 不属于 Goods，不可库存、不被正常生产“消耗”。Settlement 的外部人口系统未来提供 AvailableLaborCapacity；生产只是占用 AllocatedLabor，停止生产后释放。
- 人口死亡、迁移、增长等规则不在本文设计，本文只要求未来人口系统能够提供 Settlement Labor Capacity。

### Factor Intensity

- Process 使用 PowerPerWork、LaborPerWork；实际执行 Work 与 Factor 消耗成比例。
- Factor 不足时实际 Work 降低或为零，Progress 不损失。

### Shared Resource Resolver

- 多个 Facility 竞争同一库存 Input、Power、Labor、Storage Capacity 时，必须通过 Resolver：explicit Production Priority → stable deterministic fallback。
- Input 竞争按完整 Batch grant；Power / Labor 允许部分分配。
- 不得使用 Dictionary iteration order、System registration order、Thread completion order 作为 Gameplay priority；通用规则见 [04](04-deterministic-simulation.md)。

### 单 Facility 并行限制

- 此处 Capability 的并行名额按 Facility Composite 内的能力类型计算，不按每个设备 Voxel 分配；同类设备贡献合计执行能力，增加设备只提高产能，不增加 Job 名额。
- 首版：一个 Production Capability 同时最多 1 个 Production 或 Recovery Batch；一个 Extraction Capability 最多 1 个 Extraction Job；一个 Construction Capability 最多 1 个 Construction / Refit / Deconstruction Job。
- 同一 Building 同时拥有不同 Capability 时，可以并行运行不同 Capability 的 Job，并共同竞争 Power / Labor。
- 不允许在同一 Capability 内手工分配 40% Job A、30% Job B、30% Job C 之类的份额。

### Tick Barrier

所有 Job 严格遵守 Simulation Tick Barrier。例如本 Tick 清空 OutputBuffer 后，不允许同 Tick 继续启动下一 Batch；状态变化先 Commit，下一 Tick 才基于新的 committed state 继续评估。

### Output Buffer

- 每个生产 / 回收 / Extraction Facility 拥有自己的 OutputBuffer；不存在 Settlement-wide Production Buffer。
- Buffer 不拥有独立 Gameplay Capacity；对 Production / Recovery，只保存一个完成 Batch 的剩余输出。Extraction 的输出提交边界仍待与连续 Damage 语义对齐，见下文开放问题。Batch 完成时产生完整整数 Outputs，尽量移入 Settlement Inventory，剩余留在 Facility OutputBuffer。Buffer 非空时该 Facility 的 production / extraction 阻塞，其他 Facility 正常运行。
- OutputBuffer 中的 Goods 已经是真实完成 Goods，但不占 Settlement Inventory Capacity、不能参与普通 Settlement Inventory 消费、不能作为其他 Facility Input、不能参与未来市场交易，必须先进入 Settlement Inventory。
- 只有 Settlement Inventory 存在扣除有效 Storage Reservation 后的可用容量，OutputBuffer 才自动内部转入，转入数量不得超过该容量。UsedCapacity ≥ Capacity 时不转入；遵守 [12 的 Overcapacity 规则](12-inventory-and-storage.md#overcapacity)，Buffer 优先级不允许制造或扩大超容。Facility 即使生产设备暂时失效，只要 Facility Composite 仍存在，Buffer 仍可继续执行这种抽象内部物流。
- Storage Resolver 优先级：已完成 OutputBuffer 的转入优先于新的普通外部入库请求。多个 Buffer 竞争时按 Production Priority → stable Facility identity 排序。
- 多输出 Batch 只有部分 Output 能立即入库时，使用规范化 VoxelDefinitionId 的 Ordinal 顺序填充空间；这只是确定性 fallback，不表示经济优先级。
- Facility Composite 真正 destroyed 时 ActiveBatch 丢失、未完成 Inputs 不返还、OutputBuffer 中已完成 Goods 丢失。未来事故 / Salvage 系统可以扩展，但首版不自动实体化散落物，也不强制写入 Overcapacity。

### Recovery 执行

- Stack Good { DefinitionId, Quantity } 按完整标准状态执行 Recovery。ItemInstance 带 specific identity、CurrentDurability 与 Runtime State，Recovery 必须真正消费指定实例。
- 对 ItemInstance：`Recovered_i = floor(BaseRecoveryOutput_i × CurrentDurability / MaxDurability)`，每种 Output 独立计算，结果可以为 0；Recovery 不保存小数 remainder，也不随机补偿。
- 除明确参与 Recovery 计算的状态外，其余 Runtime State 在 Recovery 中销毁：Power Equipment 的 CurrentFuelEnergyRemaining 不会自动还原为部分 Fuel Good。
- Recovery 必须由带兼容 Facility Tag 的真实 Facility 通过 Batch Runtime 执行，占用其 Production Capability 并经 Work / Power / Labor 与 OutputBuffer；复用 Industrial Machinery，不新增 Recycling Machinery，也不是库存菜单中的瞬时分解按钮（资源转录 Q346）。
- Composite 不能直接 Recovery，必须先显式 Deconstruction（[14](14-construction-refit-and-deconstruction.md)）。

### Job 与 Facility 生命周期

- 存在 ActiveBatch、非空 OutputBuffer、ExtractionJob 或 RecoveryJob 时，Facility 不允许直接进入 Refit / Deconstruction。
- 持续 Production Plan 应先 Enabled = false → 当前 Batch 自然完成 → OutputBuffer 清空 → facility idle → 开始 Refit / Deconstruction。施工系统不隐式 Cancel 生产事务。

### 失败与暂停

- Power / Labor 不足：Progress 保留，ActualWork 降低或为零。
- Facility 仍存在但 Capability 暂时为 0：ActiveBatch、OutputBuffer、Production Plan 保留，修复后继续。
- Facility destroyed：ActiveBatch 丢失、Inputs 不返还、OutputBuffer Goods 丢失。
- Extraction 的对应规则见 [10](10-resources-and-extraction.md)，Construction / Refit / Deconstruction 见 [14](14-construction-refit-and-deconstruction.md)。

### 内容更新与热重载

- ProductionProcessDefinition 与 RecoveryProcessDefinition 使用稳定 ID 和 Semantic Revision。
- Batch 启动时冻结执行契约：ProcessDefinitionId、StartedFromRevision、Frozen Outputs、Frozen WorkRequired、Frozen factor intensities、ProgressWork；Inputs 已在启动时消费。当前 Batch 按启动时 snapshot 完成，下一 Batch 按 ProcessDefinitionId 解析当前 Definition 并使用当前规格。因此 Process 热更新不会出现“按旧 Inputs 启动、按新 Outputs 完成”。
- 若更新删除仍被 Plan / ActiveBatch 引用的必需 ProcessDefinitionId，拒绝整次内容发布，不静默关闭工厂；与 [02](02-definitions-and-content.md) 的候选内容校验一致。
- Job 不冻结执行能力：每 Tick 读取当前 Capability、Power、Labor、Facility operational state；设备受损、修复或内容规格更新可以自然改变执行速度。

### 存档

Production Plans、ActiveBatch、OutputBuffer、RecoveryJob、CurrentFuelEnergyRemaining、Job ProgressWork 与 priorities 都是权威 Runtime State，进入 Runtime Delta；完整清单与恢复要求见 [09](09-persistence.md)。

## 首版范围

ProductionProcessDefinition 与 RecoveryProcessDefinition 及稳定 ID、Facility Tags 与 Behavior / Capability 分层、Industrial / Extraction / Construction / Storage / Power 基础 Capability、Settlement-wide Power Pool 与 Labor Pool 接口、Production Plan 与 ActiveBatch、Production / Recovery Batch Runtime、Facility-local OutputBuffer、Shared Resource Resolver、Job 失败与暂停语义、运行中 Job 的热重载语义。范围归属见 [10](10-resources-and-extraction.md)。

## 明确延后

多 Production Slot、机器邻接加成、规模经济、复杂效率曲线、连续工艺故障、半成品腐坏、Production Batch 主动 Cancel / Refund、特殊 Process 的连续运行失败规则；Facility destroyed 后自动回收；Settlement AI / Production Planning；人口增长、Labor 换算、职业、工资、疾病与医疗系统；Control Equipment 的具体玩法。

## 开放问题

- 地图迷雾（不立票）：未冻结参数：WorkRequired、PowerPerWork、LaborPerWork、Industrial Machinery base capability、Construction Equipment base capability、FuelEnergyPerUnit、Power Equipment output capability、Primary Production Site 条件阈值、Facility Priority 默认值、Simulation 中各 Resolver 的具体调度参数。
- 地图迷雾（不立票）：Extraction 连续 Work → 离散 Damage 的输出提交边界尚未定义，不能直接套用 Production / Recovery 的“一个完成 Batch 剩余”；首个 Extraction 实现切片须明确同 Tick 多次 Damage 与 Buffer 阻塞的衔接，并同步 [10 的开放问题](10-resources-and-extraction.md#开放问题)。

按[分期决议](https://github.com/odradekk/train/issues/145#issuecomment-5896477710)，资源生产不属于本阶段；相关实现切片被选入当前或下一切片、且需要明确决策才能推进时再立票。[#78](https://github.com/odradekk/train/issues/78) 已随之延期；它仅验证开采伤害与 Yield，明确不含 Facility、Job 和 OutputBuffer，不受此处输出提交边界阻塞。
