# Keep the World Model as the single source of truth and treat Godot as a derived adapter

Game state (voxels, composites, the rail network, settlements, the simulation clock) lives in plain C# domain types that never inherit from `GodotObject` or `Node` and never read the `SceneTree`; Godot supplies rendering, collision, windows, input, audio, and asset loading as adapters rebuilt from world state. We chose this over the idiomatic node-centric Godot design because the simulation must stay deterministic and testable without the engine, several windows must present views of one world, definitions must hot-reload without touching live scenes, and Godot physics is documented as non-deterministic.

## Consequences

Chunk meshes, track rendering meshes, composite visuals, Godot collision shapes, and window state are caches that may be discarded and rebuilt at any time; nothing gameplay-relevant may be inferred from them, and "a voxel exists" is never answered by the presence of a `MeshInstance3D`. Definitions reference presentation assets only through an `AssetId` that the presentation adapter resolves when a region is actually rendered. Domain events are plain C# messages; Godot signals exist only inside adapters. Godot physics may assist collision detection but is never the authority for train position or damage. Simulation time comes from an engine-independent clock hosted by Godot, so frame rate, camera visibility, and window focus cannot change simulation results.

[#32](https://github.com/odradekk/train/issues/32) clarifies that continuous physical rail geometry and explicit connections are domain state, not presentation caches. Only the rendering mesh and query indexes are disposable; see [ADR-0006](0006-preserve-physical-rail-geometry-and-local-breaks.md).
