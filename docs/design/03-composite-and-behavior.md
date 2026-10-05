# 03 Composite、Behavior 与 Capability

> **所属层**：核心模拟层（列车与建筑的表示）；体素布局与合法性属于设计编辑器。
>
> **修订中**：全文由[列车与建筑在核心模拟层的表示](https://github.com/odradekk/train/issues/201)改写；“本阶段静态建筑契约”由[聚居地的经营、建设与场景](https://github.com/odradekk/train/issues/206)改写，其中“建筑验收矩阵”和“本阶段改装操作”同时交给[地图](https://github.com/odradekk/train/issues/193)迷雾“新阶段”。连通与断裂、身份继承、Damage Transaction 中依赖全局体素世界的部分已被 [ADR-0008](../adr/0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 取代。改写完成前，上述规则不能作为实现依据；其余规则继续有效。

> 决策出处：转录 Q10–Q16、Q23–Q30、Q43–Q52、Q63–Q88；2026-09-05 [#30](https://github.com/odradekk/train/issues/30) 确认本阶段改装、伤害操作与阵营延期。

## 边界

本文定义 Composite 的结构、合法性、玩家设计与 Refit、所有权、连通与断裂、死亡与解体、Damage Transaction，以及 Tag、Behavior、Capability 三层与 Behavior 执行架构。Composite 的渲染见 [07](07-rendering.md)；Damage Transaction 在 Tick 中的位置见 [04](04-deterministic-simulation.md)；车辆在铁路上的运动、碰撞与脱落见 [06](06-railway.md)；正式 Gameplay 中消耗 Goods 的耗时 Construction、Refit 与 Deconstruction Job 见 [14](14-construction-refit-and-deconstruction.md)。

## 不变量

- Voxel Local Coordinate 永远为整数；Composite Transform 连续。
- Voxel 同一时间只有一个 Owner：World Grid 或 Composite。
- 新建及正常改装后的 Composite 要求六面连通；车辆运行时每个 Composite 仍只拥有一个 Connected Component。受损静态建筑允许在同一建筑身份下暂时保留多个有核心的分离部分，整体失效待修复，见本阶段静态建筑契约。
- 位置不是持久身份：RuntimeId = identity，Coordinate = location。
- Damage、Refit、Construction 等结构修改按 Transaction 原子结算。
- Composite Creation 永远来自显式 Gameplay 行为。
- Behavior 注册顺序没有游戏语义。

## 规则

### Composite

- Composite 是由多个 Voxel 构成的具备独立身份的游戏对象：车头、车厢、建筑、复杂机器。
- CompositeInstance { InstanceId, DefinitionId, LastUsedRevision, Archetype, Transform, Owned Voxels }。DefinitionId 用于解析定义，LastUsedRevision 用于识别规格变化；外部定义按当前安装内容解析，存档内自定义布局按 [09](09-persistence.md) 恢复。
- Composite 内部 Voxel 坐标永远是整数 Local Grid；Composite 本身可以拥有连续 World Transform。因此火车和生物可以平滑移动，而不会破坏 Voxel Grid。

### 不允许递归嵌套

Composite 只能直接拥有 Voxel。更高层对象使用 Gameplay Assembly：Train = Locomotive Composite + Carriage Composite + …；聚居地 = Building Composite + … + World Voxels（聚居地本身不是 Composite，见 [16](16-settlements.md)）。避免建立无限递归 Composite Tree。

### 合法性三分

Composite 使用数据驱动的 CompositeArchetype 描述规则，合法性分成三个独立概念：

- **Construction Validity**：这个设计是否能够作为这种 Composite 被制造。例如 Locomotive 可能需要 propulsion、traction、control、六面连通、合法 Rail Contact、满足车辆净空要求。
- **Runtime Identity**：已经制造出来的 Composite 是否仍然保持原身份。使用独立的数据驱动 IdentityRetentionRules；Construction Rules 和 Identity Retention Rules 不共用。
- **Operational Condition**：当前还能执行哪些功能。由 Runtime Voxel、Behavior 和 Capability 推导，可能产生 Operational、Degraded、Disabled、Destroyed。FSM 只解释高层生命周期，不复制底层真实状态。

### Draft 与玩家设计

- 玩家可以自由设计车头内部、车头外观、车厢、功能设备、Voxel 布局；面向玩家的完整自由设计在 Demo 之后提供，Demo 中玩家基于预置蓝图建造和改装车辆。建筑与车辆复用制造事务入口及通用占用、连通校验，各自采用 Archetype 专属合法性规则。聚居地层面主要放置预置建筑；正式成本派生见 [14](14-construction-refit-and-deconstruction.md)，本阶段不实现建筑材料成本。
- 设计器允许保存非法 Draft：Draft → validate → Valid Definition → Instantiate。只有合法 Definition 可以制造。
- 早期切片使用手写 JSON 验证 Blueprint 的加载、校验和制造；车辆制造与其他世界修改一样，由世界编辑器或控制台提交为统一的世界修改命令（[04](04-deterministic-simulation.md)）。本轮由世界编辑器提供独立的车辆／建筑蓝图创作视图，草稿保存、来源复制、发布与实例提取按 [02 的内容创作流程](02-definitions-and-content.md#世界编辑器内容创作流程)分片交付；不把编辑器设计工具继续视为范围外。
- 每个车头或车厢声明 [06](06-railway.md) 的五档尺寸之一；可以不填满固定包络，但所有实体体素与附加模块均不得越界，也不能自动升档。制造与改装同时检查机械约束，尺寸合法不等于可任意布置接触点。尺寸为可进入的内部空间留出余量，角色通行与正式内部设计玩法尚未验证。

### Refit

- 玩家修改已经存在的 Composite 时，例如 Instance #100 从 Revision `SHA-256(改装前定义的规范化语义内容)` Refit 到 `SHA-256(改装后定义的规范化语义内容)`，Instance Identity 保持；只有真正拆除并重新制造才创建新 Instance ID。这里以哈希计算表达式示意 Revision，实际 ID 使用 [02](02-definitions-and-content.md) 规定的语义内容哈希，不使用递增序号。
- Refit 采用 Atomic Transaction，不让施工中间状态暴露给 Simulation。世界编辑器和控制台提交的瞬时 Atomic Refit 是开发验证入口，经统一的世界修改命令执行；正式 Gameplay 的 Refit 是消耗 Delta BOM 的耗时 Job，完成时原子提交最终 Layout，期间 Composite 处于 UnderRefit 锁定状态，见 [14](14-construction-refit-and-deconstruction.md)。
- Refit 必须追踪真实 Voxel Identity：把同一个 Engine 从 old cell 移到 new cell，它仍然是同一个 Engine，Durability、Temperature、Behavior State 不会被重置。

Mod 或内置内容更新不要求先执行 Refit：读档与热重载按 [02](02-definitions-and-content.md) 采用当前外部规格，同时保留实际布局和实例身份，按新约束适配状态。Refit 表达玩家对车辆结构的显式修改；外部引擎数值更新不改变自定义车辆设计自身的 Revision。

### 本阶段改装操作

- 世界编辑器和控制台的世界修改命令完整覆盖添加、移除、移动、旋转和替换车辆体素；旋转遵守体素正交方向约束。
- 支持一次提交多项修改，例如交换两个零件的位置。按最终方案校验并原子提交，施工中间状态不进入 Simulation。
- 移动、旋转已有零件时保留其身份、耐久和已有运行状态；替换表示旧件移除、新件加入，不将旧件身份和状态作为新件继承。
- 非法改装整体拒绝，原车辆及其零件状态不变；成功改装保留车辆身份。制造新车仍走 Blueprint 加载、Construction Validation 与 Instantiate 流程。

### 本阶段静态建筑契约

[静态建筑的结构、占用与新增内容更新契约](https://github.com/odradekk/train/issues/106)已确认 Q1–Q15，其中 Q5、Q10 采用用户修正：断裂不产生多个建筑，原建筑整体失效；修复必须完整还原该实例最后确认的完整设计。以下为目标契约，不表示建筑实现已经完成；建筑的单一身份规则取代车辆多后继规则在建筑上的适用。

- 建筑锚点使用整数世界坐标，整体仅允许 0°、90°、180°、270° 水平旋转，不缩放。内部零件仍按各自定义允许的正交朝向放置。建筑不套用车辆五档尺寸，按实际体素、世界边界和放置约束校验。
- 普通建筑的新建条件为非空、六面连通且至少含一个带指定建筑核心标签的部件。核心允许多个，不要求某个不可替换的特殊零件。受损后的核心判定须与下述单一建筑身份规则共同解释，不再按车辆规则自动产生多个建筑后继；身份、结构合法性与运营功能仍是不同概念。
- 主动新增或改装建筑必须同时验证地形、其他建筑、轨道占格和既有完整线路的通行净空；冲突时整次拒绝并保留原状态。没有列车经过也不允许把完整线路改成无法通过；新铺轨道同样不得侵入已有建筑。
- 世界编辑器中的建筑操作完整覆盖添加、移除、移动、旋转、替换零件及批量原子修改。移动/旋转保留零件身份和耐久；替换产生新零件。按最终布局校验，非法方案整体拒绝。伤害经过正式耐久和断裂结算，不因结果不再满足新建条件而拒绝伤害。
- 建筑无论如何断裂都仍视为一个建筑，不增加聚居地的建筑数量；一旦发生断裂即失效，必须修复后才可恢复正常工作。多个仍含核心的分离部分继续归同一建筑，保留原身份、类型及聚居地归属；只要建筑身份存在，聚居地列表仍记录这一个失效建筑。即使无核心部分转为世界体素后剩余建筑恰好连通，也不能据此自动解除本次断裂造成的失效。
- 修复必须完整还原该实例最后确认的完整设计：制造时记录所用蓝图，建筑完好时成功改装可更新目标。伤害不修改目标，外部 Blueprint/Layout 更新也不替换目标；失效建筑不能把残缺布局确认为新目标来绕过修复。实际受损布局与修复目标是两份不同事实。
- 修复成功后，布局、零件定义与朝向完全符合目标蓝图，所有目标零件耐久恢复到当前定义上限。仍存在且符合目标的零件保留身份，缺失零件新建并分配新身份；建筑保留原身份并解除结构失效。仅连接断口、满足新建最低条件或改成另一合法布局，都不能作为完成修复。
- 修复允许将可确认来自原建筑、仍在目标位置且符合目标蓝图的世界体素原子转回建筑所有权；来源、零件身份、位置和匹配关系须可验证，不能只凭相同材质猜测归属。不符合这些条件的残块不自动吸收。其他建筑、地形或轨道占据目标空间，或修复侵入铁路净空时，整次拒绝并保留原状态。
- 修复目标固定的是布局、零件定义标识与朝向，不把外部零件数值冻结为旧版本；数值继续采用当前有效定义。目标需要的内容同样进入引用校验。修复不重建铁路、不消除列车故障；建筑结构、站点停靠资格和列车运行条件分别校验。
- 所有核心均被摧毁时，原建筑彻底毁坏，结束其身份并从聚居地实际建筑列表移除；剩余体素原位转为世界体素。随后重新建造产生新的建筑身份，不复用已毁坏的身份。
- 无核心碎块在同一事务中原位转为 World ownership，保留位置、朝向、耐久及已有零件身份，占用和碰撞连续更新。本阶段不模拟建筑支撑力学或坍塌，移除地基后不自动下落；转为世界体素的悬空部分也保留原位。
- 建筑体素的放置占用与铁路净空均按实际存在体素的整格保守检查，不因碰撞形状为空或局部盒而允许侵入线路；普通碰撞查询按实际碰撞形状处理。建筑中没有体素的空格保持为空，可供满足净空约束的线路穿行。

#### 本阶段内容与功能边界

- 内容须能够数据驱动地声明普通建筑、站点和其核心规则、建筑 Blueprint、站点部件接轨接口与聚居地 Layout；生成器和世界编辑器都通过正式校验与实例化入口使用它们。建筑不需要虚假的车辆尺寸或轨道接触点才能加载。
- 车辆内容继续覆盖 [06](06-railway.md) 已定五档、混编与活动车头/接触能力。普通建筑不因存在或带有类型标签就获得生产能力；核心标签只参与分类与身份/结构校验，不伪装为 Behavior。
- 碰撞沿用 [01](01-voxel-engine-and-scenes.md) 的 None、Full Box、一个或少量格内局部 Box 及正交方向约束；建筑和车辆复用此语义。三路表现及资产契约由 [07](07-rendering.md) 和[表现验收](https://github.com/odradekk/train/issues/107)收敛。
- 只实现本阶段车辆运行、建筑结构和站点接轨确需的逻辑；Capability 必须有真实实现来源，不开放 JSON 任意伪造能力或任意行为执行。资源、库存、生产、耗时施工、材料成本、通用规则语言及插件框架不在本阶段。

#### 建筑验收矩阵

| 场景 | 必须观察到的结果 |
| --- | --- |
| 整数锚点、四向建筑旋转、内部零件方向 | 合法位置落位一致；越界、同格冲突、非法整体旋转或缩放整体拒绝，不套用车辆尺寸档。 |
| 新建结构与核心 | 空布局、非六面连通布局或没有核心的布局拒绝；满足其他放置约束的单核心、多核心连通建筑均可新建。 |
| 建筑与铁路双向空间校验 | 新增/改装侵入地形、其他建筑、轨道占格或既有完整线路净空均整体拒绝，即使线路没有列车；反向铺轨侵入建筑也拒绝。 |
| 五类修改与批量交换 | 原子提交最终布局；移动/旋转保留零件身份和耐久，替换分配新身份；失败不改变实例、所有权及身份分配状态。 |
| 双核心建筑断开、单核心侧翼脱落 | 前者仍是同一个失效建筑并拥有多个分离部分；后者无核心碎块原位转世界，即使剩余部分连通也保持断裂失效。聚居地计数均不增加。 |
| 最后核心毁坏 | 原建筑身份结束、聚居地引用撤销、残块原位转世界；新建不复活旧身份。 |
| 修复目标的建立与更新 | 制造时记录完整设计；完好建筑成功改装后更新目标。伤害、外部 Blueprint/Layout 更新不改目标；失效建筑不能确认残缺布局来绕过修复。 |
| 修复缺口及受损零件 | 完整恢复实例目标布局、定义和朝向，并恢复当前定义上限耐久；可复用零件保身份，缺失件分配新身份。单纯接通断口不能冒充完整修复。 |
| 原位残块与外部障碍 | 原建筑且匹配的残块可原子收回；外来同材质体素不能冒充。目标被占或侵入铁路净空时整次拒绝，不部分修复、不偷偷挪动障碍。 |
| 占用、碰撞、净空与显示 | None/局部盒仍占整格且参与整格铁路净空检查；碰撞按实际形状，空洞不按建筑包围盒填充。修改和所有权转移后无重复占用、重复显示或残留碰撞。 |
| 站点断裂、接口部件消失、建筑修复 | 站点可用性和接口状态反映真实建筑；物理轨道不随站房受损自动删除，列车已有占用与安全保护不消失。 |
| Blueprint/Layout、零件语义及纯表现更新 | 新模板不重写已有布局或修复目标；合法零件更新按当前定义适配，纯表现不改变碰撞/连接；非法候选整批拒绝并保留旧内容与实例。 |
| 保存受损、多分离部分、原位残块及修复后状态 | 恢复实际布局、单一建筑身份、失效状态、修复目标、可验证残块来源与站点引用；读档不自动修复或多生建筑。完整目标入档、残块来源及唯一所有权校验见 [09](09-persistence.md#静态建筑与修复目标恢复)，跨进程对照见 [04](04-deterministic-simulation.md#确定性验收)。 |

以上需有领域契约检查、真实 Godot 显示/碰撞验证和世界编辑器操作证据。损伤与修复中的事务及确定性沿用 [04](04-deterministic-simulation.md)；建筑位置、统计、站点关系由 [16](16-settlements.md)消费，不再保存与实际实例脱节的手工计数。

### Voxel Ownership

下文的所有权原则同样适用于静态建筑；本阶段建筑的已确认操作见上一节。

- 任意 Voxel Instance 同一时间只有一个空间所有者：World Grid 或 Composite，不能同时属于两者。发生脱落时执行 Ownership Transfer，而不是复制实例。
- 普通 World Voxel 不要求全部拥有 Runtime ID。需要长期个体身份的 Voxel 才分配 VoxelRuntimeId：Composite 内设备、被其他系统引用的部件、拥有复杂 Runtime State 的个体。
- 对于拥有 RuntimeId 的 Voxel，RuntimeId 是身份，Coordinate 是位置；坐标不能成为长期身份引用。

### 连通与断裂

- 新建及正常改装后的 Composite 必须六面连通；车辆的每个运行时 Composite 也始终只有一个 Connected Component。静态建筑受损后的例外遵守上文单一身份与失效规则。
- 当一次 Damage Transaction 造成车辆结构断裂，按 Connected Components 形成同类型 Composite 候选，并按 IdentityRetentionRules 判断：0 个存活 → Composite destroyed；1 个存活 → 保留 Composite；N 个存活 → 产生 N 个独立 Composite。静态建筑不按此规则增加实例，按上文统一保留或结束建筑身份。

### 断裂后的身份继承

以下多后继身份继承规则适用于车辆断裂，不用于静态建筑。车辆存在多个 surviving fragments 时：

1. 计算各 Fragment 中所有 VoxelDefinition.Value 之和；
2. Value 最大的 Fragment 成为 Primary Successor；
3. Primary 继承原 Composite InstanceId；
4. 其他 Survivor 获得新的 Runtime InstanceId；
5. Value 完全相同时，通过 WorldSeed 派生的确定性随机规则打破平局。

所有 surviving fragments 在本次断裂中保留原定义引用、自身 Voxel Runtime State 与 provenance 等可继承属性，并重新计算 Capability 与 Operational Condition；这不阻止之后按 [02](02-definitions-and-content.md) 采用当前外部定义。Identity-level 外部引用（Train Assembly membership、UI Selection、AI Order、Route Assignment）默认只由 Primary 继承；Secondary 成为独立对象。Faction 的设计与实现整体延后，Demo 不保留阵营占位字段。

身份继承不能维持已经断开的车辆物理连接。#33 规定结构断裂后依据实际保留的车辆与车钩连接重新确定编组；仍具沿轨运动条件的各部分从事件发生时的状态建立独立运动，不重新摆车，也不继续被旧编组隔空牵引。无动力部分独立滑行减速，见 [06](06-railway.md)。车体身份存活与沿轨运动资格分别检查，不能将半截车壳自动视为完整车厢。

### 死亡与解体

- 无法满足 IdentityRetentionRules 的 Fragment：Composite ownership → World ownership；Voxel Runtime State 保留。
- 不会自动扫描这些 Voxel 并尝试组成另一个 Composite。Composite Creation 必须来自显式行为：World Generator、玩家 Construction、AI Construction、Blueprint Instantiation、Composite Fracture。世界绝不会根据 Voxel 排列"猜测"一个 Composite。正式 Gameplay 的 Construction 消费库存中的 Voxel Goods 并在耗时 Job 完成时原子实例化，见 [14](14-construction-refit-and-deconstruction.md)。

### Damage Transaction

同一逻辑 Damage Event 内的所有破坏首先统一应用：apply damage → remove hp ≤ 0 → mark topology dirty → connectivity calculation → fragmentation。不能"destroy one voxel → fracture → destroy another → fracture again"，否则结果可能依赖遍历顺序。

本阶段由世界编辑器和控制台的伤害命令对指定体素施加指定伤害，支持一次事件影响多个体素，覆盖地形、车辆及静态建筑体素。这些命令和测试都必须经过正式的耐久扣减、归零删除与断裂结算流程，不能只用直接删除代替伤害验证。事故碰撞等玩法触发源后续再做。Extraction 是 Damage Transaction 的一种显式 Gameplay 使用方式，只有它产生 Raw Good，见 [10](10-resources-and-extraction.md)。

### Tag、Behavior、Capability

三者严格分离：

- **Tag** 用于被动分类：metal、structural、organic 等。
- **Behavior** 描述真正的逻辑机制：EngineBehavior、WeaponBehavior、StorageBehavior、HeatBehavior 等。
- **Capability** 是 Behavior 对外提供的能力：Propulsion、Traction、Storage、Weapon、Power 等。Capability 不允许在 Mod JSON 中任意伪造，它由实际注册的 Behavior 推导。Facility 的 Production、Extraction、Construction、Storage、Power Capability 同样只来自建筑内真实设备 Voxel 的 Behavior，Facility Tag 只表达兼容性，见 [13](13-production-runtime-and-facilities.md)。

### Behavior 架构

- 采用 Behavior Registry + Composition。避免 Voxel → MechanicalVoxel → EngineVoxel → DieselEngineVoxel 这样的继承链；一个 Voxel 组合 EngineBehavior、FuelConsumerBehavior、HeatBehavior。
- Behavior 是组件语义，System 是执行语义：EngineSystem、HeatSystem、WeaponSystem、StorageSystem 负责批量运行对应 Runtime State。不为每一个 Voxel 创建 Node 或独立 Tick。
- Behavior 按 Owner Context 生效（转录 Q69）。

## Demo 范围

CompositeDefinition、CompositeInstance、Local Voxel Grid、Archetype、Construction Validation、Ownership、CompositeVisual、基础 Damage 与 Fragmentation、Behavior 与 Capability 基础；Blueprint 的加载、校验与制造，以及上述完整改装操作。玩家基于预置蓝图建造和改装车辆，正式 Gameplay 的施工与改装 Job 见 [14](14-construction-refit-and-deconstruction.md)。世界编辑器的车辆／建筑蓝图创作是本阶段的开发工具，不是 Demo 的玩家内容，交付次序和保存发布边界见 [02](02-definitions-and-content.md#世界编辑器内容创作流程)。

## Demo 之后

面向玩家的完整自由车辆设计器、事故碰撞等伤害玩法触发源、Faction 的设计与实现、武器相关 Behavior 与 Capability；复杂 Damage Types、结构力学、万能 Composite 递归树、万能状态机、完整 ECS Framework。世界编辑器内的蓝图创作是开发工具，不在此列。

## 开放问题

- 本阶段能力范围已由[体素能力决策](https://github.com/odradekk/train/issues/92)确认，静态建筑的结构、身份、完整修复与内容边界已由[建筑契约](https://github.com/odradekk/train/issues/106)确认，见上文；数据结构和文件拆分随实施选择，不重开已定产品语义。
- 地图迷雾（不立票）：范围之外的 Behavior、Capability、System 接口扩展及 Archetype 规则（转录 Q22、Q43）、完整编挂操作与运营命令继承；选入近期切片后再评估是否需要决策。编组运动与断开处理已有规则见 [06](06-railway.md)，本阶段多列车最小运行规则见该篇关联决策票。
- 未冻结参数：非车辆 Blueprint 尺寸限制，通过实际开发确定。
