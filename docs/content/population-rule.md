# 人口规则

文件为 `resources/rules/<local id>.population-rule.json`。Schema 见 [population-rule.schema.json](schemas/population-rule.schema.json)，完整样例见 [demo-population.population-rule.json](examples/example-pack/resources/rules/demo-population.population-rule.json)。玩法、消费仲裁、统计期冻结与缺失恢复以[设计 18](../design/18-population-and-labor.md)为准；解析结果对应 `PopulationRuleProjection`。

本规则采用一文件一定义和严格 JSON，未知字段、重复字段、注释、尾随逗号均无效。引用遵循[通用规则](README.md#通用规则)，本包用 local id，跨包用命名空间标识并声明依赖；不以标签替代明确货物引用。规则没有表现资产或显示字段。一局由世界配置选择一份对应规则，不能按聚居地类型覆盖。所有字段必填，无隐式默认值。`schema_version` 固定为 1，`id` 必须与文件名一致。连续量须在进入核心前检查为有限数，拒绝 NaN 和无穷值。Schema 检查单文件结构，加载器负责引用、文件名和字段间约束。任意定义无效使整包无效。

真实 `ContentFactory` 的 Initial 加载已支持本格式的解析、校验与核心投影；人口消费和变化的模拟行为仍由核心任务验证。示例值只演示写法，尚未配平。

## `simulation`

| 字段 | 类型、单位与范围 |
| --- | --- |
| `needs` | 固定六键对象，见下表，不允许增删需求 |
| `consumption_goods` | 六类生活货物及换算数组，见下文 |
| `labor_ratio` | 有限数，[0,1]；人口换算劳动力比例 |
| `consumption_interval_ticks` | 正 32 位整数；60 ticks = 1 模拟秒 |
| `statistics_period_ticks` | 正 32 位整数；必须是消费间隔大于 1 的整数倍 |
| `base_growth_per_second` | 有限非负数；每模拟秒基准增长比例 |
| `improvement_growth_per_second` | 有限非负数；每模拟秒最大改善增长加成 |
| `maximum_loss_per_second` | 有限非负数；每模拟秒最大流失比例 |

`needs` 的每一项都必填 `class`、`amount_per_person_second` 和 `weight`。六项人均率均为有限正数，单位是需求量/人/模拟秒，不能用零关闭某项需求；权重也为有限正数，在两类内分别归一化。

| 键 | `class` | 明确的货物角色 |
| --- | --- | --- |
| `food` | `basic` | 主食，缺口可用加工食品补足 |
| `clothing` | `basic` | 衣物 |
| `improved_diet` | `improvement` | 加工食品 |
| `consumer_goods` | `improvement` | 日用品 |
| `consumer_electronics` | `improvement` | 消费电子 |
| `medical_supplies` | `improvement` | 医疗用品 |

每个 `consumption_goods` 元素必填：

| 字段 | 类型、单位与范围 |
| --- | --- |
| `good` | 明确的货物定义引用，必须可库存 |
| `satisfies` | 非空数组；每项有 `need`（上述六键之一）和有限正数 `amount_per_good_unit`（需求量/货物单位） |
| `selection_order` | 非负 32 位整数；越小越先选，不是需求权重 |

## 加载器检查

- 恰好六种不重复的生活货物，角色由明确换算表表达：主食只满足 `food`，衣物只满足 `clothing`，加工食品同时满足 `food` 与 `improved_diet`，其余三种只满足各自改善需求。每种货物内部不得重复需求；满足 `food` 的货物不能带 `raw_good` 标签（Raw Good），因此 Grain、Produce、Animal Products 不能作为口粮；也不能让其他改善货物替代食物或衣物。
- `selection_order` 不重复，主食必须排在加工食品之前。此顺序只选择能满足当前需求的货物；不能改变基本消费、设施请求、改善消费的资源仲裁顺序。
- 同一份加工食品按表同时换算两项满足量，库存只扣一次，两项各自封顶；不得拆成两条相互独立的消费货物记录。
- 校验全部引用存在、可库存且跨包依赖已声明，并检查统计期倍数与有限数。

核心投影将固定键转换为 `PopulationNeed`，分类转换为 `PopulationNeedClass`；换算表转换为 `ConsumptionGoodProjection` / `NeedConversion`。核心消费、剩余量和人口变化不由加载器执行。
