# 内容、Mod 与定义

本模块负责发现内容包、校验定义及资产、计算修订并发布目录快照。

本模块依赖 .NET 和[日志模块](../Logging/README.md)，通过 `TrainLogger` 记录扫描与发布诊断。宿主通过 `IContentSource` 提供文件访问和图片解码；[App 模块的 GodotContentSource](../../App/GodotContentSource.cs) 负责 Godot 虚拟文件系统和 PNG 解码。程序集依赖方向见 [ADR 0009](../../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)。

| 类型或目录 | 职责 | 对应设计章节 |
| --- | --- | --- |
| `IContentSource`、`MemoryContentSource` | 定义内容读取边界，并提供测试用的内存内容源。 | [Definition 来源](../../docs/design/02-definitions-and-content.md#definition-来源) |
| `ContentPackageWalk`、`ExternalPath`、`ExternalContent` | 稳定扫描内容包，检查路径包含关系和链接。 | [内容目录与格式](../../docs/design/02-definitions-and-content.md#内容目录与格式) |
| `ModManifest`、`ModDiscovery` | 校验 manifest、版本与命名空间，隔离无效或重复的 Mod。 | [ID 与命名空间](../../docs/design/02-definitions-and-content.md#id-与命名空间)、[Mod 与热加载](../../docs/design/02-definitions-and-content.md#mod-与热加载) |
| `Voxels/`、`Composites/`、`Behaviors/` | 表示体素、蓝图与 Archetype 定义，校验朝向、局部坐标和 Behavior 参数。 | [体素定义](../../docs/design/02-definitions-and-content.md#体素定义)、[语义哈希字段与规范化](../../docs/design/02-definitions-and-content.md#语义哈希字段与规范化) |
| `VoxelsJsonParser`、`CompositesJsonParser` | 严格解析当前带版本的定义格式。 | [内容目录与格式](../../docs/design/02-definitions-and-content.md#内容目录与格式)、[表现资产校验](../../docs/design/02-definitions-and-content.md#表现资产校验) |
| `VoxelCatalog`、`CompositeCatalog` | 保存目录快照，计算修订与差异，并提供变化通知。 | [三种内容版本](../../docs/design/02-definitions-and-content.md#三种内容版本)、[语义哈希字段与规范化](../../docs/design/02-definitions-and-content.md#语义哈希字段与规范化) |
| `ContentReloader` | 准备候选、检查内容引用，再发布有变化的体素和蓝图目录。 | [Mod 与热加载](../../docs/design/02-definitions-and-content.md#mod-与热加载)、[候选内容与运行中引用校验](../../docs/design/02-definitions-and-content.md#候选内容与运行中引用校验) |

身份与版本规则的决策依据见 [ADR 0001](../../docs/adr/0001-use-namespaced-good-identities-and-versioned-content-schema.md) 和 [ADR 0005](../../docs/adr/0005-resolve-current-definitions-and-separate-content-versions.md)。世界生成所需内容和开局地点的校验属于[世界生成流程](../../docs/design/05-world-graph-and-generation.md#世界生成流程)的职责。

当前实现保留既有体素和蓝图 schema，包括 contacts、Archetype 与现有 Behavior 参数；不扫描旧 `*.settlements.json`。修订编码仍将碰撞几何计入体素语义指纹，与上述语义哈希设计存在差异。

当前内置内容只有 [`core:ferrous-ore`](../../builtin/core/resources/voxels/ferrous-ore.voxels.json) 定义及其 `icon`、`top`、`side`、`bottom` 四张 PNG。各图片的 `.import` 使用 `importer=keep`，保留原始字节供 Godot 内容源和导出版本读取。Core 测试通过公开接口和最小内存内容包验证体素、蓝图与 Archetype 的解析、引用和发布契约，包括 Behavior 默认参数的语义等价、拒绝时保留原目录、无变化时保持代数和通知不变，以及重载互斥。

当前重载器只发布内容目录；运行实例的引用校验和状态适配按[候选内容与运行中引用校验](../../docs/design/02-definitions-and-content.md#候选内容与运行中引用校验)在对应玩法中实现。

`ContentLimits` 的字节数、图片尺寸、定义数和诊断数上限沿用当前 schema，尚未根据新的玩法负载重新测量；当前不作为玩法调参数据。

对应测试位于 [Train.Tests/Core/Content](../../Train.Tests/Core/Content)。通过统一入口按类别和范围选择：

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Scope content -BuildGame
pwsh -NoProfile -File scripts/run-tests.ps1 -Correctness -Scope content
```

第一条选择 content 范围的单元测试并构建游戏；第二条选择该范围的非导出正确性检查，包含真实 Godot 内容检查。C# 测试通过工程引用核验内容规则及物理目录边界；Windows 导出与实际运行验证见[本地验证手册](../../docs/local-environment.md#windows-export-and-runtime-validation)。
