# 02 Definition、Revision、Instance 与内容来源

> 决策出处：转录 Q53–Q62、Q86–Q88、Q176；2026-09-05 [#31](https://github.com/odradekk/train/issues/31) 确认迁移与内容格式，随后 [#35](https://github.com/odradekk/train/issues/35) 将读档及热重载修订为采用当前外部定义，并确定版本、哈希与存档规则。见 [ADR-0001](../adr/0001-use-namespaced-good-identities-and-versioned-content-schema.md)、[ADR-0002](../adr/0002-limit-mods-to-data-packages-and-isolate-package-failures.md)、[ADR-0005](../adr/0005-resolve-current-definitions-and-separate-content-versions.md)。代码迁移由后续切片执行。

## 边界

本文定义"某种东西是什么"的数据、其版本与实例的关系、定义的来源、Mod 与热加载，以及 Inventory 数量与空间实例的关系。Composite 的 Definition 与 Archetype 见 [03](03-composite-and-behavior.md)；存档中的 Save-local Definition 见 [09](09-persistence.md)；Resource Source、Raw Good 与货物目录见 [10](10-resources-and-extraction.md)、[11](11-goods-and-production-network.md)；Stack 与 ItemInstance 的库存规则见 [12](12-inventory-and-storage.md)；经济价格属于市场上下文，规则留待后续扩充。

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
- **Value** 即术语表的基本货币价值：当前定义中的固定基础价值，不随世界经济变化，但可随内容更新改变。用于基础经济计算、Composite 断裂后的身份继承、其他稳定估值。例如 Iron Ore 的 Value = 100。
- **Price** 不属于 VoxelDefinition，也不属于 VoxelInstance。Price 是 MarketState × VoxelDefinition 产生的 Runtime Quote，例如 Mining Town 的 Iron Ore = 70，Capital 的 Iron Ore = 135。
- **Mass** 为 Voxel 的基础质量，未来用于 Composite 总质量、列车动力学、碰撞损伤、其他 Gameplay 计算。
- **Material**：首版没有通用 MaterialDefinition 系统。只有当实际开发中大量 Voxel 重复相同材料规则时，再抽象 Material 层。
- **自然资源**：Resource Source 与 Raw Good 严格分离（[10](10-resources-and-extraction.md)）。世界中的资源源体素带 `resource_source` 标签并由 ResourceSourceBehavior 定义产出；开采得到的初级货物带 `raw_good` 标签，是普通可库存、可交易的 VoxelDefinition。世界生成与聚居地资源记录检查 `resource_source` 标签与其 Behavior 产出（[05](05-world-generation-and-settlements.md)）。旧的“带 `natural_resource` 标签的体素自身就是货物”语义废止，不保留含糊兼容层。[#77](https://github.com/odradekk/train/issues/77) 已把可交易初级货物与聚居地种类集合改为 `raw_good`；[#78](https://github.com/odradekk/train/issues/78) 仍待实现 ResourceSourceBehavior 与 `resource_source`。当前聚居地内容仍直接列出可获得的初级货物种类，尚未从周边资源源扫描派生。

### Good 迁移边界

- 在首个需要体素定义的切片中，一次性迁移根目录项目的 GoodDefinition、GoodId、GoodCatalog、内容文件及其消费者和引用，改用体素定义与 VoxelDefinitionId；不保留旧 goods schema 的兼容层或临时双轨目录。
- 新 schema 从版本 1 开始，不作为 goods schema v2。切片 1 曾将自然资源类别转为 `natural_resource` 标签；[#77](https://github.com/odradekk/train/issues/77) 已把这些可交易货物改为 `raw_good`。对应的世界资源源体素仍待以 `resource_source` 与 ResourceSourceBehavior 定义，见 [#78](https://github.com/odradekk/train/issues/78)。基本货币价值保留为 Value；Value 已用于断裂后的身份继承，不随 TradeMetadata 一起延后。
- `archive/mvp/` 保持原样。本票只确定方案，不执行生产代码或内容文件迁移。Catalog 版本、Revision 哈希与存档绑定按下文执行。

### Inventory 与实体化

- Inventory 中的数量不是空间 Voxel Instance。Cargo Stack { VoxelDefinitionId = `core:ferrous-ore`, Quantity = 500 } 表示 500 个可实体化的单位；只有被放置进入世界时才产生 Voxel Instance。
- 普通无特殊 Runtime State 的 Voxel 被收纳时可以转化为 Stack；需要保存 Durability、Behavior State、Voxel Runtime ID 的 Voxel 使用独立的 ItemInstance。统一的 Stackability predicate、库存地点、容量与 ItemInstance 消费规则见 [12](12-inventory-and-storage.md)。

### Definition、Revision 与 Instance

- **Definition** 定义"某种东西是什么"。
- **Revision** 是 Definition 的一个不可变语义版本。内部 Revision ID 使用 Semantic Content Hash，而不是 r1、r2、r3；完全相同的语义内容产生相同 Revision。UI 可额外维护纯数字的人类版本号（Version 1、2、3），只用于展示，不承担身份职责。
- Semantic Revision 用于识别规格变化，不强制实例锁定旧外部规格。读档与热重载按 DefinitionId 解析当前安装的定义；定义本身仍是不可变快照，更新通过发布新快照完成。
- **Instance** 保存当前 Durability、Behavior Runtime State、Ownership、当前几何变化、其他 Runtime 信息。Definition 永远不保存实例状态。

### 三种内容版本

| 名称 | 含义 |
| --- | --- |
| Semantic Revision | 对定义自身的有效玩法数据计算 SHA-256，识别规格变化 |
| Asset Revision | 对单个资产文件的内容字节计算 SHA-256，识别图片、模型等变化 |
| Catalog Generation | 成功发布内容后的递增序号；实际内容无变化时不递增，不承担持久身份职责 |

显示文本、资产绑定、纯表现配置和资产内容的有效变更也会触发 Catalog 发布，但不因此改变 Semantic Revision。Asset Revision 不包含文件路径；相同内容字节得到相同哈希，绑定变化由表现元数据和 Catalog 发布表达。

### 语义哈希字段与规范化

| 参与语义哈希 | 不参与语义哈希 |
| --- | --- |
| 定义类型、DefinitionId、哈希编码版本 | 名称、说明等显示文本 |
| Value、MaxDurability、Mass、Tags | 图片、模型、材质、声音的内容与路径 |
| Behavior 类型与参数 | 纯视觉配置及资产绑定 |
| Collision 的规则、形状与尺寸 | 编辑器排版等辅助信息 |
| Composite 体素布局、构造与身份保留规则引用 | 引用目标的 Revision |

- 先解析并校验为领域值，再生成规范化编码；不对原始 JSON 文本直接哈希。采用字段边界明确的规范化 JSON、UTF-8 编码，固定字段顺序；空白与源文件字段书写顺序不影响结果。
- 对象字段和字符串键按 Ordinal 顺序编码。标签等无序集合稳定排序；体素布局按整数坐标稳定排序；有顺序含义的列表保留顺序。
- 数字按实际领域类型统一编码；`double` 使用与文化无关的无损往返表示，不截断或舍入到显示精度。有效数据不含 `NaN` 或无穷值。
- 语义引用记录目标 DefinitionId，不包含目标 Revision，也不递归哈希依赖内容。修改 Mod 引擎功率会改变引擎 Revision，不改变引用它的车辆设计自身的 Revision；使用方仍必须根据当前引擎规格重算受影响能力。
- 名称、说明与表现绑定作为按 DefinitionId 解析的当前显示信息处理，不因语义哈希相同而丢弃更新。权威碰撞数据属于语义；更换显示模型不能自动改变碰撞形状。
- 验收覆盖重新排版不变、更改有效语义字段产生变化、纯表现更新不改变语义 Revision、依赖更新生效但不改变引用方自身 Revision。

### Definition 来源

- 四类：Built-in、Mod、Player Blueprint Library、Save-local Definitions。
- 玩家在某个存档内设计的新版本默认只进入 Save-local Definitions；只有显式 Save as Blueprint 才复制或发布到 Player Blueprint Library；其他存档不会自动看到 save-local revision。
- Save-local Definition 及其所需的其他存档内定义直接包含在存档中。外部定义仍依赖当前安装内容；存档不包含外部旧定义副本，也不因此保证缺失 Mod 时可加载。

### 内容目录与格式

| 来源 | 组织方式 |
| --- | --- |
| Built-in | `builtin/core/resources/` 下按 `voxels`、`composites`、`layouts` 分类 |
| Mod | `user://mods` 中各包的 `resources/` 下沿用相同结构 |
| Player Blueprint Library | `user://blueprints/<蓝图 UUID>.composites.json`，复用 Composite 定义格式 |
| Save-local Definitions | 直接内嵌存档，不作为独立内容目录扫描 |

- 新定义文件使用明确的 `schema_version`，从 1 开始；按类型使用 `.voxels.json`、`.composites.json`、`.layouts.json`。目录用于组织文件，不决定定义身份。
- 存档位于 `user://saves/<存档 UUID>/save.json`。Blueprint 与存档使用稳定 UUID 命名，显示名称保存在文件内，重命名不改变身份和引用；文件名不替代定义内部的 DefinitionId。
- 当前首版通过手写 JSON 和测试操作使用这些格式，不要求正式设计器。

### ID 与命名空间

- 继续采用稳定的 namespaced identity：`namespace:local-id`，例如 `core:ferrous-ore`、`mod-namespace:titanium-engine`。文件路径不是 Definition 身份。
- 不同领域保持强类型 ID：VoxelDefinitionId、CompositeDefinitionId、CompositeArchetypeId、BehaviorTypeId 等。它们共享 Namespace 规则、Parser、Normalize、Validation，但不统一成一个完全无类型的 DefinitionId。
- ADR-0001 的规则沿用：built-in 使用 `core` 命名空间，Mod 命名空间来自 manifest UUID，定义文件只含 local id。

### Mod 与热加载

- **第一阶段 Data Mod**：支持新增 VoxelDefinition、Composite Definition、Layout，修改自有定义的数值、引用资产、组合已有 Behavior。首版不允许覆盖或 patch 内置及其他 Mod 的定义，重复定义继续报错；不能直接注入任意 C# 代码（ADR-0002）。
- **第二阶段 Code Mod**：等游戏规则和公开 API 足够稳定后，再考虑 C# 插件、注册新 Behavior、注册新 System、公共 Mod API。
- **Hot Reload**：发布新的不可变定义快照，运行中实例立即解析并采用当前规格。修改耐久上限、Behavior、布局或引擎功率产生新的 Semantic Revision；修改 Texture、Mesh、Material、Sound 更新 Asset Revision 和表现。布局定义更新不覆盖已存在车辆保存的实际布局。

### 内容更新与实例状态

- 读档和热重载都采用当前安装的外部定义，更新数值作用于已有车辆；保留车辆及零件身份、实际布局与运行状态。玩家自定义设计保留自身布局，其中的外部零件按 ID 采用当前定义。
- 状态保留绝对值，仅在超出新定义允许范围时修正，不按比例换算、不自动恢复。原耐久上限 100、当前耐久 80：新上限 150 时仍为 80，新上限 60 时截到 60。
- 功率、质量等定义属性直接采用新值，并重算受影响的能力和派生状态。记录上次使用的 Revision 以识别变化，不借此保留旧外部规格。

### 候选内容与运行中引用校验

[静态建筑与内容更新契约](https://github.com/odradekk/train/issues/106)已确认建筑同样遵守以下更新边界：建筑 Blueprint 和聚居地 Layout 模板更新只影响后续新建，不重写已有实例布局；零件数值、碰撞和表现更新作用于已有实例，保留身份、实际布局与耐久等状态，并沿用上述数值范围适配。候选发布须校验所有受影响建筑、站点、空间关系与引用；必需引用缺失、建筑身份不能保留或产生非法占用/铁路净空时，整次热重载拒绝。纯表现更新不改变权威碰撞或铁路连接。建筑修复使用实例最后确认的完整设计，具体目标与身份规则见 [03](03-composite-and-behavior.md#本阶段静态建筑契约)；外部模板更新不替换该目标，不可借热重载直接重建现存建筑。修复目标引用的零件定义以及残块归属等恢复必需信息也属于受影响引用，不能只校验当前仍在建筑里的零件。

- 新加载的定义必须引用有效内容，非法 Mod 整包排除；内置内容出错则拒绝发布。排除后的候选内容仍必须自洽，不能留下未解析引用。
- 必须基于候选当前定义校验全部受影响的运行中引用与状态适配。缺失必需定义或无法解释旧状态时，拒绝整次热重载，保留原定义、Catalog Generation 和世界状态；不能发布部分更新或留下部分修正后的实例。
- 读档遇到相同问题时明确报告并失败。首版不自动替换零件、删除状态或回退到存档中的旧 Mod 定义。
- 全部校验与状态适配成功后再原子发布内容及相关实例更新。这替代 #31 曾确定的“保留旧 Revision 即可接受目录删除”规则。

### 本阶段表现资产校验

[三路表现契约](07-rendering.md#本阶段表现验收契约)确认 Cube、CustomStaticMesh 与领域专用表现共存；CustomStaticMesh 使用 LOD0 静态模型、基础颜色/贴图、光照/阴影和不透明或镂空裁剪材质。资产原点、尺寸、方向及适用表现绑定须有明确约定，具体文件格式和字段由实施规格记录；模型不能替代权威碰撞定义。

模型、材质等必需资产缺失、损坏或不支持，以及专用渲染绑定不存在或不适用，均须明确诊断。首次加载沿用上文内容包校验与候选自洽规则；热重载失败保留旧 Catalog、世界及有效表现，不部分发布，也不以静默替换 Cube 或隐藏对象冒充成功。合法纯表现变化采用 Asset Revision/表现绑定及 Catalog 发布机制，更新受影响视觉，不改变 Semantic Revision、碰撞、身份、铁路连接或模拟结果。区域未驻留不妨碍内容有效性校验，再次显示时使用当前有效内容。

## 首版范围

VoxelDefinition；JSON/Data Mod；Definition、Revision、Instance 三分；手写 JSON 车辆 Blueprint，由测试场景加载、校验与制造，并完整验证已有车辆体素的添加、移除、移动、旋转、替换及批量原子改装；正式设计器后续开发，详见 [03](03-composite-and-behavior.md)。

## 明确延后

TradeMetadata 的字段与实现、Code Mod API、通用 Material System；跨包覆盖与 patch、Mod 依赖、加载顺序覆盖、启用状态管理（ADR-0002）。

## 开放问题

- 无。库存系统与非堆叠 ItemInstance（转录 Q175）已由 [12](12-inventory-and-storage.md) 确定。
