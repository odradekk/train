# 内容、Mod 与定义

本模块负责发现内容包、校验定义及资产、计算修订并发布一个完整的不可变内容批次。

内容加载按领域格式组织。体素和蓝图共用发现、JSON 读取、身份冲突处理、整包隔离和发布流程；各自负责格式校验、表现资产或引用与属性计算。切片的预置身份、数量和目标值不进入加载架构，相关决定见 [#222 的重设计记录](https://github.com/odradekk/train/issues/222#issuecomment-6054190677)。

本模块依赖 .NET 和[日志模块](../Logging/README.md)，通过 `TrainLogger` 记录扫描与发布诊断。宿主通过 `IContentSource` 提供文件访问和图片解码；[App 模块的 GodotContentSource](../../App/GodotContentSource.cs) 负责 Godot 虚拟文件系统和 PNG 解码。程序集依赖方向见 [ADR 0009](../../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)。

| 类型或目录 | 职责 | 对应设计章节 |
| --- | --- | --- |
| `IContentSource`、`MemoryContentSource` | 发现包和全部 JSON 文件、读取字节及解码 PNG；不解释定义类型。 | [Definition 来源](../../docs/design/02-definitions-and-content.md#definition-来源) |
| `ContentPackageWalk`、`ExternalPath`、`ExternalContent` | 稳定扫描内容包，检查路径包含关系和链接。 | [内容目录与格式](../../docs/design/02-definitions-and-content.md#内容目录与格式) |
| `ModManifest`、`ModDiscovery` | 校验 manifest、版本与命名空间，隔离无效或重复的 Mod。 | [ID 与命名空间](../../docs/design/02-definitions-and-content.md#id-与命名空间)、[Mod 与热加载](../../docs/design/02-definitions-and-content.md#mod-与热加载) |
| `Voxels/`、`Composites/`、`Behaviors/` | 表示体素、蓝图和类型化功能行为，汇总设计属性及 BOM。 | [体素定义](../../docs/design/02-definitions-and-content.md#体素定义)、[设计属性](../../docs/design/03-vehicles-buildings-and-designs.md#设计属性) |
| `Voxels/`、`Composites/` 中的 Parser 和 ContentFormat | 严格解析各领域格式，校验资产、引用及派生数据，并参加共同加载流程。 | [内容目录与格式](../../docs/design/02-definitions-and-content.md#内容目录与格式)、[表现资产校验](../../docs/design/02-definitions-and-content.md#表现资产校验) |
| `VoxelContentEntry`、`CompositeContentEntry` | 在各自领域保存已校验定义、修订、资产或派生属性；构造能力保留在内容模块内部。 | [语义哈希字段与规范化](../../docs/design/02-definitions-and-content.md#语义哈希字段与规范化) |
| `ContentSnapshot`、`ContentChange` | 查询同一世代的强类型索引；通知携带一次发布前后的完整快照。 | [三种内容版本](../../docs/design/02-definitions-and-content.md#三种内容版本) |
| `ContentLoader`、`ContentFormat` | 组合当前领域格式，共用加载与身份处理；依据真实包归属迭代隔离失效包，冻结自洽候选。 | [候选内容与运行中引用校验](../../docs/design/02-definitions-and-content.md#候选内容与运行中引用校验) |
| `ContentReloader` | 独占重载与发布权，一次替换完整快照并通知一次；拒绝并发及通知期间的重入。 | [Mod 与热加载](../../docs/design/02-definitions-and-content.md#mod-与热加载) |

身份与版本规则的决策依据见 [ADR 0001](../../docs/adr/0001-use-namespaced-good-identities-and-versioned-content-schema.md) 和 [ADR 0005](../../docs/adr/0005-resolve-current-definitions-and-separate-content-versions.md)。世界生成所需内容和开局地点的校验属于[世界生成流程](../../docs/design/05-world-graph-and-generation.md#世界生成流程)的职责。

体素和蓝图文件只接受 `schema_version: 2`；旧版本明确拒绝，Mod manifest 仍使用 schema 1。蓝图保留 `id`、`name`、`description`、`size_class`、`voxels` 五个必填字段，删除 `archetypes`、`archetype` 和 `contacts`。布局必须非空、坐标唯一、引用候选目录中的体素，朝向须属于引用定义允许的集合。本步不检查六面连通或尺寸包络；五档尺寸声明仍保留。上述首个切片范围见 [#222](https://github.com/odradekk/train/issues/222)。

当前功能行为为 `propulsion`、`control`、`cargo`，每个体素可组合不同种类，每种最多声明一次。动力必须填写有限正数 `traction`（kN）、`rated_speed`（m/s）；控制没有参数；货舱必须填写整数 `capacity`（1 至 Int32.MaxValue 个货物单位）。旧 `track_contact` 和功率参数不再接受。

`ICoreVoxelDefinition` 只暴露身份、Value、质量、耐久上限、标签和功能行为。`DesignAttributeCalculator.Calculate` 通过该接口读取已解析的体素序列，不读取表现、碰撞或坐标：质量、牵引力和载量相加，额定速度取动力部件的最小值，无动力为 null；存在控制行为即具有控制能力。只有无功能行为的体素贡献结构耐久上限。BOM 按身份统计每格消耗，并累加总 Value；组合行为只计一次质量、价值和材料。`DesignAttributes` 和 BOM 不可变，不保存实例状态；汇总溢出拒绝候选。

统一重载在发布前校验引用并重算设计属性，冻结后通过一个原子引用替换发布。调用方捕获一次 `Current`，从同一快照的 `Voxels` 和 `Blueprints` 查询；旧快照保持不可变。任何有效变化只增加一次完整内容世代，无变化保留同一个快照对象且不通知。无效必需包保留整个旧快照；可选包失败则移除该包的全部类型，再重查级联引用。必需性来自 `ContentPackage.IsRequired`，不从命名空间推断。订阅者异常记录日志并继续通知，不回滚已经发布的内容。

蓝图自身的语义修订只包含身份、尺寸档位和布局，不包含依赖体素的修订；设计属性参与发布指纹。只改体素数值会重算蓝图属性，仍保持蓝图语义修订。纯贴图或碰撞变化触发新的内容世代，但不改变蓝图属性及指纹。体素语义编码版本为 3：碰撞属于发布编码，与核心玩法语义分开；蓝图语义编码仍为 2。

内容源统一发现 JSON，领域格式决定文件准入，继续忽略旧 `*.settlements.json`。玩家蓝图包只接受根级、与包 UUID 同名的 `.composites.json`，不接受体素声明。新增选定的领域格式时，在加载器中组合格式并在快照中增加强类型索引；包隔离、JSON 读取和发布机制无需按类型复制，也不增加内容源方法。

已实现并提交的内置内容仍是 schema 2 的 [`core:ferrous-ore`](../../builtin/core/resources/voxels/ferrous-ore.voxels.json) 及其四张 PNG；三种货物、车辆体素和预置蓝图在 #222 的后续步骤接入。各图片的 `.import` 使用 `importer=keep`，保留原始字节供 Godot 内容源和导出版本读取。Core 测试通过统一公开接口和最小内存包验证字段、引用、属性、失败保留旧快照、无变化不增加世代，以及重载互斥。

当前设计只发布内容；运行实例的引用校验和状态适配按[候选内容与运行中引用校验](../../docs/design/02-definitions-and-content.md#候选内容与运行中引用校验)在对应玩法中实现。

设计属性的手算例子位于 `Train.Tests/Core/Content/DesignAttributeExamples.cs`。统一重载的数据例子及手算预期位于 `Train.Tests/Core/Content/ContentReloadExamples.cs`，对应功能测试为 `ContentPublicationTests`；其他格式及隔离契约由现有内容测试覆盖。图片像素拥有副本，外部输入数组修改不会改变图像，见 `ContentImageTests`。

`ContentLimits` 的字节数、图片尺寸、定义数和诊断数上限沿用当前 schema，尚未根据新的玩法负载重新测量；当前不作为玩法调参数据。

对应测试位于 [Train.Tests/Core/Content](../../Train.Tests/Core/Content)。通过统一入口按类别和范围选择：

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Scope content -BuildGame
pwsh -NoProfile -File scripts/run-tests.ps1 -Correctness -Scope content -Scope composites -Scope voxels -Scope identity
```

第一条选择 content 范围的单元测试并构建游戏；第二条选择该范围的非导出正确性检查，包含真实 Godot 内容检查。C# 测试通过工程引用核验内容规则及物理目录边界；Windows 导出与实际运行验证见[本地验证手册](../../docs/local-environment.md#windows-export-and-runtime-validation)。
