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
rail start at 330
rail start assembly 12 at 330
```

本命令提交 `train.start` 世界修改，与体素、载具、铁路修改共用 [`voxel edits`](voxel-edits.md) 的编号、边界顺序、日志与存档。目标步写成末尾 `<step>` 或 `at <step>`，二者只能写一个；可执行目标步是闭区间 `[当前模拟步, ulong.MaxValue]`，无参数表示当前步。小于当前步输出 `Rail PastStep.`，不分配修改编号、权威状态不变。`ulong.MaxValue` 是终态步：到达后仍执行已接受的修改，但不能再 [`rail step`](rail-step.md)。`rail start 0` 仍是目标步 0，不是编组号。

无 `assembly` 且只有一个编组时，打那一列，保持原场景。没有编组输出 `Rail NoAssembly.`。两个及以上编组且未写 `assembly <id>` 时输出 `Rail UnknownAssembly.`，不会把一次启动广播给所有编组。这些解析失败以及 `ReverseUnsupported`、`NoRailway` 都在提交前拒绝，不分配修改编号。显式 `assembly <id>` 只操作该编组并写入修改；当前步执行时身份不存在得到 `UnknownAssembly`，未来步写从未分配的身份输出 `Rail UnallocatedReference.`。

接受后分配修改编号和当前边界的 `(step, sequence)`，不分配实体 ID，也不推进步号。目标等于当前步时立即在该位置执行，输出 `Rail Started edit <id> step <now> at <target>.`；执行条件不满足时同样占用该编号并记录为拒绝，输出 `Rail <status> edit <id>.`。未来步输出 `Rail Pending edit <id> step <now> at <target>.`，所有执行条件（铁路、编组、动力、控制、支撑、前方进路、事务额度）都到目标步边界才检查：失败只把这一条记录为拒绝并出队，其余修改和模拟继续推进，结果用 [`voxel edits log`](voxel-edits.md) 查看。同一边界上按接受顺序执行，早于该步 [`rail step`](rail-step.md) 积分；因此暂停时依次提交的“移除轨格 → `rail repair` → `rail start`”在同一步得到连续位置，与在运行中提交到同一目标步的结果相同，与每帧推进多少步无关。初态速度仍为 0；之后用步进或开发场景的 1× 主机时钟积分。

`reverse` 在本票单车头夹具上明确拒绝，输出 `Rail ReverseUnsupported.`，权威状态不变，也不会自动改用其他动力；无动力滑行后车同样不能靠本命令获得倒行。启动会检查铁路、指定编组、实际车钩零件、活动动力/控制、每个必要接触的当前支撑，以及沿各接触实际路径的前方可行进路。无活动动力的独立后车输出 `Rail MissingPropulsion.`，不会激活其他车头，修轨也不会赋予牵引。活动车头失去 control 后编组按当前速度滑行，再启动输出 `Rail MissingControl.`，不能继续当作有效牵引。前方缺口或支撑失效停车不保留待恢复旧速度；当前接触失支撑输出 `Rail InsufficientSupport.`。未修复的前方缺口使各接触已无可行进路时输出 `Rail DisconnectedPath.`，保持零速和 `halt forward-gap`。线路末端仍可正式启动，但恢复后保持零速，阻塞原因按当前世界重查为末端。接触点之间已有缺口但各接触仍有支撑时，不视为支撑失效，后方接触到达缺口入侧后才停车。[`rail repair`](rail-repair.md) 只把有支撑且前方可走的有动力编组转为可启动，必须再执行本命令从零加速。

提交前拒绝（无修改编号）：`ReverseUnsupported`、`NoRailway`、`NoAssembly`、`UnknownAssembly`（无法确定目标时）、`PastStep`、`InputExhausted`、`SimulationFaulted`、`UnallocatedReference`。执行位置拒绝（占用修改编号）：`NoRailway`、`UnknownAssembly`、`MissingPropulsion`、`MissingControl`、`InsufficientSupport`、`DisconnectedPath`、`VehicleConstraint`、`TransactionExhausted`。执行中的内部故障按 [`voxel edits`](voxel-edits.md) 回滚本条并停机。

暂停只停止主机时钟自动推进，不改变牵引状态；停车必须用 [`rail stop`](rail-stop.md)。用 [`rail query assembly`](rail-query.md) 或 [`rail query motion`](rail-query.md) 查看速度、牵引开关、步号和待执行启动/停车。正式铁路、编组、运动和待执行修改随 [`voxel save`](voxel-save.md) 写入格式 5；[`voxel load`](voxel-load.md) 从文件恢复后须再执行本命令才会从开发停车重新启动；前方缺口仍阻塞时本命令继续拒绝。
