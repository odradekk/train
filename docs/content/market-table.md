# 市场表

市场表是首个切片的临时格式：按货物给出价格对供需的敏感度和价格上下限，底价读取同一内容快照里体素的 Value。该临时模型不再作为完整 Demo 的实施依据；替代规则已确定为 [19 的共用市场规则](../design/19-markets-and-trading.md#一局共用的市场规则)，使用真实库存与用途派生的供需、边际价格和局部精确货币计算。新格式、规范编码与投影由[内容契约](https://github.com/odradekk/train/issues/288)交付。本文、Schema 和样例暂保留记录原格式，不表示已经完成替换。文件名是 `<local id>.market-table.json`，放在 `resources/markets/`。Schema 见 [schemas/market-table.schema.json](schemas/market-table.schema.json)，示例见 [examples/example-pack/resources/markets/](examples/example-pack/resources/markets/)。

以下仅描述历史临时格式：市场表只有核心模拟层读取，只有 `simulation` 一节。旧模型把全局选表和恢复间隔留给世界配置；Demo 不恢复虚构供需，也不把此表直接当成新市场规则。

```json
{
  "schema_version": 2,
  "id": "basic",
  "simulation": {
    "goods": [{ "voxel": "grain", "sensitivity": 0.5, "min_price": 50, "max_price": 200 }]
  }
}
```

## 字段

| 字段 | 类型 | 必填 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- |
| `schema_version` | 整数 | 是 | 固定为 2 | 首个切片 |
| `id` | 字符串 | 是 | local id，必须和文件名一致 | 首个切片 |
| `simulation.goods` | 对象数组 | 是 | 至少一项，同一种货物只能写一次 | 首个切片 |
| `goods[].voxel` | 引用 | 是 | 货物的体素定义，必须带 `simulation` 一节 | 首个切片 |
| `goods[].sensitivity` | 数 | 是 | 价格对供需的敏感度，有限的非负数 | 首个切片 |
| `goods[].min_price`、`goods[].max_price` | 整数 | 是 | 价格下限和上限，正整数，下限不大于上限；不要求区间包含体素的 Value | 首个切片 |

## 加载器检查的规则

- `id` 和文件名一致；引用的体素存在，并且是货物。
- 同一种货物不重复；`min_price` 不大于 `max_price`。

## 样例

| 文件 | 展示的写法 | 阶段 |
| --- | --- | --- |
| `example-pack` 的 `basic` | 一种货物的价格参数 | 首个切片 |

## 和现有代码的差别

现在的代码读 `schema_version: 1` 的 `.market-tables.json`：一个文件多张表，根对象是 `market_tables` 数组，字段不分节，货物引用写作 `definition`。它与本文的单文件临时格式都尚未迁移到 Demo 规则；后续实施以内容契约交付的新格式为目标，不再以实现本临时格式为终点。
