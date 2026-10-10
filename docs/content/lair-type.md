# 生物聚落类型

文件为 `resources/lair-types/<local id>.lair-type.json`，`schema_version` 为 1。遵循 [通用规则](README.md)：一文件一份定义、严格 JSON、命名空间引用和包依赖。Schema 见 [对应 Schema](schemas/lair-type.schema.json)。样例只展示格式，不冻结配平数值。

核心字段投影为 `LairTypeProjection`；`scene_model` 只交给场景消费者。加载器检查成员引用唯一、蓝图类别为生物，场景模型引用类别为场景模型。生物数值来自蓝图的 creature/weapon 行为，不在聚落重复声明。

## 字段

所有字段必填，没有隐式默认值；数组可以为空，除非下述业务校验另有限制。连续值必须有限。

显示字段组成 `DisplayText(Name, Description)`，供 `LairTypeDraft.Display` 编辑，并作为 `LairAppearanceProjection.Display` 的 `ContentDisplay.Text`。本格式尚未定义图标和地图符号资产，`ContentDisplay.Icon` 与 `ContentDisplay.MapSymbol` 均为 `null`。

| 字段 | 说明 |
| --- | --- |
| `display.name` | 显示名称，去掉首尾空白后 1–100 个字符。 |
| `display.description` | 说明，去掉首尾空白后 1–2000 个字符。 |
| `generation_weight` | 正整数，运行中新聚落选择权重；初始生成使用配置的 lair_weights。 |
| `members` | 每项 creature 是生物蓝图引用，weight 为正整数；引用唯一且至少一项。 |
| `maximum_members` | 正整数，成员上限。 |
| `spawn_interval_seconds` | 正数，补充成员间隔，模拟秒。 |
| `raid_interval_seconds` | 正数，袭击间隔，模拟秒。 |
| `roaming_radius_hops` | 非负整数，游荡范围，图上跳数。 |
| `raid_radius_hops` | 非负整数，袭击范围，图上跳数。 |
| `rail_damage_work_seconds` | 正数，在线路上制造断点所需模拟秒。 |
| `clearance_reward` | 最小货币单位的非负整数。 |
| `scene_model` | 生物聚落场景模型蓝图引用。 |
