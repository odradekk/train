# 02 Definition、Revision、Instance 与内容来源

> 决策出处：转录 Q53–Q62、Q86–Q88、Q176；2026-09-05 [#31](https://github.com/odradekk/train/issues/31) 确认迁移、内容格式与重载边界，并修订 [ADR-0001](../adr/0001-use-namespaced-good-identities-and-versioned-content-schema.md)、[ADR-0002](../adr/0002-limit-mods-to-data-packages-and-isolate-package-failures.md)。代码迁移由后续切片执行。

## 边界

本文定义"某种东西是什么"的数据、其版本与实例的关系、定义的来源、Mod 与热加载，以及 Inventory 数量与空间实例的关系。Composite 的 Definition 与 Archetype 见 [03](03-composite-and-behavior.md)；存档中的 Save-local Definition 见 [09](09-persistence.md)；经济价格属于市场上下文，规则留待后续扩充。

## 不变量

- Definition 是不可变数据；Runtime State 不写回 Definition。
- Semantic Revision 与 Presentation Asset Revision 分离。
- Price 属于 Market Context，不属于 Voxel。
- Inventory Quantity 不等于空间 Voxel Instance。
- Capability 不允许在 Mod JSON 中伪造，由实际注册的 Behavior 推导（[03](03-composite-and-behavior.md)）。

## 规则

### VoxelDefinition

- 所有可实体化对象共享统一的 VoxelDefinition。现有独立的货物定义并入这一体系，Cargo、Market、Trade 后续统一引用 VoxelDefinitionId（转录 Q176）。TradeMetadata 的字段与实现整体延后到贸易接入时，首版不预留空对象。
- 首版核心字段：DefinitionId、Value、MaxDurability、Mass、Tags、Behaviors、Presentation、Collision。
- **Value** 即术语表的基本货币价值：游戏开始时定义的恒定基础价值，不随世界经济变化。用于基础经济计算、Composite 断裂后的身份继承、其他稳定估值。例如 Iron Ore 的 Value = 100。
- **Price** 不属于 VoxelDefinition，也不属于 VoxelInstance。Price 是 MarketState × VoxelDefinition 产生的 Runtime Quote，例如 Mining Town 的 Iron Ore = 70，Capital 的 Iron Ore = 135。
- **Mass** 为 Voxel 的基础质量，未来用于 Composite 总质量、列车动力学、碰撞损伤、其他 Gameplay 计算。
- **Material**：首版没有通用 MaterialDefinition 系统。只有当实际开发中大量 Voxel 重复相同材料规则时，再抽象 Material 层。
- **自然资源**：以 Tags 中约定的 `natural_resource` 标签替代 GoodCategory.NaturalResource。世界生成、聚居地资源记录及其引用校验统一检查此标签；它不代表可交易性，也不限制其他标签。

### Good 迁移边界

- 在首个需要体素定义的切片中，一次性迁移根目录项目的 GoodDefinition、GoodId、GoodCatalog、内容文件及其消费者和引用，改用体素定义与 VoxelDefinitionId；不保留旧 goods schema 的兼容层或临时双轨目录。
- 新 schema 从版本 1 开始，不作为 goods schema v2。迁移时将自然资源类别转为 `natural_resource` 标签，基本货币价值保留为 Value；Value 已用于断裂后的身份继承，不随 TradeMetadata 一起延后。
- `archive/mvp/` 保持原样。本票只确定方案，不执行生产代码或内容文件迁移。Catalog 的语义与资产版本拆分、Revision 哈希和存档绑定由 #35 确定。

### Inventory 与实体化

- Inventory 中的数量不是空间 Voxel Instance。Cargo Stack { VoxelDefinitionId = `core:iron-ore`, Quantity = 500 } 表示 500 个可实体化的单位；只有被放置进入世界时才产生 Voxel Instance。
- 普通无特殊 Runtime State 的 Voxel 被收纳时可以转化为 Stack。未来复杂设备可能需要保存 Durability、Behavior State、Voxel Runtime ID，届时使用独立的 Item Instance，而不是 Stack。

### Definition、Revision 与 Instance

- **Definition** 定义"某种东西是什么"。
- **Revision** 是 Definition 的一个不可变语义版本。内部 Revision ID 使用 Semantic Content Hash，而不是 r1、r2、r3；完全相同的语义内容产生相同 Revision。UI 可额外维护纯数字的人类版本号（Version 1、2、3），只用于展示，不承担身份职责。
- **Instance** 保存当前 Durability、Behavior Runtime State、Ownership、当前几何变化、其他 Runtime 信息。Definition 永远不保存实例状态。

### Definition 来源

- 四类：Built-in、Mod、Player Blueprint Library、Save-local Definitions。
- 玩家在某个存档内设计的新版本默认只进入 Save-local Definitions；只有显式 Save as Blueprint 才复制或发布到 Player Blueprint Library；其他存档不会自动看到 save-local revision。
- Save-local Definition 必须直接包含在存档中，使 Save 自包含。

### 内容目录与格式

| 来源 | 组织方式 |
| --- | --- |
| Built-in | `builtin/core/resources/` 下按 `voxels`、`composites`、`layouts` 分类 |
| Mod | `user://mods` 中各包的 `resources/` 下沿用相同结构 |
| Player Blueprint Library | `user://blueprints/`，复用 Composite 定义格式 |
| Save-local Definitions | 直接内嵌存档，不作为独立内容目录扫描 |

- 新定义文件使用明确的 `schema_version`，从 1 开始；按类型使用 `.voxels.json`、`.composites.json`、`.layouts.json`。目录用于组织文件，不决定定义身份。
- Blueprint 库内具体命名及存档落盘细节由 #35 确定。当前首版通过手写 JSON 和测试操作使用这些格式，不要求正式设计器。

### ID 与命名空间

- 继续采用稳定的 namespaced identity：`namespace:local-id`，例如 `core:iron-ore`、`mod-namespace:titanium-engine`。文件路径不是 Definition 身份。
- 不同领域保持强类型 ID：VoxelDefinitionId、CompositeDefinitionId、CompositeArchetypeId、BehaviorTypeId 等。它们共享 Namespace 规则、Parser、Normalize、Validation，但不统一成一个完全无类型的 DefinitionId。
- ADR-0001 的规则沿用：built-in 使用 `core` 命名空间，Mod 命名空间来自 manifest UUID，定义文件只含 local id。

### Mod 与热加载

- **第一阶段 Data Mod**：支持新增 VoxelDefinition、Composite Definition、Layout，修改自有定义的数值、引用资产、组合已有 Behavior。首版不允许覆盖或 patch 内置及其他 Mod 的定义，重复定义继续报错；不能直接注入任意 C# 代码（ADR-0002）。
- **第二阶段 Code Mod**：等游戏规则和公开 API 足够稳定后，再考虑 C# 插件、注册新 Behavior、注册新 System、公共 Mod API。
- **Hot Reload**：语义 Definition 热加载是"旧 Revision + 新 Revision"，而不是修改旧 Revision。Presentation Asset 热加载与 Gameplay Revision 分离：修改 Texture、Mesh、Material、Sound 可以立即影响引用该 AssetId 的现有实例；修改 Durability、Behavior、Voxel Layout、Engine Power 则产生新的 Semantic Revision。

### 候选内容与运行中引用校验

- 新加载的定义必须引用有效内容，非法 Mod 整包排除；内置内容出错则拒绝发布。排除后的候选内容仍必须自洽，不能留下未解析引用。
- 运行中实例继续使用所绑定的旧 Revision。只要该 Revision 及其依赖仍完整可用，就不因当前内容目录移除了该定义而拒绝重载；不得隐式把实例重绑到新 Revision。
- 任何实际无法解析的运行中引用仍导致整次重载失败，原内容与世界状态保持不变。保留旧 Revision 不豁免新候选的引用校验。
- 旧 Revision 如何写入存档、重启后如何解析由 #35 确定；这里不承诺缺失 Mod 的存档兼容或回退。

## 首版范围

VoxelDefinition；JSON/Data Mod；Definition、Revision、Instance 三分；手写 JSON 车辆 Blueprint，由测试场景加载、校验与制造，并通过最小改装操作验证修改已有车辆；正式设计器后续开发，详见 [03](03-composite-and-behavior.md)。

## 明确延后

TradeMetadata 的字段与实现、Code Mod API、通用 Material System；跨包覆盖与 patch、Mod 依赖、加载顺序覆盖、启用状态管理（ADR-0002）。

## 开放问题

- [定义 Revision 语义哈希、Catalog 版本拆分与存档绑定](https://github.com/odradekk/train/issues/35)：哪些字段进入语义哈希与规范化序列化；GoodCatalog 的 Version 与含 PNG 像素的 Fingerprint 如何拆成 Definition Revision 与 Asset Revision；存档对内置 Revision 的绑定。
- 地图迷雾：库存系统与非堆叠 Item Instance（转录 Q175）。
