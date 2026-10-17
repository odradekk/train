# 蓝图

一个蓝图描述一种复合体：车辆、建筑、大型设备、场景模型或生物。文件名是 `<local id>.blueprint.json`，放在 `resources/blueprints/`。Schema 见 [schemas/blueprint.schema.json](schemas/blueprint.schema.json)，示例见 [examples/](examples/)。

规则的来由：实例与蓝图见 [ADR-0012](../adr/0012-give-each-composite-instance-its-own-design-and-keep-blueprints-as-templates.md)，嵌套和货物见 [ADR-0013](../adr/0013-nest-composites-by-copying-designs-and-treat-storable-composites-as-goods.md)，行为和功能复合体见 [ADR-0014](../adr/0014-use-code-provided-behavior-types-and-sealed-functional-composites.md)，合法性见设计文档 [03 的设计合法性](../design/03-vehicles-buildings-and-designs.md#设计合法性)。

**蓝图 = `schema_version` + `id` + 一份设计对象。** 被嵌套的复合体存的是一份设计对象的副本，存档里内嵌的设计也用同一种写法。

节选：

```json
{
  "schema_version": 3,
  "id": "mg-turret",
  "category": "large_equipment",
  "layout": {
    "palette": [
      { "voxel": "hull-plate", "paint": "#626750" },
      { "voxel": "machine-gun", "paint": "#626750" }
    ],
    "cells": [[0, 0, 0, 0], [0, 1, 0, 1]]
  },
  "appearance": {
    "behaviors": { "rotation": { "axis": "y", "pivot": [0, 0, 0], "range": [-180, 180] } }
  },
  "display": { "name": "机枪塔", "description": "……" }
}
```

## 顶层

| 字段 | 类型 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- |
| `schema_version` | 整数 | 是 | 固定为 3 | 首个切片 |
| `id` | 字符串 | 是 | local id，必须和文件名一致；玩家的蓝图是 UUID | 首个切片 |
| `used_packages` | 对象数组 | 玩家的蓝图必填，包里的蓝图不能写 | 保存时用到的内容包，每项是 `id`（UUID 或 `core`）、`name`、`version`，只用于显示。缺依赖时蓝图显示为不可用，并列出缺了哪些包 | Demo |
| 设计对象的各字段 | — | — | 见下一节 | — |

## 设计对象

本页规定有效蓝图和设计的格式、计算与合法性。编辑器可打开和保存不合法草稿，包括保留缺失引用的草稿；草稿不进入有效内容目录，保存蓝图、立即应用或提交改装前仍须完整校验。草稿的公开表示、诊断和持久化边界见[编辑协议](../contracts/editing.md)，不能直接用有效蓝图的准入条件拒绝所有不合法草稿。

| 字段 | 类型 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- |
| `category` | 字符串 | 是 | 蓝图类别，由代码提供：`vehicle`、`building`、`large_equipment`、`scene_model`、`creature` | Demo；历史首切片仅有 `vehicle` |
| `simulation` | 对象 | 看类别 | 核心模拟层读取的字段，见[各类别的字段](#各类别的字段) | 首个切片 |
| `layout` | 对象 | 是 | 布局：自己的体素和被嵌套的复合体，至少有一格或一个被嵌套的复合体 | 首个切片 |
| `appearance` | 对象 | 否 | 体素引擎层读取的字段，见[各类别的字段](#各类别的字段) | Demo |
| `display` | 对象 | 是 | `name`：1–100 个字符；`description`：1–2000 个字符 | 首个切片 |

- **设计哈希**：对整个设计对象的规范化编码计算 SHA-256，包括名称、说明、形状和涂装，不包括 `schema_version`、`id` 和 `used_packages`。编码规则见[设计文档 02 的语义哈希字段与规范化](../design/02-definitions-and-content.md#语义哈希字段与规范化)：格子按坐标排序，被嵌套的复合体按 `at` 排序，调色板按实际使用展开，所以调色板的写法和列表顺序不影响哈希。两份布局相同、名称不同的设计是两份设计。蓝图的修订就是它的设计哈希。
- **构成哈希**：去掉名称、说明、形状和涂装（每一层都去掉，包括功能复合体引用上的 `paint`）以后计算的哈希。组装体作为货物、在 BOM 和市场里都按构成哈希区分，只改名、改形状或涂装不改变货物种类，改了构成才改变。存档给每个构成哈希记一个显示名、说明和形状，取它第一次出现时那份设计的，装在宿主上的设备用宿主设计自己的那份（[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)；形状见 [Demo 内容与资产清单：覆盖场景与制作验收](https://github.com/odradekk/train/issues/283)，2026-10-10）。
- **货物种类**有三种：体素定义标识、功能复合体标识、组装体的构成哈希。
- **顺序和部件编号**：坐标按 x、y、z 依次比较，从小到大。部件编号覆盖所有体素，不论是不是部件：先排自己的体素，再按 `at` 排被嵌套的复合体，组装体内部递归同样排列，功能复合体整件占一个号。编号不看定义，内容缺失也不会移动。设计哈希、部件编号、改装前后的部件对应和存档里的部件记录，都用这同一个顺序。
- **设计属性**（质量、能力、结构耐久上限、部件表、BOM、建造合法性、车辆长度等）由内容层计算，不写在文件里。
- **生物例外**：生物个体不复制设计，只引用生物种类，读档和热重载时用当前定义（ADR-0012 的修订）。
- **没有图标字段**：蓝图库的缩略图和大型设备作为货物时的图标，由游戏按设计自动渲染，见[美术 07 的物品图标](../art/07-icons-and-ui.md#物品图标)。

## 坐标与朝向

坐标是整数格，一格边长 0.5 米。+X 向右，+Y 向上，+Z 向前（体素的正面）。排序时按 x、y、z 依次比较，从小到大。

| 类别 | 原点 | 前方 |
| --- | --- | --- |
| 车辆 | 纵向中点、车体中心线、轨面；y=0 是轨面上的第一层格 | +Z 是车头方向。档位包络的范围：长 L 时 z 取 −⌊L/2⌋ 到 L−⌊L/2⌋−1，宽 W 时 x 取 −⌊W/2⌋ 到 W−⌊W/2⌋−1，高 H 时 y 取 0 到 H−1；车辆可以不填满包络 |
| 建筑 | 地块占地的中心、地面；y=0 是地面上的第一层格 | +Z 朝地块的正面，即临路或临轨的一边 |
| 大型设备、场景模型、生物 | 由作者自定，建议取底面中心 | 由作者自定 |

朝向写成 `<right>_<up>`：局部 +X 和局部 +Y 在宿主中指向哪根轴，轴写成 `px`、`nx`、`py`、`ny`、`pz`、`nz`；局部 +Z 等于 right × up。共 24 种，`px_py` 是默认朝向。体素定义的 `four_way` 允许 `px_py`、`pz_py`、`nx_py`、`nz_py`。

## `layout`

| 字段 | 类型 | 默认值 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- |
| `palette` | 对象数组 | `[]` | 用到的“体素、朝向、形状、涂装”组合，见下表 | 首个切片 |
| `cells` | 数组的数组 | `[]` | 每格 `[x, y, z, 调色板序号]`；坐标不能重复，序号必须在调色板内 | 首个切片 |
| `composites` | 对象数组 | `[]` | 被嵌套的复合体，见下表；列表顺序没有意义 | Demo |

调色板的每一项：

| 字段 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- |
| `voxel` | 是 | 体素定义的引用 | 首个切片 |
| `orientation` | 否 | 默认 `px_py`；必须是体素定义允许的朝向。只按这一份设计自己的调色板检查，被嵌套后整体转动不受体素朝向的限制 | 首个切片 |
| `shape` | 否 | 形状目录里的名字；不写就是体素定义 `shapes` 的第一项。必须在该定义的 `shapes` 里，否则报错；自定义模型体素不能写。形状只是外观，不改变货物种类，每一格仍算一件该货物（[Demo 内容与资产清单：覆盖场景与制作验收](https://github.com/odradekk/train/issues/283)，2026-10-10） | Demo |
| `paint` | 否 | 主色，小写 `#rrggbb`；不写时的外观、内置设计默认色和可涂区域的目标规则见[美术 03 的玩家涂装](../art/03-color-and-paint.md#玩家涂装)，迁移责任见[美术 03 的本阶段](../art/03-color-and-paint.md#本阶段)。不能涂装的材质类别忽略该字段，不报错 | Demo |

被嵌套的复合体有两种写法（[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)）：

- **组装体**（例如炮塔）复制设计：写 `design`。
- **功能复合体**按身份引用：写 `blueprint` 和可选的 `paint`。读档和热重载时用当前定义，参数随内容更新；设计哈希只包含引用。它的定义缺失时按占位处理，占格未知。

| 字段 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- |
| `at` | 是 | `[x, y, z]`，被嵌套设计的原点放在宿主网格的哪一格；同一个宿主里不能重复 | Demo |
| `orientation` | 否 | 默认 `px_py`，24 种都可以 | Demo |
| `design` | 组装体必填 | 一份完整的设计对象，从蓝图复制而来，不引用蓝图的 `id`。复制的设计里任何一层都不能有功能性行为 | Demo |
| `blueprint` | 功能复合体必填 | 功能复合体的引用 | Demo |
| `paint` | 否 | 只用于功能复合体，整件一个颜色 | Demo |

**引用的解析**：设计里的引用按它所在的文件解析。内容包里的文件可以写 local id，指本包的定义；复制到其他包或玩家蓝图里的设计，引用一律写完整标识，例如 `5e1f0c3a-…:hull-plate`。

## 各类别的字段

| 类别 | `simulation` | `appearance` | 用哪种体素 | 能装进去的 |
| --- | --- | --- | --- | --- |
| `vehicle` | `size_class`，必填：`small`、`medium`、`large`、`extra_large`、`titan` | 无 | 货物体素 | 大型设备 |
| `building` | `plot_size`，必填：`small`、`medium`、`large`、`giant`、`small_station`、`large_station`；`building_type`，必填；`facility_tags`，默认 `[]` | 无 | 货物体素 | 大型设备 |
| `large_equipment` | `behaviors`：只有功能复合体才写 | `behaviors.rotation`，可选 | 货物体素 | 大型设备；功能复合体不能装 |
| `scene_model` | 没有这一节 | `flatten`，可选 | 场景体素 | 场景模型 |
| `creature` | `behaviors`：`creature` 必填，`weapon` 可选 | 无 | 场景体素 | 无 |

- 建筑设计若包含 `construction`（含嵌套功能复合体），声明的最高车辆档位必须放得进建筑自身 `plot_size` 包络；比较高度，并允许水平旋转 90°。不以模板实际放置地块替代此检查；放不下时拒绝加载。`max_plot_size` 为必填的普通建筑上限，`max_size_class` 缺省不能造车，见[体素行为参数](voxel.md#功能性行为的核心参数)。
- `building_type` 和 `facility_tags` 的取值由代码提供，写错了整个包无效。Demo 的建筑类型有 6 种：`production`、`warehouse`、`power`、`construction`、`station`、`defense`，只有 `station` 有放置约束；设施兼容标签是 Demo 的 10 个，见[Demo 内容与资产清单](demo-catalog.md#流程与设施兼容标签)。
- 每个流程要求一个设施兼容标签，建筑的 `facility_tags` 包含它，这座建筑才兼容该流程；标签不提供能力，见[流程的格式](process.md)。`plot_size` 为 `small_station` 或 `large_station` 的建筑必须是 `station` 类型，反过来也一样。
- 地块包络上限按 X×Z×Y（格）：`small` 48×48×48、`medium` 96×96×80、`large` 192×192×128、`giant` 384×384×256、`small_station` 192×112×48、`large_station` 256×208×64。站点本地 X 沿轨、Z 跨轨；尺寸标准见[设计文档 16 的地块与建筑](../design/16-settlements.md#地块与建筑)。
- `flatten`：`from`、`to` 是 `[x, z]`，两端都包括；`height` 是整数。放置场景模型时，这个矩形里 y 小于 `height` 的格是地面，以上清空。

### 蓝图上的行为

| 类型 | 写在 | 参数 | 阶段 |
| --- | --- | --- | --- |
| 功能性行为 | `simulation.behaviors` | 和体素的同名行为相同，见[体素定义的功能性行为](voxel.md#功能性行为的核心参数)。蓝图带功能性行为，就是功能复合体：只能来自内容包，里面只装没有功能性行为的体素，不嵌套复合体；宿主按身份引用它，不复制，在宿主里整件是一个部件 | Demo |
| `rotation` | `appearance.behaviors` | `axis`：`x`、`y` 或 `z`，指设备自身的轴；`pivot`：`[x, y, z]`，转轴经过这一格的中心；`range`：`[最小角, 最大角]`，单位度，范围 −180 到 180，最小角小于最大角，0 是设计里的姿态，`[-180, 180]` 是整圈；`speed`：每秒多少度，默认 90 | Demo |
| `creature` | `simulation.behaviors` | `hp`：正整数；`speed`：米/秒，大于 0；`retreat_threshold`：0 到 1；`raid_duration`：秒，大于 0 | Demo |
| `weapon`（生物） | `simulation.behaviors` | 和体素的 `weapon` 相同 | Demo |

玩家的蓝图不能带功能性行为，复制进来的设计里也不能有；需要功能复合体时引用内容包里的定义。所以玩家改不了功能复合体的参数。

## 加载器检查的规则

Schema 之外，加载器按[设计文档 03 的设计合法性](../design/03-vehicles-buildings-and-designs.md#设计合法性)检查，任何一条不满足，整个包无效：

- `id` 和文件名一致；包里的蓝图没有 `used_packages`，玩家的蓝图有。
- 引用的体素存在，朝向是它允许的，形状在它的 `shapes` 里（自定义模型体素不能写形状）；类别用的体素种类正确（场景体素或货物体素）。
- 格子的坐标不重复，调色板序号在调色板范围内。
- 嵌套连同最外层最多 4 层，类别能装进去的组合正确，被嵌套的复合体和其他部分不重叠，同一个宿主里 `at` 不重复。
- 复制的设计里任何一层都没有功能性行为；`blueprint` 引用的是功能复合体。
- 车辆、建筑和大型设备的全部体素和被嵌套的复合体六面连成一体；场景模型和生物不要求。
- 不超出车辆档位或地块尺寸的包络；站点地块尺寸和站点建筑类型互相对应。
- `rotation` 的最小角小于最大角；转动扫过的格是空的，并且在宿主的包络之内。
- `flatten` 的 `from` 每一项不大于 `to` 的对应项。
- 功能复合体的规则，以及玩家的蓝图不能带功能性行为。

## 玩家的蓝图

- 用户内容库里每个蓝图是一个文件：`user://content-library/<UUID>.blueprint.json`，`id` 就是这个 UUID。格式和包里的蓝图相同，另有 `used_packages`；引用一律写完整标识。
- 本局蓝图库用同一种格式，存在存档里，见设计文档 [09](../design/09-persistence.md)。

## 样例

| 文件 | 类别 | 展示的写法 | 阶段 |
| --- | --- | --- | --- |
| `example-pack` 的 `tree` 树 | 场景模型 | 无行为，只含体素 | Demo |
| `grove` 树丛 | 场景模型 | 无行为，只含复合体：3 棵朝向不同的树 | Demo |
| `park` 街心花园 | 场景模型 | 无行为，两者都含：草地加一个树丛和一棵树，嵌套 3 层；`flatten` | Demo |
| `small-locomotive` 小型车头 | 车辆 | 动力和控制体素的基础写法；不代表 Demo 仅支持这些字段 | 历史首切片样例 |
| `mg-turret` 机枪塔 | 大型设备 | 组装的武器设备：底座加两挺机枪；`rotation`；涂装；调色板里用 `shape: wedge` | Demo |
| `large-diesel-engine` 大型柴油机 | 大型设备 | 功能复合体：蓝图上带 `propulsion`，里面只有结构体素 | Demo |
| `armed-locomotive` 武装车头 | 车辆 | 装了武器的车辆：复制的机枪塔，加按身份引用并整件涂装的大型柴油机；转向架、车头灯、楔形车鼻 | Demo |
| `warehouse` 仓库 | 建筑 | `plot_size`、`building_type`；涂装的混凝土外墙加货舱 | Demo |
| `small-station` 小站点 | 建筑 | 站点：`plot_size` 为 `small_station`，`building_type` 为 `station`；带 `platform` 的站台控制台 | Demo |
| `village-ground` 村落地面 | 场景模型 | 聚居地模板用的地面：草地加一个树丛，`flatten` | Demo |
| `example-addon` 的 `armored-wagon` 装甲货车 | 车辆 | 第二个包：依赖 `example-pack`，用完整标识引用它的体素，嵌套的机枪塔副本换了涂装 | Demo |
| `content-library/d1b6e8a2-….blueprint.json` 我的小型车头 | 车辆 | 玩家的蓝图：`id` 是 UUID，带 `used_packages`，引用写完整标识 | Demo |

## 和现有代码的差别

当前宿主通过真实 `ContentFactory` 加载本文版本 3 的 `.blueprint.json`，支持车辆、建筑、组装体、功能复合体和场景模型，包含四层设计复制、功能复合体身份引用、24 向朝向、稳定编号、整件设备 BOM 和行为全角度转动扫格，保留形状、涂装及场景模型平整区域。生物设计仍待后续工单；用户库加载及编辑已实现，见[内容契约](../contracts/content.md#草稿蓝图库与实例目标)。公开草稿求值尚不能提供根功能复合体的定义身份，该入口仍明确拒绝；包加载不受此限制。旧版本 2 的 `.composites.json` 加载实现已在 [#452](https://github.com/odradekk/train/issues/452) 退役。当前范围见[内容契约](../contracts/content.md)，行为覆盖见[蓝图加载测试](../../Tests/Content/Loading/BlueprintLoadingTests.cs)、[设计评估测试](../../Tests/Content/Loading/DesignEvaluationTests.cs)、[嵌套设计测试](../../Tests/Content/Loading/NestedDesignTests.cs)、[嵌套内容生命周期测试](../../Tests/Content/Loading/NestedContentLifecycleTests.cs)及[文档蓝图测试](../../Tests/Content/Loading/DocumentationBlueprintTests.cs)。
