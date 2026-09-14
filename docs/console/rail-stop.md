# `rail stop`：正式停车并清零速度

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail stop
```

参数：无。在当前模拟步把编组速度立即置零并关闭牵引，不保留待恢复的旧速度。成功输出 `Rail Stopped step <n>.`。已经停车时为幂等成功。没有编组时输出 `Rail NoAssembly.`。

停车消费一次事务，不分配实体 ID，也不推进模拟步。之后 [`rail step`](rail-step.md) 只会推进时钟，不会再产生位移，除非再次 [`rail start`](rail-start.md)。
