# Roadmap：首版范围、明确延后、未冻结参数与实施顺序

> 本文会频繁变化；按 AGENTS.md，路线图是临时的，一个提出的方向不是实现承诺。

## 首版明确实现范围

首版目标是通过专门测试场景验收体素基础，具体条件见 [00](00-product-and-core-loop.md)。正式游戏场景与贸易玩法后续接入；达到验收条件即可进入游戏场景开发，不等待整个系统完成。以下清单是整个首版的能力范围；[#37](https://github.com/odradekk/train/issues/37) 已确定下文前四个端到端切片，不要求首批切片完成全部清单。

- 有限 3D Voxel World
- VoxelDefinition
- JSON/Data Mod
- Definition、Revision、Instance
- World Chunk
- Composite
- 手写 JSON 车辆 Blueprint 的加载、校验与制造
- Construction Validation
- Refit：添加、移除、移动、旋转、替换体素，支持批量原子提交
- 基础 Damage 与 Fragmentation；调试操作对单个或多个地形、车辆体素施加指定伤害
- Behavior + Capability 基础
- Deterministic Simulation
- 连续量使用标量 double；无状态随机；确定性重复运行、存档恢复与渲染独立性验收（#34）
- Runtime Delta Save
- 读档与热重载采用当前外部定义并原子适配状态；语义、资产、目录发布版本分离（#35）
- Seeded World Generation
- Settlement Layout
- 三维聚居地锚点与朝向、生成用途的 Archetype、实际建筑和资源派生统计、站点 RailSocket、静态建筑统一占用与净空查询（#36）
- 基础 Rail Network
- 连续物理轨段、明确连接、实体占格与局部断轨；直线、连续水平弯与平滑缓坡（#32）
- 五档单节车辆尺寸包络，五档共用正常线路；尺寸与机械约束共同校验（#32）
- Continuous Train Movement
- Multi-contact Vehicle Constraint
- 编组统一运动与断开后独立滑行；单车头不能倒行，双车头停稳互斥换端；稳定沿轨引用、断轨停车与显式恢复、沿轨显示插值（#33）
- TrackRenderer
- Chunk Rendering
- Dynamic Composite Rendering
- Visual Streaming
- Bottom Dock Window（Windows AppBar）、Floating 与专门窗口测试场景
- 失焦正常渲染、暂不检测遮挡、最小化停主窗口渲染且模拟继续；可配置渲染帧率上限
- Perspective Train Camera

改装入口覆盖全部所列操作，验收既有零件身份与状态保留、替换语义及非法改装整体拒绝；具体契约见 [03](03-composite-and-behavior.md)。Docked 先通过独立窗口场景验证工作区预留与释放、模式切换、多显示器与混合 DPI、Godot 窗口消息共存，再接入体素测试场景，见 [08](08-desktop-window-and-camera.md)。

## 明确延后的 Feature

正式游戏场景、经济与自动贸易玩法、正式车辆设计 UI、事故碰撞等伤害玩法触发源、专门遮挡检测后续开发。Faction 的设计与实现整体延后，首版不保留占位字段；当前测试场景复用正式世界模型、模拟、渲染与存档代码，并提供开发测试所需操作。

TradeMetadata 的字段与实现、Mod 跨包覆盖与 patch 延后（#31）；Value 与自然资源标签属于当前体素定义，继续保留。

首版不实现有状态随机流或定点数体系；领域层暂不使用硬件宽度可变的向量计算，后续优化须经性能与确定性验证（#34）。

以下系统不是当前欠缺，而是刻意延后：Code Mod API、Offline Progress、Simulation LOD、Greedy Meshing、Voxel Geometry LOD、通用 Material System、复杂 Damage Types、World structural collapse、复杂重力系统、玩家铁路施工 UI、AI 铁路施工、复杂桥梁生成、复杂隧道生成、Signal、Block System、Reservation、Interlocking、复杂道岔控制、单双线运营规则、复杂车辆交通调度、Snapshot Save Optimization、正式版本 Save Migration。

轨道交叉与道岔结构后续专门设计，首版不允许独立线路共占一格（#32）。

完整制动管路、复杂阻力、坡道反向溜车与刚体脱轨延后；#33 采用无动力沿轨减速和断轨安全停车，不提前实现完整事故动力学。

架构不能主动阻止这些系统未来加入，但首版不为其支付完整实现成本。

## 暂不冻结的参数

必须通过实际开发确定，进入 Config / Tuning 而不是架构不变量：Chunk Size、World Size、Simulation Tick Rate、Camera FOV、Zoom Min/Max、Render Residency Radius、Unload Radius、Directional Prefetch Distance、Rail Gauge、Loading Gauge、非车辆 Blueprint 尺寸限制、World Generation Retry Count、Meshing Threshold、Custom Mesh LOD Distance、Voxel Geometry LOD Distance、Window Default Height、默认渲染帧率上限。

车辆五档尺寸已由 #32 确定，见 [06](06-railway.md)。水平半径 192、缓和长度 160、坡角 10°、左右各 8 格净空与顶部净空 12 为原型通过的起始参数，不是最小安全值；接触点、车钩、底部高度与轨距等实验配置尚未成为正式规格。弯坡叠加、多节编组、连续扫掠、实际占格与局部破坏以及 Godot 集成须在铁路切片补验，未验证线路组合不默认开放给生成器。

#33 的无动力滑行减速度及沿轨约束求解精度随实施确定。铁路切片还须覆盖双车头换端、断开后独立运动、断轨及修复后显式启动、全毁但被引用轨段的存档恢复和渲染帧率独立性；不能把 #32 的几何原型通过视为这些状态转换已获验证。

## 当前实施顺序：前四个端到端切片

以下范围经 #37 确认，按 **切片 1 → 2 → 3 → 4** 逐步交付。每个切片都贯通正式领域逻辑、可操作测试场景和对应验收，不先按引擎层把整个系统实现完。开发直接在仓库根目录进行，`archive/mvp/` 只作只读证据；旧的“第二切片迁入贸易循环”候选不再适用。

| 切片 | 回答的问题 | 可操作结果 |
| --- | --- | --- |
| 1 小型体素世界修改与破坏 | 体素能否作为真实世界状态被可靠修改并显示？ | 单 Chunk 场景按坐标添加、移除、施加伤害并更新可见面 |
| 2 跨 Chunk 修改与世界恢复 | 跨空间边界、退出重启后，世界是否仍一致？ | 多 Chunk 边界修改、基础碰撞查询、体素存档与恢复 |
| 3 静置车辆的改装与破坏 | 车辆是否是可修改、可破坏、可保存的体素对象？ | 加载 Blueprint、完整原子改装、车体断裂及车辆存档 |
| 4 直线铁路运行与状态衔接 | 运动能否与已有破坏、编组和存档规则一致衔接？ | 一车头一车厢沿直轨运行、断裂／断轨处理、运动状态恢复 |

### 切片 1：小型体素世界修改与破坏

- 按 [02](02-definitions-and-content.md) 与 #31，一次性迁移根目录现有 Goods 模型、内容目录及引用到 VoxelDefinition，不保留旧兼容层。归档 MVP 不改。
- 建立有限三维整数网格，先采用一个 Chunk 和固定测试视角；世界状态由普通 C# 领域类型持有，Godot 显示派生网格。
- 提供按坐标添加、移除与施加伤害的最小开发入口。伤害须经过正式耐久结算，归零删除不能仅由演示脚本直接删节点代替。
- 验证修改后占用与可见面正确，无重叠占格或残留显示；测试与场景使用同一套正式修改逻辑。
- 不纳入车辆、铁路、程序生成、存档、桌面停靠与贸易；基础碰撞查询在切片 2 接入。单 Chunk 通过不表示跨区或规模性能达标。

### 切片 2：跨 Chunk 修改与世界恢复

- 依赖切片 1。扩展到多个 Chunk，验证边界处添加、移除与破坏时，相邻网格正确更新。
- 接入基础碰撞查询，检查体素删除或新增后世界状态、显示与碰撞一致，不存在残留碰撞。
- 基于可重复的测试基底，按 [09](09-persistence.md) 保存世界体素修改，退出再加载后恢复相同的占用、耐久与朝向；显示与碰撞由恢复后的状态重建。
- 存档只覆盖当前世界体素，不提前实现车辆存档或完整程序生成。多 Chunk 规模用于验证边界，不承担大世界性能达标目标。

### 切片 3：静置车辆的改装与破坏

- 依赖切片 2。从手写 JSON Blueprint 制造静置车辆，校验 [06](06-railway.md) 的声明尺寸档位、结构连通及当前支持的接触点配置；铁路动态机械约束留到运行切片验证。
- 完整覆盖车内体素的添加、移除、移动、旋转、替换及批量原子改装。非法修改整体拒绝，保留原车辆与零件状态，不以少数操作替代完整覆盖。
- 验证车辆及零件身份、耐久等运行状态保留；Composite 所有权与显示独立于世界 Chunk，不能重复拥有或重复渲染车辆体素。
- 通过正式伤害流程验证车体断裂、身份继承及碎块脱落，遵守 [03](03-composite-and-behavior.md) 的结构结算与所有权规则。
- 扩展切片 2 的存档能力，保存并恢复实际车辆布局、零件状态及破坏结果。不纳入沿轨运行、编组牵引或正式设计器。

### 切片 4：直线铁路运行与状态衔接

- 依赖切片 3。通过正式 RailConstructionPlan／Transaction 入口生成固定直轨，用一个车头和一节车厢验证统一编组牵引、接触点与车钩约束；轨道几何、占格和定位遵守 [06](06-railway.md)，不从显示节点推导运动。
- 提供启动、停车操作；单车头不能倒行，到线路末端停车，不自动反向。画面采用沿轨插值，相机跟随车辆的显示姿态。
- 运行中破坏轨道或车辆，验证任何必要接触点本次移动将穿过前方缺口时限制编组移动并停车、车下支撑失效只暂停受影响编组，以及编组断开后独立滑行和修复后显式启动；测试入口走正式伤害、结构结算与轨道修改流程。
- 保存运行中、滑行中与停车中的状态，按 [04](04-deterministic-simulation.md) 验证相同条件下读档继续与不中断运行一致。
- 弯道、坡道、双车头换端、五档组合矩阵和规模性能测试安排在后续铁路切片；桌面停靠另行验证。直轨通过不等于全部铁路几何或完整首版已经通过。

### 四个切片的验收分工

- **领域自动测试**：复用 `Train.Tests` 的无 Godot 测试边界，通过正式命令验证当前切片涉及的占用、伤害、改装原子性、身份继承、编组运动与存档恢复。只测试已经进入该切片的行为，不为后续系统建占位实现。
- **Godot 集成检查**：复用现有 `scripts/run-godot-*-smoke` 的组织方式，验证真实场景加载及领域状态到显示的接续；接入碰撞后同时检查修改时的碰撞更新。现有内容与日志冒烟不是体素世界验收的替代品。
- **人工观感检查**：检查当前切片的可见面，以及接入车辆后对应的刚性车体、车钩、插值和相机表现。自动检查通过不替代人工观感确认。
- 每个切片保留可重复运行的测试场景与操作步骤。测试入口调用正式逻辑，不另写一套仅供演示的世界修改、存档或运动逻辑。
- [#38](https://github.com/odradekk/train/issues/38) 将四个范围分别展开为 spec，明确具体场景尺寸、输入序列、运行方式和通过条件；在实现前明确，不由实现者临时决定。每份 spec 区分本切片验收与尚未证明的能力。

### 性能基线与后续验收

- 前四个切片各用固定场景、操作序列和固定且记录清楚的运行环境，持续记录体素数量、修改耗时、帧耗时与内存；接入存档后记录保存与加载耗时。跨切片比较时复用已有固定基线用例，新增能力另增对应场景，不把不同负载或环境的结果直接当作性能退化。
- 出现明显卡顿或性能退化，在当前切片排查，不等最后集中处理。不提前承诺未经测量的 FPS 或把小场景结果外推成规模保证。
- 大世界、高频修改、多列车压力测试安排在后续切片，结合实际测量确定正式规模与性能门槛。前四个切片完成不等于 [00](00-product-and-core-loop.md) 的全部首版验收完成。
- 后续仍需安排连续弯坡及车辆组合、双车头换端、完整世界生成与聚居地、桌面停靠专门场景、规模性能等能力的验证。保留在本文，待前面的切片交付后再选择和细化，不在 #38 一次发布全部后续实现票。

## 引擎能力分层参考

以下八个 Phase 仅保留为能力归类，不再表示实施先后顺序。例如切片 1 已同时涉及领域与渲染，切片 2 接入存档，不能等参考表的 Phase 8 才保存世界。

| Phase | 内容 | 目标 |
| --- | --- | --- |
| 1 Voxel Domain | VoxelDefinition、VoxelDefinitionId、RevisionId、Voxel storage、Chunk、Ownership、Durability；首个需要体素定义的切片一次性迁移根目录货物模型、目录、内容与引用到新 schema，不保留 goods 兼容层，归档 MVP 不变（#31） | 领域基础可用 |
| 2 World Rendering | Chunk → Face Culling Mesher → Chunk Mesh；Custom Static Mesh batch、AssetResolver、Dirty Chunk | 可以在 3D View 中稳定显示并破坏一个较大的 Voxel World |
| 3 Composite | CompositeDefinition、CompositeInstance、Local Voxel Grid、Archetype、Construction Validation、ownership、CompositeVisual、基础 Damage 与 Fragmentation、JSON Blueprint、完整 Refit 测试操作 | 可以加载设计、制造、改装、移动与破坏车辆，验证状态保留与事务原子性 |
| 4 Deterministic Simulation | SimulationClock、Command、Transaction、Evaluate、Resolve、Commit、Structural Resolution、Domain Event；建立 Determinism 测试 | 相同 Save + Input 可以稳定得到相同结果 |
| 5 World Generation | Seed、GeneratorVersion、Terrain、Resources、Settlement placement、Layout、基础 Rail Graph、Physical Rail | 给定 Seed 可以生成可重复、合法、可驾驶的世界 |
| 6 Railway Core | 连续物理轨段、明确连接、实体占格、局部断轨、平滑弯坡、五档车辆包络、compressed graph、RailPosition、multi-contact carriage movement、TrackRenderer | 验证已确认观感的正式实现，以及占格、破坏、存档和连续扫掠约束 |
| 7 Desktop Product Form | Primary 3D Window、Windows AppBar、Floating、Display selection、Focus behavior、Perspective Camera、Orbit、Zoom、target switching、minimized rendering suspension、可配置渲染帧率上限 | 窗口测试场景通过工作区、切换、多显示器、DPI 与消息共存验证后，接入体素测试场景验证桌面常驻 |
| 8 Save / Runtime Delta | Save-local Definition、Runtime Delta、WorldSeed、IDs、World reconstruction、double 无损往返 | 正常 Save → Quit → Load → Continue，结果与不中断运行一致 |

## 开放问题

- [编写首批切片 spec 并发布](https://github.com/odradekk/train/issues/38)：按本文已确认范围，为四个切片分别发布 spec 与原生依赖；具体场景尺寸、输入序列、通过条件及基线记录方式在 spec 中落实。
- 后续能力切片的具体顺序、压力测试规模与正式性能门槛，随实际交付和测量确定。
