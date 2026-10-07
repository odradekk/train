# 内容、Mod 与定义

本模块负责发现内容包、校验定义及资产、计算修订并发布目录快照。

本模块依赖 .NET 和[日志模块](../Logging/README.md)，通过 `TrainLogger` 记录扫描与发布诊断。宿主通过 `IContentSource` 提供文件访问和图片解码；根项目的 [GodotContentSource](../../Content/Godot/GodotContentSource.cs) 负责 Godot 虚拟文件系统和 PNG 解码。程序集依赖方向见 [ADR 0009](../../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)。

| 类型或目录 | 职责 | 对应设计章节 |
| --- | --- | --- |
| `IContentSource`、`MemoryContentSource` | 定义内容读取边界，并提供测试用的内存内容源。 | [Definition 来源](../../docs/design/02-definitions-and-content.md#definition-来源) |
| `ContentPackageWalk`、`ExternalPath`、`ExternalContent` | 稳定扫描内容包，检查路径包含关系和链接。 | [内容目录与格式](../../docs/design/02-definitions-and-content.md#内容目录与格式) |
| `ModManifest`、`ModDiscovery` | 校验 manifest、版本与命名空间，隔离无效或重复的 Mod。 | [ID 与命名空间](../../docs/design/02-definitions-and-content.md#id-与命名空间)、[Mod 与热加载](../../docs/design/02-definitions-and-content.md#mod-与热加载) |
| `Voxels/`、`Composites/`、`Behaviors/` | 表示体素、蓝图与 Archetype 定义，校验朝向、局部坐标和 Behavior 参数。 | [体素定义](../../docs/design/02-definitions-and-content.md#体素定义)、[语义哈希字段与规范化](../../docs/design/02-definitions-and-content.md#语义哈希字段与规范化) |
| `VoxelsJsonParser`、`CompositesJsonParser` | 严格解析当前带版本的定义格式。 | [内容目录与格式](../../docs/design/02-definitions-and-content.md#内容目录与格式)、[表现资产校验](../../docs/design/02-definitions-and-content.md#表现资产校验) |
| `VoxelCatalog`、`CompositeCatalog` | 保存目录快照，计算修订与差异，并提供变化通知。 | [三种内容版本](../../docs/design/02-definitions-and-content.md#三种内容版本)、[语义哈希字段与规范化](../../docs/design/02-definitions-and-content.md#语义哈希字段与规范化) |
| `ContentReloader`、`IContentReloadParticipant` | 准备候选、检查引用、协调宿主更新计划，再发布有变化的目录。 | [候选内容与运行中引用校验](../../docs/design/02-definitions-and-content.md#候选内容与运行中引用校验)、[内容更新与实例状态](../../docs/design/02-definitions-and-content.md#内容更新与实例状态) |

身份与版本规则的决策依据见 [ADR 0001](../../docs/adr/0001-use-namespaced-good-identities-and-versioned-content-schema.md) 和 [ADR 0005](../../docs/adr/0005-resolve-current-definitions-and-separate-content-versions.md)。世界生成所需内容和开局地点的校验属于[世界生成流程](../../docs/design/05-world-graph-and-generation.md#世界生成流程)的职责。

当前实现保留既有体素和蓝图 schema，包括 contacts、Archetype 与现有 Behavior 参数；不扫描旧 `*.settlements.json`。修订编码仍将碰撞几何计入体素语义指纹，与上述语义哈希设计存在差异。

当前内置内容只有 [`core:ferrous-ore`](../../builtin/core/resources/voxels/ferrous-ore.voxels.json) 定义及其 `icon`、`top`、`side`、`bottom` 四张 PNG。各图片的 `.import` 使用 `importer=keep`，保留原始字节供 Godot 内容源和导出版本读取。Core 测试使用最小内存内容包验证体素、蓝图与 Archetype 的解析、引用和发布契约。

测试使用目录内部发布与修订编码验证契约，因此[程序集内部访问声明](../AssemblyInfo.cs)允许测试程序集访问这些成员；应用通过公开接口加载和重载内容。

`ContentLimits` 的字节数、图片尺寸、定义数和诊断数上限沿用当前 schema，尚未根据新的玩法负载重新测量；当前不作为玩法调参数据。

对应测试位于 [Train.Tests/Core/Content](../../Train.Tests/Core/Content)。通过统一入口按类别和范围选择：

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Scope content -BuildGame
pwsh -NoProfile -File scripts/run-tests.ps1 -Family headless -Scope content
```

这些 C# 测试验证内容规则与实际物理目录边界；Godot 虚拟文件系统、PNG 解码及导出仍需相应真实 Godot 检查。
