# Limit mods to data packages and isolate package failures

Mods are untrusted, declarative content packages under the game's `user://mods` directory; this system loads JSON and PNG data but does not execute scripts, assemblies, GDScript, or PCK content. Each manifest uses a canonical non-nil UUID as its namespace, and each mod is validated atomically so one invalid package can be excluded without preventing valid built-in content and other mods from loading.

## Consequences

Duplicate mod UUIDs disable every package using that UUID, duplicate definitions invalidate their owning package, paths cannot escape the package `resources` root, links are not followed, and bounded file, image, definition, and diagnostic limits protect synchronous reloads. A reload may commit without invalid mods and report warnings, but application-level reference validation rejects any candidate that would leave the active world with unresolved `GoodId` references. Mod dependencies, load-order overrides, code execution, and enable-state management are deferred until a concrete gameplay need justifies reopening this boundary.