# 设计文档集：火车放置贸易游戏

本目录是项目唯一且权威的设计契约。每条决定的出处见 `docs/research/gdd-grilling-transcript.md`，正文以"转录 Qn"引用。它由 v0.1 原稿拆分而来，原稿只作留底（提交 `6576bf6`），不再作为依据。

**文档内写明的内容视为已定。** 未定或自相矛盾之处只出现在各文档的"开放问题"小节，并链接到 wayfinder 地图 [规范化 GDD+Technical Contract 并产出首批切片 spec](https://github.com/odradekk/train/issues/24) 下的票；票关闭后由该票的会话回写对应章节。

## 文档目标

本项目为独立开发项目，设计文档不追求在开发前穷举实现细节。设计遵循：

1. 优先明确稳定的系统边界和不变量。
2. 具体数值尽量通过实现、Benchmark、Profiling 和 Playtest 确定。
3. 当前首版只实现通过专门测试场景验收体素基础所需的系统；达到 [00](00-product-and-core-loop.md) 的验收条件后进入正式游戏场景开发，贸易玩法后续接入。
4. 对明确会在未来加入的重要功能，架构上避免封死扩展空间，但不提前实现。
5. 组合优于继承。
6. 领域模型与 Godot SceneTree、渲染、UI 等表现层严格分离。
7. Deterministic Simulation 是整个项目的基础架构原则。

经济、AI、自动贸易、列车调度、车辆设计 UI 等系统在后续独立扩充。

## 文档地图

| 文件 | 内容 |
| --- | --- |
| [00-product-and-core-loop.md](00-product-and-core-loop.md) | 产品形态、长期核心循环与当前首版验收目标 |
| [01-voxel-world.md](01-voxel-world.md) | 体素世界与网格不变量 |
| [02-definitions-and-content.md](02-definitions-and-content.md) | Definition、Revision、Instance 与内容来源 |
| [03-composite-and-behavior.md](03-composite-and-behavior.md) | Composite、Behavior 与 Capability |
| [04-deterministic-simulation.md](04-deterministic-simulation.md) | 确定性模拟 |
| [05-world-generation-and-settlements.md](05-world-generation-and-settlements.md) | 世界生成与聚居地 |
| [06-railway.md](06-railway.md) | 铁路 |
| [07-rendering.md](07-rendering.md) | 渲染 |
| [08-desktop-window-and-camera.md](08-desktop-window-and-camera.md) | 桌面窗口与相机 |
| [09-persistence.md](09-persistence.md) | 存档 |
| [roadmap.md](roadmap.md) | 首版范围、明确延后、未冻结参数、实施顺序 |

每份文档的固定小节：边界、不变量、规则、首版范围、明确延后、开放问题。

术语以仓库根目录 `CONTEXT.md` 为准：体素定义、体素定义标识、聚居地、站点、货物、货物标识、基本货币价值、贸易环线、交易停靠站、贸易方案、运行圈、运行报告、策略迭代。VoxelDefinition、VoxelDefinitionId 分别对应体素定义、体素定义标识，Value 即基本货币价值。Voxel、Composite、Revision、RailPosition 等尚未进入术语表的概念沿用英文，待对应票落定后再收入。文中的 Simulation Tick 指模拟步进，与术语表的市场刻不是同一概念。

## 总体架构

```text
                   World Domain
                        │
          ┌─────────────┼─────────────┐
          │             │             │
      Simulation     Persistence    Generation
          │
          └─────────────┬─────────────┘
                        │
                    World Model
                        │
          ┌─────────────┴─────────────┐
          │                           │
       Godot Adapter              Management UI
          │
   ┌──────┼────────┐
   │      │        │
Render  Audio    Input
```

- **World Model 是游戏事实来源。** Godot Node、Mesh、Collider、窗口和 UI 都不是游戏事实本身："世界中存在一个 Voxel"不能通过"SceneTree 中存在某个 MeshInstance3D"来判断。Rail Graph、Chunk Mesh、Collision Shape、CompositeVisual 均属于 World Model 的派生表示、缓存或 Adapter。
- **Domain Model 使用普通 C# 类型。** 不采用 `VoxelInstance : Node3D`、`CompositeInstance : Node3D`。VoxelDefinition、Voxel runtime data、CompositeDefinition、CompositeInstance、World、Chunk、Simulation、RailNetwork 等核心领域都不依赖 SceneTree 生命周期。
- **Godot 负责** Rendering、Input、Audio、Windows、UI、Asset Loading、Physics Adapter。
- **Asset 引用。** Domain Definition 不直接依赖 Godot Resource 作为核心身份，使用 AssetId（MeshAssetId、TextureAssetId、MaterialAssetId）；Presentation Adapter 按需解析 AssetId 到 Godot Resource，只有实际需要渲染某区域时才加载视觉资产。
- **Domain Event 与 Godot Signal。** Simulation 内部使用普通 C# Domain Event / Message（VoxelDestroyed、CompositeConditionChanged、CapabilityChanged）；Adapter 可以转换成 Godot Signal，但 Domain 不为了 Signal 而继承 GodotObject。

这一决定记录为 [ADR-0004](../adr/0004-keep-the-world-model-as-the-single-source-of-truth-and-treat-godot-as-a-derived-adapter.md)；出处为转录 Q17、Q34、Q49、Q50、Q147。

## 核心不变量汇总

实现过程中优先保护：

1. 一个 Voxel = 一个 Cell。
2. 一个 Cell 最多一个 Voxel。
3. Voxel Local Coordinate 永远为整数。
4. Voxel Orientation 永远为正交方向。
5. Voxel 同一时间只有一个 Owner。
6. Composite Voxel 始终形成一个六面连通体。
7. Definition 是不可变数据。
8. Runtime State 不写回 Definition。
9. Semantic Revision 与 Presentation Asset Revision 分离。
10. 位置不是持久身份。
11. World Model 不依赖 SceneTree。
12. Physical Rail State（轨道体素、连续几何及明确连接）是铁路 Runtime Truth。
13. Logical Rail Graph 是派生索引。
14. RailPosition 是列车权威位置，World Transform 是派生结果。
15. Rendering、Collider 都是 World State 的派生缓存。
16. Simulation 与 FPS、Camera、Window Focus 解耦。
17. 注册顺序、容器枚举顺序、线程完成顺序不得决定 Gameplay。
18. 所有 Gameplay Random 最终源于 WorldSeed。
19. 首版全部使用 Stateless Random；有状态随机流在后续确有需求时加入。
20. 后续引入 Stateful RNG 时必须持久化完整状态。
21. Damage、Refit、Construction 等结构修改按 Transaction 原子结算。
22. Composite Creation 永远来自显式 Gameplay 行为。
23. Price 属于 Market Context，不属于 Voxel。
24. Inventory Quantity 不等于空间 Voxel Instance。
25. 首版性能优化必须服从 Profiling，而不是提前猜测。

## 设计指导思想

本项目不追求建立理论上最通用的 Voxel Engine、ECS、铁路模拟器或物理模拟框架。核心目标是：**用少量稳定、直观且可组合的基础规则，支持一个具有高度自由度的体素世界和长期可扩展的火车贸易游戏。**

对未来需求：简单模型已能满足玩法就继续使用；Profiling 证明性能不足再优化数据布局；真实 Gameplay 证明某个抽象不足再增加新系统；不因为"未来可能需要"就提前实现复杂框架。尤其避免：一个 Voxel 一个 Node、一种物品一个 C# 子类、万能 Composite 递归树、万能状态机、完整 ECS Framework、完整 Event Sourcing、提前实现跨平台 bit-perfect determinism、提前实现结构力学、提前实现完整铁路联锁。

长期稳定性来自：清晰边界 + 少量强不变量 + 数据驱动 + 组合 + 确定性 Simulation + 可替换的 Presentation Optimization，而不是过度抽象。

一句话总结：**游戏是一个有限、完整三维、数据驱动的体素世界；所有可实体化对象共享 VoxelDefinition，Voxel 可组成拥有独立身份和能力的 Composite；整个世界由确定性 Simulation 持续运行，程序生成世界通过物理体素与高层语义索引共同构成，其中铁路是核心空间与视觉系统；Godot 负责表现与平台适配，而不是承担 World Model 本身。**
