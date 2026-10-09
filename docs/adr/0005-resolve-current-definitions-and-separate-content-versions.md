# Resolve current definitions and separate semantic, asset and catalog versions

Accepted through [#35](https://github.com/odradekk/train/issues/35). Loading a save and hot reloading resolve external definitions by `DefinitionId` against the currently installed content. A revision identifies the specification that was used; it does not pin an installed engine to obsolete mod values. This replaces #31's retained-old-revision rule because users expect mod balance changes to apply to existing vehicles.

Definitions remain immutable snapshots. A successful content publication replaces resolved specifications while preserving instance identities, actual vehicle layouts and runtime state. Preserve absolute state values and clamp values that exceed a new upper bound: durability 80 stays 80 when the maximum rises from 100 to 150, and becomes 60 when the maximum falls to 60. Recompute affected capabilities. Missing required definitions or incompatible state interpretation fail loading, or reject the entire hot reload with the previous definitions and world intact. There is no automatic replacement, state deletion or fallback to old external definitions.

Semantic Revision is SHA-256 over canonical, validated gameplay data, including the definition kind, ID and encoding version. References include target IDs, not target revisions: changing an engine's power does not change a saved vehicle layout's own revision. Display text, presentation configuration and asset files are excluded. Asset Revision is SHA-256 over one asset file's bytes. Catalog Generation is a runtime publication counter that advances only when effective content changes, including display metadata and asset bindings; it is not a persistent identity.

Saves embed player-defined content and its required save-local definitions, while external references store IDs and last-used revisions for change detection, not external definition copies. Saves reside at `user://saves/<save-uuid>/save.json`; the original Blueprint files used `user://blueprints/<blueprint-uuid>.composites.json`, superseded as the authoring target by the revision below. Display names live in the files, so renaming does not change identity. See [02](../design/02-definitions-and-content.md) for hash inputs and [09](../design/09-persistence.md) for loading rules. Deterministic save/resume equivalence assumes the same effective semantic content; installing changed mod values intentionally changes simulation inputs.

## 世界编辑器内容创作修订

2026-09-30，[内容创作决策](https://github.com/odradekk/train/issues/141)确认以 `user://content-library/` 容纳已发布的蓝图、Layout 和体素定义，以 `user://authoring-drafts/` 单独保存未发布草稿；旧蓝图库的读取／迁移保留原文件与身份，具体安排须进入实施票。存档内三类已发布内容及必要依赖继续内嵌普通存档，草稿不混入有效内容。

显式复制内容时分配新身份，必要的存档内依赖整批复制并改写副本引用；来源及其既有引用保持不变。用户主动制作的副本不等于普通保存或恢复自动嵌入旧外部定义：未复制的内置、Mod 及用户库依赖仍按原 ID 解析当前内容，缺失仍失败，不建立旧定义兜底。

内容身份与存档文件身份分离。普通世界存档另存为／导入保持内嵌定义标识及引用，按各存档隔离解析，不因新存档 UUID 而重编内容或将另一副本的定义发布到当前世界；新建或独立复制内容的身份分配规则见 ADR-0001。

不可变快照、语义／资产版本分离、当前外部定义适配、保留实例身份与实际状态等原则不变。模板更新不重建已有布局或建筑修复目标；发布须展示影响并在完整边界重新校验。存档内发布标记世界未保存；用户库持久写入和当前会话必要适配按一次发布处理，正常失败保留原内容与世界。此修订描述目标行为，不宣称当前保存格式及加载器已经支持全部创作能力。

## 核心存档修订

2026-10-05，[存档：核心状态的保存、恢复与兼容](https://github.com/odradekk/train/issues/209)确认存档完整保存核心状态，不再依赖生成基底。存档内嵌的范围随之扩大：存档中引用到的蓝图版本全部内嵌，不论来自内置、Mod 还是用户库；已有聚居地使用的布局也内嵌。蓝图版本只固定形状和对体素定义的引用，体素定义等其他外部内容仍按 ID 解析为当前定义，所以 Mod 的平衡更新照常作用于已有车辆。见 [09](../design/09-persistence.md#存档中的内容)。

## 编辑器去向修订

2026-10-05，[编辑器的去向](https://github.com/odradekk/train/issues/211)取消了存档内定义：存档只内嵌引用到的蓝图版本和已有聚居地使用的布局，用户内容库只保存蓝图，其他内容由创作模式写入数据包。用户库中的蓝图版本可以随时删除，因为用到它的存档都有内嵌副本。见 [09](../design/09-persistence.md#存档中的内容) 和 [15](../design/15-design-editor-and-tools.md)。

## 实例设计修订

2026-10-09，[实例与蓝图：实例保存自己的设计，模板的存放与改装目标](https://github.com/odradekk/train/issues/260)取消了蓝图版本：每个实例有自己的不可变设计，蓝图只是模板，不分版本；存档内嵌设计和本局蓝图库，不再内嵌蓝图版本。上文中实例引用蓝图版本、用户库保留全部版本、存档内嵌蓝图版本的部分由 [ADR-0012](0012-give-each-composite-instance-its-own-design-and-keep-blueprints-as-templates.md) 取代；当前外部定义适配和三种版本分离不变。
