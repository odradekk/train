# 聚居地类型

聚居地类型是聚居地的玩法分类，例如农庄、城市。它决定名称、地图符号、生成权重、地貌偏好和人口范围；首个切片里还临时决定供需和站台数。文件名是 `<local id>.settlement-type.json`，放在 `resources/settlement-types/`，资产放在同名文件夹里。Schema 见 [schemas/settlement-type.schema.json](schemas/settlement-type.schema.json)，示例见 [examples/example-pack/resources/settlement-types/](examples/example-pack/resources/settlement-types/)。

类型不列出自己有哪些模板，由模板声明自己属于哪个类型，见[聚居地模板](settlement-template.md)。规则的来由见设计文档 [16 的聚居地类型](../design/16-settlements.md#聚居地类型)。首切片临时供需和站台数字段不再作为完整 Demo 的实施依据，见 [00 的本阶段范围](../design/00-product-and-core-loop.md#本阶段范围)；相关字段、Schema 和样例暂保留，待市场规则与内容契约确定后同步替换。

```json
{
  "schema_version": 2,
  "id": "farm",
  "simulation": {
    "supply": [{ "voxel": "grain", "capacity": 200, "restore_per_tick": 2 }],
    "preferred_terrain": ["core:plains"],
    "weight": 3,
    "population_min": 50,
    "population_max": 200,
    "initial_platform_count": 1
  },
  "display": { "name": "农庄", "description": "……", "map_symbol": "symbol.png" }
}
```

## 字段

| 字段 | 类型 | 必填 | 默认值 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- | --- |
| `schema_version` | 整数 | 是 | — | 固定为 2 | 首个切片 |
| `id` | 字符串 | 是 | — | local id，必须和文件名一致 | 首个切片 |
| `simulation.supply` | 对象数组 | 否 | `[]` | 临时规则：各货物的供给，每项见下表；同一种货物只能写一次 | 首个切片 |
| `simulation.demand` | 对象数组 | 否 | `[]` | 临时规则：各货物的需求上限，写法同上 | 首个切片 |
| `simulation.preferred_terrain` | 字符串数组 | 否 | `[]` | 偏好的地貌类型，写完整标识，例如 `core:plains`；空数组表示不限。地貌类型还没有定义目录，暂不检查是否存在 | 首个切片 |
| `simulation.weight` | 整数 | 是 | — | 世界生成选类型时的权重，正整数 | 首个切片 |
| `simulation.population_min`、`simulation.population_max` | 整数 | 是 | — | 常住人口范围，正整数，下限不大于上限。世界生成时在范围内取值；玩家建立的聚居地取下限 | 首个切片 |
| `simulation.initial_platform_count` | 整数 | 否 | 0 | 临时规则：首个切片没有模板和站点建筑时的站台数。有模板的聚居地不读这个字段，站台由站点里的 `platform` 部件提供；Demo 时去掉 | 首个切片 |
| `display.name` | 字符串 | 是 | — | 1–100 个字符 | 首个切片 |
| `display.description` | 字符串 | 是 | — | 1–2000 个字符 | 首个切片 |
| `display.map_symbol` | 资产路径 | 是 | — | 宏观地图上的类型符号：正方形 PNG，边长是 24 的整数倍；只画白色图形和透明底，界面按关系色着色，见[美术 07](../art/07-icons-and-ui.md) | 首个切片 |
| `display.major` | 布尔 | 否 | `false` | 是否按城市的大小画地图上的圆 | 首个切片 |

供给和需求的每一项：

| 字段 | 说明与校验 |
| --- | --- |
| `voxel` | 货物的体素定义引用，必须带 `simulation` 一节 |
| `capacity` | 上限，正整数 |
| `restore_per_tick` | 每次恢复的数量，非负整数；可以为 0，也可以大于上限，恢复时截到上限 |

正式的市场和人口规则确定后，供需和临时站台数会被替换。

## 加载器检查的规则

- `id` 和文件名一致；`map_symbol` 指向的图片存在，是正方形，边长是 24 的整数倍。
- 供给和需求里的引用存在并且是货物，同一边不重复。
- `population_min` 不大于 `population_max`。

## 和现有代码的差别

现在的代码读 `schema_version: 1` 的 `.settlement-types.json`：一个文件多份类型，字段不分节，货物引用写作 `definition`，所有字段必填，没有说明、地图符号和 `major`。改成本格式由实施工单完成。
