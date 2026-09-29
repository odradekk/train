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

### 世界编辑器内容创作流程

> 决策来源：[确定蓝图、布局与内容定义的创作和发布流程](https://github.com/odradekk/train/issues/141)的 Q1–Q21 已逐项确认，Q22 已确认完整规格定稿。既有身份、版本和候选发布契约继续有效；本节不表示创作工具已实现。

#### 创作视图与内容来源

蓝图、聚居地 Layout 和体素定义在同一个世界编辑器内使用独立创作视图，从内容库打开或新建创作文档，复用选择、体素编辑和检查器。车辆蓝图可离轨设计，建筑蓝图可独立检查，Layout 可编排建筑及接口，不必先在当前世界制造对象。创作预览不参与当前世界模拟；只有明确实例化或发布才按对应契约影响世界，不另建长期产品场景。

首批编辑器把内置与已安装 Mod 作为只读来源，允许查看、引用和创建用户副本。副本使用新内容身份，不覆盖原定义，不自动替换世界中的原对象；自己的内容可继续编辑和发布。不直接修改内置文件或已安装 Mod，不支持跨包覆盖或 patch；这不妨碍 Mod 作者在外部维护自己的包。沿用现有内容隔离规则。

#### 从实例提取蓝图

默认提取实例当前实际零件布局、定义与朝向，形成设计草稿，不复制实体 ID、耐久损伤、速度、运行计划或待执行命令。受损建筑可明确选择按当前形态提取，或按实例完整修复目标提取；两种来源不能混用或静默替换。

提取本身不修复或修改原实例，也不改变其修复目标。缺核心、结构非法等草稿不能作为可制造蓝图发布；合法设计制造时仍须经过正式校验。不把本规则扩大为复制受损运行状态的另一种存档。

#### 草稿保存与发布范围

独立创作文档区分保存草稿与发布可用内容。未完成、缺核心或暂有引用错误的设计也能保存为独立草稿，下次继续；草稿不进入有效内容库，不参与生成或制造，也不随普通世界保存被当成已发布内容。关闭有未保存修改的创作文档时提供保存草稿、放弃和取消。世界操作的临时放置预览仍不持久化，不因独立文档可保存而改变该边界。

草稿保存明确的来源内容与发布目标范围。在世界视图和创作视图间切换可以保留文档，真正切换世界或退出前处理文档未保存修改，不能自动将旧草稿归属新世界。旧草稿可继续编辑，但原目标世界未打开、目标内容不存在或依赖不全时，不能直接发布到当前世界；须打开原目标，或显式创建当前范围的副本。没有打开世界时仍可编辑草稿及发布用户库内容，不能发布到存档内。

发布须完整校验；失败保留草稿、原有效内容及当前世界。车辆／建筑蓝图、Layout 和体素定义都支持存档内与跨存档用户内容库两种范围，在当前世界创作时默认发布到存档内，随普通存档保存；只有显式发布到用户内容库才供其他存档使用。界面明确显示归属，打开创作视图本身不修改全局内容。

存档内发布更新当前世界的有效内容并标记未保存，普通保存后才持久化，不自动保存世界。用户库发布必须成功写入库文件才算完成；库写入与当前会话必要的内容适配按一次发布处理，正常失败不留下半份更新。若适配改变当前世界的持久状态，该世界仍须单独保存。保存草稿既不发布，也不清除世界未保存标记。

这扩展了 ADR-0001／ADR-0005 原有的仅使用 Composite 文件的玩家蓝图库安排，新增 Layout 与体素定义的用户复用入口；不是将三种数据塞进现有 Composite 格式。稳定身份、不可变版本及当前定义解析原则继续保留，存储目标和旧目录迁移见下文；不把设计目标写成当前实现已支持。

#### 内容身份与跨范围依赖

修改自己的已发布内容保留原身份，版本沿用现有语义与表现规则；修改显示名不换身份。另存副本、从存档内复制到用户库、或将库内容复制为存档内变体，都建立新的目标身份。界面区分更新已有内容和创建副本；副本独立演进，不自动同步回来源，不自动改写现有实例或模板指向原内容的引用。

新建用户内容及独立内容副本的身份由系统基于 UUID 自动分配，沿用 `namespace:local-id` 和类型区分，避免不同存档中新建内容碰撞；显示名不参与身份，不要求用户填写命名空间。内容身份与存档文件身份分开：普通世界存档另存为或导入保留内嵌定义标识与引用，各副本的存档内内容隔离解析，不因存档 UUID 改变而重编内容。显式复制某份内容到另一范围仍建立新内容身份。具体 UUID 组织与字段编码留给实施规格，但必须明确隔离与冲突校验。

存档内内容发布到用户库前，列出根内容及必须一起复制的完整存档内依赖，为这些副本建立一致的新身份，并改写副本内部引用，整批校验后发布。例如车辆蓝图引用本存档自创的轮子，必须一并处理轮子定义，不能只发布蓝图而留下对原存档的依赖。

内置、Mod 和已有用户库内容继续保留明确外部引用，不偷偷复制为旧版本；缺失依赖明确拒绝。用户库不得依赖某个存档内的内容；发布副本不改变原存档及其引用。

#### 发布影响与重新校验

发布更新前列出目标、作用范围、依赖及当前世界受影响对象，区分模板只影响后续创建与体素定义适配已有实例，指出耐久截断、能力变化、碰撞冲突等结果，由用户明确确认。更新用户库还须提示其他存档下次打开时将采用新定义；首批不自动扫描或保证所有未打开存档兼容。

确认后的提交仍在完整边界按当前条件重新校验；条件变化或适配失败则整次拒绝，保留原内容和世界。预览不替代正式发布校验，不构成提前获准修改世界。

发布前还须核对草稿所基于的目标内容，包括显示信息和资产绑定。目标已被其他发布或热重载改变时，保留草稿并提示重新载入后编辑或另存独立副本，不静默覆盖，也不在首批自动合并。依赖变化须重新验证、刷新影响预览，必要时重新确认，不能沿用旧的通过结果。

#### 用户内容删除

首批允许删除自己可编辑的内容，先列出引用影响。其他已发布内容或当前世界的实际对象、修复目标、待执行命令等仍需该内容时，拒绝删除，不级联删除对象或替换定义。未发布草稿可以保留并标记缺失引用。

删除用户库内容须明确提示未打开存档可能无法恢复，确认后才执行；首批不扫描所有历史存档，也不承诺它们删除后仍可打开。内置及已安装 Mod 的只读边界不变。

#### 蓝图与 Layout 创作

车辆／建筑蓝图支持零件添加、移除、移动、旋转、替换及批量编辑，显示尺寸、质量、核心、连通性、已有能力及不合法原因。车辆明确选择五档之一，检查该档包络和机械约束，不自动升档。离轨设计预览通过不等于任意位置可制造或运行；制造到世界时仍须验证实际空间、轨道和净空。

首批 Layout 是一个聚居地的空间模板，编排建筑蓝图、道路／装饰等世界体素，以及可选的一个站点和接轨接口；可设置局部锚点、位置、朝向、占地及场地约束。沿用四种水平正交朝向，不提供任意缩放、镜像或递归嵌套 Layout。Layout 不包含整张地图、列车、运行计划或铁路网络；实际铁路由正式施工或联合生成建立，接轨接口不自动成为轨道连接。

支持从明确选中的建筑、道路／装饰体素及可选站点提取 Layout 草稿，由用户指定局部锚点。只有设计一致时才引用现有建筑蓝图，否则为实际设计形成配套蓝图草稿；受损建筑仍须明确选择当前形态或完整修复目标。提取预览列出包含和排除项，不携带实例 ID、损伤、列车、运行计划、轨道连接或通行保护，也不默认带走周围地形。提取不改世界，最终整批校验后才能发布。

Layout 同时用于手工放置和程序生成。手工放置先预览朝向、完整建筑／世界体素、站点接口及所需有限整地，按场地、占用、净空和引用规则整体验证，一次提交或拒绝，不隐式接通附近铁路。程序生成只使用生成配置明确选择的候选 Layout；发布新模板不自动改变候选集或已有世界。单个 Layout 合法不能代替四站、多列车地图的联合验证。

#### 体素定义表单

首批提供受正式规则约束的属性表单，可编辑名称、说明、价值、质量、耐久上限、标签、允许朝向、碰撞，以及已注册 Behavior 的合法参数和现有表现资产绑定。字段按适用类型显示，错误定位到具体字段或引用。能力由实际 Behavior 推导，不能手工声明不存在的能力；更换显示模型不自动改变碰撞。新行为逻辑仍由代码实现，模型／贴图仍由外部资产工具制作，不建设通用脚本或节点编程器。

#### 存储与交付边界

已发布用户内容放在 `user://content-library/`，独立草稿放在不被有效内容加载器扫描的 `user://authoring-drafts/`，存档内定义继续内嵌普通存档。各类文件均有明确格式版本，按稳定身份组织；显示名变化不改变路径身份。体素、Composite 与 Layout 继续使用各自定义格式。旧 `user://blueprints/` 的读取／迁移安排须进入实施票，保留原文件，不静默改变身份或丢弃内容；具体字段和多文件发布方法由实施者按契约落实。

相对交付顺序为车辆／建筑蓝图 → 体素定义 → Layout：先贯通蓝图草稿、校验、发布、实例化和恢复，再补体素表单与已有实例适配，最后随聚居地能力接入 Layout 编排、提取、放置和生成选用。每片只开放当时支持的字段和行为，未实现能力明确显示；不先建设完整通用编辑框架。与铁路、生成等工作的整体依赖由[分期重组](https://github.com/odradekk/train/issues/145)统一安排。

每类内容用代表案例贯通创作、草稿重新打开、校验发布和实际使用；存档内内容验证新进程恢复，用户库内容验证另一个存档可复用。共享发布机制集中覆盖写入失败、引用缺失、来源已更新及适配拒绝等风险，不按三类内容复制同一矩阵。另验证模板更新不重建既有实例、不改建筑修复目标，副本不改变原引用；交互变化补必要人工检查。合法蓝图或 Layout 不能代替铁路运行与生成地图验收。测试选择遵守[最小充分覆盖标准](../implement-sop.md#testing-standard)。

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

- 四类有效来源：Built-in、Mod、用户内容库、Save-local Definitions。独立创作草稿不是有效内容来源。
- 玩家在当前世界发布自定义内容时默认进入 Save-local Definitions；显式发布到用户内容库后才可跨存档复用。库从原有 Player Blueprint Library 扩展为蓝图、Layout 和体素定义，跨范围复制按上文建立新身份及必要依赖。
- 已发布的存档内定义及其所需的其他存档内定义包含在普通存档中；未放置为实例不等于可以丢弃已发布设计。外部引用仍依赖当前安装内容，保存不会暗中嵌入外部旧定义以兜底。用户显式创建的新身份副本属于自己的内容，仍须保留并检查其外部依赖，不能据此宣称缺失 Mod 或资产也能加载。

### 内容目录与格式

| 来源 | 组织方式 |
| --- | --- |
| Built-in | `builtin/core/resources/` 下按 `voxels`、`composites`、`layouts` 分类 |
| Mod | `user://mods` 中各包的 `resources/` 下沿用相同结构 |
| 用户内容库 | 目标目录 `user://content-library/`，按类型使用体素、Composite、Layout 定义格式；旧 `user://blueprints/` 读取／迁移单独落实 |
| Save-local Definitions | 直接内嵌存档，不作为独立内容目录扫描 |
| 独立草稿（非有效来源） | `user://authoring-drafts/`，带版本的创作文档，不参与有效内容扫描 |

- 新定义文件使用明确的 `schema_version`，从 1 开始；按类型使用 `.voxels.json`、`.composites.json`、`.layouts.json`。目录用于组织文件，不决定定义身份。
- 存档位于 `user://saves/<存档 UUID>/save.json`。内容文件按稳定身份组织，显示名称保存在文件内，重命名不改变身份和引用；路径不替代定义内部的 DefinitionId。旧蓝图文件形式为 `user://blueprints/<蓝图 UUID>.composites.json`，其身份与原文件保护遵守上文迁移要求。
- 早期切片通过手写 JSON 和测试操作使用定义格式；本轮将创作视图纳入世界编辑器，按上述顺序分片交付，不再以“正式设计器延后”排除编辑器能力。

### ID 与命名空间

- 继续采用稳定的 namespaced identity：`namespace:local-id`，例如 `core:ferrous-ore`、`mod-namespace:titanium-engine`。文件路径不是 Definition 身份。
- 不同领域保持强类型 ID：VoxelDefinitionId、CompositeDefinitionId、CompositeArchetypeId、BehaviorTypeId 等。它们共享 Namespace 规则、Parser、Normalize、Validation，但不统一成一个完全无类型的 DefinitionId。
- ADR-0001 的规则沿用：built-in 使用 `core` 命名空间，Mod 命名空间来自 manifest UUID，定义文件只含 local id。
- 用户内容的新身份由系统基于 UUID 分配，命名空间不由显示名或当前存档文件 UUID 推导；内容更新、独立复制与整个存档复制的区别见[内容身份与跨范围依赖](#内容身份与跨范围依赖)。存档内解析须隔离，不能让另一存档的同标识内容覆盖当前定义。

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

VoxelDefinition；JSON/Data Mod；Definition、Revision、Instance 三分；Blueprint 加载、校验、制造及已有车辆的完整改装。本轮增加上文世界编辑器的蓝图、体素定义与 Layout 创作流程，按可用切片交付；原手写 JSON 与独立自动测试仍可用于验证，不代替常用创作界面。Composite 规则见 [03](03-composite-and-behavior.md)。

## 明确延后

TradeMetadata 的字段与实现、Code Mod API、通用 Material System；跨包覆盖与 patch、Mod 依赖、加载顺序覆盖、启用状态管理（ADR-0002）。

## 开放问题

- 无。库存系统与非堆叠 ItemInstance（转录 Q175）已由 [12](12-inventory-and-storage.md) 确定。
