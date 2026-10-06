# 界面

本目录提供主菜单与 GameConsole。当前主菜单只显示“退出”；GameConsole 负责输入焦点、开关与日志显示。其他旧场景仍待清理，当前启动入口不再进入这些场景。

| 类型 | 职责 |
| --- | --- |
| `MainMenu` | 显示退出按钮，通过 QuitRequested 事件将动作交给 App，不查询宿主节点。 |
| `GameConsole` | 将输入框提交的一行文本交给基础分发器，显示环形缓冲中的日志快照；不持有世界会话或观察器。 |

GameConsole 依赖 Godot 控件、[Core/Console](../Core/Console/README.md) 和 [Core/Logging](../Core/Logging/README.md)。宿主先创建日志缓冲与分发器，通过 `Initialize` 传入，再将控制台加入场景树；宿主负责日志释放及退出、清屏、重载的回调。界面不查找 Main 或 App 节点。

`App/App.tscn` 实例化 `MainMenu.tscn`，宿主在就绪时连接退出事件。菜单取得退出按钮的键盘焦点；按下按钮、控制台 quit 和窗口关闭都由 App 处理。真实主菜单检查确认只显示一个可用按钮，并触发该按钮来观察宿主收到退出请求。

界面职责见[窗口与界面](../docs/design/08-window-and-ui.md#边界)和[设计器与工具：调试命令](../docs/design/15-design-editor-and-tools.md#调试命令)，操作规则见[控制台手册](../docs/console/README.md)。

反引号或波浪号开关面板，打开后 Escape 关闭；这些按键被界面接管，不再交给场景。Enter 通过输入框提交信号执行命令，提交后保持编辑模式，允许连续输入命令。打开面板时延迟取得焦点，并清除可能进入输入框的开关字符。核心模拟暂停不影响控制台输入。

关闭面板不停止日志收集。打开后，日志数量或首尾序号变化会触发重绘，包括环形缓冲覆盖和 clear 清屏；格式化日志会转义富文本标记。解析、分发与持久回显由 [Core 控制台测试](../Train.Tests/Core/Console/ConsoleCommandTests.cs)验证，Godot 控件输入和显示需要真实引擎检查。
