# 09 存档

> 决策出处：转录 Q29、Q42、Q79–Q88；2026-09-05 [#34](https://github.com/odradekk/train/issues/34) 确认首版仅使用无状态随机与浮点往返要求。

## 边界

本文定义存档的组成、世界重建方式与兼容策略。Save-local Definition 的来源规则见 [02](02-definitions-and-content.md)；Stateful RNG 与运行时 ID 见 [04](04-deterministic-simulation.md)。

## 不变量

- 基础世界通过 Seed + GeneratorVersion 重建，之后应用 Runtime Delta。
- 所有主要格式从现在开始保留 Version 字段。
- Next Transaction ID、Next Entity ID、Save-local Definitions 必须进入存档；首版只有无状态随机，不需要保存可推进的 RNG State。后续添加有状态随机流时，其完整状态必须进入存档。
- 旧 Save 不能因为 Generator 更新而偷偷使用新算法重新解释世界。

## 规则

### 首版存档

```text
Save
├─ SaveFormatVersion
├─ WorldSeed
├─ GeneratorVersion
├─ Runtime Delta
├─ Next Transaction ID
├─ Next Entity ID
└─ Save-local Definitions
```

后期如果加载性能出现瓶颈，用 Snapshot + Delta 作为优化。首版不做完整 Event Sourcing。

连续量以可无损往返的 JSON 数值保存，使用与文化无关的 round-trip 表示，恢复相同的 `double` 值；不截断小数位，不允许非有限值。验收须比较中途保存恢复与不中断运行的结果，详见 [04](04-deterministic-simulation.md)。

### 存档兼容

- 开发阶段不保证存档兼容。
- 进入正式发行阶段后，存档向前迁移成为硬性约束；届时可采用保存旧 Generator、Migration、Snapshot 等方式保证兼容。

## 首版范围

Save-local Definition、Runtime Delta、WorldSeed、IDs、World reconstruction、double 无损往返；达到正常 Save → Quit → Load → Continue（roadmap Phase 8）。

## 明确延后

Snapshot Save Optimization、正式版本 Save Migration；缺失 Mod 的兼容与回退（转录 Q42、Q88）。

## 开放问题

- [定义 Revision 语义哈希、Catalog 版本拆分与存档绑定](https://github.com/odradekk/train/issues/35)：存档中实例引用内置 Revision 的哈希在游戏更新后如何解析，嵌入引用到的内置 Revision 还是重绑到当前 Revision。#31 已定 Save-local 内嵌存档、Blueprint 库位于 `user://blueprints/` 并复用 Composite 格式；本票确定库内具体命名、存档落盘细节及保留旧 Revision 的持久化与重启解析。
- 地图迷雾：存档格式细节与 Snapshot。
