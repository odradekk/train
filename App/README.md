# 应用适配

本模块将库中的服务接入 Godot 运行环境。当前的 `GodotLogOutput` 实现 `Train.Core.Logging.IGodotLogOutput`，使用 Core 的共享可读格式，将事件交给 Godot 的普通、警告或错误输出通道。模块依赖 Godot 和 `Train.Core`。

现有根目录中的 `Main` 管理日志器生命周期，并取出工作线程的输出队列。适配器必须在 Godot 主线程调用，本身不调度或缓冲调用。

此边界遵循 [ADR 0009](../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)，并为[调试模式](../docs/design/15-design-editor-and-tools.md#调试模式)提供诊断输出。会话、队列和格式契约见[日志模块](../Core/Logging/README.md)。
