# 聚居地类型

聚居地类型是聚居地的玩法分类，例如农庄、城市。它决定名称、地图符号、生成权重、地貌偏好和初始人口范围。文件名是 `<local id>.settlement-type.json`，放在 `resources/settlement-types/`，资产放在同名文件夹里。Schema 见 [schemas/settlement-type.schema.json](schemas/settlement-type.schema.json)，示例见 [examples/example-pack/resources/settlement-types/](examples/example-pack/resources/settlement-types/)。

类型不列出自己有哪些模板，由模板声明自己属于哪个类型，见[聚居地模板](settlement-template.md)。规则的来由见设计文档 [16 的聚居地类型](../design/16-settlements.md#聚居地类型)。Demo 供需由 [19](../design/19-markets-and-trading.md)规定的真实库存、人口与启用的生产计划派生；站台由模板建筑的完好 `platform` 部件提供。目标格式不再接受临时供需、恢复量或初始站台数字段。

Demo 的全部聚居地共用一套独立的人口规则内容数据，见 [18](../design/18-population-and-labor.md#一局共用的人口规则)。类型不提供不同的消费率、劳动比例或人口变化规则；格式见[人口规则](population-rule.md)。

```json
{
  "schema_version": 3,
  "id": "farm",
  "simulation": {
    "preferred_terrain": ["core:plains"],
    "weight": 3,
    "population_min": 50,
    "population_max": 200
  },
  "display": { "name": "农庄", "description": "……", "map_symbol": "symbol.png" }
}
```

## 字段

| 字段 | 类型 | 必填 | 默认值 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- | --- |
| `schema_version` | 整数 | 是 | — | 固定为 3 | 首个切片 |
| `id` | 字符串 | 是 | — | local id，必须和文件名一致 | 首个切片 |
| `simulation.preferred_terrain` | 字符串数组 | 否 | `[]` | 偏好的地貌类型，写完整标识，例如 `core:plains`；空数组表示不限；引用必须解析为地貌类型 | 首个切片 |
| `simulation.weight` | 整数 | 是 | — | 世界生成选类型时的权重，正整数 | 首个切片 |
| `simulation.population_min`、`simulation.population_max` | 整数 | 是 | — | 初始常住人口范围，正 64 位整数，下限不大于上限。世界生成时在范围内取值；玩家建立的聚居地取下限。不是运行中的人口上限或保底人数 | 首个切片 |
| `display.name` | 字符串 | 是 | — | 1–100 个字符 | 首个切片 |
| `display.description` | 字符串 | 是 | — | 1–2000 个字符 | 首个切片 |
| `display.map_symbol` | 资产路径 | 是 | — | 宏观地图上的类型符号：正方形 PNG，边长是 24 的整数倍；只画白色图形和透明底，界面按关系色着色，见[美术 07](../art/07-icons-and-ui.md) | 首个切片 |
| `display.major` | 布尔 | 否 | `false` | 是否按城市的大小画地图上的圆 | 首个切片 |

## 加载器检查的规则

- `id` 和文件名一致；`map_symbol` 指向的图片存在，是正方形，边长是 24 的整数倍。
- `population_min` 不大于 `population_max`。
- 地貌引用存在，跨包依赖已声明。

## 和现有代码的差别

当前宿主通过真实 `ContentFactory` 的 Initial 加载采用本文版本 3，解析类型、校验地貌引用和地图符号，并交出核心及显示投影；不接受旧的临时供需与站台数字段。旧版本 1 的 `.settlement-types.json` 加载实现已在 [#452](https://github.com/odradekk/train/issues/452) 退役；当前范围见[内容契约](../contracts/content.md)。
