# 战斗规则

文件为 `resources/combat-rules/<local id>.combat-rule.json`，`schema_version` 为 1。遵循 [通用规则](README.md)：一文件一份定义、严格 JSON、命名空间引用和包依赖。Schema 见 [对应 Schema](schemas/combat-rule.schema.json)。样例只展示格式，不冻结配平数值。

一局使用的规则由生成配置 `rules.combat` 选择，解析成核心 `CombatRuleProjection`。内容不执行战斗、士气变化或新聚落事件。

## 字段

所有字段必填，没有隐式默认值；数组可以为空，除非下述业务校验另有限制。连续值必须有限。

| 字段 | 说明 |
| --- | --- |
| `retreat_seconds` | 正数，撤退等待模拟秒。 |
| `morale_damage_coefficient` | 非负数，损伤的士气系数。 |
| `weapon_destroyed_morale_loss` | [0,1]，每个武器被毁的士气损失比例。 |
| `morale_recovery_per_second` | 非负数，每模拟秒恢复率。 |
| `surrender_threshold` | [0,1]，投降阈值。 |
| `new_lair_interval_seconds` | 正数，新聚落事件的模拟秒间隔。 |
