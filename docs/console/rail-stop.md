# `rail stop`：在指定模拟步正式停车并清零速度

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail stop
rail stop 101
```

可执行目标步是闭区间 `[当前模拟步, ulong.MaxValue]`。无参数表示当前步。小于当前步输出 `Rail PastStep.`，不改权威状态、不入队、不消费事务。没有编组时输出 `Rail NoAssembly.`。`ulong.MaxValue` 是终态步：到达后仍执行已接受的停车，再推进为 `StepOverflow`。

接受后一律入队并消费一次事务（已经停车再次提交也是），把速度置零并关闭牵引，不保留待恢复的旧速度，不分配实体 ID，也不推进模拟步。当前步与未来步走同一执行路径：按入队 `Sequence` 执行，到步时新提交的命令不能越过此前已接受的同目标步命令。目标等于当前步时，入队后立刻执行该步全部到期命令。未来步在 [`rail step`](rail-step.md) 于该步积分前按同一顺序执行；30/60/144 FPS 一帧推进多步时仍在目标步停车。成功输出 `Rail Stopped step <now> at <target>.`。

之后 [`rail step`](rail-step.md) 只会推进时钟，不会再产生位移，除非再次 [`rail start`](rail-start.md)。
