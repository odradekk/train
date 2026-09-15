# `rail step`：正式推进指定模拟步

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail step
rail step 300
```

无参数推进 1 步；一个十进制整数 `n` 推进 `n` 步。成功输出 `Rail Advanced step <now> advanced <n>.`。`n=0` 为成功空操作。无法解析的文本回到用法提示。

整批原子：步号加 `n` 会超出 `ulong` 时整批拒绝，输出 `Rail StepOverflow.`，不回绕、不部分提交。每个运动步先按入队顺序执行已分配到该步的 [`rail start`](rail-start.md) / [`rail stop`](rail-stop.md)，再按编组身份稳定顺序积分全部已提交编组。有活动动力的编组继续牵引；无活动动力且速度大于 0 的编组按当前有效 `track_contact` 减速度沿原方向滑行，零点分段。从 `MaxValue - 1` 推进一步会到达终态步并执行已分配到 `ulong.MaxValue` 的命令；时钟已在 `MaxValue` 时再推进仍为 `StepOverflow`。每个运动步不增加事务计数。暂停不阻止本命令。无编组时仍推进时钟。推进失败还可能返回 `NoAssembly`、`MissingPropulsion`、`InsufficientSupport`、`DisconnectedPath`、`VehicleConstraint`；失败整批回滚到推进前权威状态，已入队但尚未到期的命令一并恢复。

固定 60 步/秒、目标速度 4、加速度 2、无动力滑行减速度 1。第 0 步 [`rail start`](rail-start.md) 后再 `rail step 300`，车头前接触应为 96、后车前接触 87、速度 4。车钩断开后再 `rail step 30`，前车 98、后车 88.875 / 3.5。
