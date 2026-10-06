# 界面

本目录中保留的 `GameConsole` 是 Godot 控制台界面，负责输入焦点、开关与日志显示。其他旧场景仍处于退役过渡中，新 App 与主菜单由启动迁移步骤接入。

| 类型 | 职责 |
| --- | --- |
| `GameConsole` | 将输入框提交的一行文本交给基础分发器，显示环形缓冲中的日志快照；不持有世界会话或观察器。 |

GameConsole 依赖 Godot 控件、[Core/Console](../Core/Console/README.md) 和 [Core/Logging](../Core/Logging/README.md)。宿主先创建日志缓冲与分发器，通过 `Initialize` 传入，再将控制台加入场景树；宿主负责日志释放及退出、清屏、重载的回调。界面不查找 Main 或 App 节点。

界面职责见[窗口与界面](../docs/design/08-window-and-ui.md#边界)和[设计器与工具：调试命令](../docs/design/15-design-editor-and-tools.md#调试命令)，操作规则见[控制台手册](../docs/console/README.md)。

反引号或波浪号开关面板，打开后 Escape 关闭；这些按键被界面接管，不再交给场景。Enter 通过输入框提交信号执行命令，提交后保持编辑模式，允许连续输入命令。打开面板时延迟取得焦点，并清除可能进入输入框的开关字符。核心模拟暂停不影响控制台输入。

关闭面板不停止日志收集。打开后，日志数量或首尾序号变化会触发重绘，包括环形缓冲覆盖和 clear 清屏；格式化日志会转义富文本标记。解析、分发与持久回显由 [Core 控制台测试](../Train.Tests/Core/Console/ConsoleCommandTests.cs)验证，Godot 控件输入和显示需要真实引擎检查。
