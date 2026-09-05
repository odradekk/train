# Limit mods to data packages and isolate package failures

Mods are untrusted, declarative content packages under the game's `user://mods` directory; this system loads JSON and PNG data but does not execute scripts, assemblies, GDScript, or PCK content. Each manifest uses a canonical non-nil UUID as its namespace, and each mod is validated atomically so one invalid package can be excluded without preventing valid built-in content and other mods from loading.

Revised on 2026-09-05 through [#31](https://github.com/odradekk/train/issues/31) for the planned voxel-definition migration. Mods may add and modify their own definitions, but cannot override or patch built-in definitions or another mod's definitions. Duplicate definitions remain errors rather than load-order overrides.

## Consequences

Duplicate mod UUIDs disable every package using that UUID, duplicate definitions invalidate their owning package, paths cannot escape the package `resources` root, links are not followed, and bounded file, image, definition, and diagnostic limits protect synchronous reloads. New candidate definitions must resolve their references: invalid mods are excluded atomically, built-in errors reject publication, and the remaining candidate must still be internally valid.

Active instances retain their bound revisions. Removing a definition from the current catalog does not itself reject a reload if the old revision and all of its dependencies remain available. Any actually unresolved active-world reference still rejects the entire reload without changing the published content or world state; retained revisions do not exempt new candidates from validation. Persistence of retained revisions and resolution after restart remain for #35.

Cross-package patches, mod dependencies, load-order overrides, code execution and enable-state management are deferred until a concrete gameplay need justifies reopening this boundary.
