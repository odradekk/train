# 12 库存与仓储

> 决策出处：资源转录 Q261–Q263、Q266–Q267、Q277–Q279、Q282–Q283、Q293–Q294、Q300、Q329、Q416–Q417（“资源转录 Qn”指 [`docs/research/resources-and-production-grilling-transcript.md`](../research/resources-and-production-grilling-transcript.md)）。资源与生产系统的总体边界见 [10](10-resources-and-extraction.md)。

## 边界

本文定义 Goods 在哪里存放以及如何计数：地点库存、Settlement Inventory、Train Inventory、Transfer、Storage Capacity 与 Storage Capability、Overcapacity、Stack 与 ItemInstance、Construction / Refit 对 ItemInstance 的消费规则。Facility OutputBuffer 见 [13](13-production-runtime-and-facilities.md)；Definition 层“Inventory 数量不是空间实例”的原则见 [02](02-definitions-and-content.md)；Storage Equipment 的生产见 [11](11-goods-and-production-network.md)。市场交易与自动 Transfer 策略不在本文。

## 不变量

- Inventory Quantity 不等于空间 Voxel Instance：库存中的 1000 个 Goods 不意味着世界中存在 1000 个实体体素。
- 普通 Goods 的 Storage Capacity 按 Quantity 计算：每 1 个 Goods unit 占 1 点容量；Mass 独立用于质量相关 Gameplay。
- 没有全局玩家物质库存：Goods 必须属于明确地点的 Inventory。
- 聚居地内部物流宏观化：同一聚居地使用统一逻辑库存池，不模拟仓库到工厂的内部搬运网络。
- Storage Capability 来自真实 Storage Equipment 的 Behavior，不由建筑类型或聚居地凭空产生。

## 规则

### 地点库存

- 不存在全局 Player Inventory。资源系统至少区分 Settlement Inventory、Train Inventory、Facility OutputBuffer 与未来其他明确地点的 Inventory。
- Train Inventory 与 Settlement Inventory 不自动合并，必须有显式 Transfer；生产 Facility 不能直接读取停靠列车货舱。唯一在此声明的例外是 [14 的 Construction / Refit 启动事务](14-construction-refit-and-deconstruction.md#construction-job)：在玩家自己的聚居地，可直接消费当前所在地可访问的玩家 Train Inventory 中的 BOM，并记录来源；它不合并库存、不允许生产或回收 Batch、发电等日常设施逻辑绕过 Transfer。Transfer 自动化规则以后可以设计，但必须仍是真实库存移动。
- 未来市场出售的货物就是聚居地实际库存中的货物，不另设独立的 Market Supply 数量（资源转录 Q279）；出售策略与市场规则本身延后。

### Settlement Inventory

- 同一 Settlement 使用统一逻辑库存池。Facility 直接从 Settlement Inventory 取 Inputs、把 Outputs 放回 Settlement Inventory。
- 不模拟 Factory input buffer、Conveyor、Warehouse-to-factory truck 或 Settlement 内部物流路径。

### Storage Capacity

- `UsedCapacity = Σ Stack.Quantity + ItemInstance.Count`；Stack unit 与 ItemInstance 都占 1 点 Storage Capacity。
- `TotalCargoMass = Σ Definition.Mass × quantity`，独立计算。
- 首版不增加 Liquid-only capacity、Refrigerated capacity、Hazardous capacity 或 Cargo weight limit；Crude Oil 等仍作为普通标准库存 Goods。

### Storage Capability

- Train：installed Storage Equipment → StorageCapability → Train Inventory Capacity。
- Settlement：Warehouse Building Composite 内实际 Storage Equipment → StorageCapability → Settlement Inventory Capacity。表面上“建仓库增加容量”，底层事实仍由仓库内实际 Storage Voxel 提供。
- Capability 由真实 Behavior 推导，Facility Tag 不能伪造 Storage Capability，见 [13](13-production-runtime-and-facilities.md)。

### Overcapacity

- 若 Storage Capability 因损伤下降到低于当前 UsedCapacity，允许临时 Overcapacity：已有 Goods 不自动删除；Goods 可以被取出；不允许新的普通入库；修复 Storage 或减少库存后恢复正常。
- [13](13-production-runtime-and-facilities.md#output-buffer) 的 OutputBuffer 转入同样受容量限制：UsedCapacity ≥ Capacity 时不得转入；只有存在实际可用容量（扣除有效 Storage Reservation）时才能转入，优先级不赋予突破容量的权限。
- 事故导致货物散落属于未来显式事故 Gameplay，不由 Storage Capacity 通用规则自动触发。

### Stack 与 ItemInstance

- 统一 Stackability predicate：只有当一个 Voxel 的 CurrentDurability 等于当前 Definition 的 MaxDurability、不存在非默认 Behavior Runtime State、不需要保留长期 Runtime Identity、且不存在其他 Gameplay-significant instance state 时，才可以折叠为 Stack { VoxelDefinitionId, Quantity }。
- 否则进入 ItemInstance { DefinitionId, Durability, Runtime Identity（如需要）, Behavior Runtime State }。例如新 Engine → Stack；带部分燃料的 Power Equipment → ItemInstance；70% 耐久的 Structural Block → ItemInstance。
- 该 predicate 同时用于 Refit 替换下来的旧件、Deconstruction 返回的部件（[14](14-construction-refit-and-deconstruction.md)）与 Recovery 的输入（[13](13-production-runtime-and-facilities.md)）。ItemInstance 的 Durability 与 Behavior Runtime State 属于权威 Runtime State，必须存档（[09](09-persistence.md)）。

### BOM 对 ItemInstance 的消费

- Construction / Refit 自动满足 BOM 时：Stack Good 可自动消费；ItemInstance 必须由玩家或上层计划显式选定。
- 若 Stack 不足且没有明确选定 ItemInstance，validation fails。系统不自动决定“最好耐久”“最差耐久”等策略。

## 首版范围

Settlement Inventory、Train Inventory、显式 Train ↔ Settlement Transfer、按 Quantity 计数的 Storage Capacity 与独立 Mass、来自 Storage Equipment 的 Storage Capability、Overcapacity、Stack 与 ItemInstance、BOM 对 ItemInstance 的显式选定。范围归属见 [10](10-resources-and-extraction.md)。

## 明确延后

Settlement 内部物流网络、Conveyor、Truck；专用液体罐、冷藏、危险品、重量容量双限制；Transfer 自动化；事故散落 Goods；市场交易。

## 开放问题

- 未冻结参数：Storage Equipment capacity（[roadmap](roadmap.md)）。
- OutputBuffer 的优先转入不构成 Overcapacity 例外；相关执行规则见 [13](13-production-runtime-and-facilities.md#output-buffer)。
