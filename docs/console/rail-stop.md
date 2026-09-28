# `rail stop`：在指定模拟步正式停车并清零速度

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail stop
rail stop 101
rail stop assembly 12
rail stop assembly 12 330
rail stop at 330
```

本命令提交 `train.stop` 世界修改，与 [`rail start`](rail-start.md) 及其他世界修改共用 [`voxel edits`](voxel-edits.md) 的编号、边界顺序、日志与存档。目标步写成末尾 `<step>` 或 `at <step>`，二者只能写一个；可执行目标步是闭区间 `[当前模拟步, ulong.MaxValue]`，无参数表示当前步。小于当前步输出 `Rail PastStep.`，不分配修改编号、不改权威状态。没有编组时输出 `Rail NoAssembly.`。两个及以上编组且未写 `assembly <id>` 时输出 `Rail UnknownAssembly.`，不会把一次停车广播给所有编组。显式 `assembly <id>` 只停该编组并把速度置零。`ulong.MaxValue` 是终态步：到达后仍执行已接受的停车，再推进为 `StepOverflow`。

接受后分配修改编号，执行时消费一次事务（已经停车再次提交也是），把速度置零并关闭牵引，不保留待恢复的旧速度，不分配实体 ID，也不推进模拟步。目标等于当前步时立即执行，输出 `Rail Stopped edit <id> step <now> at <target>.`；未来步输出 `Rail Pending edit <id> step <now> at <target>.`，在该步边界按接受顺序、早于该步积分执行；30/60/144 FPS 一帧推进多步时仍在目标步停车。结构事件若未改变该编组拓扑，身份保持不变，已排队命令仍打原目标；真正拆分时命令留在稳定继承者上，不复制给独立后车。目标编组在执行前已消失时，命令保留在队列和存档中，到自己的位置记录为 `UnknownAssembly` 拒绝并出队，模拟继续推进；加载只拒绝从未分配的编组身份。事务额度耗尽同样在执行位置记录为 `TransactionExhausted`。

暂停（[`rail pause`](rail-pause.md)）不是停车：它只停止自动推进，牵引状态保持不变。之后 [`rail step`](rail-step.md) 只会推进时钟，不会再产生位移，除非再次 [`rail start`](rail-start.md)。

提交前拒绝（无修改编号）：`NoAssembly`、`UnknownAssembly`（无法确定目标时）、`PastStep`、`InputExhausted`、`SimulationFaulted`、`UnallocatedReference`。执行位置拒绝（占用修改编号）：`UnknownAssembly`、`TransactionExhausted`。
