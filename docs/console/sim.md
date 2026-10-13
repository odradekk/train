# `sim`：调试核心模拟

[返回命令索引](README.md)

`sim` 只由 Debug 游戏宿主提供，用于观察和控制当前活动模拟会话。它不会创建世界、玩家、存档或模拟会话；没有会话时报告“没有活动模拟会话”。发布构建不注册该命令。

默认 App 使用真实工厂装配的 `HostCoordinator` 和 `SimulationConsoleCommands`。实例级 `CreateHost` 委托供引擎测试替换宿主。

```text
sim
sim status
sim pause
sim resume
sim speed 1|2|4|8
sim step [N]
sim checkpoint
```

`sim` 与 `sim status` 输出当前位置、模拟秒、暂停状态、设定与实际倍速、帧余量和故障。宿主使用 `HostStatus`，没有 `PendingSteps`，并分别显示玩家／通知暂停、失焦暂停与技术操作锁；核心故障也会使会话保持暂停。预算内未执行的整步额度不会跨帧补算，只保留不足一步的余量。

宿主有四个独立暂停来源：玩家／通知、失焦、技术操作锁、核心冻结。`pause` 与 `resume` 只设置或解除玩家／通知来源；其他来源仍有效时，`resume` 会说明模拟保持暂停。聚焦恢复只解除失焦来源。“失焦时自动暂停”默认关闭，目前没有设置界面或命令。`speed` 只接受 1、2、4、8，不解除任何暂停，最高档暂定，须经性能实测确认。

`step` 默认推进一步，`N` 必须是大于零的十进制 `UInt64`。手动推进只允许在宿主已暂停时执行；运行中会报告 `simulation_requires_pause`，不会隐式暂停。手动步同步执行，不受自动推进的每帧预算限制；较大的 N 会占用当前帧。宿主逐步调用核心，遇到通知暂停立即停止，并报告请求步数与实际完成步数；命令处理器不再次分发事件。默认通知策略全部为 `None`，暂停／降速策略目前通过注入的测试策略验证。故障已冻结的会话仍允许 `pause` 和 `speed N`，但拒绝 `resume` 和 `step`；故障显示 `CoreFault` 的 `Code`、`Operation`、`Field`。

`checkpoint` 只输出当前权威位置与 SHA-256 摘要，供确定性诊断使用；它不会写存档，也不会输出完整权威文档。

宿主在正常会话调用 `Capture(Save)`，在冻结会话调用 `Capture(Diagnostic)`，直接读取捕获结果声明的位置与摘要，不解析不透明载荷。捕获失败通过 `simulation_checkpoint_failed` 报告。技术操作锁期间，修改类子命令报告 `simulation_busy`；状态查询与 checkpoint 仍可用。

内部故障发生时，宿主会立即记录一次 `simulation_fault` 错误日志，包含异常详情并冻结会话。重复查询或操作冻结会话只报告当前状态，不再重复记录该故障的错误日志。

宿主保留原有日志事件 ID，故障字段按契约迁移如下：

| 旧字段 | 新字段或处理 |
| --- | --- |
| `fault_code` | 保留 |
| `fault_phase` | 改为 `operation`、`field` |
| `attempted_completed_steps`、`attempted_sequence` | 改为 `fault_completed_steps`、`fault_sequence`，来自 `CoreFault.Position` |
| `attempted_command_id` | 改为 `command_id`，没有命令时为 null |
| `entity_id` | 改为 `subject`，来自有类型的 `CoreFault.Subject` |
| `attempted_transaction_id` | 删除 |
| 无 | 新增 `run_session`，标识当前运行会话 |
| 异常对象 | 改为 `CoreDiagnostic` 的 `diagnostic_message`、`diagnostic_stack_trace` 字段 |

`simulation_fault` 保持 Error 级别。`simulation_frozen` 保留 `fault_code`，并用 `operation`、`field` 替代旧 `fault_phase`；拒绝原因另由 `problem_code`、`problem_field` 表达。每次 checkpoint 捕获失败只记录一条 Warning `simulation_checkpoint_failed`，普通失败带 `problem_code`、`problem_field`，故障失败带 `fault_code`、`operation`、`field`、`fault_completed_steps`、`fault_sequence`、`subject` 和 `diagnostic_*`。冻结后诊断捕获再次返回故障也不会重复记录 `simulation_fault`。

手动推进结果仍用 `requested_steps`、`steps_advanced`、`completed_steps`；checkpoint 仍用 `completed_steps`、`sequence`、`sha256`。

参数形状在访问会话前严格检查。多余 token、未知子命令、零、负数以及不支持的倍速都会输出用法警告，不修改宿主或模拟状态。所有结果通过共享日志的 `simulation` 分类输出；数值使用与系统区域设置无关的格式。
