# 12 库存与仓储

> **所属层**：核心模拟层。

> 决策出处：资源转录 Q261–Q263、Q266–Q267、Q277–Q279、Q282–Q283、Q293–Q294、Q300、Q329、Q416–Q417（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)）。资源与生产系统的总体边界见 [10](10-resources-and-extraction.md)。删除 ItemInstance、库存只有 Stack，由[货物与内容定义是否继续绑定体素定义](https://github.com/odradekk/train/issues/204)于 2026-10-05 确认。功能复合体按身份引用、读档缺内容时的状态保留、部件计入结构耐久与伤害结算、嵌套顺序和构成哈希，由[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)于 2026-10-09 确认。

## 边界

本文定义 Goods 在哪里存放以及如何计数：地点库存、Settlement Inventory、Train Inventory、Transfer、Storage Capacity 与 Storage Capability、Overcapacity，以及库存只有 Stack 的规则。Facility OutputBuffer 见 [13](13-production-runtime-and-facilities.md)；货物与体素定义的关系见 [02](02-definitions-and-content.md#库存中的货物)；Storage Equipment 的生产见 [11](11-goods-and-production-network.md)。市场交易与自动 Transfer 策略不在本文。

## 不变量

- 库存中的货物只按种类和数量计数，没有个体身份或个体状态。
- 普通 Goods 的 Storage Capacity 按 Quantity 计算：每 1 个 Goods unit 占 1 点容量；Mass 独立用于质量相关 Gameplay。
- 没有全局玩家物质库存：Goods 必须属于明确地点的 Inventory。
- 聚居地内部物流宏观化：同一聚居地使用统一逻辑库存池，不模拟仓库到工厂的内部搬运网络。
- Storage Capability 来自任意车辆或建筑中完好的、带 `storage` 行为的部件，不由建筑类型或聚居地凭空产生。

## 规则

### 地点库存

- 不存在全局 Player Inventory。资源系统至少区分 Settlement Inventory、Train Inventory、Facility OutputBuffer 与未来其他明确地点的 Inventory。
- Train Inventory 与 Settlement Inventory 不自动合并，必须有显式 Transfer；生产 Facility 不能直接读取停靠列车货舱。唯一在此声明的例外是 [14 的 Construction / Refit / Repair 启动事务](14-construction-refit-and-deconstruction.md#construction-job)：在玩家自己的聚居地，可直接消费当前所在地可访问的玩家 Train Inventory 中的 BOM，并记录来源；它不合并库存、不允许生产或回收 Batch、发电等日常设施逻辑绕过 Transfer。Transfer 自动化规则以后可以设计，但必须仍是真实库存移动。
- 未来市场出售的货物就是聚居地实际库存中的货物，不另设独立的 Market Supply 数量（资源转录 Q279）；出售策略与市场规则本身延后。

### Settlement Inventory

- 同一 Settlement 使用统一逻辑库存池。Facility 直接从 Settlement Inventory 取 Inputs、把 Outputs 放回 Settlement Inventory。
- 不模拟 Factory input buffer、Conveyor、Warehouse-to-factory truck 或 Settlement 内部物流路径。

### Storage Capacity

- `UsedCapacity = Σ Stack.Quantity`；每个货物单位占 1 点 Storage Capacity。
- `TotalGoodsMass = Σ Definition.Mass × quantity`，独立计算。
- Demo 不增加 Liquid-only capacity、Refrigerated capacity、Hazardous capacity 或 Cargo weight limit；Crude Oil 等仍作为普通标准库存 Goods。

### Storage Capability

- Train：车辆中完好的存储部件 → StorageCapability → Train Inventory Capacity。
- Settlement：建筑中完好的存储部件 → StorageCapability → Settlement Inventory Capacity。表面上“建仓库增加容量”，底层事实仍由建筑设计中的存储部件提供，不限于仓库建筑；部件被毁时容量相应减少，见 [03 的设计属性](03-vehicles-buildings-and-designs.md#设计属性)。
- Capability 由真实 Behavior 推导，Facility Tag 不能伪造 Storage Capability，见 [13](13-production-runtime-and-facilities.md)。

### Overcapacity

- 若 Storage Capability 因损伤下降到低于当前 UsedCapacity，允许临时 Overcapacity：已有 Goods 不自动删除；Goods 可以被取出；不允许新的普通入库；修复 Storage 或减少库存后恢复正常。
- [13](13-production-runtime-and-facilities.md#output-buffer) 的 OutputBuffer 转入同样受容量限制：UsedCapacity ≥ Capacity 时不得转入；只有存在实际可用容量（扣除有效 Storage Reservation）时才能转入，优先级不赋予突破容量的权限。
- 事故导致货物散落属于未来显式事故 Gameplay，不由 Storage Capacity 通用规则自动触发。

### 只有 Stack

- 库存只有 Stack { 货物种类, 数量 }，没有 ItemInstance。货物种类有三种：体素定义标识、功能复合体标识，或组装体（可编辑的大型设备，例如炮塔）的构成哈希（ADR-0013，[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)）。构成哈希是去掉名称、说明和涂装后的设计哈希，改名或重涂不改变货物种类，见 [02](02-definitions-and-content.md#语义哈希字段与规范化)。库存和市场显示存档为这个哈希记的名称。库存里缺失的货物保留为缺失货物，不能使用和交易。拆下的完好部件变回普通货物，受损的修好后返还，被毁的不返还（[03](03-vehicles-buildings-and-designs.md#建造改装与修理)、[14](14-construction-refit-and-deconstruction.md)），所以库存中不会出现受损或带状态的件。大型设备整体拆下时同样先修好再入库。
- 建造、改装与修理按 BOM 自动消费 Stack，不需要玩家挑选具体的件。

## Demo 范围

Settlement Inventory、Train Inventory、显式 Train ↔ Settlement Transfer、按 Quantity 计数的 Storage Capacity 与独立 Mass、来自带 `storage` 行为的部件的 Storage Capability、Overcapacity、只有 Stack 的库存。范围归属见 [10](10-resources-and-extraction.md)。

## Demo 之后

Settlement 内部物流网络、Conveyor、Truck；专用液体罐、冷藏、危险品、重量容量双限制；Transfer 自动化；事故散落 Goods；市场交易。

## 开放问题

- 地图迷雾（不立票）：未冻结参数：存储部件的容量。

库存与仓储属于当前完整 Demo，范围与验收统一见 [00](00-product-and-core-loop.md#本阶段范围)。市场交易不再因本文的旧分期排除在 Demo 外，具体规则由[市场与交易：库存、定价与复合体买卖](https://github.com/odradekk/train/issues/281)补齐。
