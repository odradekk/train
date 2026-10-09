# 02 Definition、Revision、Instance 与内容来源

> **所属层**：内容。

> 决策出处：转录 Q53–Q62、Q86–Q88、Q176；2026-09-05 [#31](https://github.com/odradekk/train/issues/31) 确认迁移与内容格式，随后 [#35](https://github.com/odradekk/train/issues/35) 将读档及热重载修订为采用当前外部定义，并确定版本、哈希与存档规则。见 [ADR-0001](../adr/0001-use-namespaced-good-identities-and-versioned-content-schema.md)、[ADR-0002](../adr/0002-limit-mods-to-data-packages-and-isolate-package-failures.md)、[ADR-0005](../adr/0005-resolve-current-definitions-and-separate-content-versions.md)。Good 并入体素定义的代码与内容迁移已由 [#43](https://github.com/odradekk/train/issues/43) 完成。每种货物仍是体素定义、字段按层划分、删除 ItemInstance、碰撞移出语义字段、蓝图版本的保存，由[货物与内容定义是否继续绑定体素定义](https://github.com/odradekk/train/issues/204)于 2026-10-05 确认。内容来源、用户库只放蓝图、其他内容写入数据包，由[编辑器的去向](https://github.com/odradekk/train/issues/211)于 2026-10-05 确认，取代了存档内定义与跨范围复制的规则。

## 边界

本文定义"某种东西是什么"的数据、其版本与实例的关系、定义的来源、Mod 与热加载，以及库存中货物的表示。车辆与建筑、蓝图版本与设计合法性见 [03](03-vehicles-buildings-and-designs.md)；存档中内嵌的内容见 [09](09-persistence.md#存档中的内容)；设计编辑器与创作模式见 [15](15-design-editor-and-tools.md)；节点资源、Raw Good 与货物目录见 [10](10-resources-and-extraction.md)、[11](11-goods-and-production-network.md)；库存规则见 [12](12-inventory-and-storage.md)；经济价格属于市场上下文，规则留待后续扩充。

## 不变量

- Definition 是不可变数据；Runtime State 不写回 Definition。
- Semantic Revision 与 Presentation Asset Revision 分离。
- Price 属于 Market Context，不属于 Voxel。
- 每种货物都是一个体素定义；车辆和建筑不是货物。
- 核心模拟层只读取体素定义中的核心字段。
- 库存中的货物只按种类和数量计数，没有个体身份或个体状态。
- Capability 不允许在 Mod JSON 中伪造，由实际注册的 Behavior 推导（[03](03-vehicles-buildings-and-designs.md)）。

## 规则

### 体素定义

- 每种货物都是一个体素定义。游戏中所有物品的外观和图标都用体素块表现，所以货物不另设定义；货物、库存、市场和贸易都引用体素定义标识（转录 Q176）。TradeMetadata 的字段与实现随贸易接入时确定。
- 字段按读取方分为三组：

  | 读取方 | 字段 |
  | --- | --- |
  | 核心模拟层（直接读取，或经内容层派生的设计属性读取） | 身份、Value、Mass、Tags、耐久上限、Behaviors |
  | 体素引擎层与渲染层 | 允许朝向、表现、碰撞 |
  | 显示 | 名称、说明 |

- 核心模拟层通过只暴露第一组字段的接口读取体素定义，不接触体素字段。
- 允许朝向默认只有默认朝向，碰撞默认是整格方块；不放进设计的货物（例如矿石）不必填写这两项。表现必须填写；物品图标是表现中单独绘制的一张图，只画物品本身，外框、底色、数量和状态由界面统一绘制，见[美术 07](../art/07-icons-and-ui.md#物品图标)。
- 带 `scene_only` 标签的体素定义只供场景生成使用，例如地形和装饰。它们不是货物，不能库存、交易，也不能放进设计；具体内容见 [01](01-voxel-engine-and-scenes.md#场景中的内容)。其余体素定义都是货物，可以库存和交易；设计中每放一格消耗一个单位的该货物，见 [14](14-construction-refit-and-deconstruction.md)。
- **Value** 即术语表的基本货币价值：定义中的固定基础价值，不随世界经济变化，但可随内容更新改变，是市场计算实际价格的基础。例如 Ferrous Ore 的 Value = 100。
- **Price** 不属于体素定义。Price 是市场状态 × 体素定义产生的运行时报价，例如 Mining Town 的 Ferrous Ore = 70，Capital 的 Ferrous Ore = 135。
- **Mass** 是一个单位的质量，用于车辆和建筑的设计质量，以及列车所载货物的质量，见 [06](06-railway.md#速度)。
- **Material**：Demo 不建立通用 MaterialDefinition 系统。Cube 表现选一个表现材质类别，Custom Mesh 的每个表面各选一个，各类别的粗糙度和金属度由渲染层维护，见[美术 02](../art/02-materials-and-textures.md#材质类别)。只有当实际开发中大量体素重复相同材料规则时，再抽象 Material 层。
- **自然资源**：初级货物带 `raw_good` 标签，是普通可库存、可交易的体素定义。节点资源直接引用初级货物的体素定义标识，不设资源源体素、`resource_source` 标签或 ResourceSourceBehavior，见 [10](10-resources-and-extraction.md)（[资源与开采改为节点属性](https://github.com/odradekk/train/issues/205)，2026-10-05）。旧的“带 `natural_resource` 标签的体素自身就是货物”语义已废止。

### 迁移历史

[#43](https://github.com/odradekk/train/issues/43) 已一次性把根目录项目的 GoodDefinition、GoodId、GoodCatalog、内容文件及其引用迁移为体素定义与 VoxelDefinitionId，没有保留旧 goods schema 的兼容层；[#77](https://github.com/odradekk/train/issues/77) 已把可交易初级货物改为 `raw_good`。`archive/mvp/` 保持原样，不参与迁移。

### 库存中的货物

- 库存中的货物只按种类和数量计数，例如 Stack { 体素定义标识 = `core:ferrous-ore`, 数量 = 500 }。货物没有个体身份，也没有耐久等个体状态；库存规则见 [12](12-inventory-and-storage.md)。
- 货物在建造、改装、修理时被消耗，成为车辆或建筑蓝图版本中的体素；拆下的完好部件变回普通货物，受损的修好后返还，被毁的不返还，见 [03](03-vehicles-buildings-and-designs.md#建造改装与修理) 和 [14](14-construction-refit-and-deconstruction.md)。

### Definition、Revision 与 Instance

- **Definition** 定义"某种东西是什么"。
- **Revision** 是 Definition 的一个不可变语义版本。内部 Revision ID 使用 Semantic Content Hash，而不是 r1、r2、r3；完全相同的语义内容产生相同 Revision。UI 可额外维护纯数字的人类版本号（Version 1、2、3），只用于展示，不承担身份职责。
- Semantic Revision 用于识别规格变化，不强制实例锁定旧外部规格。读档与热重载按 DefinitionId 解析当前安装的定义；定义本身仍是不可变快照，更新通过发布新快照完成。
- **Instance** 是车辆或建筑的实例，保存引用的蓝图版本、部件耐久、结构耐久、所属关系等运行状态，见 [03](03-vehicles-buildings-and-designs.md#车辆与建筑实例)。Definition 永远不保存实例状态。

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
| Value、耐久上限、Mass、Tags、允许朝向 | 图片、模型、材质、声音的内容与路径 |
| Behavior 类型与参数 | 碰撞、纯视觉配置与资产绑定、编辑器排版等辅助信息 |
| 蓝图的体素布局（格、定义身份、朝向）与档位声明 | 蓝图的涂装；引用目标的 Revision |

- 先解析并校验为领域值，再生成规范化编码；不对原始 JSON 文本直接哈希。采用字段边界明确的规范化 JSON、UTF-8 编码，固定字段顺序；空白与源文件字段书写顺序不影响结果。
- 对象字段和字符串键按 Ordinal 顺序编码。标签和 Behavior 等无序集合稳定排序，Behavior 按类型标记的 Ordinal 顺序编码；体素布局按整数坐标稳定排序；有顺序含义的列表保留顺序。
- 数字按实际领域类型统一编码；`double` 使用与文化无关的无损往返表示，不截断或舍入到显示精度。有效数据不含 `NaN` 或无穷值。
- 语义引用记录目标 DefinitionId，不包含目标 Revision，也不递归哈希依赖内容。修改 Mod 引擎牵引力会改变引擎 Revision，不改变引用它的车辆设计自身的 Revision；使用方仍必须根据当前引擎规格重算受影响能力。
- 名称、说明与表现绑定作为按 DefinitionId 解析的当前显示信息处理，不因语义哈希相同而丢弃更新。碰撞只在场景中使用，属于表现数据，不参与语义哈希；更换显示模型不自动改变碰撞形状。
- 验收覆盖重新排版不变、更改有效语义字段产生变化、纯表现更新不改变语义 Revision、依赖更新生效但不改变引用方自身 Revision。

### Definition 来源

- 三类有效来源：Built-in、Mod、用户内容库。草稿不是有效内容来源。
- 用户内容库只保存玩家在设计编辑器中发布的蓝图。体素定义、聚居地模板，以及场景模型、生物等不由玩家设计的蓝图，由创作模式写入数据包：开发时写入 `core`，Mod 作者写入自己的 Mod 包，见 [15](15-design-editor-and-tools.md#创作模式)。
- 蓝图每次发布产生一个不可变的版本，以语义 Revision 识别。用户内容库保留所有已发布的版本；存档内嵌所有被引用的蓝图版本，不论来自内置、Mod 还是用户库，即使来源中已删除也能读档；已有聚居地使用的布局同样内嵌，见 [09](09-persistence.md#存档中的内容)。所以玩家可以随时删除库中的旧版本，不检查引用，见 [15](15-design-editor-and-tools.md#草稿与发布)。
- 体素定义等外部引用依赖当前安装的内容，保存时不会暗中嵌入外部旧定义以兜底。

### 内容目录与格式

| 来源 | 组织方式 |
| --- | --- |
| Built-in | `builtin/core/resources/` 下按内容类型分类 |
| Mod | `user://mods` 中各包的 `resources/` 下沿用相同结构 |
| 用户内容库 | 目标目录 `user://content-library/`，只保存玩家发布的蓝图；旧 `user://blueprints/` 读取／迁移单独落实 |
| 草稿（非有效来源） | `user://authoring-drafts/`，设计编辑器和创作模式的草稿，不参与有效内容扫描 |

定义文件带明确的 `schema_version`，各内容类型独立管理格式与版本。目录用于组织文件，不决定定义身份。当前已实现的格式如下：

| 定义类型 | 文件后缀 | `schema_version` |
| --- | --- | --- |
| 体素定义 | `.voxels.json` | 2 |
| 蓝图 | `.composites.json` | 2 |
| 市场表 | `.market-tables.json` | 1 |
| 聚居地类型 | `.settlement-types.json` | 1 |

- 聚居地模板的格式尚未实现，由[聚居地模板：初始状态、格式说明、Schema 与样例](https://github.com/odradekk/train/issues/266)决定。场景模型和生物的蓝图、生物聚落类型的格式也尚未实现，见 [01](01-voxel-engine-and-scenes.md#场景模型) 和 [17](17-combat.md#世界生物)。旧过渡格式 `settlements/*.settlements.json` 已退役，内容加载不创建旧聚居地实例，也不要求首次目录包含聚居地。聚居地类型与模板见 [16](16-settlements.md)；世界生成在生成边界校验所需内容和开局地点，见 [05](05-world-graph-and-generation.md)。
- 存档位于 `user://saves/<存档 UUID>/save.json`。内容文件按稳定身份组织，显示名称保存在文件内，重命名不改变身份和引用；路径不替代定义内部的 DefinitionId。旧蓝图文件形式为 `user://blueprints/<蓝图 UUID>.composites.json`，其身份与原文件保护遵守上文迁移要求。
- 早期切片通过手写 JSON 和测试操作使用定义格式；设计编辑器与创作模式见 [15](15-design-editor-and-tools.md)。

### ID 与命名空间

- 继续采用稳定的 namespaced identity：`namespace:local-id`，例如 `core:ferrous-ore`、`mod-namespace:titanium-engine`。文件路径不是 Definition 身份。
- 不同领域保持强类型 ID，例如体素定义标识和蓝图标识。它们共享 Namespace 规则、Parser、Normalize、Validation，但不统一成一个完全无类型的 DefinitionId。
- Behavior 目前只能由代码注册，内置 Behavior 类型标识是不带命名空间的字符串，例如 `propulsion`。带命名空间的强类型 BehaviorTypeId 在第二阶段 Code Mod 允许注册新 Behavior 时引入。
- ADR-0001 的规则沿用：built-in 使用 `core` 命名空间，Mod 命名空间来自 manifest UUID，定义文件只含 local id。定义声明的 `id` 和定义之间的引用都直接校验原文，首尾空白无效；强类型 ID 构造器仍按其公开契约规范化输入。
- 用户蓝图的身份由系统基于 UUID 分配，沿用 `namespace:local-id` 和类型区分，不由显示名推导，也不要求用户填写命名空间。修改自己的蓝图产生新版本，身份不变；“复制为新蓝图”建立新身份。存档内嵌的蓝图版本按各存档隔离解析，另存为或导入不重编身份。

### Mod 与热加载

- **第一阶段 Data Mod**：支持新增体素定义、蓝图、市场表和聚居地类型，以及修改自有定义的数值、引用资产、组合已有 Behavior；Layout 是目标格式，尚未实现。Demo 不允许覆盖或 patch 内置及其他 Mod 的定义，重复定义继续报错；不能直接注入任意 C# 代码（ADR-0002）。
- **第二阶段 Code Mod**：等游戏规则和公开 API 足够稳定后，再考虑 C# 插件、注册新 Behavior、注册新 System、公共 Mod API。
- **Hot Reload**：发布新的不可变定义快照，运行中实例立即解析并采用当前规格。修改耐久上限、Behavior、布局或引擎牵引力产生新的 Semantic Revision。修改 Texture、Mesh、Material 或 Sound 的文件字节更新相应的 Asset Revision；修改碰撞、资产绑定或纯表现配置不改变 Asset Revision。这两类有效变化都触发 Catalog 发布，不改变 Semantic Revision。蓝图更新产生新版本，不改变已有实例引用的版本。

### 内容更新与实例状态

- 读档和热重载都采用当前安装的外部定义，更新数值作用于已有车辆和建筑：实例保留引用的蓝图版本、部件耐久与运行状态，蓝图版本中的体素按定义身份采用当前定义，设计属性随之重算。
- 状态保留绝对值，仅在超出新定义允许范围时修正，不按比例换算、不自动恢复。原耐久上限 100、当前耐久 80：新上限 150 时仍为 80，新上限 60 时截到 60。
- 牵引力、质量等定义属性直接采用新值，并重算受影响的能力和派生状态。记录上次使用的 Revision 以识别变化，不借此保留旧外部规格。

### 候选内容与运行中引用校验

车辆和建筑引用的蓝图版本随存档保存。蓝图和聚居地模板的更新只影响以后的建造和改装，不改变已有实例引用的版本或已放置的聚居地。体素定义的数值、碰撞和表现更新作用于已有实例：重算设计属性，部件耐久保留绝对值并按新上限截断，见 [03](03-vehicles-buildings-and-designs.md#车辆与建筑实例)。实例引用的蓝图版本所需的体素定义，同样属于受影响的引用。

- 新加载的定义必须引用有效内容，非法 Mod 整包排除；内置内容出错则拒绝发布。排除后的候选内容仍必须自洽，不能留下未解析引用。
- 必须基于候选当前定义校验全部受影响的运行中引用与状态适配。缺失必需定义或无法解释旧状态时，拒绝整次热重载，保留原定义、Catalog Generation 和世界状态；不能发布部分更新或留下部分修正后的实例。
- 读档遇到相同问题时明确报告并失败。Demo 不自动替换体素、删除状态或回退到存档中的旧 Mod 定义。
- 全部校验与状态适配成功后再原子发布内容及相关实例更新。这替代 #31 曾确定的“保留旧 Revision 即可接受目录删除”规则。

### 表现资产校验

Cube、CustomStaticMesh 与领域专用表现共存（[07](07-rendering.md#总体策略)）；CustomStaticMesh 使用 LOD0 静态模型、基础颜色/贴图、光照/阴影和不透明或镂空裁剪材质。资产原点、尺寸、方向及适用表现绑定须有明确约定，具体文件格式和字段由实施规格记录；模型不能替代权威碰撞定义。

Cube 表现绑定 `icon`、`voxel_top`、`voxel_side`、`voxel_bottom` 四张 PNG，可选 `voxel_front`；另可声明平铺跨度 `tile_span`（1、2、4、8，默认 1）和表现材质类别 `material`（7 种，默认 `painted_metal`，取值见[美术 02](../art/02-materials-and-textures.md#材质类别)）。图标为正方形；顶面、侧面、底面为边长相同的 2 的幂正方形；正面图为 2 的幂正方形，尺寸可以与其他面不同。贴图方向与平铺坐标见 [07](07-rendering.md#cube-voxel-rendering)。这些字段都是表现配置，修改时只触发 Catalog 发布，不改变 Semantic Revision。由[立方体体素的面贴图形式](https://github.com/odradekk/train/issues/243)于 2026-10-09 确认。

Demo 中 Cube 表现可以再声明一个形状字段，从游戏内置的形状目录中选形状和标准变体，默认立方体；它同样是表现配置。表现不提供缩放和剪切参数，模型按原尺寸使用；要不同大小，就用不同的标准变体或不同的体素定义。加载 CustomStaticMesh 时检查模型的包围盒，超出所在的格（允许极小的容差）就明确诊断并拒绝。由[非立方体形状与构造语言](https://github.com/odradekk/train/issues/245)于 2026-10-09 确认。

模型、材质等必需资产缺失、损坏或不支持，以及专用渲染绑定不存在或不适用，均须明确诊断。首次加载沿用上文内容包校验与候选自洽规则；热重载失败保留旧 Catalog、世界及有效表现，不部分发布，也不以静默替换 Cube 或隐藏对象冒充成功。合法纯表现变化采用 Asset Revision/表现绑定及 Catalog 发布机制，更新受影响视觉，不改变 Semantic Revision、碰撞、身份、铁路连接或模拟结果。场景未打开不妨碍内容有效性校验，再次构建场景时使用当前有效内容。

## Demo 范围

体素定义（每种货物都是体素定义）；JSON/Data Mod；Definition、Revision、Instance 三分；蓝图版本的加载、校验、建造与改装；TradeMetadata 随贸易核心循环接入时确定字段与实现。面向玩家的设计编辑器属于 Demo；创作模式只在开发构建中提供，见 [15](15-design-editor-and-tools.md)。手写 JSON 与独立自动测试仍可用于验证。车辆与建筑的规则见 [03](03-vehicles-buildings-and-designs.md)。

## Demo 之后

Code Mod API、通用 Material System；跨包覆盖与 patch、Mod 依赖、加载顺序覆盖、启用状态管理（ADR-0002）。

## 开放问题

- 无。
