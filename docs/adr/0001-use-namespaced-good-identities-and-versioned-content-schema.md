# Use namespaced voxel identities and strict versioned definition schemas

Revised on 2026-09-05 through [#31](https://github.com/odradekk/train/issues/31). The first slice that needs voxel definitions will replace the root project's separate goods model, catalog, content files and references with `VoxelDefinition` and `VoxelDefinitionId`. It will introduce new definition schemas at version 1, with no legacy goods compatibility layer; `archive/mvp/` remains unchanged. This is an accepted migration decision, not a claim that the code has already migrated.

Built-in and mod definitions retain the `<namespace>:<local-id>` identity shape. Natural resources use the `natural_resource` tag instead of a closed goods category; the tag is independent of tradability. `Value` remains on voxel definitions because fracture identity inheritance already needs it, while `TradeMetadata` design and implementation wait for the later trade feature.

## Consequences

Built-in IDs use the `core` namespace, mod namespaces come from their manifest UUID, and their definition files contain only local IDs. Folders remain an authoring convention rather than an identity boundary. Files use explicit `schema_version` and type-specific `.voxels.json`, `.composites.json` and `.layouts.json` suffixes. Unknown fields, unknown schema versions, duplicate definitions and invalid components invalidate the owning content package.

Built-in and mod resources are organized under `voxels`, `composites` and `layouts`. The player Blueprint library resides at `user://blueprints/` and reuses the Composite format; save-local definitions are embedded in saves rather than scanned from a separate content directory. See [the content design](../design/02-definitions-and-content.md) for source roots and [ADR-0005](0005-resolve-current-definitions-and-separate-content-versions.md) for Blueprint filenames, revision hashes and current-definition save bindings.
