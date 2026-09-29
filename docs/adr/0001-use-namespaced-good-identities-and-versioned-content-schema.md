# Use namespaced voxel identities and strict versioned definition schemas

Revised on 2026-09-05 through [#31](https://github.com/odradekk/train/issues/31). The first slice that needs voxel definitions will replace the root project's separate goods model, catalog, content files and references with `VoxelDefinition` and `VoxelDefinitionId`. It will introduce new definition schemas at version 1, with no legacy goods compatibility layer; `archive/mvp/` remains unchanged. This is an accepted migration decision, not a claim that the code has already migrated.

Built-in and mod definitions retain the `<namespace>:<local-id>` identity shape. Natural resources originally used the `natural_resource` tag instead of a closed goods category; the tag is independent of tradability. Revised on 2026-09-14 by the resource and production design ([10](../design/10-resources-and-extraction.md)): world resource source voxels use `resource_source` with a ResourceSourceBehavior that defines their yields, and tradable primary goods use `raw_good`; `natural_resource` is retired without a compatibility alias. [#77](https://github.com/odradekk/train/issues/77) migrated tradable primary goods and settlement kind sets to `raw_good`. [#78](https://github.com/odradekk/train/issues/78) still covers ResourceSourceBehavior and `resource_source`. `Value` remains on voxel definitions because fracture identity inheritance already needs it, while `TradeMetadata` design and implementation wait for the later trade feature.

## Consequences

Built-in IDs use the `core` namespace, mod namespaces come from their manifest UUID, and their definition files contain only local IDs. Folders remain an authoring convention rather than an identity boundary. Files use explicit `schema_version` and type-specific `.voxels.json`, `.composites.json` and `.layouts.json` suffixes. Unknown fields, unknown schema versions, duplicate definitions and invalid components invalidate the owning content package.

Built-in and mod resources are organized under `voxels`, `composites` and `layouts`. The original player Blueprint library used `user://blueprints/` and the Composite format; the world-editor revision below expands this authoring scope. Save-local definitions remain embedded in saves rather than scanned from a separate content directory. See [the content design](../design/02-definitions-and-content.md) for source roots and [ADR-0005](0005-resolve-current-definitions-and-separate-content-versions.md) for revision hashes and current-definition save bindings.

## 世界编辑器内容创作修订

2026-09-30，[蓝图、布局与内容定义创作决策](https://github.com/odradekk/train/issues/141)已确认将用户复用范围扩展到车辆／建筑蓝图、Layout 和体素定义。已发布用户内容的目标目录为 `user://content-library/`，三类定义仍使用各自带版本的格式；可保存的未发布创作草稿独立放在 `user://authoring-drafts/`，不进入有效内容扫描。只用 Composite 文件的旧蓝图库不足以承载这些类型，不能将它们强塞入原格式。

身份不取决于显示名或文件路径；更新原内容保留身份，跨范围或另存副本建立新身份并处理依赖。存档内发布内容继续内嵌普通存档，用户库不得依赖某个存档内定义。内置、Mod 的命名空间及不允许跨包覆盖的规则不变。旧 `user://blueprints/` 的读取／迁移必须在实施票中明确，保留原文件，不静默改身份或丢内容；此为目标契约，尚未实现迁移。

新建用户内容与独立内容副本由系统基于 UUID 分配稳定身份，保留 `namespace:local-id` 及类型区分，不要求用户填写命名空间，不由显示名或存档文件 UUID 推导。整个世界存档的另存为／导入保留内嵌定义标识和引用，各副本隔离解析；这与显式复制内容产生新身份不同。具体 UUID 组织和编码由实施规格决定，必须明确碰撞检查与隔离范围。
