# Preserve physical rail geometry and local breaks

Status: proposed

## Problem

Physical rail needs an authoritative state. Occupied cells alone cannot uniquely recover a curve, and removing one rail voxel should not reshape intact neighbours or invalidate an entire arbitrarily long storage segment.

## Proposal

Accepted through [#32](https://github.com/odradekk/train/issues/32). Physical rail state includes rail voxels, continuous segment geometry and explicit connections; occupied cells alone cannot uniquely recover a curve. Generate continuous alignments first, then register their physical footprint in the voxel grid, rather than forcing cell-local turns or fitting a visual spline after construction.

Rendering meshes, arc-length lookup tables and the compressed rail graph are derived from that state. Removing a rail voxel invalidates the intervals that depend on it, without reshaping intact neighbours or invalidating an entire arbitrarily long storage segment. Retaining curve parameters does not keep a missing interval traversable. This clarifies the track-geometry cache wording in `2026-09-04-world-model-as-single-source-of-truth`; it does not move authority into the renderer.

All compliant vehicle tiers must pass normally generated intact lines; there are no tier-specific route classes in the first version. See [the railway contract](../../../../docs/design/06-railway.md) for limits and pending implementation validation. RailPosition ownership, references, break handling for existing trains and interpolation are decided separately by #33 in `2026-09-05-drive-assemblies-from-rail-positions`.

### 2026-10-05 状态

本决定把物理轨道几何作为铁路的事实来源，而它所依赖的全局体素世界已被 `2026-10-05-voxel-free-core-and-presentation-scenes` 取消。铁路改为世界图上边的属性之后，本决定将由 [列车在图上的运行与调度](https://github.com/odradekk/train/issues/203) 取代；文中的几何参数和机械参数是否改作运行场景的生成与显示标准，也由该票决定。在此之前，不得依据本决定新增实现。

### 2026-10-05 已被取代

[列车在图上的运行与调度](https://github.com/odradekk/train/issues/203)已取代本决定：列车在世界图的线路上运行，按铁轨号、方向和沿线路的距离定位，同一条铁轨用方向锁加同向跟车，列车在节点可以从任何一条线路离开，规则见 [06](../../../../docs/design/06-railway.md)。本决定中的物理轨道几何、局部断轨和几何参数不再适用；线路场景怎样表现弯道、坡道和道岔，见 [01](../../../../docs/design/01-voxel-engine-and-scenes.md#线路场景)。

## Alternatives considered

**Cell-local turns, or a visual spline fitted after construction.** Occupied cells alone cannot uniquely recover a curve, so continuous alignments are generated first and only then registered in the voxel grid.

## Acceptance criteria

Physical rail state stores rail voxels, continuous segment geometry and explicit connections. Rendering meshes, arc-length tables and the compressed rail graph are derived from it, and removing a rail voxel invalidates only the intervals that depend on it.

## Risks

The prototypes establish useful starting parameters and user-approved vehicle size envelopes, not an exhaustive swept-clearance proof or final contact, coupler and gauge specifications.
