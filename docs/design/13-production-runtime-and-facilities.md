# 13 生产运行时与设施能力

> **所属层**：核心模拟层。


> 决策出处：资源转录 Q182、Q190、Q224–Q228、Q280–Q281、Q284、Q295–Q299、Q301–Q335、Q343–Q346、Q372–Q374、Q408–Q413、Q418–Q419、Q423、Q426–Q428（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)）。资源与生产系统的总体边界见 [10](10-resources-and-extraction.md)。删除 ItemInstance 回收、剩余燃料改由聚居地电力池共用，由[货物与内容定义是否继续绑定体素定义](https://github.com/odradekk/train/issues/204)于 2026-10-05 确认。流程的三种类型与格式、设施兼容标签的匹配、环境要求、每种能力一份计划、单位与联合分配、燃料、建筑锁定期间的能力、对外可见的运行状态、备货用的潜在消耗和内容更新规则，由[生产与供能：Demo 的内容参数和运行契约](https://github.com/odradekk/train/issues/282)于 2026-10-10 确认。

## 边界

本文定义 Production Process 与 Recovery Process 的 Definition、Facility 的兼容性与 Capability 分层、Production Plan 与 ActiveBatch、Work / Power / Labor 等 Production Factors、共享资源 Resolver、Facility-local OutputBuffer、Recovery 的执行、Job 的失败与暂停、内容热更新对运行中 Job 的影响。生产网络的内容见 [11](11-goods-and-production-network.md)；节点资源与开采见 [10](10-resources-and-extraction.md)；库存见 [12](12-inventory-and-storage.md)；Construction / Refit / Deconstruction Job 见 [14](14-construction-refit-and-deconstruction.md)；Tick 流水线与 Resolver 的通用规则见 [04](04-deterministic-simulation.md)。人口与劳动力供给见 [18](18-population-and-labor.md)；Settlement AI 与生产规划策略不在本文。

## 不变量

- Capability 必须来自真实 Behavior。Facility Tag 只负责兼容性，不能伪造 Production / Extraction / Construction / Storage / Power Capability。
- 共享资源竞争必须显式 Resolve；注册顺序、容器枚举顺序、线程完成顺序不得决定 Gameplay。
- Goods Quantity 永远是整数；Process 的 Goods Input 和 Output 也必须是整数。
- 所有长生命周期 Job、OutputBuffer、Reservation、Fuel remainder 等影响未来模拟的状态都必须持久化。
- 已启动事务的承诺不能被内容热更新重新解释：需要冻结的执行契约在 Job 启动时冻结；外部 Definition 身份仍按 DefinitionId / current-definition 规则解析。
- 所有 Job 严格遵守 Simulation Tick Barrier。
- 玩家与 NPC 聚居地使用同一套 ProcessDefinition、Facility 与 Capability 规则；差异只来自设施、产能与地域，NPC 聚居地不使用另一套简化公式凭空产出 Goods（资源转录 Q182）。这条规则覆盖生产与库存；Demo 中 NPC 聚居地不自行建造，见 [16](16-settlements.md#npc-聚居地)（[聚居地的经营、建设与场景](https://github.com/odradekk/train/issues/206)，2026-10-05）。

## 规则

### 术语

- **Production Process**：把 Goods 与 Production Factors 转换为 Goods 的正式内容 Definition。
- **Recovery Process**：把耐用货物有损转换为已有货物 的正式内容 Definition。二者使用不同的强类型 ID 和不同的 DAG，但复用同一套 Batch Runtime。
- **Production Factors**：不属于 Goods、不可库存的生产约束：Work Capacity、Power、Labor、Site / Environmental Requirements。
- **Facility**：承担 Production、Extraction、Construction 等功能的建筑。职责分成两层：建筑蓝图的 Facility Tags 决定允许什么类型的 Process；其中完好部件的 Behavior / Capability 决定当前拥有多少实际执行能力。

### Process Definition

- Process 是正式内容 Definition，使用强类型、namespaced ID：ProductionProcessDefinitionId、RecoveryProcessDefinitionId，例如 `core:ferrous-refining`、`core:mechanical-components`、`core:recover-power-equipment`。显示名称、文件路径不是身份。ID 与 Semantic Revision 规则沿用 [02](02-definitions-and-content.md)。
- 流程分三种：`production`（加工与初级生产）、`extraction`（开采）、`recovery`（回收）。前两种使用 ProductionProcessDefinitionId，回收使用 RecoveryProcessDefinitionId。使用哪种能力由种类决定：生产与回收用 Production Capability，开采用 Extraction Capability。
- 流程的字段：种类、Inputs、Outputs、WorkRequired、PowerPerWork、LaborPerWork、一个 RequiredFacilityTag、可选的 EnvironmentalRequirements。开采流程不写 Inputs、Outputs，改写一项储量输入 `extracts`，产出隐含为同一种货物、同样数量，见 [10](10-resources-and-extraction.md#开采)。所有 Goods Input / Output Quantity 为正整数；Demo 的流程只处理体素货物。字段写法见[流程的格式](../content/process.md)。
- Process 可以多输入、多输出、跨所谓“加工层级”直接引用已有 Goods、与同一 Output 的其他显式替代 Process 并存。Process 不允许隐式 Tag 替代 Input，也不允许 `any metal` 之类自动配方替换；同一 Product 的两种路线必须是两个显式 ProcessDefinition。
- RecoveryProcessDefinition 使用单独 Definition 类型和 DAG，Runtime 复用 Production Batch Framework。Recovery 输入是库存中的标准货物；Recovery 不从 Production Recipe 自动反推。内容覆盖范围见 [11](11-goods-and-production-network.md)。
- Production Plan 与 ActiveBatch 通过带类型的 ProcessRef 引用 Process：它指向一个 ProductionProcessDefinitionId 或一个 RecoveryProcessDefinitionId，两类 ID 仍各自强类型，不合并为无类型的 ProcessDefinitionId（2026-10-01 设计讨论）。

### Facility 与 Capability 分层

- Settlement 不直接拥有 IndustrialCapacity、MiningCapacity、StorageCapacity 等无来源的抽象事实；能力来自建筑设计属性中完好部件提供的能力，见 [03 的设计属性](03-vehicles-buildings-and-designs.md#设计属性)。生产设施是聚居地拥有的 Building Composite，其产出进入该聚居地的 Settlement Inventory（资源转录 Q226）。
- 节点资源、当前 Production Capabilities 与当前 Goods Supply 是三份独立事实，分别由节点、建筑内设备与 Settlement Inventory 提供，不互相推导：农业聚居地没有任何节点资源也可以大量生产 Grain（资源转录 Q228，[05](05-world-graph-and-generation.md)）。
- 建筑蓝图保存 Facility compatibility tags，例如 metallurgy、assembly、textile、food_processing、agriculture、recovery、mining、forestry、oil_extraction。Tag 只表示“此 Facility 允许执行某类 Process”；实际执行还必须拥有来自部件 Behavior 的对应 Capability。
- 每个流程恰好要求一个 Tag；建筑可以有多个 Tag，包含流程要求的 Tag 即兼容。Tag 是由代码提供的封闭清单，Demo 的完整清单由[Demo 内容与资产清单](https://github.com/odradekk/train/issues/283)确定。
- 例如 Smelter `facility_tags = [metallurgy]`，其中的 Industrial Machinery 提供实际 ProductionCapability；设备全部损坏时 facility tag 仍是 metallurgy，但 ProductionCapability = 0。
- Demo 的 Facility 通用能力设备：Industrial Machinery → ProductionCapability；Extraction Equipment → ExtractionCapability；Construction Equipment → ConstructionCapability；Storage Equipment → StorageCapability（任何车辆或建筑中完好的、带 `storage` 行为的部件都提供存储能力，不限于仓库建筑）；Power Equipment → PowerCapability。Propulsion Equipment 与 Control Equipment 也是 Demo 功能设备，但提供的是车辆的动力与控制能力（见 [03](03-vehicles-buildings-and-designs.md)），不是 Facility 能力；车头制造需要控制能力，因此 2026-10-01 设计讨论撤销了 [#85](https://github.com/odradekk/train/issues/85) 将 Control Equipment 移出货物目录的修订，目录见 [11](11-goods-and-production-network.md)。Tag、Behavior、Capability 三层分离的通用规则见 [03](03-vehicles-buildings-and-designs.md)。

### Environmental Requirements

- Site / Environmental Requirements 属于 Process，而不是硬编码在 Building 类型中。同一个 Agricultural Facility 可以兼容 Grain Farming 和 Produce Farming，但不同 Process 可以拥有不同环境要求。
- 环境要求是对聚居地所在节点地貌概况的条件，例如 Grain Farming 要求平原。Demo 只支持“允许的地貌类型”一项，起伏程度、地形特征等到有流程需要时再加。
- 地貌在生成后固定（[05](05-world-graph-and-generation.md)，[资源与开采改为节点属性](https://github.com/odradekk/train/issues/205)），但内容更新可能改变流程的要求，所以每次 Batch 启动时检查；设置或启用 Plan 时只用于提示。建造建筑时不检查，因为建筑本身不绑定流程。
- 开采流程另外要求本节点有对应资源的储量，并在 Batch 启动时扣除储量，见 [10](10-resources-and-extraction.md#开采)。

### Production Plan

- 底层 Production Plan 只表达 { ProcessRef, Enabled, Priority }。Enabled 时 Facility 持续尝试执行该 Process。Stack Good 的 Recovery 同样可以作为持续 Plan。
- 一个设施的每种能力（Production、Extraction）至多一份 Plan；更换流程就是修改这份 Plan。Priority 是这份 Plan 在争用库存 Input、Power、Labor 与入库时的排序。
- 底层系统不负责自动判断最赚钱商品、自动维持目标库存、自动决定生产多少 Batch；这些属于未来 Player Policy / Settlement AI / Economy 层。
- 缺 Input 时 Enabled = true、ActiveBatch = none、WaitingForInputs；新 Input 到达后自动继续尝试。
- Plan 只保存稳定的 ProcessRef。

### ActiveBatch 与 Batch Start

- ActiveBatch 是权威 Runtime State：{ ProcessRef, StartedFromRevision, FrozenExecutionSnapshot, ProgressWork, diagnostics / provenance as needed }。Goods 只在完整 Batch 上转换；Work 可以连续积累。
- Batch 启动必须 All-or-Nothing：检查全部 Inputs、Facility compatibility、当前执行资格，解决共享资源竞争，然后原子消费完整 Inputs 并创建 ActiveBatch。不允许半装料 Batch、缺一个 Input 时先扣其他 Input、或完成时才临时检查 Input。Inputs 一旦扣除，就已经进入在制品状态。
- Demo 不提供 Production Batch 的主动 Cancel / Refund。

### Work 与 Production Capability

- Production 使用抽象 Work：`BatchCompletionTime = WorkRequired / actual executable Work rate`。实际 Work 受 Production Capability、Power、Labor 共同限制。
- 实际 Work Capacity 主要来自 Industrial Machinery → ProductionBehavior → ProductionCapability。Demo 线性叠加：N 台完好的 Industrial Machinery → N × base capability，被毁的部件不计入。暂不加入邻接 Bonus、边际递减、规模经济、复杂布局效率。

### Power 与 Fuel

- Power 不是 Goods，不可库存。Settlement 使用统一 Power Pool：所有 active Power Equipment → Settlement Power Pool；不模拟内部 Cable、Substation、Grid topology。
- Power Equipment 消耗 Settlement Inventory 中的 Fuel。采用 demand-driven 发电：actual delivered Power → proportional FuelEnergy consumption；闲置 Power Equipment 不按额定功率白白消耗燃料。
- Fuel Inventory Quantity 仍是整数。聚居地电力池保存一份 `CurrentFuelEnergyRemaining : double`：剩余能量不足时从 Settlement Inventory 取出一个完整 Fuel unit，实际发电持续扣减。它属于电力池，不属于单台 Power Equipment，设备拆下或被毁都不影响它，随存档保存。它不是可库存的 Power。
- Fuel 是体素定义 `simulation` 中写有 `fuel_energy`（每单位电量）的货物，Demo 只有 Fuel 一种。因为余量属于电力池，任何 Power Equipment 都可以使用任何燃料；多种燃料并存时按规范化 VoxelDefinitionId 的 Ordinal 顺序取用。
- 聚居地的发电能力是完好 Power Equipment 的 `output` 之和。可用功率同时受燃料余量和库存燃料能提供的电量限制。
- Demo 中列车不消耗燃料，燃料只由聚居地电力池消耗。

### Labor

- Labor 不属于 Goods，不可库存、不被正常生产“消耗”。核心的人口模拟提供 AvailableLaborCapacity；生产只是占用 AllocatedLabor，停止生产后释放。
- 人口换算、短缺反馈与人口规则缺失时的行为统一见 [18 的劳动力规则](18-population-and-labor.md#劳动力与玩家操作)及[内容缺失规则](18-population-and-labor.md#内容缺失与更新)，不是由 Godot 或内容层计算的外部供给。人口增长与 Labor 换算已经纳入 Demo。

### Factor Intensity

- Process 使用 PowerPerWork、LaborPerWork；实际执行 Work 与 Factor 消耗成比例。
- Factor 不足时实际 Work 降低或为零，Progress 不损失。

### 单位与实际速率

| 量 | 单位 | 来源 |
| --- | --- | --- |
| 工作量 | 抽象单位 | 流程的 `work_required`；施工任务的工作量见 [14](14-construction-refit-and-deconstruction.md#construction-capability-与-work) |
| 工作速率 | 工作量/秒 | `production`、`extraction`、`construction` 行为的 `work_rate`，同类完好部件相加 |
| 电量 | 抽象单位 | 燃料的 `fuel_energy`（电量/燃料单位）；电力池的燃料能量余量 |
| 功率 | 电量/秒 | `power` 行为的 `output` |
| PowerPerWork | 电量/工作量 | 流程的 `power_per_work`；施工任务的系数见 [14](14-construction-refit-and-deconstruction.md#construction-capability-与-work) |
| 劳动力 | 与 [18](18-population-and-labor.md#劳动力与玩家操作) 的可用劳动力相同 | 人口与劳动比例 |
| LaborPerWork | 劳动力/(工作量/秒) | 流程的 `labor_per_work`：以 1 工作量/秒运行时占用的劳动力 |

- 实际工作速率 = min（能力速率，分到的功率 ÷ PowerPerWork，分到的劳动力 ÷ LaborPerWork）；系数为 0 的一项不构成限制。每 Tick 推进“实际工作速率 × Tick 时长”的工作量。
- 运行中的任务（生产、开采、回收的 ActiveBatch 与 [14](14-construction-refit-and-deconstruction.md) 的施工任务）按 Priority，再按稳定的设施身份和能力种类排序，逐个联合分配：按该任务能达到的速率同时扣除功率和劳动力，不让任务占着劳动力却因缺电用不上。施工任务同样带 Priority。

### Shared Resource Resolver

- 多个 Facility 竞争同一库存 Input、Power、Labor、Storage Capacity 时，必须通过 Resolver：explicit Production Priority → stable deterministic fallback。
- Input 竞争按完整 Batch grant；Power / Labor 允许部分分配。
- 不得使用 Dictionary iteration order、System registration order、Thread completion order 作为 Gameplay priority；通用规则见 [04](04-deterministic-simulation.md)。
- 居民与设施在同一步竞争库存时，按 [18 的消费仲裁顺序](18-population-and-labor.md#同步竞争的顺序)执行；这不允许人口系统取回已投入批次的原料，也不改变设施间的既有排序。

### 单 Facility 并行限制

- 此处 Capability 的并行名额按设施建筑内的能力类型计算，不按每个设备部件分配；同类设备贡献合计执行能力，增加设备只提高产能，不增加 Job 名额。
- Demo：一个 Production Capability 同时最多 1 个 Production 或 Recovery Batch；一个 Extraction Capability 最多 1 个开采 Batch；一个 Construction Capability 最多 1 个 Construction / Refit / Repair / Deconstruction Job。
- 同一 Building 同时拥有不同 Capability 时，可以并行运行不同 Capability 的 Job，并共同竞争 Power / Labor。
- 不允许在同一 Capability 内手工分配 40% Job A、30% Job B、30% Job C 之类的份额。

### Tick Barrier

所有 Job 严格遵守 Simulation Tick Barrier。例如本 Tick 清空 OutputBuffer 后，不允许同 Tick 继续启动下一 Batch；状态变化先 Commit，下一 Tick 才基于新的 committed state 继续评估。

### Output Buffer

- 每个生产、回收、开采设施拥有自己的 OutputBuffer；不存在 Settlement-wide Production Buffer。
- Buffer 不拥有独立 Gameplay Capacity；只保存一个完成 Batch 的剩余输出。Batch 完成时产生完整整数 Outputs，尽量移入 Settlement Inventory，剩余留在 Facility OutputBuffer。Buffer 非空时该 Facility 的 production / extraction 阻塞，其他 Facility 正常运行。
- OutputBuffer 中的 Goods 已经是真实完成 Goods，但不占 Settlement Inventory Capacity、不能参与普通 Settlement Inventory 消费、不能作为其他 Facility Input、不能参与未来市场交易，必须先进入 Settlement Inventory。
- 只有 Settlement Inventory 存在扣除有效 Storage Reservation 后的可用容量，OutputBuffer 才自动内部转入，转入数量不得超过该容量。UsedCapacity ≥ Capacity 时不转入；遵守 [12 的 Overcapacity 规则](12-inventory-and-storage.md#overcapacity)，Buffer 优先级不允许制造或扩大超容。Facility 即使生产设备暂时失效，只要 Facility Composite 仍存在，Buffer 仍可继续执行这种抽象内部物流。
- Storage Resolver 优先级：已完成 OutputBuffer 的转入优先于新的普通外部入库请求。多个 Buffer 竞争时按 Production Priority → stable Facility identity 排序。
- 多输出 Batch 只有部分 Output 能立即入库时，使用规范化 VoxelDefinitionId 的 Ordinal 顺序填充空间；这只是确定性 fallback，不表示经济优先级。
- 设施建筑被摧毁时 ActiveBatch 丢失、未完成 Inputs 不返还、OutputBuffer 中已完成 Goods 丢失。未来事故 / Salvage 系统可以扩展，但 Demo 不自动实体化散落物，也不强制写入 Overcapacity。

### Recovery 执行

- Recovery 只处理库存中的标准货物 Stack { DefinitionId, Quantity }，按完整标准状态执行；库存中没有受损或带状态的件，见 [12](12-inventory-and-storage.md#只有-stack)。
- Recovery 总是以 ActiveBatch 运行，可以作为持续 Plan。
- Recovery 必须由带兼容 Facility Tag 的真实 Facility 通过 Batch Runtime 执行，占用其 Production Capability 并经 Work / Power / Labor 与 OutputBuffer；复用 Industrial Machinery，不新增 Recycling Machinery，也不是库存菜单中的瞬时分解按钮（资源转录 Q346）。
- 车辆和建筑不能直接 Recovery，必须先显式拆解（[14](14-construction-refit-and-deconstruction.md)）。Demo 中 Recovery 只处理体素货物（按体素定义标识计数的 Stack）；库存中的大型设备不能回收，拆解入库的大型设备留到 Demo 之后。

### Job 与 Facility 生命周期

- 存在 ActiveBatch（包括 Recovery Batch）或非空 OutputBuffer 时，Facility 不允许直接进入 Refit / Deconstruction。
- 持续 Production Plan 应先 Enabled = false → 当前 Batch 自然完成 → OutputBuffer 清空 → facility idle → 开始 Refit / Deconstruction。施工系统不隐式 Cancel 生产事务。
- 建筑处于 UnderRefit、UnderRepair、UnderDeconstruction 时，不提供 Production、Extraction、Construction 与 Power Capability，其 Plan 处于“锁定”状态；Storage Capability 保留，避免库存无故超容。设施不能用自己的 Construction Capability 改装、修理或拆解自己。

### 失败与暂停

- Power / Labor 不足：Progress 保留，ActualWork 降低或为零。
- Facility 仍存在但 Capability 暂时为 0：ActiveBatch、OutputBuffer、Production Plan 保留，修复后继续。
- Facility destroyed：ActiveBatch 丢失、Inputs 不返还、OutputBuffer Goods 丢失。
- 开采按本文的 Batch 规则执行，见 [10](10-resources-and-extraction.md)；Construction / Refit / Deconstruction 见 [14](14-construction-refit-and-deconstruction.md)。

### 对外可见的运行状态

界面、市场备货和测试使用同一套状态，由核心给出，不由界面或其他模块各自推断。每个设施每种能力上的 Plan，任何时刻只处于下列一种状态：

| 状态 | 含义 | 计入备货 |
| --- | --- | --- |
| 停用 | 没有 Plan，或 Plan 未启用 | 否 |
| 运行中 | 附实际速率、能力速率和限制因素（能力、电力或劳动力） | 是 |
| 等待原料 | 缺少完整一批的 Input；开采表示储量不足 | 是 |
| 出料阻塞 | OutputBuffer 非空 | 是 |
| 能力为零 | 对应的设备全部被毁 | 否 |
| 不满足条件 | 设施的 Tag 或节点地貌不满足当前流程定义 | 否 |
| 锁定 | 建筑处于改装、修理或拆解中 | 否 |
| 流程缺失 | 读档时缺少引用的流程定义 | 否 |

其他对外可见的信息：

- **批次**：流程、StartedFromRevision、进度（ProgressWork ÷ 冻结的 WorkRequired）、冻结的产出，以及当前定义是否已与冻结的规格不同。
- **库存**：容量、已用、已预留、可用，可用 = 容量 − 已用 − 已预留。每项预留对应一个改装或拆解任务，列出预计返还的货物，见 [14](14-construction-refit-and-deconstruction.md)。生产没有预留，Input 在 Batch 启动时就扣除。
- **电力池**：发电能力、需求功率、实际供电和燃料能量余量。
- **节点资源**：每项的货物、储量和是否可再生；可再生的还有上限和恢复速率。耗尽和再生事件见 [10](10-resources-and-extraction.md)。

状态、事件和查询的具体类型由[核心模拟层公开契约](https://github.com/odradekk/train/issues/287)确定。

### 备货用的潜在消耗

[19 的补货目标](19-markets-and-trading.md#补货目标与出售保留量)需要生产在正常供料下的潜在消耗。它由核心计算，市场和界面读取同一结果：

- 只计入状态为运行中、等待原料或出料阻塞的 Plan。施工任务不计入。
- 潜在速率按上文同一套联合分配计算，并假设：计入的 Plan 都在运行，Input 与燃料齐全，出料不阻塞。功率取完好 Power Equipment 的 `output` 之和，劳动力取当前可用劳动力。
- 换算：
  - 每秒批次数 = 潜在速率 ÷ WorkRequired；
  - 每种 Input 的需求速率 = 每秒批次数 × 每批数量；开采没有货物 Input；
  - 燃料的需求速率 = Σ（潜在速率 × PowerPerWork）÷ 所用燃料的 `fuel_energy`。
- 目标 = 需求速率 × 备货时长。每个 Plan 至少能凑出一个完整批次的 Input 与燃料（一批燃料 = WorkRequired × PowerPerWork ÷ `fuel_energy`），按优先级先凑。燃料需求扣除电力池的燃料能量余量；已投入批次的 Input 不再计入；未完成的产物不抵扣。
- 短时缺 Input、缺燃料、出料阻塞不减少需求；设备受损、发电能力不足、人口少等长期限制会减少需求。
- 容量预算见 19；取整与整数分配由[核心模拟层公开契约](https://github.com/odradekk/train/issues/287)确定。

### 内容更新与热重载

- ProductionProcessDefinition 与 RecoveryProcessDefinition 使用稳定 ID 和 Semantic Revision。
- Batch 启动时冻结执行契约：ProcessRef、StartedFromRevision、Frozen Outputs、Frozen WorkRequired、Frozen factor intensities、ProgressWork；Inputs 已在启动时消费。当前 Batch 按启动时 snapshot 完成，下一 Batch 按 ProcessRef 解析当前 Definition 并使用当前规格。因此 Process 热更新不会出现“按旧 Inputs 启动、按新 Outputs 完成”。
- 若更新删除仍被 Plan / ActiveBatch 的 ProcessRef 引用的 Process Definition，拒绝整次内容发布，不静默关闭工厂；与 [02](02-definitions-and-content.md) 的候选内容校验一致。改动流程的种类视为删除旧流程、新建流程，同样适用这条规则。
- 更新后流程要求的 Tag 或环境不再被当前设施满足时，不拒绝发布：当前 Batch 按冻结的规格完成，Plan 保留，状态变为“不满足条件”，停止对应的备货。
- 燃料的 `fuel_energy` 改变或被删除时，电力池的余量以电量计，不受影响；之后取用燃料按新值计算，不再是燃料的货物不再被取用。
- Job 不冻结执行能力：每 Tick 读取当前 Capability、Power、Labor、Facility operational state；设备受损、修复或内容规格更新可以自然改变执行速度。

### 存档

Production Plans、ActiveBatch、OutputBuffer、聚居地电力池的 CurrentFuelEnergyRemaining、Job ProgressWork 与 priorities 都是核心状态，随存档保存；完整清单与恢复要求见 [09](09-persistence.md#资源与生产状态恢复)。

## Demo 范围

ProductionProcessDefinition 与 RecoveryProcessDefinition 及稳定 ID、Facility Tags 与 Behavior / Capability 分层、Industrial / Extraction / Construction / Storage / Power 基础 Capability、Settlement-wide Power Pool 与 Labor Pool 接口、Production Plan 与 ActiveBatch、Production / Recovery Batch Runtime、Facility-local OutputBuffer、Shared Resource Resolver、Job 失败与暂停语义、运行中 Job 的热重载语义。范围归属见 [10](10-resources-and-extraction.md)。

## Demo 之后

多 Production Slot、拆解入库的大型设备、机器邻接加成、规模经济、复杂效率曲线、连续工艺故障、半成品腐坏、Production Batch 主动 Cancel / Refund、特殊 Process 的连续运行失败规则；Facility destroyed 后自动回收；Settlement AI / Production Planning；职业、工资、疾病与医疗系统。

## 开放问题

- 地图迷雾（不立票）：未冻结的数值：各流程的 WorkRequired、PowerPerWork、LaborPerWork，各设备的 `work_rate` 与 `output`，Fuel 的 `fuel_energy`，Primary Production 的地貌条件，Plan 与施工任务 Priority 的默认值。它们由 [Demo 内容与资产清单](https://github.com/odradekk/train/issues/283)配置，再由实施与试玩配平。

资源与生产属于当前完整 Demo，范围与验收统一见 [00](00-product-and-core-loop.md#本阶段范围)。人口与劳动力的规则已确定，见 [18](18-population-and-labor.md)；生产与供能的内容参数和运行规则由[生产与供能：Demo 的内容参数和运行契约](https://github.com/odradekk/train/issues/282)确定，公开类型与查询由[核心模拟层公开契约](https://github.com/odradekk/train/issues/287)和[内容层公开契约](https://github.com/odradekk/train/issues/288)交付。
