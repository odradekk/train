# 日志

本模块提供结构化诊断日志事件、会话级阈值过滤、日志保存和共享的可读格式。代码使用 .NET 库，编入引用 Godot SDK 的宿主项目；[GodotLogOutput](../../App/GodotLogOutput.cs) 实现宿主输出接口，将事件交给 Godot。

| 类型 | 职责 |
| --- | --- |
| `LogEvent`、`LogExceptionData`、`LogLevel` | 表示事件数据、捕获的异常信息和日志级别。直接构造事件或异常数据时不校验，也不复制传入的数据。 |
| `TrainLogger` | 校验输入并制作快照，分配会话序号，统一过滤正常输出通道，隔离输出故障，并为工作线程的宿主输出排队。 |
| `ILogSink`、`IGodotLogOutput` | 定义结构化日志接收器和由宿主管理的输出接口。 |
| `JsonlLogSink` | 为每个事件写入并刷新一条 UTF-8 JSONL 记录，尽力按保留策略清理旧会话文件。 |
| `MemoryLogSink`、`RingBufferLogSink` | 保存事件引用，用于检查或容量有限的控制台历史；快照只复制集合，不复制事件对象。 |
| `LogLevelText` | 统一解析日志级别、生成可读格式和控制台 BBCode，并转义文本中的字面方括号。 |

应在宿主输出线程创建 `TrainLogger`，并在同一线程调用 `DrainGodotQueue()` 和 `Dispose()`。持久输出是同步的，与序号分配在同一锁内按顺序进行。队列不等待空位，但文件或备用写入器的 I/O 仍可能阻塞。队列溢出只影响宿主输出副本。日志器负责释放自身的 JSONL 接收器，传入的输出对象仍由调用方管理。

本模块为[调试模式](../../docs/design/15-design-editor-and-tools.md#调试模式)中的诊断工具提供支持，包括现有的 [loglevel](../../docs/console/loglevel.md) 和 [clear](../../docs/console/clear.md) 命令。模块归属遵循[四模块契约](../../docs/contracts/README.md)；设计中的录制和重放等工具尚未由本模块实现。

## 限制

以下数值用于诊断日志契约或限制递归处理，不用于调整玩法。当前实现沿用规格中确定的数值；实现中的递归保护值尚无测量或设计依据来说明最优取值。

| 设置 | 当前含义和来源 |
| --- | --- |
| 类别／事件标识：64／100 | 最多允许的 ASCII 字符数。标识符以小写字母开头，由小写字母、数字和下划线组成；由[结构化日志规格](https://github.com/odradekk/train/issues/18)确定。 |
| 内部异常：8 | 除最外层异常以外，最多捕获的内部异常链条目数；由[结构化日志规格](https://github.com/odradekk/train/issues/18)确定。直接构造异常数据时不强制此上限。 |
| 工作线程输出队列：4096 | 等待宿主输出的事件引用数上限；由[结构化日志规格](https://github.com/odradekk/train/issues/18)确定。 |
| 会话文件：10 | 匹配日志文件的尽力保留目标；由[结构化日志规格](https://github.com/odradekk/train/issues/18)确定。 |
| 控制台历史：500 | 保留的事件引用数上限；由[控制台规格](https://github.com/odradekk/train/issues/21)确定。 |
| 属性遍历：16 | `LogAttributes` 现有的递归保护值，用于限制属性规范化的递归处理，尚无独立的设计或测量依据。它按递归深度检查，不限制属性总数。 |
| 可读消息：200 | 从消息中保留的 UTF-16 代码单元数上限；沿用[可读格式规格](https://github.com/odradekk/train/issues/23)。不限制整行或保存的消息长度，不添加截断标记，也不替换换行符。 |
| 可读级别宽度：5 | 用于显示对齐的固定宽度；由[可读格式规格](https://github.com/odradekk/train/issues/23)确定。 |
| 控制台颜色 | [可读格式规格](https://github.com/odradekk/train/issues/23)确定的固定诊断样式，属于默认显示设置，不是可配置的玩法数据。 |

## 固定标识符

当前实现保持以下字符串不变，供现有环境设置、事件使用方和文件清理共用。规格没有明确规定具体拼写的值属于现有实现约定，未找到针对其拼写的独立设计、测量或决策记录。

| 标识符 | 含义和来源 |
| --- | --- |
| `TRAIN_LOG_LEVEL`、`application_ready` | 启动阈值覆盖和可检索的就绪标记，由[结构化日志规格](https://github.com/odradekk/train/issues/18)明确命名。 |
| `application`、`logging`、`godot_events_dropped`、`sink_faulted` | [TrainLogger](TrainLogger.cs) 定义的应用生命周期类别、日志器类别和故障标记，由结构化日志测试检查，用于规格要求的生命周期记录、溢出报告和故障隔离。 |
| `train-session-`、`train-session-*.jsonl` | [JsonlLogSink](LogSinks.cs) 使用的文件前缀和搜索模式，用于在保留清理时识别本实现的会话文件；由 `Session_files_retain_ten_matches_and_leave_unrelated_files` 覆盖。 |
| `command_echo` | [LogLevelText](LogEvent.cs) 用于识别命令回显的共享事件标识，按[可读格式规格](https://github.com/odradekk/train/issues/23)显示为青色；由 `Command_echo_uses_the_cyan_message_color` 覆盖。 |

## 示例与验证

XML 文档中的具体示例使用[可读格式测试](../../Tests/Godot/Logging/HumanLogFormatTests.cs)、[环形缓冲测试](../../Tests/Godot/Logging/RingBufferLogSinkTests.cs)和[结构化日志测试](../../Tests/Godot/Logging/StructuredLoggingTests.cs)中的输入。结构化日志测试还覆盖阈值修改、属性复制、异常捕获、队列优先级、并发取出队列和关闭。

在仓库根目录运行 `pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Scope logging -BuildGame` 可执行日志相关检查。宿主项目未开启 XML 文档生成；日志注释由代码审查核对。
