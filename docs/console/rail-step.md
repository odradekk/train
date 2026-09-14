# `rail step`：正式推进指定模拟步

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail step
rail step 300
```

无参数推进 1 步；一个十进制整数 `n` 推进 `n` 步。成功输出 `Rail Advanced step <now> advanced <n>.`。`n=0` 为成功空操作。无法解析的文本回到用法提示。

整批原子：步号加 `n` 会超出 `ulong` 时整批拒绝，输出 `Rail StepOverflow.`，不回绕、不部分提交。每个运动步不增加事务计数。暂停不阻止本命令。无编组时仍推进时钟。推进失败还可能返回 `NoAssembly`、`MissingPropulsion`、`InsufficientSupport`、`DisconnectedPath`、`VehicleConstraint`；失败整批回滚到推进前权威状态。

固定 60 步/秒、目标速度 4、加速度 2。第 0 步 [`rail start`](rail-start.md) 后再 `rail step 300`，车头前接触应为 96、后车前接触 87、速度 4。
