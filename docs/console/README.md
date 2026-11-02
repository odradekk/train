# 控制台命令手册

控制台位于开发工具底栏，提供以下命令：

| 命令 | 用途 |
| --- | --- |
| [help](help.md) | 列出可用命令，或查看一条命令的中文用法 |
| [clear](clear.md) | 清空内存中的控制台日志缓冲区 |
| [quit](quit.md) | 委托宿主处理退出；宿主接入存档离开保护 |
| [reload](reload.md) | 委托宿主重新加载内容 |
| [loglevel](loglevel.md) | 查询或设置日志过滤等级 |
| [sim](sim.md) | 查询或控制当前活动模拟会话（仅 Debug） |
| [train](train.md) | 编组、解编、启停列车和替换运行计划；含节点内停车、预约保留、计划替换、出站固定铁轨与失效重选、修理期间启动限制（仅 Debug） |
| [lighting](lighting.md) | 为世界、设计编辑器和模板预览切换检查光照并恢复游戏光照（仅 Debug） |
| [dev](dev.md) | 打开或关闭现有开发调试窗口（仅 Debug） |

玩家构建的 `help` 列表不包含 `reload`、`sim`、`train`、`lighting` 或 `dev`；Debug 游戏宿主列出 `help, clear, quit, reload, loglevel, sim, train, lighting, dev`。编辑器内运行和 debug 模板导出均为开发构建，release 模板导出为玩家构建。

默认 App 通过 `App/Composition/` 装配真实工厂，使用 `HostCoordinator` 与契约命令。模拟控制及内容重载行为见 [`sim`](sim.md) 和 [`reload`](reload.md)。

开发工具底栏只在游戏会话的宏观地图和节点视图中打开。主菜单、设计编辑器和创作模式中不可打开；切入这些画面会关闭底栏。当前底栏只有控制台，没有页签；`dev` 仍打开独立开发调试窗口，迁移见 #906。

## 打开与输入

- 按反引号键（通常与 `~` 共用）开关底栏；开发构建也可从游戏菜单的“开发工具”打开。拖动上沿调整高度，游戏界面会让出空间。
- 按 `Enter` 执行当前行。输入时提供命令名和第一个参数的补全，每项附说明；更深的参数查看输入框上方的当前用法。
- 按 `Tab` 接受补全；有候选时按 `↑`、`↓` 选择候选，无候选时翻阅历史，翻到末尾恢复原草稿。`Esc` 先收起候选，再关闭底栏。
- 命令名和参数按空白分词；连续空白和首尾空白会被忽略。不提供引号、转义、分号串联或行内注释语法。
- 命令名不带 `/` 前缀，不区分大小写。空行不产生动作。
- 附带 token 按各页说明处理；`clear`、`quit`、`reload` 忽略多余 token。
- `help <命令>` 显示中文用法；未知命令会从当前可用命令中给出最接近的建议。玩家构建输入 `reload`、`lighting` 等未注册命令也按未知命令处理。

## 输出与复制

每行输出包含时间、级别和分类。可选择文字复制；新日志到来时保留选区。“复制全部”复制当前筛选下可见的输出，“清空”和 `clear` 只清空内存缓冲，不删除磁盘日志。缓冲最多保留 500 条，超出后丢弃最早的条目。

显示筛选提供“全部”“警告和错误”“信息”“调试”，只影响当前显示，不改变磁盘日志或日志接收等级。`loglevel` 控制新日志的接收等级，见[命令说明](loglevel.md)。看不到命令结果时，同时检查显示筛选和 `loglevel`。

控制台自身的用法、建议和错误说明使用中文，其他系统日志保留原文。命令输入错误和普通业务失败只写日志，不弹游戏错误面板；核心故障仍会冻结世界并显示故障提示。

## 实现依据

基础分发器见 [`App/Console/ConsoleCommands.cs`](../../App/Console/ConsoleCommands.cs)，模拟命令与宿主协调见 [`App/SimulationConsoleCommands.cs`](../../App/SimulationConsoleCommands.cs) 与 [`App/HostCoordinator.cs`](../../App/HostCoordinator.cs)，游戏内界面见 [`Ui/GameConsole.cs`](../../Ui/GameConsole.cs)。宿主创建基础分发器，开发构建再通过 [`ConsoleCommandRegistration`](../../App/Console/ConsoleCommandRegistration.cs) 注册开发命令，执行、帮助和补全共用这组注册数据。界面从 `RingBufferLogSink` 读取显示内容。
