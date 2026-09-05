# 03 Composite、Behavior 与 Capability

> 决策出处：转录 Q10–Q16、Q23–Q30、Q43–Q52、Q63–Q88；2026-09-05 [#30](https://github.com/odradekk/train/issues/30) 确认首版测试操作与阵营延期。

## 边界

本文定义 Composite 的结构、合法性、玩家设计与 Refit、所有权、连通与断裂、死亡与解体、Damage Transaction，以及 Tag、Behavior、Capability 三层与 Behavior 执行架构。Composite 的渲染见 [07](07-rendering.md)；Damage Transaction 在 Tick 中的位置见 [04](04-deterministic-simulation.md)；车辆在铁路上的运动、碰撞与脱落见 [06](06-railway.md)。

## 不变量

- Voxel Local Coordinate 永远为整数；Composite Transform 连续。
- Voxel 同一时间只有一个 Owner：World Grid 或 Composite。
- Composite Voxel 始终形成一个六面连通体；Runtime 中一个 Composite 始终只拥有一个 Connected Component。
- 位置不是持久身份：RuntimeId = identity，Coordinate = location。
- Damage、Refit、Construction 等结构修改按 Transaction 原子结算。
- Composite Creation 永远来自显式 Gameplay 行为。
- Behavior 注册顺序没有游戏语义。

## 规则

### Composite

- Composite 是由多个 Voxel 构成的具备独立身份的游戏对象：车头、车厢、建筑、复杂机器。
- CompositeInstance { InstanceId, DefinitionRevision, Archetype, Transform, Owned Voxels }。
- Composite 内部 Voxel 坐标永远是整数 Local Grid；Composite 本身可以拥有连续 World Transform。因此火车和生物可以平滑移动，而不会破坏 Voxel Grid。

### 不允许递归嵌套

Composite 只能直接拥有 Voxel。更高层对象使用 Gameplay Assembly：Train = Locomotive Composite + Carriage Composite + …；聚居地 = Building Composite + … + World Voxels（聚居地本身不是 Composite，见 [05](05-world-generation-and-settlements.md)）。避免建立无限递归 Composite Tree。

### 合法性三分

Composite 使用数据驱动的 CompositeArchetype 描述规则，合法性分成三个独立概念：

- **Construction Validity**：这个设计是否能够作为这种 Composite 被制造。例如 Locomotive 可能需要 propulsion、traction、control、六面连通、合法 Rail Contact、满足车辆净空要求。
- **Runtime Identity**：已经制造出来的 Composite 是否仍然保持原身份。使用独立的数据驱动 IdentityRetentionRules；Construction Rules 和 Identity Retention Rules 不共用。
- **Operational Condition**：当前还能执行哪些功能。由 Runtime Voxel、Behavior 和 Capability 推导，可能产生 Operational、Degraded、Disabled、Destroyed。FSM 只解释高层生命周期，不复制底层真实状态。

### Draft 与玩家设计

- 玩家可以自由设计车头内部、车头外观、车厢、功能设备、Voxel 布局。
- 设计器允许保存非法 Draft：Draft → validate → Valid Definition → Instantiate。只有合法 Definition 可以制造。
- 首版用手写 JSON 提供车辆 Blueprint，由测试场景加载、校验并生成车辆，验证正式的设计与制造流程；正式设计器留到后续版本。

### Refit

- 玩家修改已经存在的 Composite 时，Instance #100 从 Definition r17 Refit 到 r18，Instance Identity 保持；只有真正拆除并重新制造才创建新 Instance ID。
- Refit 首版采用 Atomic Transaction，不让施工中间状态暴露给 Simulation。
- Refit 必须追踪真实 Voxel Identity：把同一个 Engine 从 old cell 移到 new cell，它仍然是同一个 Engine，Durability、Temperature、Behavior State 不会被重置。

### 首版改装测试操作

- 测试场景提供最小操作入口，完整覆盖添加、移除、移动、旋转和替换车辆体素；旋转遵守体素正交方向约束。
- 支持一次提交多项修改，例如交换两个零件的位置。按最终方案校验并原子提交，施工中间状态不进入 Simulation。
- 移动、旋转已有零件时保留其身份、耐久和已有运行状态；替换表示旧件移除、新件加入，不将旧件身份和状态作为新件继承。
- 非法改装整体拒绝，原车辆及其零件状态不变；成功改装保留车辆身份。制造新车仍走 Blueprint 加载、Construction Validation 与 Instantiate 流程。

### Voxel Ownership

- 任意 Voxel Instance 同一时间只有一个空间所有者：World Grid 或 Composite，不能同时属于两者。发生脱落时执行 Ownership Transfer，而不是复制实例。
- 普通 World Voxel 不要求全部拥有 Runtime ID。需要长期个体身份的 Voxel 才分配 VoxelRuntimeId：Composite 内设备、被其他系统引用的部件、拥有复杂 Runtime State 的个体。
- 对于拥有 RuntimeId 的 Voxel，RuntimeId 是身份，Coordinate 是位置；坐标不能成为长期身份引用。

### 连通与断裂

- 合法 Composite 必须是严格的六面连通体；Runtime 中也要求一个 Composite 始终只拥有一个 Connected Component。
- 当一次 Damage Transaction 造成结构断裂，Composite 拆成 Connected Components，每个 Component 暂时作为同类型 Composite 候选按 IdentityRetentionRules 判断：0 个存活 → Composite destroyed；1 个存活 → 保留 Composite；N 个存活 → 产生 N 个独立 Composite。

### 断裂后的身份继承

存在多个 surviving fragments 时：

1. 计算各 Fragment 中所有 VoxelDefinition.Value 之和；
2. Value 最大的 Fragment 成为 Primary Successor；
3. Primary 继承原 Composite InstanceId；
4. 其他 Survivor 获得新的 Runtime InstanceId；
5. Value 完全相同时，通过 WorldSeed 派生的确定性随机规则打破平局。

所有 surviving fragments 保留原 Definition Revision、自身 Voxel Runtime State 与 provenance 等可继承属性，并重新计算 Capability 与 Operational Condition。Identity-level 外部引用（Train Assembly membership、UI Selection、AI Order、Route Assignment）默认只由 Primary 继承；Secondary 成为独立对象。Faction 的设计与实现整体延后，首版不保留阵营占位字段。

### 死亡与解体

- 无法满足 IdentityRetentionRules 的 Fragment：Composite ownership → World ownership；Voxel Runtime State 保留。
- 不会自动扫描这些 Voxel 并尝试组成另一个 Composite。Composite Creation 必须来自显式行为：World Generator、玩家 Construction、AI Construction、Blueprint Instantiation、Composite Fracture。世界绝不会根据 Voxel 排列"猜测"一个 Composite。

### Damage Transaction

同一逻辑 Damage Event 内的所有破坏首先统一应用：apply damage → remove hp ≤ 0 → mark topology dirty → connectivity calculation → fragmentation。不能"destroy one voxel → fracture → destroy another → fracture again"，否则结果可能依赖遍历顺序。

首版由测试场景的调试操作对指定体素施加指定伤害，支持一次事件影响多个体素，覆盖地形体素与车辆体素。测试必须经过正式的耐久扣减、归零删除与断裂结算流程，不能只用直接删除代替伤害验证。事故碰撞等玩法触发源后续再做。

### Tag、Behavior、Capability

三者严格分离：

- **Tag** 用于被动分类：metal、structural、organic 等。
- **Behavior** 描述真正的逻辑机制：EngineBehavior、WeaponBehavior、StorageBehavior、HeatBehavior 等。
- **Capability** 是 Behavior 对外提供的能力：Propulsion、Traction、Cargo、Weapon、Power 等。Capability 不允许在 Mod JSON 中任意伪造，它由实际注册的 Behavior 推导。

### Behavior 架构

- 采用 Behavior Registry + Composition。避免 Voxel → MechanicalVoxel → EngineVoxel → DieselEngineVoxel 这样的继承链；一个 Voxel 组合 EngineBehavior、FuelConsumerBehavior、HeatBehavior。
- Behavior 是组件语义，System 是执行语义：EngineSystem、HeatSystem、WeaponSystem、StorageSystem 负责批量运行对应 Runtime State。不为每一个 Voxel 创建 Node 或独立 Tick。
- Behavior 按 Owner Context 生效（转录 Q69）。

## 首版范围

CompositeDefinition、CompositeInstance、Local Voxel Grid、Archetype、Construction Validation、Ownership、CompositeVisual、基础 Damage 与 Fragmentation、Behavior 与 Capability 基础；手写 JSON Blueprint 的加载、校验与制造，以及上述完整改装测试操作（roadmap Phase 3）。

## 明确延后

正式车辆设计器、事故碰撞等伤害玩法触发源、Faction 的设计与实现；复杂 Damage Types、结构力学、万能 Composite 递归树、万能状态机、完整 ECS Framework。

## 开放问题

- 地图迷雾：Behavior、Capability 注册表与 System 的首版最小集合及其在 Godot C# 中的接口（转录 Q22）；各 Archetype 的 Construction 与 IdentityRetention 具体规则（转录 Q43）；列车 Assembly 与耦合。
