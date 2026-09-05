# Roadmap：首版范围、明确延后、未冻结参数与实施顺序

> 本文会频繁变化；按 AGENTS.md，路线图是临时的，一个提出的方向不是实现承诺。

## 首版明确实现范围

首版目标是通过专门测试场景验收体素基础，具体条件见 [00](00-product-and-core-loop.md)。正式游戏场景与贸易玩法后续接入；达到验收条件即可进入游戏场景开发，不等待整个系统完成。以下为现有范围清单，具体边界由 #30、#37 按这些目标核对：

- 有限 3D Voxel World
- VoxelDefinition
- JSON/Data Mod
- Definition、Revision、Instance
- World Chunk
- Composite
- 玩家 Blueprint
- Construction Validation
- Behavior + Capability 基础
- Deterministic Simulation
- Runtime Delta Save
- Seeded World Generation
- Settlement Layout
- 基础 Rail Network
- Straight、Curve、Standard Slope
- Continuous Train Movement
- Multi-contact Vehicle Constraint
- TrackRenderer
- Chunk Rendering
- Dynamic Composite Rendering
- Visual Streaming
- Bottom Dock Window
- Perspective Train Camera

已知不一致，待 [补齐首版范围缺口](https://github.com/odradekk/train/issues/30) 消除：

- 首版已确认需要在测试场景中验证修改、破坏与车辆自定义；本清单尚未列 Damage 与 Fragmentation，具体范围需结合 [01](01-voxel-world.md) 与下文 Phase 2、Phase 3 补齐。
- 玩家 Blueprint 与 Construction Validation 的测试场景用途和具体含义仍需明确；正式车辆设计 UI 延后，不妨碍提供开发测试用的车辆修改入口。

## 明确延后的 Feature

正式游戏场景、经济与自动贸易玩法、正式车辆设计 UI 后续开发；当前测试场景复用正式世界模型、模拟、渲染与存档代码，并提供开发测试所需操作。

以下系统不是当前欠缺，而是刻意延后：Code Mod API、Offline Progress、Simulation LOD、Greedy Meshing、Voxel Geometry LOD、通用 Material System、复杂 Damage Types、World structural collapse、复杂重力系统、玩家铁路施工 UI、AI 铁路施工、复杂桥梁生成、复杂隧道生成、Signal、Block System、Reservation、Interlocking、复杂道岔控制、单双线运营规则、复杂车辆交通调度、Snapshot Save Optimization、正式版本 Save Migration。

架构不能主动阻止这些系统未来加入，但首版不为其支付完整实现成本。

## 暂不冻结的参数

必须通过实际开发确定，进入 Config / Tuning 而不是架构不变量：Chunk Size、World Size、Simulation Tick Rate、Camera FOV、Zoom Min/Max、Render Residency Radius、Unload Radius、Directional Prefetch Distance、Rail Gauge、Loading Gauge、最大 Blueprint 尺寸、World Generation Retry Count、Meshing Threshold、Custom Mesh LOD Distance、Voxel Geometry LOD Distance、Window Default Height。

## 推荐首版实现顺序

以下八个 Phase 是现有的引擎分层参考，不代表必须逐层完成后才做集成验证。#37 将其转为专门测试场景中的端到端切片，每个切片验证所选能力后再推进。

| Phase | 内容 | 目标 |
| --- | --- | --- |
| 1 Voxel Domain | VoxelDefinition、VoxelDefinitionId、RevisionId、Voxel storage、Chunk、Ownership、Durability；将现有货物与 Data Mod 系统逐渐迁移到统一 VoxelDefinition 模型 | 领域基础可用 |
| 2 World Rendering | Chunk → Face Culling Mesher → Chunk Mesh；Custom Static Mesh batch、AssetResolver、Dirty Chunk | 可以在 3D View 中稳定显示并破坏一个较大的 Voxel World |
| 3 Composite | CompositeDefinition、CompositeInstance、Local Voxel Grid、Archetype、Construction Validation、ownership、CompositeVisual、基础 Damage 与 Fragmentation | 可以制造、移动、破坏一个由 Voxel 构成的完整车辆结构 |
| 4 Deterministic Simulation | SimulationClock、Command、Transaction、Evaluate、Resolve、Commit、Structural Resolution、Domain Event；建立 Determinism 测试 | 相同 Save + Input 可以稳定得到相同结果 |
| 5 World Generation | Seed、GeneratorVersion、Terrain、Resources、Settlement placement、Layout、基础 Rail Graph、Physical Rail | 给定 Seed 可以生成可重复、合法、可驾驶的世界 |
| 6 Railway Core | Rail Ports 与 Internal Connections、Straight、Curve、Standard Slope、compressed graph、continuous path、RailPosition、multi-contact carriage movement、TrackRenderer | 首版视觉品质最需要投入的阶段之一 |
| 7 Desktop Product Form | Primary 3D Window、Bottom Dock、Floating、Display selection、Focus behavior、Perspective Camera、Orbit、Zoom、target switching、minimized rendering suspension | 游戏能够真正作为桌面常驻放置游戏运行 |
| 8 Save / Runtime Delta | Save-local Definition、Runtime Delta、RNG State、IDs、World reconstruction | 正常 Save → Quit → Load → Continue |

## 开放问题

- [补齐首版范围缺口：破坏触发源、玩家 Blueprint 含义、Faction 与窗口行为](https://github.com/odradekk/train/issues/30)。
- [把实现阶段转成端到端切片并确定前四个切片](https://github.com/odradekk/train/issues/37)：按 00 的验收目标组织测试场景中的端到端切片，覆盖体素修改与破坏、车辆修改与运行、存档和性能验证，并明确测试接缝。原“第二切片迁入 MVP 贸易循环”的候选方向已由 #29 的本次确认替换，贸易玩法留到后续版本；前四个切片的具体内容与顺序待本票确定。
- [编写首批切片 spec 并发布](https://github.com/odradekk/train/issues/38)：只写前 3 至 4 个切片，后续留在本文。
