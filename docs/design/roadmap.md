# Roadmap：首版范围、明确延后、未冻结参数与实施顺序

> 来源：原稿 §77–79、§81；转录矛盾 4、5。本文会频繁变化；按 AGENTS.md，路线图是临时的，一个提出的方向不是实现承诺。

## 首版明确实现范围（§77）

首版目标集中于完成核心闭环。必须：

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

- 本清单未列 Damage 与 Fragmentation，而 [01](01-voxel-world.md) 的耐久规则与下文 Phase 2、Phase 3 的目标明确包含破坏与基础断裂。
- 本清单把玩家 Blueprint 与 Construction Validation 列为必须，而原 §1 与 §2 把车辆设计 UI 列为后续扩充，转录 Q24 也说现在不做编辑器 UI。

## 明确延后的 Feature（§78）

以下系统不是当前欠缺，而是刻意延后：Code Mod API、Offline Progress、Simulation LOD、Greedy Meshing、Voxel Geometry LOD、通用 Material System、复杂 Damage Types、World structural collapse、复杂重力系统、玩家铁路施工 UI、AI 铁路施工、复杂桥梁生成、复杂隧道生成、Signal、Block System、Reservation、Interlocking、复杂道岔控制、单双线运营规则、复杂车辆交通调度、Snapshot Save Optimization、正式版本 Save Migration。

架构不能主动阻止这些系统未来加入，但首版不为其支付完整实现成本。

## 暂不冻结的参数（§79）

必须通过实际开发确定，进入 Config / Tuning 而不是架构不变量：Chunk Size、World Size、Simulation Tick Rate、Camera FOV、Zoom Min/Max、Render Residency Radius、Unload Radius、Directional Prefetch Distance、Rail Gauge、Loading Gauge、最大 Blueprint 尺寸、World Generation Retry Count、Meshing Threshold、Custom Mesh LOD Distance、Voxel Geometry LOD Distance、Window Default Height。

## 推荐首版实现顺序（§81）

原稿按尽快形成可运行闭环的顺序给出八个 Phase。它们是引擎分层，不是端到端的可玩切片；转成切片的工作见开放问题。

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
- [把 §81 阶段转成端到端切片并确定前四个切片](https://github.com/odradekk/train/issues/37)：候选首切片是手工小体素世界、直线弯道坡道轨道、一列多接触点列车在两个站点间往返、RailPosition 权威、TrackRenderer 连续几何、域层不依赖 Godot；第二切片把 MVP 的贸易循环搬到该世界；同时决定每个切片的测试接缝。
- [编写首批切片 spec 并发布](https://github.com/odradekk/train/issues/38)：只写前 3 至 4 个切片，后续留在本文。
