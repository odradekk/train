# Preserve physical rail geometry and local breaks

Accepted through [#32](https://github.com/odradekk/train/issues/32). Physical rail state includes rail voxels, continuous segment geometry and explicit connections; occupied cells alone cannot uniquely recover a curve. Generate continuous alignments first, then register their physical footprint in the voxel grid, rather than forcing cell-local turns or fitting a visual spline after construction.

Rendering meshes, arc-length lookup tables and the compressed rail graph are derived from that state. Removing a rail voxel invalidates the intervals that depend on it, without reshaping intact neighbours or invalidating an entire arbitrarily long storage segment. Retaining curve parameters does not keep a missing interval traversable. This clarifies the track-geometry cache wording in [ADR-0004](0004-keep-the-world-model-as-the-single-source-of-truth-and-treat-godot-as-a-derived-adapter.md); it does not move authority into the renderer.

All compliant vehicle tiers must pass normally generated intact lines; there are no tier-specific route classes in the first version. The prototypes establish useful starting parameters and user-approved vehicle size envelopes, not an exhaustive swept-clearance proof or final contact, coupler and gauge specifications. See [the railway contract](../design/06-railway.md) for limits and pending implementation validation. RailPosition ownership, references, break handling for existing trains and interpolation are decided separately by #33 in [ADR-0007](0007-drive-connected-assemblies-from-physical-rail-positions.md).

## 2026-10-05 状态

本决定把物理轨道几何作为铁路的事实来源，而它所依赖的全局体素世界已被 [ADR-0008](0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 取消。铁路改为世界图上边的属性之后，本决定将由 [列车在图上的运行与调度](https://github.com/odradekk/train/issues/203) 取代；文中的几何参数和机械参数是否改作运行场景的生成与显示标准，也由该票决定。在此之前，不得依据本决定新增实现。
