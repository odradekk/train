# 施工规则

文件为 `resources/rules/<local id>.construction-rule.json`。Schema 见 [construction-rule.schema.json](schemas/construction-rule.schema.json)，完整样例见 [demo-construction.construction-rule.json](examples/example-pack/resources/rules/demo-construction.construction-rule.json)。玩法与冻结边界以[设计 14](../design/14-construction-refit-and-deconstruction.md)及[设计 16 的建城](../design/16-settlements.md#建立聚居地)为准；解析结果对应 `ConstructionRuleProjection`。

三种规则均采用一文件一定义和严格 JSON，未知字段、重复字段、注释、尾随逗号均无效。引用遵循[通用规则](README.md#通用规则)，本包用 local id，跨包用命名空间标识并声明依赖；不以标签替代明确货物引用。规则没有表现资产或显示字段。一局由世界配置选择一份对应规则，不能按聚居地类型覆盖。所有字段必填，无隐式默认值。`schema_version` 固定为 1，`id` 必须与文件名一致。连续量须在进入核心前检查为有限数，拒绝 NaN 和无穷值。Schema 检查单文件结构，加载器负责引用、文件名和字段间约束。任意定义无效使整包无效。

当前仅为规划格式和示例，不证明生产加载器已支持或模拟行为已验证。示例值只演示写法，尚未配平。

## `simulation`

| 字段 | 类型、单位与范围 |
| --- | --- |
| `founding_duration_ticks` | 正无符号 64 位整数（1–18446744073709551615）；建城固定工期，60 ticks = 1 模拟秒；开始时冻结 |
| `construction_work_per_item` | 正有符号 64 位整数；每件 BOM 的建造工作量 |
| `refit_work_per_changed_item` | 正有符号 64 位整数；每个新增、移除或替换单位的改装工作量 |
| `deconstruction_work_per_item` | 正有符号 64 位整数；每件 BOM 的拆解工作量 |
| `repair_work_per_damaged_part` | 正有符号 64 位整数；每个受损但未被毁部件的修理工作量 |
| `power_per_work` | 有限非负数；每工作量消耗的电量，0 表示不用电 |
| `labor_per_work` | 有限非负数；以 1 工作量/模拟秒运行时占用的劳动力，0 表示不占劳动力 |

有符号 64 位正整数上限均为 9223372036854775807。四项工作量系数不能写浮点小数。计数由内容的设计投影提供，总工作量和损伤相关修理量由核心计算；运行进度和工作速率仍为 `double`。大型设备作为 BOM 成品整件计数，不递归展开安装成本。

加载器检查文件名、有限数与范围。规则不含手写 BOM、每类货物装配系数、施工能力或地块配置，也不把建城固定工期改为依赖施工能力的任务。工作量及强度在施工任务启动时冻结；发布规则不追改已启动任务。
