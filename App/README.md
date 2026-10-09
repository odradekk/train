# 应用适配

本模块将 Core 的内容、模拟、诊断和控制台服务接入 Godot。`App.tscn` 是项目主场景，包含主菜单；`App` 负责这些服务的生命周期，并运行隔离自检。模块依赖 Godot、`Train.Core` 和 `Ui`。

| 类型 | 职责 |
| --- | --- |
| `App` | 管理日志生命周期、加载内容并连接主菜单与 GameConsole；在主线程驱动活动模拟会话、报告完整结果，并逐帧取出工作线程日志。 |
| `SimulationConsoleCommands` | 在 Debug 构建中将 `sim` 调试命令交给同一个 App 模拟入口。 |
| `GodotContentSource` | 通过 Godot 虚拟文件系统、物理文件系统和 PNG 解码实现 Core 的内容读取边界。 |
| `GodotLogOutput` | 用 Core 的共享可读格式，把事件交给 Godot 的普通、警告或错误输出通道。 |
| `Checks/CheckArguments` | 从命令行选择一项自检及其用户目录、只读内容输入。 |
| `Checks/LoggingCheck` | 验证工作线程日志经 App 主线程输出，释放仍由 App 完成。 |
| `Checks/ContentCheck` | 读取两份仅含 grain、市场表、聚居地类型及 smoke-ore 探针的预先准备内容，验证初始加载、重载新增和移除。 |
| `Checks/StartupChecks` | 检查主菜单退出连接和导出版本的内容访问。 |
| `Checks/SimulationCheck` | 用检查专用领域验证实际帧推进、主线程、暂停边界、命令、控制台手动步进和检查点。 |

日志器和 Godot 输出适配器在主线程创建、取出队列并释放。退出按钮、窗口关闭和控制台 quit 共用退出回调；控制台 clear 只清内存缓冲。Debug 构建保留 Ctrl+Shift+R 与 reload 命令的内容重载；发布构建不开放这些交互入口。当前重载原子发布包含体素、蓝图、市场表和聚居地类型的完整 `ContentSnapshot`；运行实例的引用校验与状态适配在对应玩法中实现。

正常启动没有模拟会话。真实世界创建或恢复成功后调用 `AttachSession(session, states)`，App 在 `_Process` 中使用 Core 驱动推进。挂接默认未暂停、1×，新绑定重置宿主余量和采样，保留给定会话的完整状态和命令日志；首帧不累计会话挂接前的时间。`DetachSession()` 移除绑定，已取得的不可变快照仍有效。

模拟入口只允许 Godot 主线程访问。`GetSimulationStatus()` 返回完整核心状态、模拟秒、暂停、目标和实际速度及当前调用的剩余额度；调用结束后的整步额度为零，无会话时返回 null。`SetSimulationPaused`、`SetSimulationSpeed` 无会话时返回 false；`StepSimulation` 要求暂停。`SubmitCommand` 保留输入来源并经过同一个会话，无会话时拒收。`SimulationAdvanced` 和 `SimulationCommandCompleted` 在核心计算结束后报告已提交结果；计算期间禁止重入修改或更换绑定。`CreateSimulationCheckpoint` 返回同一完整边界的权威检查点。

每帧工作预算暂为 5 ms，只在完整步之间检查；预算耗尽后放弃未执行的整步额度，跨帧保留不足一步的余量。游戏时间可以因过载变慢，后续帧不加速补算；已完成状态和事件不丢失。实际速度来自完成的模拟秒与真实耗时，不直接显示目标档位。Debug 控制台提供 [`sim`](../docs/console/sim.md) 的状态、暂停、恢复、倍速、手动步进和检查点命令；无会话时明确提示。

自动推进、手动步进或命令执行新发生内部故障时，App 在收到完整冻结结果后、通知观察者之前记录一次 `simulation_fault` Error。日志保留稳定故障、失败事务尝试位置和原始异常详情，写入本次会话 JSONL；冻结后的调用与恢复的故障不会重复记录历史诊断。

自检使用 `-- --check=<名称>`。`logging`、`content`、`mods-export`、`content-export`、`main-menu` 在 Debug 和发布构建都可用；`simulation-host` 需要开发构建，使用 Debug 控制台及检查专用会话，另外验证三个故障入口只记录一次诊断并写入 JSONL。在发布构建选择它会明确报告“需要开发构建”。成功输出 `APP_CHECK_PASSED <名称>` 后退出 0，失败输出失败标记并以非零退出。检查不保存玩法状态，也不改写用户内容；会话日志写入本次输出目录。

统一[测试入口](../docs/run-tests.md)为每项检查提供隔离用户目录，参数为 `--check-user-data=<物理路径>`；Godot 原生日志另由 `--log-file` 写入检查输出目录。content 还读取 `--check-content-root=<物理路径>` 下的 `initial/` 与 `added/` 两份资源，运行器只复制 grain 定义及其共用的 `voxels/grain/placeholder.png`，并生成 smoke 市场表、聚居地类型和 added 中的 smoke-ore 探针。检查只切换读取来源；PNG 解码、文件访问和发布仍经过 `GodotContentSource` 及同一个重载器。源码检查验证 Debug 快捷键，发布构建通过宿主回调验证重载。

当前 Godot 内容检查的成功标记为 `CONTENT_GRAIN_LOADED`、`CONTENT_EXPORT_GRAIN_LOADED` 和 `MODS_EXPORT_GRAIN_LOADED`；`CONTENT_MARKET_SETTLEMENT_READY` 是 content 检查的必需标记。`*_PNG_DECODED` 同时确认 grain 的 icon、top、side、bottom 四个绑定都指向同一 PNG，且实际解码图片非空。三种货物、七种聚居地类型、四部件和两蓝图的具体内容见 [#236](https://github.com/odradekk/train/issues/236)。

此边界遵循 [ADR 0009](../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)，启动界面按[窗口与界面规则](../docs/design/08-window-and-ui.md#规则)逐步开放；诊断接入见[调试模式](../docs/design/15-design-editor-and-tools.md#调试模式)，会话、队列和格式契约见[日志模块](../Core/Logging/README.md)。
