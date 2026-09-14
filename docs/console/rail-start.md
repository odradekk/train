# `rail start`：按指定模拟步启动编组牵引

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail start
rail start reverse
rail start 0
rail start reverse 0
```

无参数或目标步等于当前模拟步时，立即启动已编组列车的牵引，不推进步号。初态速度仍为 0；之后用 [`rail step`](rail-step.md) 或开发场景的 1× 主机时钟积分。成功输出 `Rail Started step <now> at <target>.`。已在牵引中再次启动为幂等成功。

一个十进制整数表示目标模拟步。大于当前步时先入队，到达该步时在该步积分前按入队顺序执行；与 [`rail stop`](rail-stop.md) 共用同一正式入口，Host 一帧推进多步时也会在批次内部执行。目标步小于当前步输出 `Rail PastStep.`，权威状态不变。

`reverse` 在本票单车头夹具上明确拒绝，输出 `Rail ReverseUnsupported.`，权威状态不变，也不会自动改用其他动力。启动会检查铁路、编组、实际车钩零件、活动动力/控制、接触支撑和前方方向，并消费一次事务、不分配实体 ID。

失败状态：`NoRailway`、`NoAssembly`、`MissingPropulsion`、`MissingControl`、`InsufficientSupport`、`VehicleConstraint`、`TransactionExhausted`、`PastStep`。

用 [`rail query assembly`](rail-query.md) 或 [`rail query motion`](rail-query.md) 查看速度、牵引开关、步号和待执行命令。停车见 [`rail stop`](rail-stop.md)。铁路与运动状态仍不能完整存档：[`voxel save`](voxel-save.md) 返回 `UnsupportedRailway`。
