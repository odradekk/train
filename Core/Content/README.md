# 内容、Mod 与定义

本模块负责发现内容包、校验定义及资产、计算修订并发布一个完整的不可变内容批次。

内容加载按领域格式组织。体素、蓝图、市场表和聚居地类型共用发现、JSON 读取、身份冲突处理、整包隔离和发布流程；各自负责格式校验、表现资产或引用与属性计算。具体货物、市场表、聚居地类型、部件和蓝图内容另见 [#236](https://github.com/odradekk/train/issues/236)，不把具体预置数量写进加载架构。

四种格式通过 `ContentJson` 共用严格 JSON 文档外层：字节上限、重复属性、语法、根对象、允许字段和 schema 版本检查。领域解析器保留自己的数组空性、显示信息和条目规则；任一条目失败则整份文件不返回部分定义。缺失或类型错误的根定义数组使用 `field_invalid` 并标出字段路径，JSON 语法错误使用 `invalid_json`。包拒绝诊断统一按真实所属包的必需性选择级别：必需包为 Error，可选包为 Warning；非失效的扫描告警保留其级别。`ContentHash` 只共用 SHA-256 和规范 JSON 写入机制，各领域决定编码字段、顺序及 writer 选项；复用机制不改变既有修订。

每轮先合并所有领域的有效包；合并使包失效时先重新合并，再由 `ContentLoader` 把本轮体素索引传入蓝图、市场表和聚居地类型的引用检查。格式不在构造时捕获其他格式的可变索引。蓝图候选只保存布局、来源、显示名和语义修订；引用与属性计算通过后才创建带发布指纹和设计属性的 `CompositeContentEntry`。市场与聚居地值对象的内部构造器保护本地数值、身份和集合不变量，解析器保留字段诊断，引用存在性留在合并后的检查中。

本模块依赖 .NET 和[日志模块](../Logging/README.md)，通过 `TrainLogger` 记录扫描与发布诊断。宿主通过 `IContentSource` 提供文件访问和图片解码；[App 模块的 GodotContentSource](../../App/GodotContentSource.cs) 负责 Godot 虚拟文件系统和 PNG 解码。程序集依赖方向见 [ADR 0009](../../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)。

| 类型或目录 | 职责 | 对应设计章节 |
| --- | --- | --- |
| `IContentSource`、`MemoryContentSource` | 发现包和全部 JSON 文件、读取字节及解码 PNG；不解释定义类型。 | [Definition 来源](../../docs/design/02-definitions-and-content.md#definition-来源) |
| `ContentPackageWalk`、`ExternalPath`、`ExternalContent` | 稳定扫描内容包，检查路径包含关系和链接。 | [内容目录与格式](../../docs/design/02-definitions-and-content.md#内容目录与格式) |
| `ModManifest`、`ModDiscovery` | 校验 manifest、版本与命名空间，隔离无效或重复的 Mod。 | [ID 与命名空间](../../docs/design/02-definitions-and-content.md#id-与命名空间)、[Mod 与热加载](../../docs/design/02-definitions-and-content.md#mod-与热加载) |
| `Voxels/`、`Composites/`、`Behaviors/` | 表示体素、蓝图和类型化功能行为，汇总设计属性及 BOM。 | [体素定义](../../docs/design/02-definitions-and-content.md#体素定义)、[设计属性](../../docs/design/03-vehicles-buildings-and-designs.md#设计属性) |
| `Markets/`、`Settlements/` | 表示市场价格参数、聚居地供需上限和生成参数；不保存运行中的市场数量。 | [当前市场模型](../../docs/design/00-product-and-core-loop.md)、[聚居地类型](../../docs/design/16-settlements.md) |
| 各领域的 Parser 和 ContentFormat | 严格解析各领域格式，校验资产、引用及派生数据，并参加共同加载流程。 | [内容目录与格式](../../docs/design/02-definitions-and-content.md#内容目录与格式)、[表现资产校验](../../docs/design/02-definitions-and-content.md#表现资产校验) |
| 各领域的 ContentEntry | 保存已校验定义、修订、资产或派生属性；构造能力保留在内容模块内部。 | [语义哈希字段与规范化](../../docs/design/02-definitions-and-content.md#语义哈希字段与规范化) |
| `ContentIdentity`、`ContentJson`、`ContentHash` | 共用身份规范、严格字段读取和规范 JSON 哈希；领域仍负责自己的结构与数值关系。 | [ID 与命名空间](../../docs/design/02-definitions-and-content.md#id-与命名空间)、[语义哈希字段与规范化](../../docs/design/02-definitions-and-content.md#语义哈希字段与规范化) |
| `ContentSnapshot`、`ContentChange` | 查询同一世代的强类型索引；通知携带一次发布前后的完整快照。 | [三种内容版本](../../docs/design/02-definitions-and-content.md#三种内容版本) |
| `ContentLoader`、`ContentFormat` | 组合当前领域格式，共用加载与身份处理；依据真实包归属迭代隔离失效包，冻结自洽候选。 | [候选内容与运行中引用校验](../../docs/design/02-definitions-and-content.md#候选内容与运行中引用校验) |
| `ContentReloader` | 独占重载与发布权，一次替换完整快照并通知一次；拒绝并发及通知期间的重入。 | [Mod 与热加载](../../docs/design/02-definitions-and-content.md#mod-与热加载) |

身份与版本规则的决策依据见 [ADR 0001](../../docs/adr/0001-use-namespaced-good-identities-and-versioned-content-schema.md) 和 [ADR 0005](../../docs/adr/0005-resolve-current-definitions-and-separate-content-versions.md)。世界生成所需内容和开局地点的校验属于[世界生成流程](../../docs/design/05-world-graph-and-generation.md#世界生成流程)的职责。

体素和蓝图文件只接受 `schema_version: 2`；旧版本明确拒绝，Mod manifest 仍使用 schema 1。蓝图保留 `id`、`name`、`description`、`size_class`、`voxels` 五个必填字段，删除 `archetypes`、`archetype` 和 `contacts`。布局必须非空、坐标唯一、引用候选目录中的体素，朝向须属于引用定义允许的集合。本步不检查六面连通或尺寸包络；五档尺寸声明仍保留。这里记录的是 #222 确立的通用格式边界，具体内容接入见 [#236](https://github.com/odradekk/train/issues/236)。

当前功能行为为 `propulsion`、`control`、`cargo`，每个体素可组合不同种类，每种最多声明一次。动力必须填写有限正数 `traction`（kN）、`rated_speed`（m/s）；控制没有参数；货舱必须填写整数 `capacity`（1 至 Int32.MaxValue 个货物单位）。旧 `track_contact` 和功率参数不再接受。

`ICoreVoxelDefinition` 只暴露身份、Value、质量、耐久上限、标签和功能行为。`DesignAttributeCalculator.Calculate` 通过该接口读取已解析的体素序列，不读取表现、碰撞或坐标：质量、牵引力和载量相加，额定速度取动力部件的最小值，无动力为 null；存在控制行为即具有控制能力。只有无功能行为的体素贡献结构耐久上限。BOM 按身份统计每格消耗，并累加总 Value；组合行为只计一次质量、价值和材料。`DesignAttributes` 和 BOM 不可变，不保存实例状态；汇总溢出拒绝候选。

统一重载在发布前校验引用并重算设计属性，冻结后通过一个原子引用替换发布。调用方捕获一次 `Current`，从同一快照的 `Voxels`、`Blueprints`、`MarketTables` 和 `SettlementTypes` 查询；旧快照保持不可变。任何有效变化只增加一次完整内容世代，无变化保留同一个快照对象且不通知。无效必需包保留整个旧快照；可选包失败则移除该包的全部类型，再重查级联引用。必需性来自 `ContentPackage.IsRequired`，不从命名空间推断。订阅者异常记录日志并继续通知，不回滚已经发布的内容。

蓝图自身的语义修订只包含身份、尺寸档位和布局，不包含依赖体素的修订；设计属性参与发布指纹。只改体素数值会重算蓝图属性，仍保持蓝图语义修订。纯贴图或碰撞变化触发新的内容世代，但不改变蓝图属性及指纹。体素语义编码版本为 3：碰撞属于发布编码，与核心玩法语义分开；蓝图语义编码仍为 2。

内容源统一发现全部小写 `.json`；物理资源通过 `ExternalContent.ListDefinitionFiles` 扫描，虚拟资源通过显式传入 `.json` 的 `ContentPackageWalk.Discover` 扫描。加载器统一执行包准入和后缀分派，继续忽略旧 `*.settlements.json`。玩家蓝图包只读取根级、与包 UUID 同名的 `.composites.json`。新增选定的领域格式时，在加载器中组合格式并在快照中增加强类型索引；包隔离、JSON 读取和发布机制无需按类型复制，也不增加内容源方法。

市场表与聚居地类型使用强类型身份和不可变定义，已注册到 `ContentLoader`。两个格式在同批次体素上校验货物引用，随后与体素、蓝图一起冻结为四个索引。价格区间、供需上限和恢复量均为内容参数，不代表运行中的市场状态。

两个格式各使用 schema 1，声明只填写局部 ID，货物引用支持同包局部 ID 或完整体素身份。文件放在 `resources/markets/*.market-tables.json`、`resources/settlement-types/*.settlement-types.json`。根对象分别只含 `schema_version` 与 `market_tables` / `settlement_types`，数组允许为空；未知字段、重复字段或无效定义使所在包无效。数据契约沿用 #222 的确认记录；本步验证格式和计算，具体预置表与类型由 [#236](https://github.com/odradekk/train/issues/236) 接入。

| 定义 | 字段与含义 |
| --- | --- |
| `MarketTableDefinition` | `id`、非空 `goods`。每种货物唯一，价格规则包含有限非负 `sensitivity` 和正 Int64 `min_price ≤ max_price`，不要求区间包含体素 Value。 |
| `SettlementTypeDefinition` | `id`、`name`、`supply`、`demand`、`preferred_terrain`、`weight`、`population_min`、`population_max`、`initial_platform_count`，全部必填。 |
| 供给与需求项 | `definition`、正 Int32 `capacity`、非负 Int32 `restore_per_tick`。两边分别按货物唯一，允许为空或引用同一种货物；恢复量可为零或大于容量，运行时恢复须截到上限。 |
| 生成参数 | 地貌是规范身份字符串集合，空集合表示不限；尚无地貌定义目录，不检查外部地貌引用。权重为正 Int32；人口为正 Int32 闭区间；初始站台数为非负 Int32。 |

货物引用必须指向同批次已有且不带 `scene_only` 的体素，不要求 `raw_good`。市场表只保存价格参数，不复制货物 Value；全局选表和每 3600 步恢复一次由后续世界/市场配置负责，类型不增加市场表引用。`initial_platform_count` 是临时生成参数；实际建筑接入后站台数量从完好建筑部件派生。具体预置数量不属于本步通用格式实现，见 [#236](https://github.com/odradekk/train/issues/236)。

市场表语义修订包含自身身份和价格参数，其全部有效字段都属于语义，发布指纹直接复用语义修订；类型语义修订包含身份及全部非显示参数，发布指纹另包含名称。引用目标 Value 不进入它们自身的修订或条目副本，但体素更新仍发布整个新快照，消费者捕获一次快照读取新 Value。来源路径与 JSON 排序不改变有效内容。两类定义不添加需要存档的实例状态。

各领域 Parser 返回不可变定义序列或 null，失败写入共享包诊断；ContentFormat 产生候选条目，再按同轮体素索引检查货物引用。它们都不执行 IO 或发布，查询与发布使用 `ContentReloader.Current`、`Reload()`、`Changed`。命名输入及人工预期见 `Train.Tests/Core/Content/MarketSettlementExamples.cs`；`MarketSettlementSchemaTests` 验证字段规则，`MarketSettlementPublicationTests` 验证完整快照、修订、包隔离及引用失败。真实 Godot 内容检查通过物理夹具验证新文件发现、参数重载和旧快照保留，并要求 `CONTENT_MARKET_SETTLEMENT_READY`。内容定义不进存档，设计属性和 BOM 在加载时重算；当前没有新增需要保存的核心实例状态。

当前内置资源目录保留最小的 schema 2 [`core:grain`](../../builtin/core/resources/voxels/grain.voxels.json) 占位定义和 [`voxels/grain/placeholder.png`](../../builtin/core/resources/voxels/grain/placeholder.png)。icon、top、side、bottom 四个绑定共用这张 PNG；其 `.import` 使用 `importer=keep`，保留原始字节供 Godot 内容源和导出版本读取。三种货物、具体市场表、七种类型、四类车辆部件及两种蓝图的设计与实现已拆到 [#236](https://github.com/odradekk/train/issues/236)。Core 测试通过统一公开接口和最小内存包验证字段、引用、属性、失败保留旧快照、无变化不增加世代，以及重载互斥。

当前设计只发布内容；运行实例的引用校验和状态适配按[候选内容与运行中引用校验](../../docs/design/02-definitions-and-content.md#候选内容与运行中引用校验)在对应玩法中实现。

设计属性的手算例子位于 `Train.Tests/Core/Content/DesignAttributeExamples.cs`。统一重载的数据例子及手算预期位于 `Train.Tests/Core/Content/ContentReloadExamples.cs`，对应功能测试为 `ContentPublicationTests`；其他格式及隔离契约由现有内容测试覆盖。图片像素拥有副本，外部输入数组修改不会改变图像，见 `ContentImageTests`。

`ContentLimits` 的字节数、图片尺寸、定义数和诊断数上限沿用当前 schema，尚未根据新的玩法负载重新测量；当前不作为玩法调参数据。

对应测试位于 [Train.Tests/Core/Content](../../Train.Tests/Core/Content)。通过统一入口按类别和范围选择：

```powershell
pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Scope content -BuildGame
pwsh -NoProfile -File scripts/run-tests.ps1 -Correctness -Scope content -Scope composites -Scope voxels -Scope identity
```

第一条选择 content 范围的单元测试并构建游戏；第二条选择该范围的非导出正确性检查，包含真实 Godot 内容检查。C# 测试通过工程引用核验内容规则及物理目录边界；Windows 导出与实际运行验证见[本地验证手册](../../docs/local-environment.md#windows-export-and-runtime-validation)。
