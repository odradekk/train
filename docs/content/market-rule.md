# 市场规则

文件为 `resources/rules/<local id>.market-rule.json`。Schema 见 [market-rule.schema.json](schemas/market-rule.schema.json)，完整样例见 [demo-market.market-rule.json](examples/example-pack/resources/rules/demo-market.market-rule.json)。玩法、精确计价、真实库存、更新与租约以[设计 19](../design/19-markets-and-trading.md)为准；解析结果对应 `MarketRuleProjection`，替代历史临时市场表。

三种规则均采用一文件一定义和严格 JSON，未知字段、重复字段、注释、尾随逗号均无效。引用遵循[通用规则](README.md#通用规则)，本包用 local id，跨包用命名空间标识并声明依赖；不以标签替代明确货物引用。规则没有表现资产或显示字段。一局由世界配置选择一份对应规则，不能按聚居地类型覆盖。所有字段必填，无隐式默认值。`schema_version` 固定为 1，`id` 必须与文件名一致。连续量须在进入核心前检查为有限数，拒绝 NaN 和无穷值。Schema 检查单文件结构，加载器负责引用、文件名和字段间约束。任意定义无效使整包无效。

真实 `ContentFactory` 的 Initial 加载已支持本格式的解析、校验与核心投影；市场、交易和租赁的模拟行为仍由核心任务验证。示例值只演示写法，尚未配平。

## `simulation`

| 字段 | 类型、单位与范围 |
| --- | --- |
| `basic_reserve_hours`、`production_reserve_hours`、`improvement_reserve_hours` | 有限非负模拟小时；分别为基本生活、生产、改善生活备货时长 |
| `default_goods_prices` | 默认货物价格参数，见下表 |
| `good_prices` | 数组；每项必填货物引用 `good` 和完整价格参数 `parameters`；允许空数组 |
| `vehicle_buy_multiplier_basis_points` | 正 32 位整数；玩家买入现车的估值倍率 |
| `vehicle_sell_multiplier_basis_points` | 正 32 位整数；玩家卖出现车的估值倍率 |
| `vehicle_purchase_limits` | 五档名额数组，每项必填 `size` 和非负 32 位整数 `maximum_for_sale`；0 表示不收购该档 |
| `automatic_evaluation_interval_ticks` | 正 32 位整数；自动交易重评间隔，60 ticks = 1 模拟秒 |
| `recent_report_limit` | 正 32 位整数；近期运行报告条数上限 |
| `commission_money_per_work` | 非负 64 位整数；每 1 工作量的最小货币单位 |
| `rent_money_per_capacity_hour` | 非负 64 位整数；每 1 容量每 1 模拟小时的最小货币单位 |
| `rentable_capacity_ratio_basis_points` | 整数 [0,10000]；真实容量的最大可租比例 |
| `lease_duration_ticks` | 正 64 位整数；租期模拟步数 |

价格参数的四个字段均为必填 32 位整数；所有 `basis_points` 字段以 10000 表示 1，整数值直接投影为 `BasisPoints`，不先转为 `double`。

| 字段 | 范围与含义 |
| --- | --- |
| `sensitivity_basis_points` | 非负；紧缺敏感度 |
| `minimum_multiplier_basis_points` | 正；价格倍率下限 |
| `maximum_multiplier_basis_points` | 正且不低于下限；价格倍率上限 |
| `spread_basis_points` | [0,9999]；买卖价差 |

## 加载器检查

- `good_prices` 的货物不得重复，必须解析为可库存货物；每项是完整覆盖，不按缺省字段局部合并。未覆盖的货物使用默认参数。
- 校验每组价格倍率上下限，以及有限数、引用与依赖。
- `vehicle_purchase_limits` 恰好覆盖 `small`、`medium`、`large`、`extra_large`、`titan`，各一次。

规则不配置供需数量、无限补货、现车生成或 NPC 资金。补货目标由核心按人口、启用生产计划与容量预算计算；覆盖参数不能改变这一来源。金额直接投影为 `Money`。整数精确计价和必要溢出检查归核心；意外溢出沿用内部故障，不引入新业务拒绝分支。
