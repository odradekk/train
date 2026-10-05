# Drive connected assemblies from physical rail positions

Accepted through [#33](https://github.com/odradekk/train/issues/33). Each physically connected train assembly owns a reference RailPosition and coordinated motion state; contact positions and rigid vehicle poses are solved together along the same physical path, rather than advanced independently. RailPosition refers to a stable physical segment, distance and orientation, not a disposable compressed edge or voxel cell, so graph rebuilds and local breaks do not move the train. Referenced segment geometry survives even complete track destruction for localization, without granting traversal; unresolved required references fail explicitly on load.

An assembly break creates independent motion units where the surviving vehicles retain rail-running conditions. Re-anchoring preserves the event-time poses and motion; an unpowered unit coasts to rest with configurable deceleration instead of following its former locomotive or stopping instantly. Identity inheritance never preserves a broken physical coupling. Track gaps instead use a deliberately simplified safety stop: clamp motion before any required contact crosses a gap, or freeze the affected assembly at its current simulation pose if support disappears underneath it. Restart requires valid support and explicit input, from zero speed. Full accident physics and braking systems are deferred.

A locomotive cannot provide traction backwards; the inactive end locomotive may be towed opposite to its own heading. Reversing powered travel requires outward-facing locomotives at both ends, with at most one active for traction. The assembly must stop before an atomic handover to the other locomotive; reference changes do not move or rotate vehicles. This supersedes the interview's rejected negative-speed, permanently fixed-reference proposal.

Presentation interpolates along the path between completed simulation states and solves the vehicle constraints there, accepting up to one simulation step of display latency without extrapolation. Splits, handovers and support loss end the previous interpolation interval. The camera follows the selected vehicle's displayed pose and body heading, not tick positions, travel direction or the active locomotive selection. See [the railway contract](../design/06-railway.md) for the complete rules and pending implementation validation.

## 2026-10-05 状态

本决定以物理轨段上的 RailPosition 作为列车的权威位置，而它所依赖的全局体素世界已被 [ADR-0008](0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 取消。列车改为在世界图上运行之后，本决定将由 [列车在图上的运行与调度](https://github.com/odradekk/train/issues/203) 取代。其中的显示插值规则（在相邻两个已完成的模拟状态之间插值、不外推，最多延迟一步）已由 ADR-0008 沿用。在此之前，不得依据本决定新增实现。

## 2026-10-05 已被取代

[列车在图上的运行与调度](https://github.com/odradekk/train/issues/203)已取代本决定：列车在世界图的线路上运行，按铁轨号、方向和沿线路的距离定位，同一条铁轨用方向锁加同向跟车，列车在节点可以从任何一条线路离开，规则见 [06](../design/06-railway.md)。本决定中的RailPosition、编组约束运动、单向牵引与双车头换端不再适用；线路场景怎样表现弯道、坡道和道岔，由地图迷雾“观察与表现”确定。显示插值规则已由 [ADR-0008](0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 沿用。
