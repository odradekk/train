# 11 货物与生产网络

> **所属层**：核心模拟层。
>
> **修订中**：不变量“所有标准货物都是 VoxelDefinition”由[货物与内容定义是否继续绑定体素定义](https://github.com/odradekk/train/issues/204)改写；“Raw Goods”“Primary Production”中的资源源与 Site 约束由[资源与开采改为节点属性](https://github.com/odradekk/train/issues/205)改写；“Future Product Families”中的武器已进入 Demo（[00](00-product-and-core-loop.md#demo-范围)），归[地图](https://github.com/odradekk/train/issues/193)迷雾“战斗、征服与世界生物”。货物拆分原则、生产与回收网络、Demo 货物目录继续有效。改写完成前，上述规则不能作为实现依据；其余规则继续有效。

> 决策出处：资源转录 Q183–Q186、Q188、Q190–Q195、Q201–Q202、Q211–Q216、Q223–Q225、Q229–Q253、Q255–Q259、Q269–Q276、Q285–Q292、Q376–Q397、Q415（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)）。资源与生产系统的总体边界见 [10](10-resources-and-extraction.md)。

## 边界

本文定义哪些东西进入资源与货物体系、货物拆分与多输出的内容设计原则、Demo 的 Raw Goods 与 Primary Production、Forward Production DAG 与 Recovery DAG 的内容骨架、Paint 与货物身份。Resource Source 与 Extraction 见 [10](10-resources-and-extraction.md)；Process Definition 的结构与执行见 [13](13-production-runtime-and-facilities.md)；库存见 [12](12-inventory-and-storage.md)；Composite 使用 Goods 建造见 [14](14-construction-refit-and-deconstruction.md)。市场 Supply / Demand、Price、Treasury、人口消费与具体装备玩法不在本文；未来经济系统以本文与 [12](12-inventory-and-storage.md)、[13](13-production-runtime-and-facilities.md) 产生和消耗的真实 Goods、Inventory、Production Capability 为事实基础。

## 不变量

- 所有标准货物都是 VoxelDefinition。不建立独立于 VoxelDefinition 的 GoodDefinition、MaterialDefinition 或 ComponentDefinition 体系；金属、能源、生物、食品原料等大类只是内容组织，不建立 ResourceCategory、RawGoodCategory 之类的强类型领域层（资源转录 Q211）。
- 资源网络中的对象必须是物质性、可库存、标准化且原则上可交易的实物；Crew、Power、Labor、Time 不属于 Goods。
- Composite 不是普通 Goods：未来可以参与资产所有权交易，但不进入普通 Cargo Stack 或 Production DAG。
- Production DAG 与 Recovery DAG 分离，且各自都是有向无环图。
- 不存在生产 Tier：Raw、Material、Component、Finished 只是内容语义或 Tag，不构成合法性层级。
- Gameplay Difference Test 决定是否拆分商品；现实中存在差异，不等于游戏中需要独立 Definition。
- 系统优先保持宏观、可理解和可扩展，不为现实主义引入没有正向 Gameplay 价值的副产品、废料、材料或中间层。

## 规则

### Gameplay Difference Test

拆分新 Goods 前必须至少能回答：它是否改变玩家的资源地域选择、加工路线、运输、市场需求、最终功能或其他有效决策？答案为否就不拆。因此 Demo 不设计 Apple / Banana、Beef / Pork、Gear / Bearing / Wire、Cotton / Wool、Coke、Industrial Chemicals、Precision Components、Scrap、Waste 与大量 Decoration Goods；树枝、碎料、Biomass 等低价值副产物以及水、空气、土壤默认不进入商品体系（资源转录 Q202、Q216）。Goods 不增加固有 Rarity 字段，稀缺性由资源分布与 Value / Price 表达（资源转录 Q219）。

### 商品阶段与生产路线

- 一个 Goods 可以同时被直接消费、作为其他 Process Input、用于 Construction、用于设备运行、被交易。Raw / Material / Component / Finished 只用于内容组织，不限制 DAG 边；Process 可以跨所谓“加工层级”直接引用已有 Goods。Raw Food 也可以拥有未来的最终消费用途，是否被继续加工不构成商品身份限制。
- 机制允许多个 Process 生产同一 Product，默认每个商品只有一条 canonical Production Route。只有替代路线能产生真正不同的 Resource dependency、Facility requirement、成本结构或地域策略时才增加额外 Process，且必须是两个显式 ProcessDefinition，见 [13](13-production-runtime-and-facilities.md)。
- 系统允许多输出 Process，但内容上克制使用：副产品必须对 Gameplay 有正向意义，否则不设计。多输出不是现实主义展示工具。

### Paint 与货物身份

- 纯表现差异不创建不同 Good Definition：不存在 Red Structural Block、Blue Structural Block 作为经济商品。
- 涂装属于蓝图的表现设置。改变颜色不改变 VoxelDefinitionId、Value、生产配方或经济身份，也不改变蓝图版本的语义 Revision。
- 涂装不是需要随库存保留的游戏状态：部件拆回库存后不保留涂装，重新安装后可以重新涂装。

### Raw Goods

Demo 的 Raw Goods 使用克制的功能性抽象。

| 分类 | Raw Good | 含义 |
| --- | --- | --- |
| Geological | Ferrous Ore | 廉价、大宗、结构工业的基础金属矿 |
| Geological | Conductive Ore | 电气工业的基础导电金属矿 |
| Geological | Rare Mineral | 抽象稀有金属、稀土和高级工业矿物 |
| Geological | Stone | 建筑材料基础 |
| Geological | Silica | Glass 与部分精密工业基础 |
| Geological | Coal | 同时连接 Structural Metal 冶金与 Fuel 路线 |
| Geological | Crude Oil | 同时连接 Fuel、Chemical Feedstock 与 Polymer 化工链 |
| Biological / Primary | Grain、Produce、Animal Products、Timber、Natural Fiber | 不拆 Apple / Banana、Beef / Pork、Cotton / Wool 等，除非未来通过 Gameplay Difference Test |

Geological Raw Goods 由 [10](10-resources-and-extraction.md) 的 Resource Source 开采得到；Timber 既可开采 Natural Forest，也可由 Forestry 生产。

### Primary Production

- 首批：Grain Farming → Grain、Produce Farming → Produce、Livestock Production（Input：Grain）→ Animal Products、Fiber Farming → Natural Fiber、Forestry → Timber。
- 除 Livestock Production 外的四种可以没有 Goods Input，由 Site、Work、Labor 等 Production Factors 约束。
- 不为了现实主义强行添加 Seed、Water、Fertilizer、Sapling、Feed 等商品；若未来这些对象产生明确贸易或产业策略价值，再加入。

### Forward Production DAG

Forward Production DAG 只表达实际 Goods 生产关系，不存在 Tier。具体整数配方数量后续平衡。

基础工业材料：

```text
Ferrous Ore + Coal → Structural Metal
Conductive Ore    → Conductive Metal
Rare Mineral      → Specialty Material
Stone             → Construction Material
Silica            → Glass
Timber            → Lumber
Natural Fiber     → Textile
```

Oil / Fuel / Chemical：首个有意义的多输出 Process 与明确的替代 Fuel 路线；不增加通用 Industrial Chemicals 中间层。

```text
Crude Oil          → Fuel + Chemical Feedstock
Coal               → Fuel
Chemical Feedstock → Polymer
```

Components：不提前增加 Precision Components / Advanced Components。

```text
Structural Metal + Polymer                                    → Mechanical Components
Conductive Metal + Structural Metal                           → Electrical Components
Electrical Components + Specialty Material + Glass + Polymer  → Electronic Components
```

Civilian Goods：Medical Supplies 是消耗性 Goods，不是永久安装设备；具体人口 / 乘员如何消耗留待人口与乘员系统设计。

```text
Grain                                             → Staple Food
Grain + Produce + Animal Products                  → Prepared Food
Textile                                           → Clothing
Lumber + Polymer + Structural Metal + Glass       → Consumer Goods
Electronic Components + Polymer + Glass           → Consumer Electronics
Chemical Feedstock + Textile + Polymer            → Medical Supplies
```

Construction Goods：Demo 只冻结三种；不预置 Window、Door、Armor Block、Decoration Goods，实际 Gameplay 需要时再添加。Rail 是正常货物，用于修建、扩建和修复线路的铁轨，见 [06](06-railway.md#修建扩建与修复)。

```text
Structural Metal      → Structural Block
Construction Material → Construction Block
Structural Metal      → Rail
```

Functional Equipment：Demo 包含七种功能设备。前五种是 Facility 通用能力设备；Propulsion Equipment 与 Control Equipment 为车辆提供动力与控制能力，二者在带 `assembly` Facility Tag 的设施中生产（2026-10-01 设计讨论）。不同功能设备即使 BOM 类似，也因为 Behavior / Capability 不同而是不同 VoxelDefinition；各自提供的 Capability 见 [13](13-production-runtime-and-facilities.md)。所有功能设备严格是单 Cell Voxel，能力通过数量叠加或不同 Definition 表达，不存在 3×2×2 之类的大型设备体素（资源转录 Q284，[01](01-voxel-engine-and-scenes.md)）。每个功能体素在车辆或建筑中是一个部件，见 [03 的部件与结构](03-vehicles-buildings-and-designs.md#部件与结构)。

```text
Structural Metal + Mechanical Components + Electrical Components → Industrial Machinery
Structural Metal + Mechanical Components                         → Storage Equipment
Structural Metal + Mechanical Components + Electrical Components → Power Equipment
Structural Metal + Mechanical Components + Electrical Components → Extraction Equipment
Structural Metal + Mechanical Components + Electrical Components → Construction Equipment
Structural Metal + Mechanical Components + Electrical Components → Propulsion Equipment
Electronic Components + Electrical Components                    → Control Equipment
```

### Future Product Families

Train Weapons、Crew Weapons、Train Equipment、Crew Equipment 仅作为设计占位，不创建万能 VoxelDefinition。对应 Gameplay 系统设计完成并通过 Gameplay Difference Test 后，再创建具体产品 VoxelDefinition、Production Process 与需要的 Recovery Process。Propulsion Equipment 与 Control Equipment 已移入上文 Functional Equipment：车头制造需要动力与控制能力（[03](03-vehicles-buildings-and-designs.md)），因此 2026-10-01 设计讨论撤销了 [#85](https://github.com/odradekk/train/issues/85) 将 Control Equipment 移出货物目录的修订，重新采用资源转录 Q288 将其作为独立成品的结论。

### Recovery DAG

- Forward Production DAG：Natural / Primary → Materials → Components → Goods / Equipment。Recovery DAG：Durable Goods / Equipment → Components / Materials → Materials。二者分别无环。
- 只有存在明确 RecoveryProcessDefinition 的 Goods 才可回收；系统不自动推导“所有东西都能拆”，Recovery 也不从 Production Recipe 自动反推。
- 允许多阶段 Recovery：Equipment → Components → Materials，不要求一步还原到底层资源。Recovery 只输出已经存在于 Forward Goods Catalog 中的 Goods，不为回收单独创建另一棵废品资源树；Demo 没有 Scrap、Waste、Broken Electronics 等通用回收中间品。若未来废料运输本身成为有价值的物流 Gameplay，再设计具体废料 Good。
- Demo 覆盖 Construction Goods、Components、Functional Equipment。默认不为 Raw Goods、Basic Materials、Food、Fuel、Chemical Feedstock、Clothing、Medical Supplies 与普通民用消费品建立 Recovery，未来按 Gameplay 需要显式增加。
- Recovery 明确不保证还原所有 Production Inputs；具体数量后续平衡。执行语义见 [13](13-production-runtime-and-facilities.md)。

首批 Recovery Skeleton：

```text
Structural Block       → Structural Metal
Construction Block     → Construction Material
Rail                   → Structural Metal
Mechanical Components  → Structural Metal + Polymer
Electrical Components  → Conductive Metal + Structural Metal
Electronic Components  → Electrical Components + Specialty Material + Polymer
Industrial Machinery   → Mechanical Components + Electrical Components + Structural Metal
Storage Equipment      → Mechanical Components + Structural Metal
Power Equipment        → Mechanical Components + Electrical Components + Structural Metal
Extraction Equipment   → Mechanical Components + Electrical Components + Structural Metal
Construction Equipment → Mechanical Components + Electrical Components + Structural Metal
Propulsion Equipment   → Mechanical Components + Electrical Components + Structural Metal
Control Equipment      → Electronic Components + Electrical Components
```

### Demo 货物目录

| 类别 | Goods |
| --- | --- |
| Raw Goods（12） | Ferrous Ore、Conductive Ore、Rare Mineral、Stone、Silica、Coal、Crude Oil、Grain、Produce、Animal Products、Timber、Natural Fiber |
| Industrial Materials（10） | Structural Metal、Conductive Metal、Specialty Material、Construction Material、Glass、Fuel、Chemical Feedstock、Polymer、Lumber、Textile |
| Components（3） | Mechanical Components、Electrical Components、Electronic Components |
| Civilian / Consumable Goods（6） | Staple Food、Prepared Food、Clothing、Consumer Goods、Consumer Electronics、Medical Supplies |
| Construction Goods（3） | Structural Block、Construction Block、Rail |
| Functional Equipment（7） | Industrial Machinery、Storage Equipment、Power Equipment、Extraction Equipment、Construction Equipment、Propulsion Equipment、Control Equipment |
| Future Product Families | Train Weapons、Crew Weapons、Train Equipment、Crew Equipment（仅占位） |

## Demo 范围

上表除 Future Product Families 占位外的货物、5 种 Primary Production、Forward Production DAG 与首批 Recovery Skeleton、Paint 作为 Presentation Override。本文冻结结构与语义，不冻结配方数量；范围归属见 [10](10-resources-and-extraction.md)。

## Demo 之后

Future Product Families 的具体产品与 Process；Window、Door、Armor Block、Decoration Goods；Seed、Water、Fertilizer、Sapling、Feed；Scrap / Waste 经济与废旧电子产业；Supply、Demand、Price curve、Treasury、Buy / Sell spread、Trade fee / tax、Settlement MarketState、AI economic planning；人口消费率；具体 Train Weapon、Crew Weapon、Train Equipment、Crew Equipment 玩法与医疗效果。现有 `VoxelDefinition.Value` 与“Price 属于 Market Context”的既有边界继续保留，Price 规则不在本文扩展。资源转录 Q357–Q366 的市场分支已在对话中作废，不构成决定。

## 开放问题

- 地图迷雾（不立票）：未冻结参数：每个 Production Recipe 的整数 Input / Output 数量、Recovery BaseOutput 数量。
- 地图迷雾（不立票）：Train Weapons 等 Future Product Families 的具体 Gameplay 功能与商品拆分尚待对应系统设计；新增 Goods / Process 须通过 Gameplay Difference Test。

按[分期决议](https://github.com/odradekk/train/issues/145#issuecomment-5896477710)，资源生产不属于本阶段；相关实现切片被选入当前或下一切片、且需要明确决策才能推进时再立票。
