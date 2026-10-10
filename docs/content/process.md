# 流程

一个流程描述设施怎样把投入的货物或节点储量转换为产出的货物，分生产、开采、回收三种。文件名是 `<local id>.process.json`，放在 `resources/processes/`；流程没有资产。Schema 见 [schemas/process.schema.json](schemas/process.schema.json)，示例见 [examples/example-pack/resources/processes/](examples/example-pack/resources/processes/)。

规则的来由见设计文档 [10 的开采](../design/10-resources-and-extraction.md#开采)和 [13 的流程定义](../design/13-production-runtime-and-facilities.md#process-definition)，格式由[生产与供能：Demo 的内容参数和运行契约](https://github.com/odradekk/train/issues/282)确定。

```json
{
  "schema_version": 1,
  "id": "hull-plate-pressing",
  "simulation": {
    "kind": "production",
    "inputs": [{ "voxel": "ferrous-ore", "quantity": 4 }],
    "outputs": [{ "voxel": "hull-plate", "quantity": 2 }],
    "work_required": 120,
    "power_per_work": 5,
    "labor_per_work": 2,
    "facility_tag": "metallurgy"
  },
  "display": { "name": "冲压车体钢板", "description": "把铁矿石冲压成车体钢板。" }
}
```

## 顶层

| 字段 | 类型 | 必填 | 说明与校验 |
| --- | --- | --- | --- |
| `schema_version` | 整数 | 是 | 固定为 1 |
| `id` | 字符串 | 是 | local id，必须和文件名一致 |
| `simulation` | 对象 | 是 | 核心模拟层读取的一节 |
| `display` | 对象 | 是 | 界面读取的一节 |

## `simulation`

| 字段 | 类型 | 必填 | 说明与校验 |
| --- | --- | --- | --- |
| `kind` | 字符串 | 是 | `production` 生产、`extraction` 开采、`recovery` 回收，见下文 |
| `inputs` | 货物数组 | 见下文 | 每批投入的货物 |
| `outputs` | 货物数组 | 见下文 | 每批产出的货物 |
| `extracts` | 货物 | 见下文 | 每批从本节点扣除的储量；产出就是同一种货物、同样数量 |
| `work_required` | 64 位整数 | 是 | 每批总工作量，范围 1–9223372036854775807 |
| `power_per_work` | 数 | 是 | 每 1 工作量消耗的电量，不小于 0；0 表示不用电 |
| `labor_per_work` | 数 | 是 | 以 1 工作量/秒运行时占用的劳动力，不小于 0；0 表示不占用劳动力 |
| `facility_tag` | 字符串 | 是 | 要求的设施兼容标签，取值由代码提供；建筑的 `facility_tags` 包含它才兼容 |
| `environment` | 对象 | 否 | 环境要求，见下文；不写表示没有要求 |

货物写成 `{ "voxel": <体素定义引用>, "quantity": <正整数> }`。被引用的体素定义必须带 `simulation` 一节，也就是货物。Demo 的流程只处理体素货物；大型设备由施工任务按蓝图制造，不由流程制造或回收。

总工作量的整数表示经[核心模拟层公开契约](https://github.com/odradekk/train/issues/287)确认；进度、工作速率、电量和劳动力保持 `double`。本文与 Schema 同步格式约定，不代表加载器或模拟实现已迁移。

单位和运行时怎样使用这些数，见设计文档 [13 的单位与实际速率](../design/13-production-runtime-and-facilities.md#单位与实际速率)。

### 三种流程

| `kind` | 标识类型 | 使用的能力 | 字段 |
| --- | --- | --- | --- |
| `production` | 生产流程标识 | 生产能力 | `outputs` 必填且非空；`inputs` 可选，默认 `[]`；不能写 `extracts` |
| `extraction` | 生产流程标识 | 开采能力 | `extracts` 必填，引用的货物必须带 `raw_good` 标签；不能写 `inputs`、`outputs` |
| `recovery` | 回收流程标识 | 生产能力 | `inputs`、`outputs` 都必填且非空；不能写 `extracts` |

- 初级生产（种植、畜牧）属于 `production`，可以没有投入。
- 生产和开采共用生产流程标识，回收用回收流程标识；同一个包里的 local id 由文件名保证不重复。
- 改动 `kind` 等于删除旧流程、新建流程，见设计文档 [13 的内容更新](../design/13-production-runtime-and-facilities.md#内容更新与热重载)。

### `environment`

| 字段 | 类型 | 必填 | 说明与校验 |
| --- | --- | --- | --- |
| `terrain_types` | 引用数组 | 是 | 允许的地貌类型，不能为空，不能重复；聚居地所在节点的地貌类型是其中之一才满足 |

- Demo 只支持地貌类型；起伏程度、地形特征等到有流程需要时再加。
- 每次批次启动时检查；设置或启用计划时只用于提示；建造建筑时不检查，因为建筑本身不绑定流程。

## `display`

| 字段 | 类型 | 必填 | 说明与校验 |
| --- | --- | --- | --- |
| `name` | 字符串 | 是 | 显示名称，去掉首尾空白后 1–100 个字符 |
| `description` | 字符串 | 是 | 说明，去掉首尾空白后 1–2000 个字符 |

## 加载器检查的规则

- `id` 和文件名一致。
- 引用的体素定义存在并且是货物；`extracts` 引用的货物带 `raw_good` 标签；引用的地貌类型存在。
- 同一个数组里不能出现同一种货物两次。
- 发布内容时，删除仍被生产计划或运行中批次引用的流程，或改动它的 `kind`，整次发布被拒绝，见设计文档 [13 的内容更新](../design/13-production-runtime-and-facilities.md#内容更新与热重载)。

`kind` 与 `inputs`、`outputs`、`extracts` 的搭配由 Schema 检查。

## 样例

| 文件 | 种类 | 展示的写法 |
| --- | --- | --- |
| `grain-farming` 谷物种植 | `production` | 没有投入；不用电；环境要求平原 `core:plains` |
| `ferrous-mining` 铁矿开采 | `extraction` | `extracts` |
| `hull-plate-pressing` 冲压车体钢板 | `production` | 有投入和产出；用电 |
| `recover-machine-tool` 回收机床 | `recovery` | 把耐用货物回收为已有货物 |

## 和现有代码的差别

现在的代码没有流程，实现由实施工单完成。
