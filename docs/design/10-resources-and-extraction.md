# 10 资源源与开采

> 决策出处：资源转录 Q180–Q181、Q187、Q189、Q196–Q210、Q217–Q222、Q367–Q371、Q375、Q406–Q407、Q410、Q414、Q424（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)，其编号与旧转录在 Q180 重叠，引用须带前缀）。本文与 [11](11-goods-and-production-network.md)、[12](12-inventory-and-storage.md)、[13](13-production-runtime-and-facilities.md)、[14](14-construction-refit-and-deconstruction.md) 共同定义资源与生产系统；它建立在既有体素、Definition、Composite、Behavior、Capability、确定性 Simulation 与存档契约之上。代码与内容迁移由后续实现切片执行，不属于本阶段。

> 实现验证保留条款：10–14 的资源与生产子系统尚未经实现验证，首个实现切片可依据实际验证结果回写修订这些设计；修改须同步受影响文档并说明理由。本文中的“冻结”表示当前设计基线，不表示禁止基于实现证据重审。

## 边界

本文定义世界中的自然资源如何表示、开采、耗尽与更新：Resource Source 与 Raw Good 的语义、Extraction 与 Damage 的关系、Yield 计算、Extraction Facility 与 Extraction Job、Resource Renewal。Raw Goods 目录与生产网络见 [11](11-goods-and-production-network.md)；Facility Tag 与 Capability 分层、Work / Power / Labor、OutputBuffer 与 Job 通用规则见 [13](13-production-runtime-and-facilities.md)；聚居地资源种类的派生见 [05](05-world-generation-and-settlements.md)。市场、价格、人口与资源探勘的地图呈现不在本文。

资源与生产系统的定位：为贸易与玩家自建列车、聚居地打基础的宏观生产链，让玩家在直接购买之外拥有低价收购原料、自行生产与建设的策略空间，不是生产线优化玩法；长期目标是局部枯竭、整体可持续（资源转录 Q180、Q187）。

## 不变量

- Resource Source 与 Raw Good 严格分离：世界中的 Source Voxel 是可开采资源源，Raw Good 才是进入标准库存与贸易体系的货物。
- Resource Source 不拥有独立 Reserve 字段；CurrentDurability 同时表达该 Source Voxel 的剩余可开采量。
- Extraction 是显式 Gameplay 行为：通过正式 Damage Transaction 造成 Effective Damage 并产生 Raw Good；普通 Damage 不产生资源。
- Extraction 只产生 Raw Good。
- Yield 完全确定且为整数；Overkill 不产生额外 Goods。
- 矿床、森林资源区等 Cluster 不建立第二份储量事实。

## 规则

### Resource Source

- Resource Source 是世界中可以通过 Extraction 获取 Raw Good 的 Voxel。它仍是普通 VoxelDefinition，不增加独立的 ResourceSourceDefinition：以 `resource_source` Tag 做被动分类，以 ResourceSourceBehavior 描述实际 Yield 与 Extraction 语义。Tag 只回答“这是 Resource Source”，实际产出由 Behavior 定义。
- 这取代旧的“带 `natural_resource` 标签的 Voxel 自身就是货物”语义，不长期保留含糊兼容层；Definition 层与聚居地资源查询的对应修订见 [02](02-definitions-and-content.md)、[05](05-world-generation-and-settlements.md)。
- 一个 Source Voxel 可以产生多种 Raw Good，但多产物是内容设计上的例外，不是默认目标；首批内容默认单主产物。
- CurrentDurability ≤ 0 时按普通 Voxel 破坏规则销毁。

### Raw Good

- Raw Good 是通过 Extraction 或 Primary Production 获得、尚未经过有 Gameplay 意义的工业加工的标准货物，使用 `raw_good` Tag。
- `raw_good` 是内容语义，不表示图论上的根节点、Tier 0 或无 Goods 输入：Animal Products 消耗 Grain，仍属于 Raw Good。Demo 的 Raw Goods 目录与 Primary Production 见 [11](11-goods-and-production-network.md)。

### Durability 即剩余可采量

- Source 不增加 Reserve 字段。例如 Forest Source 的 MaxDurability = 500、CurrentDurability = 500，表示一个完整 Source Voxel 拥有标准 500 点可开采量。
- 同一种 Source Definition 的完整 Voxel 使用相同 MaxDurability。资源丰富度来自 Source 类型 × Source Voxel 数量 × 空间密度，而不是每个实例额外维护 Richness / Reserve。

### Extraction 与普通 Damage

```text
Extraction Command / Job → ResourceSourceBehavior → formal Damage Transaction → Effective Damage → deterministic Yield
Ordinary Damage → Damage Transaction → Durability decreases → no Raw Good yield
```

- Extraction 是 Damage 的一种显式 Gameplay 使用方式，但 Damage 不等于 Extraction：武器、事故等普通 Damage 只扣减 Durability。Damage Transaction 的结算规则见 [03](03-composite-and-behavior.md)。

### Effective Damage 与 Yield

- Yield 只按实际发生的 Effective Damage 计算：`EffectiveDamage = min(RequestedDamage, CurrentDurability)`。Overkill 不得产生额外 Goods。
- ResourceSourceBehavior 支持整数比率 `N Damage → M Goods`。一次 Extraction 使 Durability 从 `old` 变为 `new` 时，对某个 Yield：`Yield = floor(old × M / N) − floor(new × M / N)`。
- 该公式不需要小数 Goods，也不需要 Yield remainder state；不依赖 MaxDurability，因此内容更新 MaxDurability 不会重算过去的产出；同样的总 Effective Damage 分一次或多次发生，最终 Yield 一致。Source 同时产出多个 Raw Good 时，对每种 Yield 独立应用相同原则。
- 完整 Source 的产出完全确定，不使用逐次随机掉落。资源稀缺性通过 Source 类型、World Generation 分布、Source Cluster 大小、地理位置与可达性表达，而不是开采时重复掷随机数。

### Source 耗尽与 Cluster

- CurrentDurability ≤ 0 时 Source Voxel 按普通破坏规则消失。不保存 Exhausted Source 空壳，也不自动转化为另一个资源状态。若某种内容确实需要耗尽后留下专门地形，应作为明确的额外 Gameplay 行为设计，而不是 Resource Source 的通用规则。
- “矿床”“森林资源区”不建立新的 Runtime Entity，只是多个实际 Source Voxel 的空间 Cluster。总储量由实际 Source Voxels 的 CurrentDurability 与 Yield 派生，不建立 `DepositInstance.Reserve` 作为权威事实。Cluster 只可以作为 World Generation 概念、地图 / UI 聚类查询、AI 探勘结果。

### Extraction Facility

- Extraction 必须由真实 Facility 提供，不允许 Settlement 拥有凭空的 `MiningCapacity`：Extraction Facility Composite → Extraction Equipment → ExtractionBehavior / ExtractionCapability → Extraction Job。
- Demo 使用一个通用 Extraction Equipment，不提前拆分 Mining Drill、Logging Machine、Oil Pump。资源类型差异通过 Facility Tag 与 Source 要求表达：Mining Facility `facility_tags = [mining]`、Forestry Facility `[forestry]`、Oil Extraction Facility `[oil_extraction]`；通用 Extraction Equipment 只提供实际 Extraction Capability。Facility Tag 与 Capability 的分层见 [13](13-production-runtime-and-facilities.md)。
- 每个 Extraction Facility 只能选择其明确 Extraction Area / Range 内、类型兼容的 Source Voxel；范围形状与数值不冻结。
- `ExtractionJob.Target` 是精确的 WorldVoxelCoord，一次明确指向一个 Source Voxel。“自动开采整片区域”属于更高层 Player / AI Extraction Plan，不由底层 Extraction Runtime 自动承担。
- 一个 Extraction Capability 同时最多运行 1 个 Extraction Job；同一 Building 内不同 Capability 的 Job 可以并行并共同竞争 Power / Labor，见 [13](13-production-runtime-and-facilities.md)。

### Extraction Work 与输出

- Extraction 使用连续 Work → 离散 Damage：ExtractionJob { Target Source, ProgressWork, WorkPerDamage }。累计 Work 达到 WorkPerDamage 时 commit 1 Extraction Damage 并计算 Yield；Source Durability 保持整数。实际 Work 受 Capability、Power、Labor 共同限制，模型见 [13](13-production-runtime-and-facilities.md)。
- 输出复用 Facility-local OutputBuffer：Settlement Inventory 无空间时 Raw Goods 进入 Facility OutputBuffer，Buffer 清空前该 Facility 阻塞，规则见 [13](13-production-runtime-and-facilities.md)。

### Job 失败、暂停与内容更新

- Source destroyed / invalid 时 Job 结束。Power / Labor / Capability 不足时 Progress 保留、实际 Work 降低或为零。
- Extraction Job 不冻结整个 Yield Definition：已经发生的 Damage 不重算，下一次 Extraction Damage 使用当前 ResourceSourceBehavior。
- 若内容热更新使正在运行 Job 的 Source 不再是 Resource Source、不再具有 ResourceSourceBehavior、或与 Facility 不再兼容，则拒绝本次内容发布，不静默取消正在运行的 Job；这与 [02](02-definitions-and-content.md) 的候选内容校验规则一致。
- ExtractionJob 及其 ProgressWork 属于权威 Runtime State，必须存档，见 [09](09-persistence.md)。

### Resource Renewal

- 不可再生资源不在原地 Respawn。长期可持续通过 Resource Renewal Event：existing resource area depleted → Resource Renewal Event → select eligible undeveloped region → generate new Resource Source cluster。
- 新资源区的选择必须服从 WorldSeed 与稳定随机键的确定性规则（[04](04-deterministic-simulation.md)）。触发条件、触发间隔、新 Cluster 大小、eligible / undeveloped region 的精确定义不冻结。

### 首批 Resource Source

| Resource Source | 主产物 |
| --- | --- |
| Ferrous Ore Deposit | Ferrous Ore |
| Conductive Ore Deposit | Conductive Ore |
| Rare Mineral Deposit | Rare Mineral |
| Stone Deposit | Stone |
| Silica Deposit | Silica |
| Coal Deposit | Coal |
| Oil Deposit | Crude Oil |
| Natural Forest | Timber |

Timber 同时可由 Forestry 这一 Primary Production 产出，见 [11](11-goods-and-production-network.md)。

## Demo 范围

资源与生产系统的 Demo 范围：`resource_source` 与 `raw_good` 语义、ResourceSourceBehavior、Durability-based deterministic Extraction、8 类基础 Resource Source、通用 Extraction Equipment 与 Extraction Capability、Extraction Job、Facility-local OutputBuffer 复用、Resource Renewal Event 的架构方向。本文冻结的是结构、语义与不变量，不是平衡数值。该系统不属于本阶段，实现顺序随后续切片确定。

## Demo 之后

耗尽后留下专门地形；自动开采整片区域的 Player / AI Extraction Plan；Mining Drill、Logging Machine、Oil Pump 等专用开采设备；逐实例 Richness / Reserve；资源探勘与地图信息呈现。

## 开放问题

- 地图迷雾（不立票）：未冻结参数：每个 Resource Source 的 MaxDurability、各 Source Yield 的 `N Damage → M Goods`、Source Cluster 大小和分布、Extraction Range / Area、WorkPerDamage、Extraction Equipment base capability、Resource Renewal Event 的触发条件、间隔和规模。
- 地图迷雾（不立票）：Extraction 连续产出如何对应 OutputBuffer 的“一个完成 Batch 剩余”尚未确定：需在首个 Extraction 实现切片明确一次输出提交的边界、同 Tick 多次 Damage 的处理及 Buffer 非空后的停止点，与 [13 的开放问题](13-production-runtime-and-facilities.md#开放问题) 同步；在此之前不将整个 Source 或整个 Job 默认视作一个 Batch。

按[分期决议](https://github.com/odradekk/train/issues/145#issuecomment-5896477710)，资源生产不属于本阶段；相关实现切片被选入当前或下一切片、且需要明确决策才能推进时再立票。[#78](https://github.com/odradekk/train/issues/78) 已随之延期；它仅验证开采伤害与 Yield，明确不含 Facility、Job 和 OutputBuffer，不受此处输出提交边界阻塞。
