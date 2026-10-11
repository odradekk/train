# 基础控制台

本模块解析一行文本并执行基础命令，通过共享日志返回回显、帮助和警告，退出与内容重载交给宿主回调。它依赖 .NET 和[宿主日志](../Logging/README.md)，不直接调用 Godot 引擎、世界会话或界面。

| 类型 | 职责 |
| --- | --- |
| `ParsedConsoleCommand` | 按空白拆出命令名和有序参数，不解释引号或领域参数。 |
| `ConsoleCommandDispatcher` | 分发五个基础命令，并在宿主提供回调时转发 `sim` 参数。 |

控制台职责对应[设计器与工具：调试命令](../../docs/design/15-design-editor-and-tools.md#调试命令)。当前保留五个基础命令；宿主可选择接入 `sim`，基础分发器只转发参数。已退役的子命令不会被识别。

输入和日志显示由 Godot 的 [GameConsole](../../Ui/GameConsole.cs) 提供；宿主创建分发器，将退出、清屏、重载回调和构建可用性传入。Debug 宿主还可传入模拟命令回调；未提供时 `sim` 与其他未知命令相同。分发器同步调用回调，不捕获其异常。`reload` 是否可用由宿主决定，不从 Core 中读取 Godot 构建状态。

非空命令先记录 Info 回显，再执行动作。clear 可以删除内存缓冲中的回显，但不会删除持久日志。loglevel 的提示在修改阈值前记录；旧阈值隐藏提示时，修改仍然生效。命令名不区分大小写；help、clear、quit、reload 忽略附带参数，loglevel 只接受零个参数或一个合法等级。

完整语法与使用限制见[控制台手册](../../docs/console/README.md)。[解析与分发测试](../../Tests/Godot/Console/ConsoleCommandTests.cs)覆盖输入规则、宿主动作、日志过滤、持久回显和已退役命令的拒绝。

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Scope console -BuildGame
```
