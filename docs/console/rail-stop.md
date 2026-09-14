# `rail stop`：在指定模拟步正式停车并清零速度

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail stop
rail stop 101
```

无参数或目标步等于当前模拟步时，立即把编组速度置零并关闭牵引，不保留待恢复的旧速度。成功输出 `Rail Stopped step <now> at <target>.`。已经停车时为幂等成功。没有编组时输出 `Rail NoAssembly.`。

一个十进制整数表示目标模拟步。大于当前步时先入队，到达该步时在该步积分前按入队顺序执行；30/60/144 FPS 一帧推进多步时仍在目标步停车，不能用显示帧切分代替。目标步小于当前步输出 `Rail PastStep.`，不改权威状态、不消费事务。

停车消费一次事务，不分配实体 ID，也不推进模拟步。之后 [`rail step`](rail-step.md) 只会推进时钟，不会再产生位移，除非再次 [`rail start`](rail-start.md)。
