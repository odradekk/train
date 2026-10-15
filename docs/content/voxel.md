# 体素定义

一个体素定义描述一种单格的东西：货物、部件，或者只用于场景的地块和装饰。文件名是 `<local id>.voxel.json`，放在 `resources/voxels/`，它的资产放在同名文件夹里。Schema 见 [schemas/voxel.schema.json](schemas/voxel.schema.json)，示例见 [examples/example-pack/resources/voxels/](examples/example-pack/resources/voxels/)。

规则的来由：分节和场景体素见 [ADR-0011](../adr/0011-move-the-content-layer-into-its-own-project-and-split-definitions-by-reader.md)，行为见 [ADR-0014](../adr/0014-use-code-provided-behavior-types-and-sealed-functional-composites.md)，文件组成见 [ADR-0015](../adr/0015-declare-package-dependencies-and-keep-missing-content-as-placeholders.md)，表现字段见设计文档 [02 的表现资产校验](../design/02-definitions-and-content.md#表现资产校验)。

```json
{
  "schema_version": 3,
  "id": "diesel-engine",
  "simulation": {
    "value": 800,
    "mass": 4,
    "max_durability": 300,
    "behaviors": { "propulsion": { "traction": 25, "rated_speed": 20 } }
  },
  "appearance": {
    "allowed_orientations": "four_way",
    "presentation": {
      "kind": "cube",
      "faces": { "top": "top.png", "side": "side.png", "bottom": "bottom.png" }
    }
  },
  "display": { "name": "柴油机组", "description": "单格柴油机组，为车辆提供牵引力。", "icon": "icon.png" }
}
```

## 顶层

| 字段 | 类型 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- |
| `schema_version` | 整数 | 是 | 固定为 3 | 首个切片 |
| `id` | 字符串 | 是 | local id，必须和文件名一致 | 首个切片 |
| `simulation` | 对象 | 否 | 核心模拟层读取的一节。有这一节的是货物，可以库存、交易和放进设计；没有这一节的是场景体素，只供场景使用 | 首个切片 |
| `appearance` | 对象 | 是 | 体素引擎层和渲染层读取的一节 | 首个切片 |
| `display` | 对象 | 是 | 界面读取的一节 | 首个切片 |

## `simulation`

| 字段 | 类型 | 必填 | 默认值 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- | --- |
| `value` | 整数 | 是 | — | 基本货币价值，1 到 10 亿 | 首个切片 |
| `mass` | 数 | 是 | — | 一个单位的质量，单位吨；大于 0，不超过 10 亿 | 首个切片 |
| `max_durability` | 整数 | 是 | — | 放进设计后这一格的耐久上限，1 到 10 亿 | 首个切片 |
| `tags` | 字符串数组 | 否 | `[]` | 取值由代码提供，不能重复。目前只有 `raw_good`（初级货物，节点资源引用它） | 首个切片 |
| `fuel_energy` | 数 | 否 | — | 每单位燃料提供的电量，大于 0，不超过 10 亿。写了就是燃料，任何发电设备都能使用，见设计文档 [13 的 Power 与 Fuel](../design/13-production-runtime-and-facilities.md#power-与-fuel) | Demo |
| `behaviors` | 对象 | 否 | `{}` | 功能性行为的核心参数，以行为类型为键，见下表 | 首个切片 |

带 `simulation` 一节的体素中，带功能性行为的是部件，其余是结构体素。部件有自己的耐久，还按代码常量“部件结构比例”把自身耐久上限的一部分计入整体的结构耐久，见设计文档 [03 的部件与结构](../design/03-vehicles-buildings-and-designs.md#部件与结构)（[功能复合体的来源、缺失内容的状态、结构耐久下限与嵌套排序](https://github.com/odradekk/train/issues/269)）。

### 功能性行为的核心参数

| 类型 | 参数 | 单位与校验 | 在一份设计里怎样汇总 | 阶段 |
| --- | --- | --- | --- | --- |
| `propulsion` | `traction`、`rated_speed` | 千牛，米/秒；都大于 0 | 牵引力相加；额定速度取最小值 | 首个切片 |
| `control` | 无，写 `{}` | — | 只看有没有 | 首个切片 |
| `storage` | `capacity` | 货物单位，正整数 | 相加，就是列车或聚居地的库存容量 | 首个切片 |
| `production` | `work_rate` | 每秒的工作量，大于 0 | 相加 | Demo |
| `extraction` | `work_rate` | 每秒的工作量，大于 0 | 相加 | Demo |
| `construction` | 必填 `work_rate`、`max_plot_size`；可选 `max_size_class` | 每秒工作量大于 0；建筑上限仅 `small`、`medium`、`large`、`giant`；车辆上限为 `small`、`medium`、`large`、`extra_large`、`titan`，缺省不能造车 | 工作速率相加；建筑和车辆上限分别取完好部件中最大的；没有车辆上限时不能造车 | Demo |
| `power` | `output` | 功率，每秒的电量，大于 0 | 相加，计入聚居地的电力池 | Demo |
| `weapon` | `damage`、`interval` | 每次伤害，正整数；两次射击的间隔，秒，大于 0 | 逐个列出，不相加 | Demo |
| `platform` | 无，写 `{}` | — | 每个完好的部件提供一个站台，按数量相加 | Demo |

- 被毁的部件不计入汇总。
- 施工参数样例：[基础吊机体素](examples/example-pack/resources/voxels/crane.voxel.json)提供中地块建筑上限且无车辆能力；[车间吊机](examples/example-pack/resources/blueprints/workshop-crane.blueprint.json)和[龙门吊](examples/example-pack/resources/blueprints/gantry-crane.blueprint.json)分别是大地块／大型车辆、巨型地块／泰坦车辆的功能复合体。单格施工设备仍只有基础吊机一种。建筑改装、修理适用同一建筑上限，拆解和制造大型设备只要求施工能力，见[设计 14](../design/14-construction-refit-and-deconstruction.md#construction-job)。
- `production`、`extraction`、`construction`、`power` 的单位和运行时的用法见设计文档 [13 的单位与实际速率](../design/13-production-runtime-and-facilities.md#单位与实际速率)；流程见[流程的格式](process.md)。
- 武器射程放在 Demo 之后。

## `appearance`

| 字段 | 类型 | 必填 | 默认值 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- | --- |
| `allowed_orientations` | 字符串 | 否 | `none` | `none` 只允许默认朝向；`four_way` 允许绕竖直轴的 4 个朝向；`all_24` 允许全部 24 个正交朝向。不放进设计的货物不必写 | 首个切片 |
| `collision` | 对象 | 否 | `{ "kind": "full_box" }` | 场景中使用的碰撞形状，见下文。占格始终是整格，拾取不依赖碰撞 | 首个切片 |
| `presentation` | 对象 | 是 | — | 外观，`kind` 是 `cube` 或 `mesh`，见下文 | 首个切片 |
| `behaviors` | 对象 | 否 | `{}` | 行为的表现参数，以行为类型为键，见下文 | Demo |

### `collision`

| `kind` | 其他字段 | 说明 |
| --- | --- | --- |
| `none` | 无 | 没有碰撞 |
| `full_box` | 无 | 整格方块 |
| `local_box` | `offset`、`size`，各有 `x`、`y`、`z` | 格内的一个方块，以格边长为 1：`offset` 每项在 0 到 1 之间，`size` 每项大于 0，并且每个轴上 `offset + size` 不超过 1 |

碰撞属于表现数据，不参与语义哈希。

### `presentation`：立方体

| 字段 | 类型 | 必填 | 默认值 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- | --- |
| `kind` | 字符串 | 是 | — | `cube` | 首个切片 |
| `shapes` | 字符串数组 | 否 | `["cube"]` | 允许放置时选用的形状，取自形状目录，由代码提供，每个标准变体是一个独立的名字；不能为空，不能重复。Demo 的取值：`cube`、`half`、`plate`、`wedge`、`corner-wedge`、`beam`、`cylinder`，见[美术 01 的形状](../art/01-construction-language.md#形状)。设计里的格子不写形状时用第一项，格子写的形状必须在这个列表里，见[蓝图的调色板](blueprint.md#layout)。形状只是外观：不参与语义哈希，不影响货物身份、价值、质量、耐久、能力和 BOM，同一种体素放成楔形或立方体是同一种货物 | Demo |
| `material` | 字符串 | 否 | `painted_metal` | 表现材质类别：`painted_metal`、`bare_metal`、`concrete`、`stone_soil`、`glass`、`polymer_rubber`、`organic`，见[美术 02](../art/02-materials-and-textures.md#材质类别)。只有 `painted_metal` 和 `concrete` 能涂装 | 首个切片 |
| `tile_span` | 整数 | 否 | 1 | 平铺跨度，1、2、4 或 8 | 首个切片 |
| `faces.top`、`faces.side`、`faces.bottom` | 资产路径 | 是 | — | 顶面、侧面、底面的贴图。几个面用同一张图就写同一个文件 | 首个切片 |
| `faces.front` | 资产路径 | 否 | 用侧面图 | 正面图，不平铺 | Demo |

形状由放置时选择，不由体素定义固定：这一处只列出允许的形状，每一格用哪种写在蓝图的调色板里（[Demo 内容与资产清单：覆盖场景与制作验收](https://github.com/odradekk/train/issues/283)，2026-10-10；它只取代[非立方体形状与构造语言](https://github.com/odradekk/train/issues/245)中把形状写在定义上的部分，取舍见 [ADR-0018](../adr/0018-choose-voxel-shapes-per-placement-so-shapes-never-create-goods.md)）。

加载器检查图片：顶面、侧面、底面是边长相同的 2 的幂正方形；正面图是 2 的幂正方形，尺寸可以不同。边长是否等于 32 像素乘以平铺跨度，由美术检查。

### `presentation`：自定义模型

| 字段 | 类型 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- |
| `kind` | 字符串 | 是 | `mesh` | Demo |
| `model` | 资产路径 | 是 | glTF 二进制模型 `.glb`，不超过 4 MiB，约定见下文。模型按原尺寸使用，包围盒超出所在的格就拒绝 | Demo |
| `surface_materials` | 字符串数组 | 是 | 每个表面一个材质类别，按模型里表面的顺序，数量和表面数相同 | Demo |

自定义模型没有 `shapes`，蓝图里放置它的格子也不能写 `shape`。

#### 模型文件的约定

由[资产访问与导入：内容、体素和 Godot 的交接契约](https://github.com/odradekk/train/issues/286)确认。加载器按 Godot 检查返回的元数据执行这些规则，见设计文档 [02 的资产的交接](../design/02-definitions-and-content.md#资产的交接)。

- **单位**：米，按 glTF 的规定。一格边长 0.5 米，加载时统一乘 2 换算成格；这是固定的单位换算，不算缩放模型。
- **原点**：格的底面中心。x 和 z 在 −0.25 到 0.25 米之间，y 在 0 到 0.5 米之间。计入节点变换后的包围盒超出这个范围 1/1000 格以上就拒绝。
- **轴**：沿用 glTF 的默认，+Y 向上，模型正面朝 +Z，也就是体素的正面（局部 +Z），不用转换。Blender 按默认设置导出即可，模型正面朝 Blender 的 −Y。24 个朝向都绕格中心旋转。
- **节点与表面**：文件里只有一个带网格的节点。这个网格的各个表面按文件里的顺序对应 `surface_materials`，在 Blender 里就是一个物体的材质槽顺序。
- **贴图**：嵌在 `.glb` 里，不能引用外部文件。
- **材质**：只用基础颜色（贴图和颜色系数）和透明方式。透明方式只能是不透明或镂空裁剪，半透明混合就拒绝。法线、粗糙度、金属度、自发光一律忽略，外观由材质类别决定。
- **不支持的内容**：有骨骼、形变目标、动画、相机或灯光就拒绝。

现在没有模型样例，第一个自定义模型随 Demo 的内容制作加入。

### 行为的表现参数

| 类型 | 参数 | 说明 | 阶段 |
| --- | --- | --- | --- |
| `wheelset` | 无，写 `{}` | 挂在转向架体素上，轮对由体素引擎层计算，按列车的显示速度转动，可以跨格。制作遵循下述[转向架排布约定](#转向架排布约定) | Demo |
| `light` | `color`，默认 `#ffd9a0`；`range`，单位格，大于 0，默认 4 | 灯光。受损时闪烁、被毁后熄灭，由耐久决定，不是参数 | Demo |

- 这两种只有表现，可以挂在货物上，也可以挂在场景体素上；不让体素成为部件。
- 功能性类型目前没有表现参数。以后加了，功能性类型只能在 `simulation.behaviors` 里也出现时才能写在这里。

### 转向架排布约定

基础贯通车辆每车前后各放一个两轴转向架。带 `wheelset` 的 `bogie` 只放在车轴所在的 Z 行；同一行横向或竖向放几格，都只生成一根车轴。轴间的侧架用不带 `wheelset` 的结构体素，不能连续填 `bogie` 而让每一行都变成车轴。

| 档位 | 两轴间距 | `bogie` 的 Y 层 | 最近轴行距车端行 |
| --- | --- | --- | --- |
| 小型 | 2 格 | 0 | 4 格 |
| 中型 | 3 格 | 0–1 | 6 格 |
| 超大型 | 5 格 | 0–3 | 10 格 |

端距按实际车体最前、最后的 Z 行与轴行之差计；两转向架相对车辆纵向中点对称。车体地板在转向架上方的第一层，轴行与侧架保持六面相连。轨距沿用档位的 3／7／15 格。

轮心、轮径和轮宽由体素引擎层推导，内容不添加这些参数。当前排布对齐 [PR #431](https://github.com/odradekk/train/pull/431) 的暂定尺寸，三档轴距均能容纳轮径并留一格间隙。该 PR 合并后的三维轮轨表现、排布和轮径仍需在 #298 看图确认；小型暂定轮径仅 0.5 米，实机观察后再决定是否调整 Scenes 的集中参数。

## `display`

| 字段 | 类型 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- |
| `name` | 字符串 | 是 | 显示名称，去掉首尾空白后 1–100 个字符 | 首个切片 |
| `description` | 字符串 | 是 | 说明，去掉首尾空白后 1–2000 个字符 | 首个切片 |
| `icon` | 资产路径 | 是 | 物品图标，正方形 PNG，128×128，透明底，只画物品本身，见[美术 07 的物品图标](../art/07-icons-and-ui.md#物品图标) | 首个切片 |

## 样例

| 文件 | 类别 | 展示的写法 | 阶段 |
| --- | --- | --- | --- |
| `grain` 粮食 | 无行为的货物 | 只写必填字段，朝向和碰撞取默认值；`raw_good` 标签；底面复用顶面图 | 首个切片 |
| `ferrous-ore` 铁矿石 | 无行为的货物 | `raw_good` 标签，开采流程引用它；三个面用同一张图 | Demo |
| `fuel` 燃料 | 无行为的货物 | `fuel_energy` | Demo |
| `hull-plate` 车体钢板 | 结构体素 | 底面引用共用的 `shared/vehicle-underside.png`；`shapes` 列出全部 7 种形状，`all_24` 朝向 | Demo |
| `concrete-block` 混凝土块 | 结构体素 | 材质 `concrete`，可以涂装 | Demo |
| `grassland` 草地 | 场景体素 | 没有 `simulation` 一节；`tile_span: 4` | 首个切片 |
| `street-lamp` 路灯 | 场景体素 | 只有表现的行为 `light`；`local_box` 碰撞；`shapes: ["beam"]` | Demo |
| `tree-trunk` 树干、`foliage` 树冠 | 场景体素 | 场景模型 `tree` 用到的体素；树干 `shapes: ["cylinder"]` | Demo |
| `diesel-engine` 柴油机组 | 部件 | `propulsion` | 首个切片 |
| `driver-cab` 驾驶台 | 部件 | `control` | 首个切片 |
| `cargo-hold` 货舱 | 部件 | `storage` | 首个切片 |
| `bogie` 转向架 | 结构体素 | 表现行为 `wheelset`；`bare_metal` | Demo |
| `headlight` 车头灯 | 结构体素 | 货物带表现行为 `light` | Demo |
| `machine-gun` 机枪 | 部件 | `weapon`；正面图 | Demo |
| `machine-tool` 机床 | 部件 | `production` | Demo |
| `drill-head` 钻头 | 部件 | `extraction` | Demo |
| `crane` 基础吊机 | 部件 | `construction`（建筑上限 `medium`，无车辆能力）加表现行为 `light`（参数取默认值） | Demo |
| `generator` 发电机 | 部件 | `power` | Demo |
| `platform-desk` 站台控制台 | 部件 | `platform` | Demo |

## 和现有代码的差别

当前宿主通过真实 `ContentFactory` 加载本文版本 3 的 `.voxel.json`，提供货物、显示和外观投影，并保留通过规则检查的原始资产字节。无嵌套的车辆、建筑和场景模型设计已有属性计算、部件能力投影及适用的连通与包络校验；嵌套、大型设备、生物设计及相应运行效果仍按后续工单交付。旧版本 2 的 `.voxels.json` 加载实现已在 [#452](https://github.com/odradekk/train/issues/452) 退役。当前范围见[内容契约](../contracts/content.md)，设计计算的行为覆盖见[设计评估测试](../../Tests/Content/Loading/DesignEvaluationTests.cs)。
