# 09 存档

> 决策出处：转录 Q29、Q42、Q79–Q88。

## 边界

本文定义存档的组成、世界重建方式与兼容策略。Save-local Definition 的来源规则见 [02](02-definitions-and-content.md)；Stateful RNG 与运行时 ID 见 [04](04-deterministic-simulation.md)。

## 不变量

- 基础世界通过 Seed + GeneratorVersion 重建，之后应用 Runtime Delta。
- 所有主要格式从现在开始保留 Version 字段。
- Stateful RNG States、Next Transaction ID、Next Entity ID、Save-local Definitions 必须进入存档。
- 旧 Save 不能因为 Generator 更新而偷偷使用新算法重新解释世界。

## 规则

### 首版存档

```text
Save
├─ SaveFormatVersion
├─ WorldSeed
├─ GeneratorVersion
├─ Runtime Delta
├─ Stateful RNG States
├─ Next Transaction ID
├─ Next Entity ID
└─ Save-local Definitions
```

后期如果加载性能出现瓶颈，用 Snapshot + Delta 作为优化。首版不做完整 Event Sourcing。

### 存档兼容

- 开发阶段不保证存档兼容。
- 进入正式发行阶段后，存档向前迁移成为硬性约束；届时可采用保存旧 Generator、Migration、Snapshot 等方式保证兼容。

## 首版范围

Save-local Definition、Runtime Delta、RNG State、IDs、World reconstruction；达到正常 Save → Quit → Load → Continue（roadmap Phase 8）。

## 明确延后

Snapshot Save Optimization、正式版本 Save Migration；缺失 Mod 的兼容与回退（转录 Q42、Q88）。

## 开放问题

- [定义 Revision 语义哈希、Catalog 版本拆分与存档绑定](https://github.com/odradekk/train/issues/35)：存档中实例引用内置 Revision 的哈希在游戏更新后如何解析，嵌入引用到的内置 Revision 还是重绑到当前 Revision；Save-local Definition 与 Player Blueprint Library 的落盘位置。
- [定义确定性与数值策略](https://github.com/odradekk/train/issues/34)：浮点在存档中的往返格式。
- 地图迷雾：存档格式细节与 Snapshot。
