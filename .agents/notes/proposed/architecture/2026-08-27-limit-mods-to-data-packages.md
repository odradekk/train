# Limit mods to data packages and isolate package failures

Mods are untrusted, declarative content packages under the game's `user://mods` directory; this system loads JSON and PNG data (model files such as `.glb` are data too) but does not execute scripts, assemblies, GDScript, or PCK content. Each manifest uses a canonical non-nil UUID as its namespace, and each mod is validated atomically so one invalid package can be excluded without preventing valid built-in content and other mods from loading.

Revised on 2026-09-05 through [#31](https://github.com/odradekk/train/issues/31) for the planned voxel-definition migration. Mods may add and modify their own definitions, but cannot override or patch built-in definitions or another mod's definitions. Duplicate definitions remain errors rather than load-order overrides.

## Consequences

Duplicate mod UUIDs disable every package using that UUID, duplicate definitions invalidate their owning package, paths cannot escape the package `resources` root, links are not followed, and bounded file, image, definition, and diagnostic limits protect synchronous reloads. New candidate definitions must resolve their references: invalid mods are excluded atomically, built-in errors reject publication, and the remaining candidate must still be internally valid.

[#35](https://github.com/odradekk/train/issues/35) replaces the earlier retained-revision rule: existing instances adopt currently installed definitions on both load and hot reload. All affected references and runtime states must be valid against the candidate content before definitions and state adaptations are published atomically. A missing required definition or incompatible state rejects the entire reload without changing content, Catalog Generation or world state; loading fails explicitly. No old external definition fallback is provided. See [ADR-0005](0005-resolve-current-definitions-and-separate-content-versions.md).

Cross-package patches, mod dependencies, load-order overrides, code execution and enable-state management are deferred until a concrete gameplay need justifies reopening this boundary.

2026-10-09，[内容包与依赖](https://github.com/odradekk/train/issues/262)加入包级依赖（UUID 加最低版本）和按依赖排序的加载，玩家构建可在主菜单启用或停用 Mod；读档缺内容改为玩家选择并用占位保留原始标识，见 [ADR-0015](0015-declare-package-dependencies-and-keep-missing-content-as-placeholders.md)。跨包覆盖、加载顺序覆盖和代码执行仍不允许。
