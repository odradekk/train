# 应用适配

本模块将 Core 的内容、诊断和控制台服务接入 Godot。`App.tscn` 是项目主场景，包含主菜单；`App` 负责这些服务的生命周期，并分发只读检查。模块依赖 Godot、`Train.Core`、Godot 内容适配器及 `Ui`。

| 类型 | 职责 |
| --- | --- |
| `App` | 管理日志生命周期、加载内容并连接主菜单与 GameConsole；逐帧取出工作线程日志。 |
| `GodotLogOutput` | 用 Core 的共享可读格式，把事件交给 Godot 的普通、警告或错误输出通道。 |
| `Checks/CheckArguments` | 从命令行选择一项自检及其用户目录、只读内容输入。 |
| `Checks/LoggingCheck` | 验证工作线程日志经 App 主线程输出，释放仍由 App 完成。 |
| `Checks/ContentCheck` | 读取两份预先准备好的内容，验证初始加载、重载新增和移除。 |
| `Checks/StartupChecks` | 检查主菜单退出接线和导出版本的内容访问。 |

日志器和 Godot 输出适配器在主线程创建、取出队列并释放。退出按钮、窗口关闭和控制台 quit 共用退出回调；控制台 clear 只清内存缓冲。Debug 构建保留 Ctrl+Shift+R 与 reload 命令的内容重载；发布构建不开放这些交互入口。重载只更新内容目录，本阶段未注册玩法状态的候选校验。

自检使用 `-- --check=<名称>`，在 Debug 和发布构建都可用。名称为 `logging`、`content`、`mods-export`、`content-export`、`main-menu`；成功输出 `APP_CHECK_PASSED <名称>` 后退出 0，失败输出失败标记并以非零退出。检查不保存玩法状态，也不改写用户内容；会话日志写入本次输出目录。

统一[测试入口](../docs/run-tests.md)为每项检查提供隔离用户目录，参数为 `--check-user-data=<物理路径>`；Godot 原生日志另由 `--log-file` 写入检查输出目录。content 还读取 `--check-content-root=<物理路径>` 下的 `initial/` 与 `added/` 两份资源，运行器提前准备文件。检查只切换读取来源；PNG 解码、文件访问和发布仍经过真实 Godot 适配器及同一个重载器。源码检查验证 Debug 快捷键，发布构建通过宿主回调验证重载。

此边界遵循 [ADR 0009](../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)，启动界面按[窗口与界面规则](../docs/design/08-window-and-ui.md#规则)逐步开放；诊断接入见[调试模式](../docs/design/15-design-editor-and-tools.md#调试模式)，会话、队列和格式契约见[日志模块](../Core/Logging/README.md)。
