# Keep the World Model as the single source of truth and treat Godot as a derived adapter

Game state (voxels, composites, the rail network, settlements, the simulation clock) lives in plain C# domain types that never inherit from `GodotObject` or `Node` and never read the `SceneTree`; Godot supplies rendering, collision, windows, input, audio, and asset loading as adapters rebuilt from world state. We chose this over the idiomatic node-centric Godot design because the simulation must stay deterministic and testable without the engine, several windows must present views of one world, definitions must hot-reload without touching live scenes, and Godot physics is documented as non-deterministic.

## Consequences

Chunk meshes, track rendering meshes, composite visuals, Godot collision shapes, and window state are caches that may be discarded and rebuilt at any time; nothing gameplay-relevant may be inferred from them, and "a voxel exists" is never answered by the presence of a `MeshInstance3D`. Definitions reference presentation assets only through an `AssetId` that the presentation adapter resolves when a region is actually rendered. Domain events are plain C# messages; Godot signals exist only inside adapters. Godot physics may assist collision detection but is never the authority for train position or damage. Simulation time comes from an engine-independent clock hosted by Godot, so frame rate, camera visibility, and window focus cannot change simulation results.

[#32](https://github.com/odradekk/train/issues/32) clarifies that continuous physical rail geometry and explicit connections are domain state, not presentation caches. Only the rendering mesh and query indexes are disposable; see [ADR-0006](0006-preserve-physical-rail-geometry-and-local-breaks.md).

## 2026-10-05 修订

[ADR-0008](0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 取代了本文对游戏状态范围的描述。游戏状态现在全部属于不涉及体素的核心模拟层；体素和物理铁路不再是全局游戏状态，体素只在局部场景中按需构建。本文其余结论仍然有效：领域使用不依赖 Godot 的普通 C# 类型，Godot 只是派生适配层；渲染和碰撞是可以丢弃的缓存；Godot 物理不决定结果；模拟时钟与帧率、相机和窗口无关。
