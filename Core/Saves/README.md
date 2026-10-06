# 存档文件读写

本模块按存档 UUID 读取和发布完整文本，负责物理文件读写、候选发布、发布失败时的原文件保护，以及候选的尽力清理。

| 类型 | 职责 |
| --- | --- |
| `ISaveStore` | 为文档编解码和恢复流程提供按 UUID 读写文本的边界。 |
| `FileSaveStore` | 在用户数据根目录中读写 UTF-8 文件，通过同目录候选文件发布。 |
| `SaveStoreStatus` | 表示文件读取和发布的五种结果，与文档校验和世界恢复状态独立。 |

本模块依赖 .NET 文件 API，并复用 [Core/Content 的 ExternalPath](../Content/ExternalPath.cs) 完成路径规范化和文本包含检查；不依赖 Godot 或世界模型。程序集依赖方向见 [ADR 0009](../../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)。

路径布局与候选发布对应[存档设计：文件、身份与写入](../../docs/design/09-persistence.md#文件身份与写入)。宿主将 `user://` 转换成物理用户数据目录；文件层沿用 `saves/<UUID>/save.json` 和 `save.json.candidate`。显示名称不参与路径计算。写入候选采用无 BOM 的 UTF-8，刷新后才移动到目标；`TryWrite` 允许覆盖，`TryWriteNew` 拒绝已有目标。

调用方负责捕获完整状态并校验文档。本模块将文本作为不透明数据处理，不解析 JSON、检查格式版本、校验内容依赖或恢复世界，也未实现自动存档、导入导出工作流和新的核心状态格式。读取只使用已发布文件，残留候选不会自动恢复或发布。旧会话暂时将文件状态转换为自己的恢复状态；旧导出流程仍通过[内部访问声明](../AssemblyInfo.cs)调用候选写入辅助方法。

调用方应提供可信根目录，并串行协调同一存档的操作。路径包含检查不验证链接，固定候选名不提供多个写入者之间的协调。候选清理是尽力而为：文件被其他进程占用时可能残留，清理异常不掩盖最初的写入失败。刷新文件数据不等于保证断电后的目录元数据持久性。

[真实文件测试](../../Train.Tests/Core/Saves/FileSaveStoreTests.cs)在独立临时目录中覆盖中文文本和换行的往返、无 BOM 编码、覆盖、缺失目标、读取失败、原文件保护、候选清理，以及拒绝覆盖的独立发布。Windows 独占文件句柄用于触发替换失败；其他平台使用候选路径的目录障碍触发写入失败，目标目录冲突另行覆盖候选发布失败。

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Family headless -Scope saves -BuildGame
```
