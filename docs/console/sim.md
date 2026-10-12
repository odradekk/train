# `sim`：调试核心模拟

[返回命令索引](README.md)

`sim` 只由 Debug 游戏宿主提供，用于观察和控制当前活动模拟会话。它不会创建世界、玩家、存档或模拟会话；没有会话时报告“没有活动模拟会话”。发布构建不注册该命令。

当前处于 #317 的生产切换前阶段。默认 App 仍使用旧模拟入口与 `LegacySimulationConsoleCommands`；实例级 `CreateHost` 测试装配委托接入 `HostCoordinator` 和新 `SimulationConsoleCommands`。以下分别说明两条路径；新路径尚未接入真实工厂。按[批准计划](https://github.com/odradekk/train/issues/317#issuecomment-6132109006)，#306、#308、#318 合入后才切换生产装配并删除旧入口。

```text
sim
sim status
sim pause
sim resume
sim speed 1|2|4|8
sim step [N]
sim checkpoint
```

`sim` 与 `sim status` 输出当前位置、模拟秒、暂停状态、设定与实际倍速、帧余量和故障。默认旧路径还输出待处理步数：它只描述当前推进调用，调用结束后为零，生产切换时删除。新路径使用 `HostStatus`，没有 `PendingSteps`，并分别显示玩家／通知暂停、失焦暂停与技术操作锁；核心故障也会使会话保持暂停。预算内未执行的整步额度不会跨帧补算，只保留不足一步的余量。

新路径有四个独立暂停来源：玩家／通知、失焦、技术操作锁、核心冻结。`pause` 与 `resume` 只设置或解除玩家／通知来源；其他来源仍有效时，`resume` 会说明模拟保持暂停。聚焦恢复只解除失焦来源。“失焦时自动暂停”默认关闭，目前没有设置界面或命令。`speed` 只接受 1、2、4、8，不解除任何暂停，最高档暂定，须经性能实测确认。

`step` 默认推进一步，`N` 必须是大于零的十进制 `UInt64`。手动推进只允许在宿主已暂停时执行；运行中会报告 `simulation_requires_pause`，不会隐式暂停。手动步同步执行，不受自动推进的每帧预算限制；较大的 N 会占用当前帧。新路径逐步调用核心，遇到通知暂停立即停止，并报告请求步数与实际完成步数；命令处理器不再次分发事件。默认通知策略全部为 `None`，暂停／降速策略目前通过注入的测试策略验证。故障已冻结的会话不能继续运行或手动推进；旧路径显示故障代码和阶段，新路径显示 `CoreFault` 的 `Code`、`Operation`、`Field`。

`checkpoint` 只输出当前权威位置与 SHA-256 摘要，供确定性诊断使用；它不会写存档，也不会输出完整权威文档。

新路径在正常会话调用 `Capture(Save)`，在冻结会话调用 `Capture(Diagnostic)`，直接读取捕获结果声明的位置与摘要，不解析不透明载荷。捕获失败通过 `simulation_checkpoint_failed` 报告。技术操作锁期间，修改类子命令报告 `simulation_busy`；状态查询与 checkpoint 仍可用。

内部故障发生时，宿主会立即记录一次 `simulation_fault` 错误日志，包含异常详情并冻结会话。重复查询或操作冻结会话只报告当前状态，不再重复记录该故障的错误日志。

新路径保留原有日志事件 ID，按契约调整故障字段：旧 `fault_phase` 由 `operation` 与 `field` 替代，故障位置取自 `CoreFault.Position`，对象来自有类型的 `Subject`；消息和栈使用 `CoreDiagnostic`，字段为 `diagnostic_message`、`diagnostic_stack_trace`。手动推进结果仍用 `requested_steps`、`steps_advanced`、`completed_steps`；checkpoint 仍用 `completed_steps`、`sequence`、`sha256`。

参数形状在访问会话前严格检查。多余 token、未知子命令、零、负数以及不支持的倍速都会输出用法警告，不修改宿主或模拟状态。所有结果通过共享日志的 `simulation` 分类输出；数值使用与系统区域设置无关的格式。
