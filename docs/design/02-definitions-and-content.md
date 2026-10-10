# 02 Definition、Revision、Instance 与内容来源

> **所属层**：内容。

> 决策出处：转录 Q53–Q62、Q86–Q88、Q176；2026-09-05 [#31](https://github.com/odradekk/train/issues/31) 确认迁移与内容格式，随后 [#35](https://github.com/odradekk/train/issues/35) 将读档及热重载修订为采用当前外部定义，并确定版本、哈希与存档规则。见 [ADR-0001](../adr/0001-use-namespaced-good-identities-and-versioned-content-schema.md)、[ADR-0002](../adr/0002-limit-mods-to-data-packages-and-isolate-package-failures.md)、[ADR-0005](../adr/0005-resolve-current-definitions-and-separate-content-versions.md)。Good 并入体素定义的代码与内容迁移已由 [#43](https://github.com/odradekk/train/issues/43) 完成。每种货物仍是体素定义、字段按层划分、删除 ItemInstance、碰撞移出语义字段、蓝图版本的保存，由[货物与内容定义是否继续绑定体素定义](https://github.com/odradekk/train/issues/204)于 2026-10-05 确认。内容来源、用户库只放蓝图、其他内容写入数据包，由[编辑器的去向](https://github.com/odradekk/train/issues/211)于 2026-10-05 确认，取代了存档内定义与跨范围复制的规则。定义按读取方分节、内容层单独成为项目、场景体素取代 `scene_only` 标签，由[定义按层划分：核心、体素层和显示各读哪些数据](https://github.com/odradekk/train/issues/259)于 2026-10-09 确认，见 ADR-0011。复合体嵌套与能入库的复合体作为货物，由[复合体嵌套：大型设备装到车辆和建筑上](https://github.com/odradekk/train/issues/249)于 2026-10-09 确认，见 ADR-0013。内容包、依赖声明和读档缺内容时的占位，由[内容包与依赖：内容定义的文件组成、依赖声明、缺依赖处理与热插拔](https://github.com/odradekk/train/issues/262)于 2026-10-09 确认，见 ADR-0015。体素定义和 manifest 的格式、`cargo` 并入 `storage`，由[体素定义的格式说明、Schema 与样例](https://github.com/odradekk/train/issues/264)于 2026-10-09 确认，格式写在 [docs/content/](../content/README.md)。蓝图和设计对象的格式、设计包含名称和说明、嵌套不再单独要求贴着宿主，由[蓝图的格式说明、Schema 与样例](https://github.com/odradekk/train/issues/265)于 2026-10-09 确认，格式写在 [docs/content/blueprint.md](../content/blueprint.md)。聚居地类型和模板的格式、模板声明所属类型、起始建筑放进模板、建立聚居地时复制设计，由[聚居地模板：初始状态、格式说明、Schema 与样例](https://github.com/odradekk/train/issues/266)于 2026-10-09 确认，格式写在 [docs/content/](../content/README.md)。功能复合体按身份引用、读档缺内容时的状态保留、部件计入结构耐久与伤害结算、嵌套顺序和构成哈希，由[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)于 2026-10-09 确认。

## 边界

本文定义"某种东西是什么"的数据、其版本与实例的关系、定义的来源、Mod 与热加载，以及库存中货物的表示。车辆与建筑、设计与设计合法性见 [03](03-vehicles-buildings-and-designs.md)；存档中内嵌的内容见 [09](09-persistence.md#存档中的内容)；设计编辑器与创作模式见 [15](15-design-editor-and-tools.md)；节点资源、Raw Good 与货物目录见 [10](10-resources-and-extraction.md)、[11](11-goods-and-production-network.md)；库存规则见 [12](12-inventory-and-storage.md)；经济价格属于市场上下文，规则见 [19](19-markets-and-trading.md)。

## 不变量

- Definition 是不可变数据；Runtime State 不写回 Definition。
- Semantic Revision 与 Presentation Asset Revision 分离。
- Price 属于 Market Context，不属于 Voxel。
- 货物是带 `simulation` 一节的体素定义，或能入库的复合体；车辆和建筑不是货物。
- 核心模拟层只读取体素定义中的 `simulation` 一节。
- 库存中的货物只按种类和数量计数，没有个体身份或个体状态。
- Capability 不允许在 Mod JSON 中伪造，由实际注册的 Behavior 推导（[03](03-vehicles-buildings-and-designs.md)）。

## 规则

### 体素定义

- 带 `simulation` 一节的体素定义都是货物。游戏中所有物品的外观和图标都用体素块表现，所以货物不另设定义（转录 Q176）。能入库的复合体，例如大型设备，也是货物，按构成哈希计数，见 [03 的复合体嵌套](03-vehicles-buildings-and-designs.md#复合体嵌套)（[复合体嵌套：大型设备装到车辆和建筑上](https://github.com/odradekk/train/issues/249)，ADR-0013）。货物种类有三种：体素定义标识、功能复合体标识，或组装体（可编辑的大型设备，例如炮塔）的构成哈希。货物、库存、市场和贸易引用这三种标识（[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)）。交易所需的内容投影依据 [19](19-markets-and-trading.md)，具体类型由核心与内容契约共同确定，不预设独立的 TradeMetadata 实现。
- 定义文件按读取方分节，身份等共用字段写在顶层（[定义按层划分：核心、体素层和显示各读哪些数据](https://github.com/odradekk/train/issues/259)，2026-10-09）：

  | 节 | 读取方 | 字段 |
  | --- | --- | --- |
  | `simulation` | 核心模拟层（直接读取，或经内容层算出的设计属性读取） | Value、Mass、Tags、耐久上限、Behaviors 的核心参数 |
  | `appearance` | 体素引擎层与渲染层 | 允许朝向、允许形状、表现、碰撞 |
  | `display` | 界面 | 名称、说明、图标 |

- 内容层读取各节，只把 `simulation` 一节和由它算出的数据交给核心模拟层，沿用 [ADR-0011](../adr/0011-move-the-content-layer-into-its-own-project-and-split-definitions-by-reader.md)。核心所需的输入类型归核心契约，由内容实现填充；核心不引用内容契约或实现，看不到其他各节，依赖规则见[契约索引](../contracts/README.md#依赖与装配)。各节的字段见[体素定义的格式](../content/voxel.md)。
- 允许朝向默认只有默认朝向，碰撞默认是整格方块；不放进设计的货物（例如矿石）不必填写这两项。表现必须填写；物品图标属于 `display` 一节，是单独绘制的一张图，只画物品本身，外框、底色、数量和状态由界面统一绘制，见[美术 07](../art/07-icons-and-ui.md#物品图标)。
- 没有 `simulation` 一节的体素定义是场景体素，只供场景使用，例如地形和装饰。它们不是货物，核心模拟层看不到它们，不能库存、交易，也不能放进车辆、建筑和大型设备的设计；具体内容见 [01](01-voxel-engine-and-scenes.md#场景中的内容)。其余体素定义都是货物，可以库存和交易；设计中每放一格消耗一个单位的该货物，见 [14](14-construction-refit-and-deconstruction.md)。
- **Value** 即术语表的基本货币价值：定义中的固定基础价值，不随世界经济变化，但可随内容更新改变，是市场计算实际价格的基础。例如 Ferrous Ore 的 Value = 100。
- **Price** 不属于体素定义。Price 是市场状态 × 体素定义产生的运行时报价，例如 Mining Town 的 Ferrous Ore = 70，Capital 的 Ferrous Ore = 135。
- **Mass** 是一个单位的质量，用于车辆和建筑的设计质量，以及列车所载货物的质量，见 [06](06-railway.md#速度)。
- **Material**：Demo 不建立通用 MaterialDefinition 系统。Cube 表现选一个表现材质类别，Custom Mesh 的每个表面各选一个，各类别的粗糙度和金属度由渲染层维护，见[美术 02](../art/02-materials-and-textures.md#材质类别)。只有当实际开发中大量体素重复相同材料规则时，再抽象 Material 层。
- **自然资源**：初级货物带 `raw_good` 标签，是普通可库存、可交易的体素定义。节点资源直接引用初级货物的体素定义标识，不设资源源体素、`resource_source` 标签或 ResourceSourceBehavior，见 [10](10-resources-and-extraction.md)（[资源与开采改为节点属性](https://github.com/odradekk/train/issues/205)，2026-10-05）。旧的“带 `natural_resource` 标签的体素自身就是货物”语义已废止。

### 迁移历史

[#43](https://github.com/odradekk/train/issues/43) 已一次性把根目录项目的 GoodDefinition、GoodId、GoodCatalog、内容文件及其引用迁移为体素定义与 VoxelDefinitionId，没有保留旧 goods schema 的兼容层；[#77](https://github.com/odradekk/train/issues/77) 已把可交易初级货物改为 `raw_good`。`archive/mvp/` 保持原样，不参与迁移。

### 库存中的货物

- 库存中的货物只按种类和数量计数，例如 Stack { 体素定义标识 = `core:ferrous-ore`, 数量 = 500 }，或 Stack { 炮塔的构成哈希, 数量 = 2 }。功能复合体按它的标识计数，和体素货物一样。货物没有个体身份，也没有耐久等个体状态；库存规则见 [12](12-inventory-and-storage.md)。
- 货物在建造、改装、修理时被消耗，成为车辆或建筑设计中的体素或大型设备；拆下的完好部件变回普通货物，受损的修好后返还，被毁的不返还，见 [03](03-vehicles-buildings-and-designs.md#建造改装与修理) 和 [14](14-construction-refit-and-deconstruction.md)。

### Definition、Revision 与 Instance

- **Definition** 定义"某种东西是什么"。
- **Revision** 是 Definition 的一个不可变语义版本。内部 Revision ID 使用 Semantic Content Hash，而不是 r1、r2、r3；完全相同的语义内容产生相同 Revision。UI 可额外维护纯数字的人类版本号（Version 1、2、3），只用于展示，不承担身份职责。
- Semantic Revision 用于识别规格变化，不强制实例锁定旧外部规格。读档与热重载按 DefinitionId 解析当前安装的定义；定义本身仍是不可变快照，更新通过发布新快照完成。
- **Instance** 是车辆或建筑的实例，保存自己的设计、部件耐久、结构耐久、所属关系等运行状态，见 [03](03-vehicles-buildings-and-designs.md#车辆与建筑实例)。Definition 永远不保存实例状态。

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
| 引用的 DefinitionId | 引用目标的 Revision |

- 先解析并校验为领域值，再生成规范化编码；不对原始 JSON 文本直接哈希。采用字段边界明确的规范化 JSON、UTF-8 编码，固定字段顺序；空白与源文件字段书写顺序不影响结果。
- 对象字段和字符串键按 Ordinal 顺序编码。标签和 Behavior 等无序集合稳定排序，Behavior 按类型标记的 Ordinal 顺序编码；体素布局按整数坐标稳定排序；有顺序含义的列表保留顺序。
- 数字按实际领域类型统一编码；`double` 使用与文化无关的无损往返表示，不截断或舍入到显示精度。有效数据不含 `NaN` 或无穷值。
- 货币定价是 [19](19-markets-and-trading.md#货物的边际价格)明确的局部精确计算例外：价格参数不能先经过 `double` 再转分数。基础价格和费率用整数金额，倍率、敏感度和价差用万分比整数（1 倍 = 10000）；JSON 直接使用整数，公开类型不引入通用分数框架。核心内部用标准整数类型保留中间计算的精确性，到已定结算边界才取整，见[市场规则格式](../content/market-rule.md)和[公共数值约定](../contracts/conventions.md#身份数值和单位)。
- 蓝图和设计不是语义哈希的对象。蓝图的 Revision 就是它的设计对象的设计哈希；设计哈希包括名称、说明、形状和涂装。
- **构成哈希**是递归去掉名称、说明、形状和涂装（包括功能复合体引用上的涂装）后的设计哈希，完整的设计哈希仍包含它们。组装体货物、BOM 中被嵌套的组装体和市场上的货物种类都用构成哈希：改名、改形状或重涂不改变货物种类，改变构成才改变。存档为每个构成哈希记一份显示名称、说明和形状，取自第一次引入它的设计，之后不变；库存和市场显示这份名称，入库的组装体沿用这份形状。装在宿主上的设备用宿主设计自己的那份副本（它的名称、形状和涂装），不受这份显示名称和形状影响，见 [09](09-persistence.md#存档中的内容)（[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)；形状见[Demo 内容与资产清单：覆盖场景与制作验收](https://github.com/odradekk/train/issues/283)（2026-10-10））。
- 设计哈希、部件编号和改装时的部件对应使用同一种遍历顺序：先排宿主自己的体素，再按 `at` 排被嵌套的复合体，坐标按 x、y、z 从小到大，见 [03](03-vehicles-buildings-and-designs.md#部件与结构)。功能复合体的引用在设计哈希里只含引用本身，不含被引用的内容。
- 语义引用记录目标 DefinitionId，不包含目标 Revision，也不递归哈希依赖内容。修改 Mod 引擎牵引力会改变引擎 Revision，不改变引用它的车辆设计的设计哈希；使用方仍必须根据当前引擎规格重算受影响能力。
- 名称、说明与表现绑定作为按 DefinitionId 解析的当前显示信息处理，不因语义哈希相同而丢弃更新。碰撞只在场景中使用，属于表现数据，不参与语义哈希；更换显示模型不自动改变碰撞形状。
- 验收覆盖重新排版不变、更改有效语义字段产生变化、纯表现更新不改变语义 Revision、依赖更新生效但不改变引用方自身 Revision。

### Definition 来源

- 有效来源：Built-in、Mod、用户内容库；读档后还有存档中的本局蓝图库。草稿不是有效内容来源。
- 本局蓝图库和用户内容库只保存玩家在设计编辑器中保存的蓝图。体素定义、聚居地模板，以及场景模型、生物等不由玩家设计的蓝图，由创作模式写入数据包：开发时写入 `core`，Mod 作者写入自己的 Mod 包，见 [15](15-design-editor-and-tools.md#创作模式)。
- 蓝图只是模板，不分版本，修改时覆盖。建造（包括委托）或世界生成时，从蓝图复制一份设计给实例，之后二者无关（[实例与蓝图：实例保存自己的设计，模板的存放与改装目标](https://github.com/odradekk/train/issues/260)，ADR-0012）；[现车买卖](19-markets-and-trading.md#现车交易与估值)转移既有实例，不重新复制蓝图。设计不可变，以设计哈希识别，即对设计全部内容（含名称、说明和涂装）的规范化编码计算的 SHA-256；存档内嵌实例、任务、委托和改装草稿用到的设计，相同内容只存一份；已有聚居地使用的聚居地模板同样内嵌，见 [09](09-persistence.md#存档中的内容)。所以玩家可以随时修改或删除库中的蓝图，不检查引用，见 [15](15-design-editor-and-tools.md#草稿与保存)。
- 体素定义等外部引用依赖当前安装的内容，保存时不会暗中嵌入外部旧定义以兜底。

### 内容目录与格式

| 来源 | 组织方式 |
| --- | --- |
| Built-in | `builtin/core/resources/` 下按内容类型分类；`core` 也是一个内容包，版本就是游戏版本 |
| Mod | `user://mods` 中各包的 `resources/` 下沿用相同结构 |
| 用户内容库 | 目标目录 `user://content-library/`，只保存玩家的蓝图，每个蓝图一个文件 `<UUID>.blueprint.json`；旧 `user://blueprints/` 读取／迁移单独落实 |
| 本局蓝图库 | 存档的内嵌内容 |
| 草稿（非有效来源） | 已明确保存的改装草稿和本局蓝图库草稿存在存档里；用户内容库蓝图和创作模式的草稿在 `user://authoring-drafts/`；未保存的编辑修改不自动入档，都不参与有效内容扫描 |

目标结构（[内容包与依赖：内容定义的文件组成、依赖声明、缺依赖处理与热插拔](https://github.com/odradekk/train/issues/262)，ADR-0015）：一个文件只写一份内容定义，文件名是 `<local id>.<种类>.json`，资产放在同名文件夹里，几个定义共用的资产放在 `shared/`；文件按种类分目录：`voxels/`、`blueprints/`、`processes/`、`settlement-types/`、`settlement-templates/`、`markets/`。字段写法、Schema 和示例包见[内容定义标准](../content/README.md)；体素定义、蓝图、聚居地类型和聚居地模板的后缀是 `.voxel.json`、`.blueprint.json`、`.settlement-type.json` 和 `.settlement-template.json`。首切片临时 `.market-table.json` 只保留为格式记录，Demo 改用 [19 的共用市场规则](19-markets-and-trading.md#一局共用的市场规则)，具体字段与新增规则目录见[市场规则格式](../content/market-rule.md)及内容定义标准。manifest 加入 `dependencies`（[体素定义的格式说明、Schema 与样例](https://github.com/odradekk/train/issues/264)）。

定义文件带明确的 `schema_version`，各内容类型独立管理格式与版本。目录用于组织文件，不决定定义身份。当前已实现的格式如下，一个文件可以写多份定义，将由实施工单改成目标结构：

| 定义类型 | 文件后缀 | `schema_version` |
| --- | --- | --- |
| 体素定义 | `.voxels.json` | 2 |
| 蓝图 | `.composites.json` | 2 |
| 市场表 | `.market-tables.json` | 1 |
| 聚居地类型 | `.settlement-types.json` | 1 |

- 聚居地模板、场景模型和生物蓝图的格式见[内容定义标准](../content/README.md)，代码尚未实现；生物聚落类型见 [17](17-combat.md#生物聚落)。旧过渡格式 `settlements/*.settlements.json` 已退役，内容加载不创建旧聚居地实例，也不要求首次目录包含聚居地。聚居地类型与模板见 [16](16-settlements.md)；世界生成在生成边界校验所需内容和开局地点，见 [05](05-world-graph-and-generation.md)。
- 存档位于 `user://saves/<存档 UUID>/save.json`。内容文件按稳定身份组织，显示名称保存在文件内，重命名不改变身份和引用；路径不替代定义内部的 DefinitionId。旧蓝图文件形式为 `user://blueprints/<蓝图 UUID>.composites.json`，其身份与原文件保护遵守上文迁移要求。
- 早期切片通过手写 JSON 和测试操作使用定义格式；设计编辑器与创作模式见 [15](15-design-editor-and-tools.md)。

### ID 与命名空间

- 继续采用稳定的 namespaced identity：`namespace:local-id`，例如 `core:ferrous-ore`、`mod-namespace:titanium-engine`。文件路径不是 Definition 身份。
- 不同领域保持强类型 ID，例如体素定义标识和蓝图标识。它们共享 Namespace 规则、Parser、Normalize、Validation，但不统一成一个完全无类型的 DefinitionId。
- Behavior 类型只能由游戏代码提供，内容只选类型、填参数，不能自带脚本；内置 Behavior 类型标识是不带命名空间的字符串，例如 `propulsion`。内容里写了未知类型，整个包无效。Demo 的类型清单见 [03](03-vehicles-buildings-and-designs.md#behavior-组合)。带命名空间的强类型 BehaviorTypeId 在第二阶段 Code Mod 允许注册新 Behavior 时引入。
- ADR-0001 的规则沿用：built-in 使用 `core` 命名空间，Mod 命名空间来自 manifest UUID，定义文件只含 local id。定义声明的 `id` 和定义之间的引用都直接校验原文，首尾空白无效；强类型 ID 构造器仍按其公开契约规范化输入。
- 用户蓝图的身份是系统分配的 UUID，写在 `id` 里，不由显示名推导，也不要求用户填写命名空间。修改自己的蓝图时覆盖内容，身份不变；“复制为新蓝图”和在本局蓝图库、用户内容库之间复制都建立新身份。存档中的本局蓝图库按各存档隔离解析，另存为或导入不重编身份。

### Mod 与热加载

- **内容包与依赖**（[内容包与依赖：内容定义的文件组成、依赖声明、缺依赖处理与热插拔](https://github.com/odradekk/train/issues/262)，ADR-0015）：
  - 内容包是发布、启用和依赖的单位，内容定义是引用和热重载的单位，属于且只属于一个包。
  - 包在 manifest 里声明依赖：包的 UUID 和最低版本；`core` 默认依赖。定义本身不声明依赖：引用了其他包的定义，那个包必须写在依赖里，否则这个包无效。
  - 加载按依赖排序；处在依赖循环里的包都不加载。缺依赖、依赖版本太低或包内任何定义有错，整个包都不加载，依赖它的包也跟着不加载。
  - 本局蓝图库和用户内容库的蓝图记下保存时用到的包（UUID、名称、版本），只用于显示；缺依赖时显示为不可用并列出缺了哪些包，不删除，也不能建造。
  - 玩家构建在主菜单里启用或停用 Mod，下次读档时生效。定义改名用的迁移表放到 Demo 之后。
- **第一阶段 Data Mod**：支持新增体素定义、蓝图、流程、聚居地类型、聚居地模板、人口/市场/施工/铁路/战斗规则、生物聚落类型、地貌和世界生成配置，修改自有定义的数值和资产，选用已有行为类型并填参数。格式、Schema、示例与投影已由[内容契约](../contracts/content.md)交付；加载器与模拟的接入仍待实施，不能直接套用临时市场表。Demo 不允许覆盖或 patch 内置及其他 Mod 的定义，重复定义继续报错；不能直接注入任意 C# 代码（ADR-0002）。
- **第二阶段 Code Mod**：等游戏规则和公开 API 足够稳定后，再考虑 C# 插件、注册新 Behavior、注册新 System、公共 Mod API。
- **Hot Reload**：发布新的不可变定义快照，运行中实例立即解析并采用当前规格。修改耐久上限、Behavior 或引擎牵引力产生新的 Semantic Revision。修改 Texture、Mesh、Material 或 Sound 的文件字节更新相应的 Asset Revision；修改碰撞、资产绑定或纯表现配置不改变 Asset Revision。这两类有效变化都触发 Catalog 发布，不改变 Semantic Revision。蓝图更新不改变已有实例的设计。

### 内容更新与实例状态

- 读档和热重载都采用当前安装的外部定义，更新数值作用于已有车辆和建筑：实例保留自己的设计、部件耐久与运行状态，设计中的体素按定义身份采用当前定义，设计属性随之重算。
- 状态保留绝对值，不按比例换算、不自动恢复。存档里的耐久值不因上限变小而改写；计算时取存档值和当前上限中较小的一个，伤害和修理按这个值结算并写回。例如上限 100、当前 80：上限降到 60 时按 60 计算，上限改回 100 又是 80（[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)）。
- 牵引力、质量等定义属性直接采用新值，并重算受影响的能力和派生状态。记录上次使用的 Revision 以识别变化，不借此保留旧外部规格。
- 已经开始的生产批次、施工任务和人口统计期按各自规则保留有限执行参数；这不是外部定义缺失时的兜底机制。业务规则分别见 [13](13-production-runtime-and-facilities.md)、[14](14-construction-refit-and-deconstruction.md)和 [18](18-population-and-labor.md#内容缺失与更新)。市场重评进度、现车名额及租约按 [19](19-markets-and-trading.md#内容更新与恢复)适配，不追改历史事实。

### 候选内容与运行中引用校验

车辆和建筑的设计随存档保存。蓝图和聚居地模板的更新只影响以后的建造和改装，不改变已有实例的设计或已放置的聚居地。体素定义的数值、碰撞和表现更新作用于已有实例：重算设计属性，部件耐久保留绝对值，计算时取存档值和新上限中较小的一个，见 [03](03-vehicles-buildings-and-designs.md#车辆与建筑实例)。实例的设计所需的体素定义，同样属于受影响的引用。

- 新加载的定义必须引用有效内容，非法 Mod 整包排除；内置内容出错则拒绝发布。排除后的候选内容仍必须自洽，不能留下未解析引用。
- 必须基于候选当前定义校验全部受影响的运行中引用与状态适配，包括人口本期执行参数仍需使用的引用。原有效的必需定义变为缺失或无法解释旧状态时，拒绝整次热重载，保留原定义、Catalog Generation 和业务状态；不能发布部分更新或留下部分修正后的实例。已进入核心入口的拒绝仍消耗命令编号并录制，不能把业务不变解释成计数和记录也不变。
- 已由读档确认的占位可原样保留或部分恢复，不要求一次补齐全部缺失；但不能让原有效的运行引用变成缺失，也不能借占位新增非法运行状态。缺 A、B 时仅补回 A 可发布，若同时移除原有效的 C 则整次拒绝。未使用蓝图和草稿的保存合法性与运行必需引用分开；候选包自身仍须自洽。
- 读档遇到缺失的定义时，先列出缺失的包和定义，由玩家选择取消或继续。继续时缺失的东西用占位代替并保留原来的标识：设计里缺失的体素和功能复合体变成占位，没有能力、质量和价值，不计入结构耐久上限，也不受伤害；存档里它们的耐久记录按部件编号原样保留；库存里缺失的货物保留为缺失货物，不能使用和交易；其他缺失的定义同样保留标识、功能失效。存档时写回原来的标识，内容装回后恢复（[内容包与依赖：内容定义的文件组成、依赖声明、缺依赖处理与热插拔](https://github.com/odradekk/train/issues/262)，ADR-0015）。不回退到存档中的旧定义，也不删除状态。
- 全部校验与状态适配成功后再原子发布内容及相关实例更新。这替代 #31 曾确定的“保留旧 Revision 即可接受目录删除”规则。

候选固定定义与资产字节，缺内容确认只针对该候选及缺失清单。准备、统一提交、取消、失败分类、资源退役及录制由[跨层协议](../contracts/transactions.md)规定。核心输入或其引用身份改变才提交核心内容发布命令；纯表现更新只发布内容世代及观察记录，不改变核心检查点；有效内容完全不变则不发布。混合变更仍整体成功或失败，不由各模块各自切换。

### 表现资产校验

Cube、CustomStaticMesh 与领域专用表现共存（[07](07-rendering.md#总体策略)）；CustomStaticMesh 使用 LOD0 静态模型、基础颜色/贴图、光照/阴影和不透明或镂空裁剪材质。模型文件是 `.glb`，单位、原点、轴和文件内容的限制见[体素定义的格式](../content/voxel.md#模型文件的约定)；模型不能替代权威碰撞定义。

Cube 表现绑定顶面、侧面、底面三张 PNG，可选正面图，物品图标写在 `display` 一节，字段写法见[体素定义的格式](../content/voxel.md#presentation立方体)；另可声明平铺跨度 `tile_span`（1、2、4、8，默认 1）和表现材质类别 `material`（7 种，默认 `painted_metal`，取值见[美术 02](../art/02-materials-and-textures.md#材质类别)）。图标为正方形；顶面、侧面、底面为边长相同的 2 的幂正方形；正面图为 2 的幂正方形，尺寸可以与其他面不同。贴图方向与平铺坐标见 [07](07-rendering.md#cube-voxel-rendering)。这些字段都是表现配置，修改时只触发 Catalog 发布，不改变 Semantic Revision。由[立方体体素的面贴图形式](https://github.com/odradekk/train/issues/243)于 2026-10-09 确认。

Demo 中 Cube 表现可以再声明 `shapes`，列出这种体素允许的形状，取自游戏内置的形状目录（含标准变体），不能为空、不能重复，默认只有立方体；它同样是表现配置。放置时由设计的每一格选其中一种，不选就用 `shapes` 的第一项；选了不在列表里的形状是校验错误。所有放置体素的布局（车辆、建筑、大型设备、场景模型、生物）都这样，自定义模型没有形状，格子也不能写形状。形状只是外观：不参与语义哈希，核心模拟层不读取，不影响货物身份、价值、质量、耐久、能力和 BOM，每一格仍算一件该货物，所以楔形的结构块和立方体的结构块是同一种货物。表现不提供缩放和剪切参数，模型按原尺寸使用；要不同大小，就用不同的标准变体或不同的体素定义。加载 CustomStaticMesh 时检查模型的包围盒，超出所在的格（允许极小的容差）就明确诊断并拒绝。由[非立方体形状与构造语言](https://github.com/odradekk/train/issues/245)于 2026-10-09 确认；形状由放置时选择、不再写在定义上，由[Demo 内容与资产清单：覆盖场景与制作验收](https://github.com/odradekk/train/issues/283)（2026-10-10）确认。取舍见 [ADR-0018](../adr/0018-choose-voxel-shapes-per-placement-so-shapes-never-create-goods.md)。

模型、材质等必需资产缺失、损坏或不支持，以及专用渲染绑定不存在或不适用，均须明确诊断。首次加载沿用上文内容包校验与候选自洽规则；热重载失败保留旧 Catalog、世界及有效表现，不部分发布，也不以静默替换 Cube 或隐藏对象冒充成功。合法纯表现变化采用 Asset Revision/表现绑定及 Catalog 发布机制，更新受影响视觉，不改变 Semantic Revision、碰撞、身份、铁路连接或模拟结果。场景未打开不妨碍内容有效性校验，再次构建场景时使用当前有效内容。

### 资产的交接

以下规则由[资产访问与导入：内容、体素和 Godot 的交接契约](https://github.com/odradekk/train/issues/286)于 2026-10-10 确认，取舍见 [ADR-0017](../adr/0017-hand-off-presentation-assets-as-raw-bytes-keyed-by-asset-revision.md)，各模块的责任见[契约索引](../contracts/README.md#表现资产的交接)。

- **身份**：Asset Revision 是各层引用资产的身份。内容层把定义里的资产路径解析成包和包内路径，读取文件字节并计算 Asset Revision；体素引擎层和 Godot 只按 Asset Revision 引用贴图和模型。相同字节只有一份运行资源，改文件名或几个定义共用一张图都不重复上传。路径只用于诊断，内容层提供从 Asset Revision 到来源（包和路径，可以有多个）的查询。
- **原始字节**：内容层保存资产的原始字节，随发布的 Catalog 一起不可变。Godot 上传时取这份字节自己解码，不按路径重新读文件，所以画面用的始终是通过校验的那一份。
- **检查**：内容层通过宿主的资产检查接口取得元数据，再执行全部规则。PNG 返回宽和高；模型返回带网格的节点数、表面数和每个表面的透明方式、计入节点变换后的包围盒（单位为米），以及有没有骨骼、形变目标、动画、相机、灯光或外部贴图。检查接口只报告事实，不判断是否合格。
- **失败**：资产失败分为文件缺失、超过大小上限、路径经过链接、无法解码、内容不受支持五种；元数据不符合规则时另报规则错误，例如尺寸不对、包围盒超出格、表面数和 `surface_materials` 不一致。诊断写明包、资产路径、定义、字段和失败种类。后果按上文：Mod 整包无效，内置内容拒绝发布，热重载整次拒绝。通过检查的字节由同一个解码器上传，之后再失败按引擎故障报告，不回滚内容。
- **读取路径**：开发目录、Mod 和导出包都在运行时读取原始字节，内容资产不使用 Godot 编辑器导入后的产物。内置的 PNG 和 `.glb` 用 Keep File 导入，原文件进入导出包。热重载只在 Debug 构建中提供；导出包里的内置内容只读，只能重载 Mod。
- **热重载**：资源创建不参与能否发布的决定。热重载被拒绝时，为候选内容做的检查结果全部丢弃。发布后怎样换上新资源、释放旧资源，见[契约索引](../contracts/README.md#表现资产的交接)。

## Demo 范围

体素定义；货物是带 `simulation` 的体素定义或能入库的复合体；JSON/Data Mod；Definition、Revision、Instance 三分；蓝图和设计的加载、校验、建造与改装；人口与市场规则及各模块所需的内容投影。面向玩家的设计编辑器属于 Demo；创作模式只在开发构建中提供，见 [15](15-design-editor-and-tools.md)。手写 JSON 与独立自动测试仍可用于验证。车辆与建筑的规则见 [03](03-vehicles-buildings-and-designs.md)。

## Demo 之后

Code Mod API、通用 Material System；跨包覆盖与 patch、加载顺序覆盖（ADR-0002）；定义改名用的迁移表（ADR-0015）。

## 开放问题

- [内容契约](../contracts/content.md)与[核心契约](../contracts/core.md)已明确规则格式、投影和金额边界；独立行为验证、生产代码迁移与正式内容制作仍待后续任务，不能以 Schema 或声明编译通过代替加载、模拟和完整 Demo 验收。
