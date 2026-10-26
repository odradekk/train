# Keep the World Model as the single source of truth and treat Godot as a derived adapter

Status: accepted
Superseded in part by: 2026-10-05-voxel-free-core-and-presentation-scenes — 游戏状态的范围

## Problem

The simulation must stay deterministic and testable without the engine, several windows must present views of one world, definitions must hot-reload without touching live scenes, and Godot physics is documented as non-deterministic.

## Proposal

Game state (voxels, composites, the rail network, settlements, the simulation clock) lives in plain C# domain types that never inherit from `GodotObject` or `Node` and never read the `SceneTree`; Godot supplies rendering, collision, windows, input, audio, and asset loading as adapters rebuilt from world state.

### Rules and consequences

Chunk meshes, track rendering meshes, composite visuals, Godot collision shapes, and window state are caches that may be discarded and rebuilt at any time; nothing gameplay-relevant may be inferred from them, and "a voxel exists" is never answered by the presence of a `MeshInstance3D`. Definitions reference presentation assets only through an `AssetId` that the presentation adapter resolves when a region is actually rendered. Domain events are plain C# messages; Godot signals exist only inside adapters. Godot physics may assist collision detection but is never the authority for train position or damage. Simulation time comes from an engine-independent clock hosted by Godot, so frame rate, camera visibility, and window focus cannot change simulation results.

[#32](https://github.com/odradekk/train/issues/32) clarifies that continuous physical rail geometry and explicit connections are domain state, not presentation caches. Only the rendering mesh and query indexes are disposable; see `2026-09-05-preserve-physical-rail-geometry`.

### 2026-10-05 修订

`2026-10-05-voxel-free-core-and-presentation-scenes` 取代了本文对游戏状态范围的描述。游戏状态现在全部属于不涉及体素的核心模拟层；体素和物理铁路不再是全局游戏状态，体素只在局部场景中按需构建。本文其余结论仍然有效：领域使用不依赖 Godot 的普通 C# 类型，Godot 只是派生适配层；渲染和碰撞是可以丢弃的缓存；Godot 物理不决定结果；模拟时钟与帧率、相机和窗口无关。

## Alternatives considered

**The idiomatic node-centric Godot design.** We chose plain C# domain types over it because of the four requirements under Problem: deterministic and engine-free testing, several windows on one world, hot reload without touching live scenes, and non-deterministic Godot physics.

## Acceptance criteria

No domain type inherits from `GodotObject` or `Node` or reads the `SceneTree`. Meshes, collision shapes and window state can be discarded and rebuilt from world state, and simulation time comes from an engine-independent clock.

## Risks

The original record lists no costs.
