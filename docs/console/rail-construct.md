# `rail construct`：提交正式直轨施工计划

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail construct
rail construct at 330
```

本命令提交 `rail.construct` 世界修改，施工计划整体写入修改载荷，与其他修改共用 [`voxel edits`](voxel-edits.md) 的编号、边界顺序、日志与存档。无 `at` 时在当前边界立即执行并输出下文的铁路结果；`at <step>` 排到该步边界，输出 `Edit <id> rail.construct pending` 与接受/目标位置，结果在执行位置按当时世界确定。

参数：无。按切片 4 固定夹具提交一份 `RailConstructionPlan`：中心线 `(16,2,16)→(144,2,16)→(272,2,16)`，轨距 1.5，整线生成后再在 `x=144` 做技术分段，接缝轨枕只生成一次。几何来自正式体积，不是手摆节点。

成功时由 `RailEditTransaction` 一次写入几何、稳定轨段身份、明确端点连接、正体积占格和 Cell→区间映射，并分配两个轨段 RuntimeId。失败不部分提交，不消费实体 ID 或事务计数，也不发成功变化。

成功按字段分行输出 `Rail Committed.`、轨段数、轨枕数、占格数和身份。失败输出 `Rail <Status>.`，例如已有轨道共占一格得到 `IndependentLineConflict`，净空扫掠命中非轨障碍得到 `ClearanceConflict`，已有非轨体素挡路得到 `WorldObstacle`。

重复施工同一夹具会因共占失败。要先看轨再改，使用 [`rail query`](rail-query.md)；损坏后恢复原轨段用 [`rail repair`](rail-repair.md)，不要再次 `construct`。伤害仍走 [`voxel damage`](voxel-damage.md)。
