# 10 资源与开采

> **所属层**：核心模拟层。

> 决策出处：节点资源的表示与分布、可再生与不可再生、开采改为生产流程、定义的位置、再生事件与原地恢复、储量耗尽，由[资源与开采改为节点属性](https://github.com/odradekk/train/issues/205)于 2026-10-05 确认。它取代了资源源体素、以伤害完成的开采、Cluster、耐久即剩余可采量和 ExtractionJob 等规则。资源与生产系统的定位、`raw_good` 的语义、开采需要真实设施和通用 Extraction Equipment，沿用资源转录 Q180–Q181、Q187、Q189、Q196–Q210、Q217–Q222（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)，其编号与旧转录在 Q180 重叠，引用须带前缀）。

> 实现验证保留条款：10–14 的资源与生产子系统可依据实际验证结果修订设计；修改须同步受影响文档并说明理由。本文中的“冻结”表示当前设计基线，不表示禁止基于实现证据重审。

## 边界

本文定义节点资源怎样表示、分布、开采、耗尽、恢复与再生。本文与 [11](11-goods-and-production-network.md)、[12](12-inventory-and-storage.md)、[13](13-production-runtime-and-facilities.md)、[14](14-construction-refit-and-deconstruction.md) 共同定义资源与生产系统。

相关内容见：
- 资源在世界生成中的位置：[05](05-world-graph-and-generation.md#资源)
- Raw Goods 目录与初级生产：[11](11-goods-and-production-network.md)
- 生产流程、Facility Tag 与 Capability、Batch、Resolver：[13](13-production-runtime-and-facilities.md)

市场、价格、人口与资源勘探不在本文。

资源与生产系统的定位：为贸易与玩家自建列车、聚居地打基础的宏观生产链，让玩家在直接购买之外拥有低价收购原料、自行生产与建设的策略空间，不是生产线优化玩法；长期目标是局部枯竭、整体可持续（资源转录 Q180、Q187）。

## 不变量

- 节点资源是某种初级货物在节点上的储量，直接引用该货物的体素定义，不另设资源定义。
- 只有开采流程减少储量并产出初级货物；储量只由原地恢复或再生事件增加。
- 储量与产出都是整数，开采结果完全确定，不使用逐次随机掉落。

## 规则

### 节点资源

- 节点资源由若干项“初级货物 + 储量”组成。储量是整数，单位与该货物相同。
- 任何节点都可以有资源，包括聚居地、生物聚落和空节点。开采要求节点上有聚居地，所以空节点和生物聚落上的资源，是玩家建立或攻取聚居地的理由。
- 资源在世界生成的第 5 步按节点的地貌概况生成，见 [05](05-world-graph-and-generation.md#资源)。
- 节点资源和储量对玩家完全可见。勘探放在 Demo 之后。
- 资源的种类直接是初级货物的体素定义标识，例如 `core:ferrous-ore`。不设资源源体素、`resource_source` 标签或 ResourceSourceBehavior。

### 可再生与不可再生

可再生性是资源种类的属性，写在资源生成配置中，不在代码里写死。

| 资源 | 主产物 | 可再生 |
| --- | --- | --- |
| Ferrous Ore | Ferrous Ore | 否 |
| Conductive Ore | Conductive Ore | 否 |
| Rare Mineral | Rare Mineral | 否 |
| Stone | Stone | 否 |
| Silica | Silica | 否 |
| Coal | Coal | 否 |
| Crude Oil | Crude Oil | 否 |
| Natural Forest | Timber | 是 |

- **不可再生**：储量采到 0 时，这一项从节点上删除，并发出“资源耗尽”事件。
- **可再生**：每项有一个上限（生成时的初始储量）和一个恢复速率。储量按速率逐步恢复，不超过上限。储量为 0 时这一项保留，继续恢复；开采快于恢复时储量会降到 0，此后开采量受恢复速率限制。
- Timber 只来自 Natural Forest，不再有 Forestry 这条初级生产。农牧业仍是受地貌约束的初级生产，不算节点资源，见 [11](11-goods-and-production-network.md#primary-production)。

### 再生事件

- 某种不可再生资源在全世界剩余的总储量，低于初始总量的一定比例时，触发一次再生事件，给一个节点新增一项该资源。
- 候选节点是地貌允许这种资源、当前没有这项资源的节点，占据者不限。从候选中用 WorldSeed、货物身份和事件序号构成的无状态带键随机选出，见 [04](04-deterministic-simulation.md#随机性)；储量按生成表决定。
- 再生事件发出领域事件，通知玩家。
- 世界图的结构不变，只改变节点资源，符合 [05](05-world-graph-and-generation.md) 的不变量。

### 开采

- 开采是普通的生产流程（Production Process），由 [13](13-production-runtime-and-facilities.md) 的 Batch 运行时执行。开采流程的输入包括本节点某种资源的储量，储量与产出 1:1，例如“消耗 10 单位 Ferrous Ore 储量 → 产出 10 Ferrous Ore”。
- 储量和库存输入一样，在 Batch 启动时扣除。同一节点上多个设施争用同一项储量时，由 Resolver 仲裁，见 [04](04-deterministic-simulation.md#resolver)。
- 开采必须由真实设施提供：带 `extraction` 标签的设施（全部开采流程共用这一个标签，见[Demo 内容与资产清单](../content/demo-catalog.md#流程与设施兼容标签)），加上提供 ExtractionCapability 的 Extraction Equipment。Demo 只有一种通用的 Extraction Equipment，不拆分钻机、伐木机、油泵。Facility Tag 只表达兼容性，见 [13](13-production-runtime-and-facilities.md)。
- 开采流程是种类为 `extraction` 的生产流程，例如 `core:ferrous-mining`：用 `extracts` 写每批扣除的储量，产出隐含为同一种货物、同样数量，使用 Extraction Capability。格式见[流程的格式](../content/process.md)，ID 与 Revision 规则见 [02](02-definitions-and-content.md)（[生产与供能：Demo 的内容参数和运行契约](https://github.com/odradekk/train/issues/282)）。
- 储量耗尽后，节点上的开采设施保留，流程因缺少输入而等待，与缺料相同。设施不会自动拆除，也不会自动改用其他流程。
- NPC 聚居地开采时同样消耗节点储量。NPC 聚居地是否运行完整的生产模拟，见 [16](16-settlements.md)。
- 输出、OutputBuffer、失败与暂停、热更新都按 [13](13-production-runtime-and-facilities.md) 的 Batch 规则处理，开采没有单独的规则。

### Raw Good

- Raw Good 是通过开采或初级生产获得、尚未经过有玩法意义的工业加工的标准货物，使用 `raw_good` 标签。
- `raw_good` 是内容语义，不表示图论上的根节点、Tier 0 或无货物输入：Animal Products 消耗 Grain，仍属于 Raw Good。Demo 的 Raw Goods 目录与初级生产见 [11](11-goods-and-production-network.md)。

### 代码迁移

重构前的代码曾用过渡格式 `settlements/*.settlements.json` 直接声明聚居地可获得的初级货物种类；该格式已随代码重构删除。初级货物种类从节点资源派生。

## Demo 范围

节点资源与可见储量；七种不可再生资源与 Natural Forest 一种可再生资源；以生产流程完成的开采；通用 Extraction Equipment；再生事件与原地恢复；储量耗尽。本文冻结的是结构、语义与不变量，不是平衡数值。

## Demo 之后

资源勘探与地图信息呈现；专用开采设备；更多可再生资源；耗尽后改变节点地貌。

## 开放问题

- 地图迷雾（不立票）：未冻结参数：各地貌生成哪些资源、储量范围，开采流程的 WorkRequired，Extraction Equipment 的基础能力，可再生资源的恢复速率，再生事件的检查周期、触发比例与新储量。
