# Use namespaced voxel identities and strict versioned definition schemas

Revised on 2026-09-05 through [#31](https://github.com/odradekk/train/issues/31). [#43](https://github.com/odradekk/train/issues/43) replaced the root project's separate goods model, catalog, content files and references with `VoxelDefinition` and `VoxelDefinitionId`. It introduced new definition schemas at version 1, with no legacy goods compatibility layer; `archive/mvp/` remains unchanged.

Built-in and mod definitions retain the `<namespace>:<local-id>` identity shape. Natural resources originally used the `natural_resource` tag instead of a closed goods category; the tag is independent of tradability. Revised on 2026-09-14 by the resource and production design ([10](../design/10-resources-and-extraction.md)): world resource source voxels use `resource_source` with a ResourceSourceBehavior that defines their yields, and tradable primary goods use `raw_good`; `natural_resource` is retired without a compatibility alias. [#77](https://github.com/odradekk/train/issues/77) migrated tradable primary goods and settlement kind sets to `raw_good`. [#78](https://github.com/odradekk/train/issues/78) covers ResourceSourceBehavior and `resource_source`; [#145](https://github.com/odradekk/train/issues/145) deferred it out of the current stage. `Value` remains on voxel definitions because fracture identity inheritance already needs it, while `TradeMetadata` design and implementation wait for the later trade feature.

## Consequences

Built-in IDs use the `core` namespace, mod namespaces come from their manifest UUID, and their definition files contain only local IDs. Folders remain an authoring convention rather than an identity boundary. Files use explicit `schema_version` and type-specific `.voxels.json`, `.composites.json` and `.layouts.json` suffixes. Unknown fields, unknown schema versions, duplicate definitions and invalid components invalidate the owning content package.

Built-in and mod resources are organized under `voxels`, `composites` and `layouts`. The `layouts` directory and `.layouts.json` format are the target and are not implemented yet. 旧过渡格式 `settlements/*.settlements.json` 已退役，内置与 Mod 内容加载不再创建聚居地实例，也不要求首次目录包含聚居地。聚居地类型和布局由 [16](../design/16-settlements.md) 定义，世界生成负责校验所需内容与开局地点，见 [05](../design/05-world-graph-and-generation.md)。 The original player Blueprint library used `user://blueprints/` and the Composite format; the world-editor revision below expands this authoring scope. Save-local definitions remain embedded in saves rather than scanned from a separate content directory. See [the content design](../design/02-definitions-and-content.md) for source roots and [ADR-0005](0005-resolve-current-definitions-and-separate-content-versions.md) for revision hashes and current-definition save bindings.

## 世界编辑器内容创作修订

2026-09-30，[蓝图、布局与内容定义创作决策](https://github.com/odradekk/train/issues/141)已确认将用户复用范围扩展到车辆／建筑蓝图、Layout 和体素定义。已发布用户内容的目标目录为 `user://content-library/`，三类定义仍使用各自带版本的格式；可保存的未发布创作草稿独立放在 `user://authoring-drafts/`，不进入有效内容扫描。只用 Composite 文件的旧蓝图库不足以承载这些类型，不能将它们强塞入原格式。

身份不取决于显示名或文件路径；更新原内容保留身份，跨范围或另存副本建立新身份并处理依赖。存档内发布内容继续内嵌普通存档，用户库不得依赖某个存档内定义。内置、Mod 的命名空间及不允许跨包覆盖的规则不变。旧 `user://blueprints/` 的读取／迁移必须在实施票中明确，保留原文件，不静默改身份或丢内容；此为目标契约，尚未实现迁移。

新建用户内容与独立内容副本由系统基于 UUID 分配稳定身份，保留 `namespace:local-id` 及类型区分，不要求用户填写命名空间，不由显示名或存档文件 UUID 推导。整个世界存档的另存为／导入保留内嵌定义标识和引用，各副本隔离解析；这与显式复制内容产生新身份不同。具体 UUID 组织和编码由实施规格决定，必须明确碰撞检查与隔离范围。

## 2026-10-05 修订

[货物与内容定义是否继续绑定体素定义](https://github.com/odradekk/train/issues/204)确认：在核心模拟层不涉及体素之后，每种货物仍是一个体素定义，因为游戏中所有物品的外观和图标都用体素块表现。字段按层划分：核心模拟层只读取身份、Value、Mass、Tags、耐久上限和 Behaviors；允许朝向、表现和碰撞只供体素引擎层与渲染层使用，允许朝向和碰撞有默认值。带 `scene_only` 标签的场景体素不是货物。断裂身份继承已随断裂规则删除，Value 只作为基本货币价值。Composite 在文档中改称蓝图；文件后缀和 schema 版本在代码重构时确定。命名空间、身份和严格校验的规则不变。[资源与开采改为节点属性](https://github.com/odradekk/train/issues/205)同日删除了 `resource_source` 标签和 ResourceSourceBehavior：节点资源直接引用初级货物的体素定义，`raw_good` 保留。

2026-10-09，[复合体嵌套：大型设备装到车辆和建筑上](https://github.com/odradekk/train/issues/249)修订：能入库的复合体（例如大型设备）也是货物，库存按去掉涂装的设计计数，见 [ADR-0013](0013-nest-composites-by-copying-designs-and-treat-storable-composites-as-goods.md)。体素定义的命名空间身份规则不变。

2026-10-09，[内容定义标准的整体一致性检查](https://github.com/odradekk/train/issues/267)修订：同日 [ADR-0011](0011-move-the-content-layer-into-its-own-project-and-split-definitions-by-reader.md) 取消 `scene_only` 标签，没有 `simulation` 一节的体素定义就是场景体素。[ADR-0015](0015-declare-package-dependencies-and-keep-missing-content-as-placeholders.md) 改为一个文件一份定义（`<local id>.<种类>.json`，目录为 `voxels/`、`blueprints/`、`settlement-types/`、`settlement-templates/`、`markets/`），取代上文的后缀和目录；Layout 由聚居地模板取代。

## 编辑器去向修订

2026-10-05，[编辑器的去向](https://github.com/odradekk/train/issues/211)确认用户内容库只保存玩家发布的蓝图；体素定义、场景模型、布局等其他内容由开发构建中的创作模式写入数据包（`core` 或 Mod 包）。存档内定义与跨范围复制、依赖闭包的规则随之取消。用户蓝图仍由系统基于 UUID 分配身份，“复制为新蓝图”建立新身份。见 [15](../design/15-design-editor-and-tools.md)。
