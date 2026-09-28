# `voxel edits`：查询世界修改命令的位置、队列与日志

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel edits
voxel edits pending
voxel edits log [<count>]
voxel edits fault
```

参数：`log` 可带一个正整数条数，省略时显示最近 8 条；其余形式不带参数。本命令只读，不占用位置、不推进时钟、不改变指纹。

## 世界修改命令与执行位置

体素 [`add`](voxel-add.md) / [`remove`](voxel-remove.md) / [`damage`](voxel-damage.md)，车辆 [`manufacture`](composite-manufacture.md) / [`damage`](composite-damage.md)，五类改装 [`add`](composite-add.md) / [`remove`](composite-remove.md) / [`move`](composite-move.md) / [`rotate`](composite-rotate.md) / [`replace`](composite-replace.md) 以及 [`batch`](composite-batch.md)，铁路 [`construct`](rail-construct.md) / [`repair`](rail-repair.md)、编组 [`assemble`](rail-assemble.md) 与列车 [`start`](rail-start.md) / [`stop`](rail-stop.md)，都先解析成同一种世界修改命令（种类依次为 `voxel.*`、`vehicle.*`、`rail.construct`、`rail.repair`、`train.assemble`、`train.start`、`train.stop`），再交给会话唯一的校验与事务入口。Playground 修改面板、起停按钮和自动验证序列走同一入口。内容重载改变世界时，在当前边界记录一条 `content.publish`（见 [`reload`](reload.md)）；它只由重载边界产生，不能从控制台或面板提交。

- 每条被接受的命令获得递增的编号 `edit_id`，并在当前边界取下一个步内序号，位置写作 `步:序号`，例如 `300:2`。全序由已完成模拟步和步内序号决定，与墙钟、帧或线程无关。
- 暂停时连续提交多条命令只递增步内序号，不推进时钟，也不移动列车。模拟推进到新的一步时序号从 0 重新开始。
- 立即命令在接受位置执行。尾随 `at <step>` 的命令进入队列，时钟到达该步边界时，按接受顺序各取一个新序号执行，不分种类；例如暂停时依次提交“移除轨格 → 修复 → 启动”，三者在同一边界取得连续位置。
- 执行时重新检查前提。目标已被摧毁、占格冲突等预期拒绝会在原位置出队并记为 `rejected`，之前成功的命令保持有效，之后的命令照常执行；不重试、不改换目标。改装与多目标伤害仍是整体事务，失败整批回滚。
- 过去步（`PastStep`）、编号或序号耗尽（`InputExhausted`）以及会话已故障（`SimulationFaulted`）直接拒绝，不消耗编号或序号。
- 进入队列的命令只能引用已经分配过的车辆或零件 ID（小于当前 `next_entity_id`）；引用从未分配 ID 的定时命令返回 `Edit UnallocatedReference.`，不入队、不消耗编号或序号，因此队列总能被存档恢复接受。目标曾存在、执行前已被摧毁的命令不受影响，仍入队并在到期时拒绝。立即命令照常执行并按业务结果拒绝，例如 `UnknownVehicle`。

## 尾随 `at <step>`

在上述任一修改命令末尾追加 `at <step>`（非负十进制整数），即在该模拟步边界执行。`<step>` 等于当前步时立即执行，早于当前步时返回 `Edit PastStep.`。数字无法解析时返回该子命令的用法提示。

```text
voxel remove 60 3 20 at 303
composite add 342 2 1 0 core:refit-marker px_py at 303
composite manufacture core:dev-hollow-shell pose 60 6 36 at 305
```

带 `at` 的命令统一用两行回报：

```text
Edit 5 vehicle.refit pending
accepted 300:4 target 303
```

目标步等于当前步时，第一行改为 `Edit <id> <kind> executed|rejected <status>`，第二行为 `accepted <位置> executed <位置>`。不带 `at` 的原有写法仍立即执行，输出与之前相同。

## 查询输出

`voxel edits` 汇总：

```text
Edits boundary 300:6 next 7
pending 2 log 4
digest 3F…（64 位十六进制）
fault none
```

`boundary` 是下一条命令将取得的位置；`digest` 是本进程执行日志的链式 SHA-256，读档或重置后从全零重新开始。

`voxel edits pending` 按接受顺序列出待执行命令：`edit <id> <kind> accepted <位置> target <步>`。

`voxel edits log [<count>]` 列出最近已执行、已拒绝或故障的记录：`edit <id> <kind> <state> <status> at <执行位置> accepted <接受位置>`。会话最多保留最近 256 条。

`voxel edits fault` 在没有故障时输出 `Edits fault none.`；否则输出命令编号、类型、位置、异常类型与消息；运动故障没有命令编号，首行为 `Edits fault motion at <位置>`，汇总行写作 `fault motion at <位置>`。

## 内部故障

事务执行中出现内部不变量错误时，本条命令的全部修改（含起停对牵引开关、速度的改变）回滚，会话保留上一个完整边界并记为 `faulted`；该命令留在队列中。编组积分破坏内部不变量时，会话同样停在最后一个完整模拟步边界，记为不带命令编号的运动故障。之后修改、步进、起停、内容重载与保存都返回 `SimulationFaulted`（重载为 `simulation_unavailable`），直到 `voxel reset` / `composite reset` 或 `voxel load`。这与预期拒绝不同：预期拒绝只让单条命令出队，会话继续运行。

## 保存与恢复

[`voxel save`](voxel-save.md) 在格式 5 的 `world_edits` 中写出下一个编号、当前边界序号，以及已接受但未执行的命令及其接受位置与目标步。保存不执行、不清空队列。[`voxel load`](voxel-load.md) 恢复后，队列里的命令仍在原目标步、按原顺序执行一次；混合了体素、载具、铁路与起停的队列在新进程中得到相同结果。保存前目标已被摧毁的命令可以正常恢复，并在到期时拒绝；已完全摧毁、仍被引用的轨段保留登记身份与几何，排队的修复据此定位；引用从未分配过的实体 ID、目标步不晚于存档步、编号或接受位置不递增等坏档整次返回 `InvalidDelta`，未知命令类型或范围内但不支持的输入格式版本返回 `UnknownFormat`；版本或类型字段类型错误、版本超出 32 位有符号整数范围等非法值属于坏档，返回 `InvalidDelta`。执行日志和摘要不写入存档。

规范编码（字段顺序、数值格式、固定向量）见 [设计文档](../design/04-deterministic-simulation.md#世界修改命令的规范编码)。
