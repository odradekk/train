# 火车放置贸易游戏：开发设计文档

**文档版本：** 0.1
**项目阶段：** 原型 / 敏捷开发阶段
**主要技术栈：** Godot 4 + C#
**首要目标平台：** Windows
**文档性质：** Game Design + Technical Design 混合设计文档

---

# 1. 文档目标

本项目为独立开发项目，因此本设计文档不追求在开发前穷举所有实现细节。

设计遵循以下原则：

1. **优先明确稳定的系统边界和不变量。**
2. **具体数值尽量通过实现、Benchmark、Profiling 和 Playtest 确定。**
3. **首版只实现满足核心 Gameplay Loop 所需的最小系统。**
4. **对于明确会在未来加入的重要功能，在架构上避免主动封死扩展空间，但不提前实现。**
5. **组合优于继承。**
6. **领域模型与 Godot SceneTree、渲染、UI 等表现层严格分离。**
7. **Deterministic Simulation 是整个项目的基础架构原则。**

本文件首先描述当前已确定的核心世界、体素、复合体、铁路、Simulation、Rendering 与桌面窗口架构。

经济、AI、自动贸易、列车调度、车辆设计 UI 等系统将在后续独立扩充。

---

# 2. 核心产品形态

游戏是一款以**火车自动运行和贸易模拟**为核心的桌面放置游戏。

玩家不是一个直接控制的角色。

主要游戏视角是一个长期存在于桌面的 3D 条形窗口：

```text
┌──────────────────────────────────────┐
│                                      │
│          用户正在使用的其他软件        │
│                                      │
├──────────────────────────────────────┤
│         游戏 3D 火车观察窗口           │
└──────────────────────────────────────┘
```

首版默认：

* Windows-first；
* 无边框；
* Bottom Dock；
* 占满目标显示器宽度；
* 高度可调整；
* 默认主显示器；
* 玩家可选择其他显示器；
* 可以切换为 Floating 模式；
* Floating 模式允许任意移动和合理范围内调整尺寸；
* Floating 模式可选 Always-on-top；
* 3D 窗口为游戏主窗口。

其他管理功能使用独立窗口，包括未来的：

* 自动贸易策略；
* 火车管理；
* 车辆升级；
* 车辆设计器；
* 经济管理；
* 其他 Gameplay UI。

这些窗口只是同一个 World Model 的不同 View，不拥有独立世界状态。

关闭辅助窗口不会影响 Simulation。

首版关闭主 3D 窗口视为退出游戏。

---

# 3. 总体架构原则

整体结构：

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

核心原则：

> **World Model 是游戏事实来源。**

Godot Node、Mesh、Collider、窗口和 UI 都不是游戏事实本身。

例如：

```text
世界中存在一个 Voxel
```

不能通过：

```text
SceneTree 中存在某个 MeshInstance3D
```

来判断。

同理：

```text
Rail Graph
Chunk Mesh
Collision Shape
CompositeVisual
```

均属于 World Model 的派生表示、缓存或 Adapter。

---

# 4. 世界空间模型

## 4.1 有限三维体素世界

世界是一个：

> **有限尺寸、完整三维、严格整数网格的 Voxel World。**

世界尺寸为创建世界时的配置参数。

具体尺寸不在当前设计阶段冻结。

世界支持真正的三维结构，因此数据模型不限制为 Heightfield。

未来可以自然支持：

* 洞穴；
* 隧道；
* 桥梁；
* 地下设施；
* 悬空结构；
* 任意方向的体素破坏。

首版世界生成算法仍然可以以较简单的地表地形为主。

---

# 5. Voxel 基础模型

## 5.1 一切皆体素

“一切皆体素”的含义是：

> 游戏世界中能够实体化并参与游戏逻辑的物品、设备、资源、地形和可交互物体，统一建立在 Voxel 模型之上。

包括：

* 地形；
* 自然资源；
* 铁轨；
* 建筑零件；
* 车轮；
* 引擎；
* 武器；
* 装饰；
* 人物或生物的结构组件；
* 其他可交互实体。

但以下内容不属于 Voxel：

* UI；
* 粒子；
* 临时视觉特效；
* 摄像机；
* 纯表现对象。

Voxel 可以使用六面 Cube 表现，也可以拥有专用 Custom Mesh。

“Voxel”是**逻辑空间单位**，不是“只能拥有六面的立方体 Mesh”。

---

# 6. Voxel Grid 不变量

以下规则属于核心不变量。

### 6.1 一个 Voxel 永远占据一个 Cell

逻辑尺寸固定为：

```text
1 × 1 × 1
```

需要表达：

```text
2 × 1 × 1
```

桌子时，应使用两个或更多 Voxel 构成 Composite，而不是创建跨 Cell Voxel。

### 6.2 一个 Cell 最多一个 Voxel

```text
occupant count ∈ {0, 1}
```

多个可交互 Voxel 不允许重叠占据同一 Cell。

### 6.3 空气不是 Voxel

Cell 状态：

```text
Empty
或
Occupied(Voxel)
```

不存在必须实例化的 `air` Voxel。

### 6.4 Orientation 严格正交

Voxel 不允许任意角度旋转。

领域模型支持完整的 24 种立方体正交朝向。

Definition 可以限制可用方向，例如：

* 四向水平旋转；
* 完整 24 向；
* 不允许旋转。

### 6.5 Occupancy 与 Collision 分离

Voxel 逻辑上始终占据整个 Cell。

但实际 Collision Shape 可以是：

* None；
* Full Box；
* 一个或少量局部碰撞 Box。

Collision 必须限制在自己的 Cell 内。

### 6.6 结构连接只看 Grid

Voxel 之间的结构连接完全根据六面邻接判断：

```text
±X
±Y
±Z
```

不检查：

* Mesh 是否接触；
* Collider 是否接触；
* 物理引擎是否认为发生碰撞。

---

# 7. VoxelDefinition

所有可实体化物品共享统一的：

```text
VoxelDefinition
```

现有独立的 Good/货物 Definition 最终应逐步并入这一体系。

建议首版核心字段：

```text
VoxelDefinition
├─ DefinitionId
├─ Value
├─ MaxDurability
├─ Mass
├─ Tags
├─ Behaviors
├─ Presentation
└─ Collision
```

## 7.1 Value

`Value` 为游戏开始时定义的**恒定基础价值**。

例如：

```text
Iron Ore
Value = 100
```

Value 不随世界经济变化。

用于：

* 基础经济计算；
* Composite 断裂后的身份继承；
* 其他稳定估值。

## 7.2 Price

Price 不属于 VoxelDefinition，也不属于 VoxelInstance。

Price 是：

```text
MarketState × VoxelDefinition
```

产生的 Runtime Quote。

例如：

```text
Mining Town:
Iron Ore = 70

Capital:
Iron Ore = 135
```

## 7.3 Mass

Mass 为 Voxel 的基础质量。

未来用于：

* Composite 总质量；
* 列车动力学；
* 碰撞损伤；
* 其他 Gameplay 计算。

## 7.4 Material

首版**没有通用 MaterialDefinition 系统**。

只有当实际开发中大量 Voxel 重复相同材料规则时，再抽象 Material 层。

---

# 8. 库存与实体化

库存中的商品数量不是空间 Voxel Instance。

例如：

```text
Cargo Stack
VoxelDefinitionId = core:iron_ore
Quantity = 500
```

表示 500 个可实体化的 Iron Ore 单位。

只有被放置进入世界时才产生 Voxel Instance。

普通无特殊 Runtime State 的 Voxel 被收纳时，可以转化为 Stack。

未来复杂设备可能需要保存：

* Durability；
* Behavior State；
* Voxel Runtime ID；

届时使用独立 Item Instance，而不是 Stack。

---

# 9. Definition、Revision 与 Instance

严格区分：

```text
Definition
Revision
Instance
```

## 9.1 Definition

定义“某种东西是什么”。

## 9.2 Revision

Definition 的一个不可变语义版本。

内部 Revision ID 使用：

> **Semantic Content Hash**

而不是单纯：

```text
r1
r2
r3
```

完全相同的语义内容产生相同 Revision。

UI 可额外维护纯数字的人类版本号：

```text
Version 1
Version 2
Version 3
```

它只用于展示，不承担身份职责。

## 9.3 Instance

运行时实例保存：

* 当前 Durability；
* Behavior Runtime State；
* Ownership；
* 当前几何变化；
* 其他 Runtime 信息。

Definition 永远不保存实例状态。

---

# 10. Definition 来源

Definition 分为四类：

```text
Definitions
├─ Built-in
├─ Mod
├─ Player Blueprint Library
└─ Save-local Definitions
```

玩家在某个存档内设计的新版本默认只进入：

```text
Save-local Definitions
```

只有显式：

```text
Save as Blueprint
```

才复制/发布到：

```text
Player Blueprint Library
```

其他存档不会自动看到 save-local revision。

Save-local Definition 必须直接包含在存档中，使 Save 自包含。

---

# 11. ID 与命名空间

继续采用稳定的 namespaced identity：

```text
namespace:local-id
```

例如：

```text
core:iron-ore
mod-namespace:titanium-engine
```

文件路径不是 Definition 身份。

建议不同领域继续保持强类型 ID：

```text
VoxelDefinitionId
CompositeDefinitionId
CompositeArchetypeId
BehaviorTypeId
...
```

它们共享：

* Namespace 规则；
* Parser；
* Normalize；
* Validation；

但不统一成一个完全无类型的 DefinitionId。

---

# 12. Mod 与热加载

## 12.1 第一阶段：Data Mod

支持：

* 新增 VoxelDefinition；
* 新增 Composite Definition；
* 新增 Layout；
* 修改数值；
* 引用资产；
* 组合已有 Behavior。

Mod 第一阶段不能直接注入任意 C# 代码。

## 12.2 第二阶段：Code Mod

等游戏规则和公开 API 足够稳定后，再考虑：

* C# 插件；
* 注册新 Behavior；
* 注册新 System；
* 公共 Mod API。

## 12.3 Hot Reload

语义 Definition 热加载：

```text
旧 Revision
+
新 Revision
```

而不是修改旧 Revision。

Presentation Asset 热加载与 Gameplay Revision 分离。

修改：

```text
Texture
Mesh
Material
Sound
```

可以立即影响引用该 AssetId 的现有实例。

修改：

```text
Durability
Behavior
Voxel Layout
Engine Power
```

则产生新的 Semantic Revision。

---

# 13. Composite

Composite 是由多个 Voxel 构成的具备独立身份的游戏对象。

例如：

* 车头；
* 车厢；
* 建筑；
* 复杂机器。

结构：

```text
CompositeInstance
├─ InstanceId
├─ DefinitionRevision
├─ Archetype
├─ Transform
└─ Owned Voxels
```

Composite 内部 Voxel 坐标永远是整数 Local Grid。

Composite 本身可以拥有连续 World Transform。

因此：

```text
Voxel local position → integer
Composite transform  → continuous
```

火车和生物可以平滑移动，而不会破坏 Voxel Grid。

---

# 14. Composite 不允许递归嵌套

Composite 只能直接拥有 Voxel。

更高层对象使用 Gameplay Assembly。

例如：

```text
Train
├─ Locomotive Composite
├─ Carriage Composite
└─ Carriage Composite
```

Settlement：

```text
Settlement
├─ Building Composite
├─ Building Composite
└─ World Voxels
```

避免建立无限递归 Composite Tree。

---

# 15. Composite 合法性

Composite 使用数据驱动的：

```text
CompositeArchetype
```

描述规则。

合法性分成三个独立概念。

## 15.1 Construction Validity

回答：

> 这个设计是否能够作为这种 Composite 被制造？

例如 Locomotive 可能需要：

* propulsion；
* traction；
* control；
* 六面连通；
* 合法 Rail Contact；
* 满足车辆净空要求。

## 15.2 Runtime Identity

回答：

> 已经制造出来的 Composite 是否仍然保持原身份？

使用独立的数据驱动：

```text
IdentityRetentionRules
```

Construction Rules 和 Identity Retention Rules 不共用。

## 15.3 Operational Condition

回答：

> 当前还能执行哪些功能？

由 Runtime Voxel、Behavior 和 Capability 推导。

可能产生：

```text
Operational
Degraded
Disabled
Destroyed
```

FSM 只解释高层生命周期，不复制底层真实状态。

---

# 16. Composite Draft 与玩家设计

玩家可以自由设计：

* 车头内部；
* 车头外观；
* 车厢；
* 功能设备；
* Voxel 布局。

设计器允许保存非法 Draft。

流程：

```text
Draft
↓ validate
Valid Definition
↓
Instantiate
```

只有合法 Definition 可以制造。

---

# 17. Refit

玩家修改已经存在的 Composite 时：

```text
Instance #100
Definition r17
↓ Refit
Definition r18
Instance #100
```

Instance Identity 保持。

只有真正拆除并重新制造才创建新 Instance ID。

Refit 首版采用：

> **Atomic Transaction**

不会让施工中间状态暴露给 Simulation。

Refit 必须追踪真实 Voxel Identity。

移动同一个 Engine：

```text
old cell → new cell
```

仍然是同一个 Engine。

它的：

* Durability；
* Temperature；
* Behavior State；

不会被重置。

---

# 18. Voxel Ownership

任意 Voxel Instance 同一时间只有一个空间所有者：

```text
World Grid
或
Composite
```

不能同时属于两者。

发生脱落时执行：

```text
Ownership Transfer
```

而不是复制实例。

普通 World Voxel 不要求全部拥有 Runtime ID。

需要长期个体身份的 Voxel 才分配：

```text
VoxelRuntimeId
```

例如：

* Composite 内设备；
* 被其他系统引用的部件；
* 拥有复杂 Runtime State 的个体。

对于拥有 RuntimeId 的 Voxel：

```text
RuntimeId = identity
Coordinate = location
```

坐标不能成为长期身份引用。

---

# 19. Composite 连通与断裂

合法 Composite 必须是严格的六面连通体。

Runtime 中也要求：

> 一个 Composite 始终只拥有一个 Connected Component。

当一次 Damage Transaction 造成结构断裂：

```text
Composite
↓
Connected Components
```

每个 Component 暂时作为同类型 Composite 候选进行：

```text
IdentityRetentionRules
```

判断。

结果：

```text
0 个存活
→ Composite destroyed

1 个存活
→ 保留 Composite

N 个存活
→ 产生 N 个独立 Composite
```

---

# 20. 断裂后的身份继承

如果存在多个 surviving fragments：

1. 计算各 Fragment 中所有 VoxelDefinition.Value 之和；
2. Value 最大的 Fragment 成为 Primary Successor；
3. Primary 继承原 Composite InstanceId；
4. 其他 Survivor 获得新的 Runtime InstanceId；
5. Value 完全相同时，通过 WorldSeed 派生的确定性随机规则打破平局。

所有 surviving fragments：

* 保留原 Definition Revision；
* 保留自身 Voxel Runtime State；
* 保留 faction/provenance 等可继承属性；
* 重新计算 Capability 与 Operational Condition。

Identity-level 外部引用，例如：

* Train Assembly membership；
* UI Selection；
* AI Order；
* Route Assignment；

默认只由 Primary 继承。

Secondary 成为独立对象。

---

# 21. Composite 死亡与解体

无法满足 IdentityRetentionRules 的 Fragment：

```text
Composite ownership
↓
World ownership
```

Voxel Runtime State 保留。

不会自动扫描这些 Voxel 并尝试组成另一个 Composite。

Composite Creation 必须来自显式行为：

* World Generator；
* 玩家 Construction；
* AI Construction；
* Blueprint Instantiation；
* Composite Fracture。

世界绝不会根据 Voxel 排列“猜测”一个 Composite。

---

# 22. Behavior、Tag 与 Capability

三者严格分离。

## Tag

用于被动分类：

```text
metal
structural
organic
...
```

## Behavior

描述真正的逻辑机制：

```text
EngineBehavior
WeaponBehavior
StorageBehavior
HeatBehavior
...
```

## Capability

Behavior 对外提供的能力：

```text
Propulsion
Traction
Cargo
Weapon
Power
...
```

Capability 不允许在 Mod JSON 中任意伪造。

它由实际注册的 Behavior 推导。

---

# 23. Behavior 架构

采用：

> **Behavior Registry + Composition**

避免：

```text
Voxel
└─ MechanicalVoxel
   └─ EngineVoxel
      └─ DieselEngineVoxel
```

建议：

```text
Voxel
├─ EngineBehavior
├─ FuelConsumerBehavior
└─ HeatBehavior
```

Behavior 是组件语义。

System 是执行语义。

例如：

```text
EngineSystem
HeatSystem
WeaponSystem
StorageSystem
```

负责批量运行对应 Runtime State。

不为每一个 Voxel 创建 Node 或独立 Tick。

---

# 24. Simulation Clock

Simulation 拥有独立于 Rendering FPS 的逻辑时钟。

Godot 仅提供 Host：

```text
Godot SimulationHost
↓
SimulationClock
↓
WorldSimulation
```

Simulation 不依赖：

* SceneTree 中是否存在某对象；
* 当前 FPS；
* Camera 是否看到某区域；
* 窗口是否获得焦点。

首版：

```text
Pause
1×
```

架构预留：

```text
2×
4×
...
```

但在系统稳定前不开放倍速 Gameplay。

---

# 25. Deterministic Simulation

Determinism 是核心架构不变量。

目标：

```text
同一游戏版本
+
同一存档
+
同一输入
+
兼容运行环境
=
相同 Simulation Result
```

当前不要求不同 CPU/操作系统之间逐 bit 完全一致。

所有能够改变 World State 的遍历顺序必须显式稳定。

不能让以下因素决定 Gameplay：

* Dictionary 枚举顺序；
* HashSet 枚举顺序；
* Behavior 注册顺序；
* Thread completion order；
* Node order；
* 非确定性 Async completion。

---

# 26. Simulation Tick Pipeline

首版采用固定 Phase：

```text
Simulation Tick

1. Apply Commands

2. Behavior Evaluation
   - 读取 committed state
   - 产生 Request / Intent

3. Resolve
   - 共享资源竞争
   - Gameplay priority
   - deterministic fallback

4. Commit

5. Structural Resolution
   - destroyed voxels
   - connectivity
   - fragmentation
   - ownership transfer

6. Derived State
   - capabilities
   - operational condition

7. Domain Events
```

Behavior 注册顺序永远没有游戏语义。

---

# 27. Resolver

多个行为竞争共享资源时：

```text
Request
↓
Resolver
↓
Commit
```

例如：

```text
Engine A → RequestFuel
Engine B → RequestFuel
```

Resolver 采用：

```text
Explicit Gameplay Priority
↓
Deterministic Fallback
```

而不是“谁先执行谁得到”。

---

# 28. Transaction

所有修改世界状态的顶层行为拥有：

```text
TransactionId
```

World 内单调递增。

用于：

* Debug；
* 日志；
* Deterministic Random Key；
* 事件索引；
* 未来 Replay 辅助。

Transaction 不采用完整 Event Sourcing。

当前 World State 才是 authoritative truth。

---

# 29. Damage Transaction

同一逻辑 Damage Event 内的所有破坏首先统一应用：

```text
Damage Event
↓
apply damage
↓
remove hp <= 0
↓
mark topology dirty
↓
connectivity calculation
↓
fragmentation
```

不能：

```text
destroy one voxel
→ fracture
destroy another
→ fracture again
```

否则结果可能依赖遍历顺序。

---

# 30. Damage

首版只有统一：

```text
Durability
```

Voxel：

```text
CurrentDurability
MaxDurability
```

当：

```text
CurrentDurability <= 0
```

Voxel 直接消失。

视觉上使用：

* 碎片；
* 火花；
* 烟尘；

表现破坏。

首版不建立大型：

```text
kinetic / fire / electric / corrosion / ...
```

Damage Type Matrix。

复杂损伤机制未来通过 Behavior 增加。

---

# 31. World Voxel 结构稳定性

首版 World-owned Voxel 不拥有通用结构支撑模拟。

因此挖掉下面的体素：

```text
███
   
```

上面的普通 Voxel 可以悬空。

只有明确具有：

```text
GravityBehavior
FallingBehavior
```

的特殊 Voxel 才会下落。

Composite 则继续使用完整六面连通与 Fragmentation。

---

# 32. Randomness

所有随机性来自：

```text
WorldSeed
```

但不存在一个全局唯一、不断消费的 `Random` 实例。

采用两类随机。

## 32.1 Stateless Keyed Random

默认方案。

适合：

* 事件一次性随机；
* Damage tie-break；
* AI 决策点；
* World generation cell；
* Loot；
* 程序装饰。

形式：

```text
HashRandom(
    WorldSeed,
    SystemKey,
    EventId,
    EntityId
)
```

相同 Key 永远得到相同结果。

## 32.2 Stateful RNG Stream

只有当“随机序列推进本身具有领域意义”时使用。

例如某个持续 stochastic process。

Stream 尽可能局部化：

```text
AIStream(EntityId)
MarketStream(SettlementId)
```

Stateful RNG State 必须进入 Save。

---

# 33. Runtime Instance ID

运行时对象使用存档内唯一的确定性整数 ID：

```text
NextEntityId : ulong
```

不使用随机 UUID。

ID 分配必须发生在确定性的 Commit 顺序中。

跨存档 Definition 身份继续使用 Namespaced ID。

---

# 34. Persistence

首版存档逻辑：

```text
Save
├─ SaveFormatVersion
├─ WorldSeed
├─ GeneratorVersion
├─ Runtime Delta
├─ Stateful RNG States
├─ Next Transaction ID
├─ Next Entity ID
└─ Save-local Definitions
```

基础世界通过：

```text
Seed + GeneratorVersion
```

重建。

之后应用 Runtime Delta。

后期如果加载性能出现瓶颈：

```text
Snapshot
+
Delta
```

作为优化。

首版不做完整 Event Sourcing。

---

# 35. 存档兼容

开发阶段：

> 不保证存档兼容。

但所有主要格式从现在开始保留 Version 字段。

进入正式发行阶段后：

> 存档向前迁移成为硬性约束。

旧 Save 不能因为 Generator 更新而偷偷使用新算法重新解释世界。

届时可采用：

* 保存旧 Generator；
* Migration；
* Snapshot；

等方式保证兼容。

---

# 36. World Generation

世界采用：

> **程序生成 + 手工 Blueprint/Layout**

而不是固定手工地图。

世界生成必须使用 Seed 可复现。

同：

```text
WorldSeed + GeneratorVersion
```

必须产生同样的初始世界。

不同 GeneratorVersion 不要求得到相同地图。

---

# 37. Generation Pipeline

世界生成采用确定性的分阶段 Pipeline：

```text
WorldSeed
↓
Terrain
↓
Resource Potential
↓
Actual Resources
↓
Settlement Candidates
↓
Settlement Archetypes
↓
Logical Settlement Nodes
↓
Logical Rail Graph
↓
Layout Selection + Rotation
↓
Settlement Materialization
↓
Physical Rail Pathfinding
↓
Decoration
↓
Validation
```

每个 Stage 使用独立派生 Seed：

```text
Hash(
 WorldSeed,
 GeneratorVersion,
 StageId,
 RetryIndex
)
```

后续阶段新增 RNG 调用不能改变前面阶段结果。

每个 Stage：

```text
Generate
↓
Validate
↓
Success / Retry
```

Retry 必须有明确上限。

生成器允许返回：

```text
WorldGenerationFailed
```

不能无限循环。

---

# 38. Resource Generation

采用：

```text
Resource Potential Field
↓
Actual Resource Voxels
```

Potential Field 用于宏观判断：

* 哪些区域矿产丰富；
* Settlement 更适合发展什么经济。

Settlement 类型原则上响应自然资源，而不是为了 Settlement 类型强行生成资源。

允许少量 Gameplay Validation 和保证性调整。

---

# 39. Settlement

Settlement 本身不是 Composite。

结构：

```text
SettlementInstance
├─ Economy
├─ Population
├─ Faction
├─ Position
├─ Buildings
└─ optional Station
```

## 39.1 SettlementArchetype

负责 Gameplay：

* 经济；
* 资源偏好；
* 生产；
* 消费；
* 其他规则。

## 39.2 SettlementLayoutDefinition

负责空间布局：

* Building Composite；
* World Voxel；
* Decoration；
* Site Constraints；
* optional Station/Rail Socket。

经济类型与外观 Layout 严格分离。

---

# 40. Settlement 与铁路

严格区分：

```text
Settlement exists
≠
Station exists
≠
Rail connection exists
```

首版世界生成可以要求：

> 所有参与自动铁路贸易的 Settlement 都有 Station 并接入 RailNetwork。

但底层允许未来存在未通铁路的 Settlement。

RailSocket 是 Station/铁路接入能力的合法性要求，而不是 Settlement 身份本身的必要条件。

---

# 41. Layout Placement

手工 Layout 首版允许：

```text
0°
90°
180°
270°
```

不默认允许：

* Mirror；
* Arbitrary Rotation；
* Scale。

Layout 拥有：

```text
Site Constraints
```

例如：

* footprint；
* 最大高度差；
* biome/tag 要求；
* forbidden terrain。

生成器允许有限、确定性的 Site Preparation：

* 清理装饰；
* 小范围填土；
* 小范围挖平。

超过约束则拒绝 Candidate。

---

# 42. Railway：设计地位

铁路与火车的机械运行是游戏核心视觉体验之一。

即使首版铁路控制系统被简化，也不能把以下问题视为可以永久接受的临时表现：

* 火车突然转向；
* 轨道接缝明显；
* 车厢切角；
* 坡道姿态跳变；
* 火车穿过错误道岔；
* 长车厢明显脱离轨迹。

铁路拥有独立 Domain 与 Presentation 系统。

---

# 43. Rail Voxel

首版只有一种基础：

```text
core:rail
```

但长期采用混合模型：

```text
Rail Definition
= 轨道的种类、属性与能力

Track Topology
= 当前实例的空间连接
```

未来可以新增：

```text
heavy rail
high-speed rail
...
```

但：

* 直线；
* 曲线；
* 坡道；
* 道岔；
* 交叉；

不作为独立 Definition。

---

# 44. Track Topology

不使用不断膨胀的简单 Shape Enum 作为长期核心。

轨道采用：

```text
Track Ports
+
Internal Connections
```

例如：

### Straight

```text
Ports:
West
East

Connection:
West ↔ East
```

### Curve

```text
North
East

North ↔ East
```

### Crossing

```text
N ↔ S
E ↔ W
```

### Switch

```text
Possible:
West ↔ East
West ↔ North
```

首版实际只需要：

* Straight；
* Curve；
* 标准坡轨。

---

# 45. Rail RouteState

区分：

```text
TrackTopology
```

和：

```text
RouteState
```

Topology：

> 理论上可以怎样连接。

RouteState：

> 当前实际允许怎样连接。

未来道岔因此可以拥有真实 Runtime State、动画、故障和控制逻辑。

---

# 46. Physical Rail 与 Logical Rail Network

Runtime 中：

> **Physical Rail Voxel 是铁路事实来源。**

Logical Rail Graph 是派生高层索引。

轨道被破坏：

```text
Physical Rail change
↓
mark rail network dirty
↓
incremental graph update
```

Logical Graph 不允许长期保留不存在的连接。

---

# 47. Compressed Rail Graph

不会把每个 Rail Voxel 当 Graph Node。

只有重要位置作为 RailControlPoint：

首版：

* Station；
* Junction；
* Endpoint。

未来：

* Signal；
* Block Boundary；
* Special Track Device。

连续普通轨道压缩成 Edge：

```text
Control Point
↓
Compressed Edge
↓
Control Point
```

Edge 记录/引用实际 Physical Rail Path。

---

# 48. Rail Operational Layer

物理轨道描述几何可能性。

单向、封锁、信号、Reservation 等属于：

```text
Operational Rail Layer
```

例如：

```text
Physical:
A ↔ B

Operational:
A → B allowed
B → A forbidden
```

首版默认所有线路双向。

架构预留：

* 单向；
* 信号；
* Block；
* Reservation；
* Occupancy；
* 调度。

---

# 49. Rail Construction

世界生成器、玩家和 AI 最终统一调用：

```text
RailConstructionPlan
↓
Validate
↓
RailEditTransaction
↓
World Voxel Changes
↓
Rail Graph Update
```

首版只有 Generator 使用。

未来加入：

* 玩家铺轨；
* AI 铺轨；
* 地形破坏施工；

时不建立另一套铁路修改规则。

---

# 50. 铁路净空

游戏存在统一的：

```text
Track Gauge
Loading Gauge / Clearance Envelope
```

具体数值由测试确定。

玩家设计的车辆必须满足：

* 合法 Rail Contact；
* 净空限制；
* 车辆几何约束。

RailConstructionPlan 同样验证整条线路的车辆 Swept Clearance，而不只检查 Rail Cell 本身。

---

# 51. 桥梁和隧道

Bridge/Tunnel 与 Rail Definition 正交。

例如：

```text
Bridge Structure
+
Rail Voxels
```

而不是：

```text
bridge_rail
```

隧道也是：

```text
Terrain excavation
+
Tunnel structure
+
Rail
```

桥梁可以没有铁路，铁路也可以不在桥梁上。

首版世界生成器不自动生成复杂桥梁和隧道。

---

# 52. Rail Movement

列车是：

> **Path-constrained / Kinematic Entity**

而不是 RigidBody 驱动。

权威状态使用：

```text
RailPosition
├─ Rail Path / Edge
├─ DistanceAlongPath
├─ Direction
└─ future lane/track info
```

World Transform 是派生状态。

---

# 53. 多接触点车辆约束

车辆不使用单个中心点切线决定姿态。

Composite Definition 定义：

```text
Rail Contact / Bogie Anchors
```

首版至少：

```text
Front Contact
Rear Contact
```

分别计算对应 Track Path Point。

车体位置与姿态由多个接触点共同决定。

这是铁路视觉核心要求。

---

# 54. 碰撞

正常列车运动不使用刚体物理求解。

发生事故：

```text
Movement
↓
Swept Collision Detection
↓
Collision Event
↓
Deterministic Collision Resolver
↓
Damage Transaction
```

Godot Physics 可以辅助检测，但不成为正常列车位置和 Gameplay Damage 的权威来源。

视觉层可以自由播放非确定性的粒子碎片，只要不改变 Simulation 结果。

---

# 55. 动态 Voxel 脱落

移动 Composite 中 Voxel/Fragment 脱落后，需要从连续空间回到整数 World Grid。

采用：

> **逻辑 Snap + 视觉动画补偿**

对于整个 Fragment：

1. 获取当前连续 World Transform；
2. Rotation Snap 到最近正交方向；
3. Anchor Snap 到 World Grid；
4. 根据 Local Grid 得到全部目标 Cell；
5. 检查冲突；
6. 在有限半径内按确定性顺序寻找完整可放置位置；
7. 成功后整体 ownership transfer；
8. 如果无法完整放置则销毁；
9. Presentation 播放自然的坠落/碎裂动画。

不逐 Voxel 独立 round。

---

# 56. Rendering 总体策略

领域世界和表现路径分开。

Presentation 分为三路：

```text
Voxel Presentation
├─ Cube
├─ CustomStaticMesh
└─ Specialized
```

---

# 57. Cube Voxel Rendering

首版：

```text
Chunk
↓
Face Culling
↓
Chunk Surface Mesh
```

只生成暴露面。

不使用：

```text
one MeshInstance per voxel
```

后期如果性能需要：

```text
Greedy Meshing
```

作为 `ChunkMesher` 可替换优化。

Greedy Meshing 不改变 World/Chunk 语义。

---

# 58. Custom Static Mesh

Custom Mesh Voxel 按：

```text
Chunk
+
Mesh
+
Material
```

进行局部批处理/实例化。

首版只要求 LOD0。

未来 Custom Mesh LOD 属于 Presentation Asset Metadata，而不属于 Gameplay Definition。

普通 Custom Mesh 的视觉主体应基本限制在自己的 Cell 内。

需要真正跨 Cell 的视觉结构进入 Specialized Renderer。

---

# 59. Specialized Renderer

需要领域专用视觉的 Voxel 使用独立 Renderer。

首要例子：

```text
TrackRenderer
```

Rail 虽然仍然是 Voxel，但视觉几何由：

```text
Track Topology
↓
Continuous Track Geometry
```

构建。

这样可以正确表现：

* 无缝直轨；
* 弯道；
* 坡道；
* 未来道岔；
* 交叉；
* 多轨结构。

---

# 60. Track Render Section

TrackRenderer 不为整个几公里 Edge 建一个巨大 Mesh。

拆成局部：

```text
TrackRenderSection
```

可按照：

* Chunk Boundary；
* Control Point；
* Junction；

等自然边界切分。

局部轨道变化只重建受影响 Section。

---

# 61. Composite Rendering

Composite-owned Voxel 不进入 World Chunk Mesh。

无论 Composite 是否移动，首版统一：

```text
CompositeVisual
├─ Cube Batch
├─ Custom Mesh Batch
└─ Specialized Visuals
```

移动时：

```text
update root Transform only
```

绝不因为移动重建内部 Mesh。

只有：

* Damage；
* Refit；
* Voxel Layout Change；

才标记内部 Visual Dirty，并批量重建。

未来可以针对大量静态 Composite 增加 Presentation-only Baking 优化。

---

# 62. Dirty Rendering

世界变化不逐 Voxel 立即重建 Mesh。

采用：

```text
World changes
↓
mark affected Chunk dirty
↓
coalesce
↓
one rebuild
```

Chunk 边界修改要同时考虑相邻 Chunk 的暴露面变化。

首版可以同步生成 Mesh。

出现实际卡顿后，再考虑异步 Meshing。

---

# 63. Visual Streaming

World Data 当前暂定完整常驻内存。

“不加载”主要表示：

> 不构建/不保留当前区域的视觉表示。

采用：

```text
Visual Residency
+
Frustum Visibility
```

Resident 区域使用：

```text
Load Radius < Unload Radius
```

形成 hysteresis，避免边界反复 Load/Unload。

同时根据：

```text
Train route + velocity
```

做 directional prefetch。

不根据 Camera 朝向决定主要预取方向。

---

# 64. LOD

首版不实现真正的 Voxel Geometry LOD。

首版依靠：

* Residency；
* Frustum Culling；
* Distance Culling；
* Custom Mesh LOD0。

后期性能需要时再增加：

* Greedy Meshing；
* Custom Mesh LOD；
* Voxel Geometry LOD。

LOD 永远只改变 Presentation，不改变 Simulation。

---

# 65. Render Origin

Domain 使用绝对世界坐标。

例如：

```text
WorldVoxelCoord = (183421, 42, 91822)
```

Godot Presentation 使用局部 Render Origin：

```text
RenderPosition =
AbsolutePosition - RenderOrigin
```

火车移动到远处后可以重新 Rebase RenderOrigin。

Rebase 只移动 Visual Proxy。

以下内容永远不变化：

* World Voxel Coordinate；
* RailPosition；
* Settlement Position；
* Save State；
* Seed；
* Simulation。

首版逻辑单位：

```text
1 Voxel Unit = 1 Godot Unit
```

但概念上保持解耦。

---

# 66. Collision Rendering / Adapter

Godot Collider 属于派生缓存。

```text
World Voxel Data
↓
Chunk Collider Builder
↓
Godot Collision
```

Voxel 被修改后标记 Collision Cache dirty。

World Model 不保存 Godot CollisionShape 作为事实。

---

# 67. Camera

主 3D View 使用：

> **Perspective Camera**

Camera Focus 永远绑定：

* Locomotive；
* 或某个 Carriage。

不支持普通自由飞行。

允许：

* 360° Horizontal Yaw；
* 受限 Vertical Pitch；
* Distance Zoom。

Zoom 只改变 Camera Distance。

FOV 基本固定。

---

# 68. Camera Follow

基础方向跟随当前列车 Heading：

```text
Camera Orientation
=
Train Heading
+
Player Orbit Offset
```

火车转弯时 Camera 平滑跟随。

玩家改变的是：

> 相对于列车的观察位置。

---

# 69. Camera Target Switch

玩家切换：

```text
Locomotive
→ Carriage #1
→ Carriage #7
```

使用平滑 Transition。

不会瞬间 Teleport。

如果当前 Focus Target 被摧毁，使用明确 fallback 选择同一 Train 中的有效目标。

具体优先级通过实际 UI 设计确定。

---

# 70. Camera Collision

首版不采用传统第三人称 Spring Arm。

Camera 不因为普通建筑/地形持续自动 Zoom In/Out。

未来如果遮挡问题明显，可考虑：

* Obstacle Fade；
* Camera framing correction。

---

# 71. 窗口与 Simulation 生命周期

3D 主窗口：

```text
Primary Window
```

辅助 UI 为 Secondary Window。

Simulation 与窗口状态严格解耦。

以下情况 Simulation 均继续：

* 主窗口失焦；
* 用户正在使用 IDE；
* Floating View 被其他窗口遮挡；
* 打开 Management UI；
* 主窗口最小化。

主窗口最小化时：

> Rendering 可以停止或显著降低，但 Simulation 不停止。

---

# 72. Pause

只有显式 Pause 才停止：

```text
SimulationClock
```

Pause 时仍然允许：

* UI；
* Camera；
* Window；
* Design 操作；
* Rendering。

首版没有 Offline Progress。

关闭游戏：

```text
Simulation stops
```

未来可作为独立 Feature 实现 Offline Progress/Bulk Simulation。

---

# 73. Docked / Floating

## Docked

首版：

* Bottom only；
* 占满目标 Monitor Width；
* Height adjustable；
* 占据 Desktop Work Area。

## Floating

* 任意移动；
* 合理范围调整尺寸；
* 不预留 Desktop Work Area；
* Always-on-top 用户可选；
* 默认关闭 Topmost。

---

# 74. Godot / C# 边界

Domain Model 使用普通 C# 类型。

不采用：

```text
VoxelInstance : Node3D
CompositeInstance : Node3D
```

核心领域：

```text
VoxelDefinition
Voxel runtime data
CompositeDefinition
CompositeInstance
World
Chunk
Simulation
RailNetwork
...
```

都不依赖 SceneTree 生命周期。

Godot 负责：

* Rendering；
* Input；
* Audio；
* Windows；
* UI；
* Asset Loading；
* Physics Adapter。

---

# 75. Asset 引用

Domain Definition 不直接依赖实际 Godot Resource 作为核心身份。

使用：

```text
AssetId
```

例如：

```text
MeshAssetId
TextureAssetId
MaterialAssetId
```

Presentation Adapter 根据需要解析：

```text
AssetId
↓
Godot Resource
```

只有实际需要渲染某区域时才加载视觉资产。

---

# 76. Domain Event 与 Godot Signal

Simulation 内部使用普通 C# Domain Event / Message。

Godot Signal 只存在于引擎/UI Adapter。

例如：

```text
VoxelDestroyed
CompositeConditionChanged
CapabilityChanged
```

由 Domain 发出。

Adapter 可以转换成：

```text
Godot Signal
```

但 Domain 不为了 Signal 而继承 GodotObject。

---

# 77. 当前首版明确实现范围

首版目标集中于完成核心闭环。

## 必须

* 有限 3D Voxel World；
* VoxelDefinition；
* JSON/Data Mod；
* Definition/Revision/Instance；
* World Chunk；
* Composite；
* 玩家 Blueprint；
* Construction Validation；
* Behavior + Capability 基础；
* Deterministic Simulation；
* Runtime Delta Save；
* Seeded World Generation；
* Settlement Layout；
* 基础 Rail Network；
* Straight/Curve/Standard Slope；
* Continuous Train Movement；
* Multi-contact Vehicle Constraint；
* TrackRenderer；
* Chunk Rendering；
* Dynamic Composite Rendering；
* Visual Streaming；
* Bottom Dock Window；
* Perspective Train Camera。

---

# 78. 明确延后 Feature

以下系统不是当前欠缺，而是刻意延后：

```text
Code Mod API
Offline Progress
Simulation LOD
Greedy Meshing
Voxel Geometry LOD
通用 Material System
复杂 Damage Types
World structural collapse
复杂重力系统
玩家铁路施工 UI
AI 铁路施工
复杂桥梁生成
复杂隧道生成
Signal
Block System
Reservation
Interlocking
复杂道岔控制
单双线运营规则
复杂车辆交通调度
Snapshot Save Optimization
正式版本 Save Migration
```

架构不能主动阻止这些系统未来加入，但首版不为其支付完整实现成本。

---

# 79. 暂不冻结的参数

以下项目必须通过实际开发确定：

* Chunk Size；
* World Size；
* Simulation Tick Rate；
* Camera FOV；
* Zoom Min/Max；
* Render Residency Radius；
* Unload Radius；
* Directional Prefetch Distance；
* Rail Gauge；
* Loading Gauge；
* 最大 Blueprint 尺寸；
* World Generation Retry Count；
* Meshing Threshold；
* Custom Mesh LOD Distance；
* Voxel Geometry LOD Distance；
* Window Default Height。

这些内容应进入：

```text
Config / Tuning
```

而不是成为架构不变量。

---

# 80. 核心不变量汇总

实现过程中，应优先保护以下不变量：

1. **一个 Voxel = 一个 Cell。**
2. **一个 Cell 最多一个 Voxel。**
3. **Voxel Local Coordinate 永远为整数。**
4. **Voxel Orientation 永远为正交方向。**
5. **Voxel 同一时间只有一个 Owner。**
6. **Composite Voxel 始终形成一个六面连通体。**
7. **Definition 是不可变数据。**
8. **Runtime State 不写回 Definition。**
9. **Semantic Revision 与 Presentation Asset Revision 分离。**
10. **位置不是持久身份。**
11. **World Model 不依赖 SceneTree。**
12. **Physical Rail 是铁路 Runtime Truth。**
13. **Logical Rail Graph 是派生索引。**
14. **RailPosition 是列车权威位置，World Transform 是派生结果。**
15. **Rendering/Collider 都是 World State 的派生缓存。**
16. **Simulation 与 FPS、Camera、Window Focus 解耦。**
17. **注册顺序、容器枚举顺序、线程完成顺序不得决定 Gameplay。**
18. **所有 Gameplay Random 最终源于 WorldSeed。**
19. **能够使用 Stateless Random 的场景默认不维护 RNG State。**
20. **Stateful RNG State 必须持久化。**
21. **Damage/Refit/Construction 等结构修改按 Transaction 原子结算。**
22. **Composite Creation 永远来自显式 Gameplay 行为。**
23. **Price 属于 Market Context，不属于 Voxel。**
24. **Inventory Quantity 不等于空间 Voxel Instance。**
25. **首版性能优化必须服从 Profiling，而不是提前猜测。**

---

# 81. 推荐首版实现顺序

为了保持敏捷开发，建议按照能够尽快形成可运行闭环的顺序推进。

## Phase 1：Voxel Domain

建立：

```text
VoxelDefinition
VoxelDefinitionId
RevisionId
Voxel storage
Chunk
Ownership
Durability
```

将现有 Good/Data Mod 系统逐渐迁移到统一 VoxelDefinition 模型。

---

## Phase 2：World Rendering

完成：

```text
Chunk
↓
Face Culling Mesher
↓
Chunk Mesh
```

同时建立：

* Custom Static Mesh batch；
* AssetResolver；
* Dirty Chunk。

目标：

> 可以在 3D View 中稳定显示并破坏一个较大的 Voxel World。

---

## Phase 3：Composite

完成：

* CompositeDefinition；
* CompositeInstance；
* Local Voxel Grid；
* Archetype；
* Construction Validation；
* ownership；
* CompositeVisual；
* 基础 Damage/Fragmentation。

目标：

> 可以制造、移动、破坏一个由 Voxel 构成的完整车辆结构。

---

## Phase 4：Deterministic Simulation

完成：

```text
SimulationClock
Command
Transaction
Evaluate
Resolve
Commit
Structural Resolution
Domain Event
```

建立 Determinism 测试。

目标：

> 相同 Save + Input 可以稳定得到相同结果。

---

## Phase 5：World Generation

完成：

* Seed；
* GeneratorVersion；
* Terrain；
* Resources；
* Settlement placement；
* Layout；
* 基础 Rail Graph；
* Physical Rail。

目标：

> 给定 Seed 可以生成可重复、合法、可驾驶的世界。

---

## Phase 6：Railway Core

完成：

* Rail Ports/Internal Connections；
* Straight；
* Curve；
* Standard Slope；
* compressed graph；
* continuous path；
* RailPosition；
* multi-contact carriage movement；
* TrackRenderer。

这是首版视觉品质最需要重点投入的阶段之一。

---

## Phase 7：Desktop Product Form

完成：

* Primary 3D Window；
* Bottom Dock；
* Floating；
* Display selection；
* Focus behavior；
* Perspective Camera；
* Orbit；
* Zoom；
* target switching；
* minimized rendering suspension。

目标：

> 游戏能够真正作为桌面常驻放置游戏运行。

---

## Phase 8：Save / Runtime Delta

完成：

* Save-local Definition；
* Runtime Delta；
* RNG State；
* IDs；
* World reconstruction。

首版达到：

> 正常 Save → Quit → Load → Continue。

---

# 82. 设计指导思想

本项目不追求建立一个理论上最通用的 Voxel Engine、ECS、铁路模拟器或者物理模拟框架。

当前架构的核心目标是：

> **用少量稳定、直观且可组合的基础规则，支持一个具有高度自由度的体素世界和长期可扩展的火车贸易游戏。**

对于未来需求：

* 如果简单模型已经能够满足玩法，就继续使用简单模型；
* 如果 Profiling 证明性能不足，再优化数据布局；
* 如果真实 Gameplay 证明某个抽象不足，再增加新的系统；
* 不因为“未来可能需要”就提前实现复杂框架。

尤其需要避免以下方向：

```text
一个 Voxel 一个 Node
一种物品一个 C# 子类
万能 Composite 递归树
万能状态机
完整 ECS Framework
完整 Event Sourcing
提前实现跨平台 bit-perfect determinism
提前实现结构力学
提前实现完整铁路联锁
```

项目的长期稳定性应来自：

```text
清晰边界
+
少量强不变量
+
数据驱动
+
组合
+
确定性 Simulation
+
可替换的 Presentation Optimization
```

而不是来自过度抽象。

---

# 83. 当前架构一句话总结

> **游戏是一个有限、完整三维、数据驱动的体素世界；所有可实体化游戏物品共享 VoxelDefinition，Voxel 可组成拥有独立身份和能力的 Composite；整个世界由确定性 Simulation 持续运行，程序生成世界通过物理体素与高层语义索引共同构成，其中铁路是核心空间与视觉系统；Godot 负责表现与平台适配，而不是承担 World Model 本身。**
