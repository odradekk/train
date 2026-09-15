# `rail start`：按指定模拟步启动编组牵引

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail start
rail start reverse
rail start 0
rail start reverse 0
rail start assembly 12
rail start assembly 12 reverse
rail start assembly 12 330
rail start assembly 12 reverse 330
```

可执行目标步是闭区间 `[当前模拟步, ulong.MaxValue]`。无参数表示当前步。小于当前步输出 `Rail PastStep.`，权威状态不变、不入队、不消费事务。`ulong.MaxValue` 是终态步：到达后仍按同一路径执行已接受命令，但不能再 [`rail step`](rail-step.md)。`rail start 0` 仍是目标步 0，不是编组号。

无 `assembly` 且只有一个编组时，打那一列，保持原场景。没有编组输出 `Rail NoAssembly.`。两个及以上编组且未写 `assembly <id>` 时输出 `Rail UnknownAssembly.`，不会把一次启动广播给所有编组。显式 `assembly <id>` 只操作该编组；未知身份同样是 `UnknownAssembly.`。

接受后一律入队并消费一次事务（已在牵引中再次启动也是），不分配实体 ID，也不推进步号。命令目标编组写入队列与存档。当前步与未来步走同一执行路径：按入队 `Sequence` 执行，到步时新提交的命令不能越过此前已接受的同目标步命令。目标等于当前步时，入队后立刻执行该步全部到期命令，不积分。未来步在 [`rail step`](rail-step.md) 于该步积分前按同一顺序执行；时钟从 `MaxValue - 1` 到达 `MaxValue` 后也会执行终态步命令。成功输出 `Rail Started step <now> at <target>.`。初态速度仍为 0；之后用步进或开发场景的 1× 主机时钟积分。

`reverse` 在本票单车头夹具上明确拒绝，输出 `Rail ReverseUnsupported.`，权威状态不变，也不会自动改用其他动力；无动力滑行后车同样不能靠本命令获得倒行。启动会检查铁路、指定编组、实际车钩零件、活动动力/控制、接触支撑和前方方向。无活动动力的独立后车输出 `Rail MissingPropulsion.`，不会激活其他车头，修轨也不会赋予牵引。活动车头失去 control 后编组按当前速度滑行，再启动输出 `Rail MissingControl.`，不能继续当作有效牵引。前方缺口或支撑失效停车不保留待恢复旧速度；[`rail repair`](rail-repair.md) 只把有支撑的有动力编组转为可启动，必须再执行本命令从零加速。支撑仍缺失时输出 `Rail InsufficientSupport.`。

失败状态：`NoRailway`、`NoAssembly`、`UnknownAssembly`、`MissingPropulsion`、`MissingControl`、`InsufficientSupport`、`VehicleConstraint`、`TransactionExhausted`、`PastStep`。

用 [`rail query assembly`](rail-query.md) 或 [`rail query motion`](rail-query.md) 查看速度、牵引开关、步号和待执行命令。停车见 [`rail stop`](rail-stop.md)。正式铁路、编组、运动和待执行命令随 [`voxel save`](voxel-save.md) 写入格式 3；[`voxel load`](voxel-load.md) 从文件恢复后须再执行本命令才会从开发停车或末端零速重新启动。
