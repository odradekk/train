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
| `construction` | `work_rate`、`max_size_class` | 每秒的工作量，大于 0；车辆尺寸档位 `small`、`medium`、`large`、`extra_large`、`titan` | 工作量相加；档位取完好部件中最大的 | Demo |
| `power` | `output` | 功率，每秒的电量，大于 0 | 相加，计入聚居地的电力池 | Demo |
| `weapon` | `damage`、`interval` | 每次伤害，正整数；两次射击的间隔，秒，大于 0 | 逐个列出，不相加 | Demo |
| `platform` | 无，写 `{}` | — | 每个完好的部件提供一个站台，按数量相加 | Demo |

- 被毁的部件不计入汇总。
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
| `shape` | 字符串 | 否 | `cube` | 形状目录里的名字，由代码提供，每个标准变体是一个独立的名字。Demo 的取值：`cube`、`half`、`plate`、`wedge`、`corner-wedge`、`beam`、`cylinder`，见[美术 01 的形状](../art/01-construction-language.md#形状) | Demo |
| `material` | 字符串 | 否 | `painted_metal` | 表现材质类别：`painted_metal`、`bare_metal`、`concrete`、`stone_soil`、`glass`、`polymer_rubber`、`organic`，见[美术 02](../art/02-materials-and-textures.md#材质类别)。只有 `painted_metal` 和 `concrete` 能涂装 | 首个切片 |
| `tile_span` | 整数 | 否 | 1 | 平铺跨度，1、2、4 或 8 | 首个切片 |
| `faces.top`、`faces.side`、`faces.bottom` | 资产路径 | 是 | — | 顶面、侧面、底面的贴图。几个面用同一张图就写同一个文件 | 首个切片 |
| `faces.front` | 资产路径 | 否 | 用侧面图 | 正面图，不平铺 | Demo |

加载器检查图片：顶面、侧面、底面是边长相同的 2 的幂正方形；正面图是 2 的幂正方形，尺寸可以不同。边长是否等于 32 像素乘以平铺跨度，由美术检查。

### `presentation`：自定义模型

| 字段 | 类型 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- |
| `kind` | 字符串 | 是 | `mesh` | Demo |
| `model` | 资产路径 | 是 | 模型文件，暂定为 `.glb`。模型按原尺寸使用，包围盒超出所在的格（允许极小的容差）就拒绝 | Demo |
| `surface_materials` | 字符串数组 | 是 | 每个表面一个材质类别，数量和模型的表面数相同 | Demo |

模型格式、原点和轴的约定随第一个自定义模型的导入确定（[美术 08](../art/08-asset-pipeline-and-review.md)），所以现在没有样例。

### 行为的表现参数

| 类型 | 参数 | 说明 | 阶段 |
| --- | --- | --- | --- |
| `wheelset` | 无，写 `{}` | 挂在转向架体素上：渲染器在这里生成轮对，按列车的显示速度转动。轮对可以跨格，轮径和轴距随轨距在车辆内容切片确定，见[美术 06](../art/06-wear-damage-and-motion.md) | Demo |
| `light` | `color`，默认 `#ffd9a0`；`range`，单位格，大于 0，默认 4 | 灯光。受损时闪烁、被毁后熄灭，由耐久决定，不是参数 | Demo |

- 这两种只有表现，可以挂在货物上，也可以挂在场景体素上；不让体素成为部件。
- 功能性类型目前没有表现参数。以后加了，功能性类型只能在 `simulation.behaviors` 里也出现时才能写在这里。

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
| `hull-plate` 车体钢板 | 结构体素 | 底面引用共用的 `shared/vehicle-underside.png` | 首个切片 |
| `hull-wedge` 斜面车体 | 结构体素 | `shape: wedge`，`all_24` 朝向 | Demo |
| `concrete-block` 混凝土块 | 结构体素 | 材质 `concrete`，可以涂装 | Demo |
| `grassland` 草地 | 场景体素 | 没有 `simulation` 一节；`tile_span: 4` | 首个切片 |
| `street-lamp` 路灯 | 场景体素 | 只有表现的行为 `light`；`local_box` 碰撞；`shape: beam` | Demo |
| `tree-trunk` 树干、`foliage` 树冠 | 场景体素 | 场景模型 `tree` 用到的体素 | Demo |
| `diesel-engine` 柴油机组 | 部件 | `propulsion` | 首个切片 |
| `driver-cab` 驾驶台 | 部件 | `control` | 首个切片 |
| `cargo-hold` 货舱 | 部件 | `storage` | 首个切片 |
| `bogie` 转向架 | 结构体素 | 表现行为 `wheelset`；`bare_metal` | Demo |
| `headlight` 车头灯 | 结构体素 | 货物带表现行为 `light` | Demo |
| `machine-gun` 机枪 | 部件 | `weapon`；正面图 | Demo |
| `machine-tool` 机床 | 部件 | `production` | Demo |
| `drill-head` 钻头 | 部件 | `extraction` | Demo |
| `crane` 吊机 | 部件 | `construction` 加表现行为 `light`（参数取默认值） | Demo |
| `generator` 发电机 | 部件 | `power` | Demo |
| `platform-desk` 站台控制台 | 部件 | `platform` | Demo |

## 和现有代码的差别

现在的代码读 `schema_version: 2` 的 `.voxels.json`：一个文件多份定义，字段不分节，`icon` 在 `presentation` 里，行为写成带 `type` 的数组，有 `cargo` 行为，资产路径从 `resources/` 起算，朝向和碰撞必填。改成本格式由实施工单完成。
