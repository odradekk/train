# 铁路规则

文件为 `resources/railway-rules/<local id>.railway-rule.json`，`schema_version` 为 1。遵循 [通用规则](README.md)：一文件一份定义、严格 JSON、命名空间引用和包依赖。Schema 见 [对应 Schema](schemas/railway-rule.schema.json)。样例只展示格式，不冻结配平数值。

一局使用的规则由生成配置 `rules.railway` 选择，解析成核心 `RailwayRuleProjection`。加载器检查 `rail_good` 引用存在且是货物。内容不执行列车运行或施工。

## 字段

所有字段必填，没有隐式默认值；数组可以为空，除非下述业务校验另有限制。连续值必须有限。

| 字段 | 说明 |
| --- | --- |
| `braking_deceleration` | 正数，m/s²。 |
| `minimum_acceleration` | 正数，启动加速度下限，m/s²。 |
| `minimum_gap_meters` | 正数，最小跟车间距，米。 |
| `rail_units_per_meter` | 正数，件/米。 |
| `build_seconds_per_meter` | 正数，模拟秒/米。 |
| `repair_seconds_per_break` | 正数，每断点模拟秒。 |
| `rail_good` | 铁路施工和修复使用的货物体素引用。 |
| `repair_rail_units_per_break` | 正整数，每断点材料件数。 |

`appearance.minimum_curve_radius_meters` 是场景中心线的最小弯道半径，正数，米；`appearance.maximum_grade` 是最大坡度，正数，以高差/水平距离表示。两项只投影给场景，不进入核心铁路规则。内容不生成中心线；算法和是否满足整条线路长度约束由体素模块处理。正式数值在场景实测后确定。

格式样例暂用本包 `hull-plate` 作为施工材料，展示货物引用，不代表正式 Rail 货物或配平选择。
