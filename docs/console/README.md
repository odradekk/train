# 控制台命令手册

本页记录当前基础控制台分发器提供的五个命令，以及 Debug 游戏宿主可选接入的模拟调试命令：

| 命令 | 用途 |
| --- | --- |
| [help](help.md) | 列出基础命令名称 |
| [clear](clear.md) | 清空内存中的控制台日志缓冲区 |
| [quit](quit.md) | 委托宿主处理退出；新契约宿主接入存档离开保护 |
| [reload](reload.md) | 委托宿主重新加载内容 |
| [loglevel](loglevel.md) | 查询或设置日志过滤等级 |
| [sim](sim.md) | 查询或控制当前活动模拟会话（仅 Debug） |

发布构建的 `help` 列表不包含 `reload` 或 `sim`；Debug 游戏宿主列出五个基础命令与 `sim`。

默认 App 仍使用旧模拟和内容加载入口；新 `HostCoordinator` 与契约命令通过实例级 `CreateHost` 测试装配委托接入，尚未连接真实工厂。按[用户拆票决定](https://github.com/odradekk/train/issues/317#issuecomment-6136143469)，#317 交付协调器，生产切换与旧入口删除归 #424。两条路径的差异见 [`sim`](sim.md) 和 [`reload`](reload.md)。

## 输入规则

- 按反引号键（通常与 `~` 共用）打开控制台；打开后按 `Esc` 关闭，按 `Enter` 执行当前行。
- 命令名和参数按空白分词；连续空白和首尾空白会被忽略。
- 不提供引号、转义、分号串联或行内注释语法。
- 命令名不带 `/` 前缀，命令名不区分大小写。
- 空行不产生动作。
- 基础命令的附带 token 会按各页说明处理；当前 `help`、`clear`、`quit`、`reload` 忽略多余 token。
- 回显和多数结果受当前日志等级过滤；看不到 `info` 结果时先执行 `loglevel info`。

## 实现依据

基础分发器见 [`App/Console/ConsoleCommands.cs`](../../App/Console/ConsoleCommands.cs)，默认旧路径的模拟命令见 [`App/LegacySimulationConsoleCommands.cs`](../../App/LegacySimulationConsoleCommands.cs)，新契约路径见 [`App/SimulationConsoleCommands.cs`](../../App/SimulationConsoleCommands.cs) 与 [`App/HostCoordinator.cs`](../../App/HostCoordinator.cs)，游戏内界面见 [`Ui/GameConsole.cs`](../../Ui/GameConsole.cs)。宿主将退出、清屏、内容重载及可选的模拟命令回调交给分发器，再将分发器与日志缓冲传入界面；界面从 `RingBufferLogSink` 读取显示内容。
