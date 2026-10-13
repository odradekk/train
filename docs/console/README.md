# 控制台命令手册

本页记录当前基础控制台分发器提供的五个命令，以及 Debug 游戏宿主接入的模拟、列车、光照和开发检查命令：

| 命令 | 用途 |
| --- | --- |
| [help](help.md) | 列出当前可用命令名称 |
| [clear](clear.md) | 清空内存中的控制台日志缓冲区 |
| [quit](quit.md) | 委托宿主处理退出；宿主接入存档离开保护 |
| [reload](reload.md) | 委托宿主重新加载内容 |
| [loglevel](loglevel.md) | 查询或设置日志过滤等级 |
| [sim](sim.md) | 查询或控制当前活动模拟会话（仅 Debug） |
| [train](train.md) | 编组、解编、启停列车和替换运行计划（仅 Debug） |
| [lighting](lighting.md) | 查询、切换检查光照并恢复游戏光照（仅 Debug） |
| [dev](dev.md) | 打开或关闭开发检查器（仅 Debug） |

玩家构建的 `help` 列表不包含 `reload`、`sim`、`train`、`lighting` 或 `dev`；Debug 游戏宿主列出 `help, clear, quit, reload, loglevel, sim, train, lighting, dev`。编辑器内运行和 debug 模板导出均为开发构建，release 模板导出为玩家构建。

默认 App 通过 `App/Composition/` 装配真实工厂，使用 `HostCoordinator` 与契约命令。模拟控制及内容重载行为见 [`sim`](sim.md) 和 [`reload`](reload.md)。

## 输入规则

- 按反引号键（通常与 `~` 共用）打开控制台；打开后按 `Esc` 关闭，按 `Enter` 执行当前行。
- 命令名和参数按空白分词；连续空白和首尾空白会被忽略。
- 不提供引号、转义、分号串联或行内注释语法。
- 命令名不带 `/` 前缀，命令名不区分大小写。
- 空行不产生动作。
- 基础命令的附带 token 会按各页说明处理；当前 `help`、`clear`、`quit`、`reload` 忽略多余 token。
- 回显和多数结果受当前日志等级过滤；看不到 `info` 结果时先执行 `loglevel info`。

## 实现依据

基础分发器见 [`App/Console/ConsoleCommands.cs`](../../App/Console/ConsoleCommands.cs)，模拟命令与宿主协调见 [`App/SimulationConsoleCommands.cs`](../../App/SimulationConsoleCommands.cs) 与 [`App/HostCoordinator.cs`](../../App/HostCoordinator.cs)，游戏内界面见 [`Ui/GameConsole.cs`](../../Ui/GameConsole.cs)。宿主将退出、清屏、内容重载及可选的模拟命令回调交给分发器，再将分发器与日志缓冲传入界面；界面从 `RingBufferLogSink` 读取显示内容。
