# 01 体素引擎与局部场景

> **所属层**：体素引擎层（局部场景与设计编辑器）。
>
> **修订中**：“体素尺度”“场景中的结构破损”“线路场景中的铁轨”三节由[列车与建筑在核心模拟层的表示](https://github.com/odradekk/train/issues/201)于 2026-10-05 确定，已经有效。其余部分仍按全局体素世界书写，由[地图](https://github.com/odradekk/train/issues/193)迷雾“观察与表现”改写为局部场景与设计编辑器中的体素规则。“有限三维体素世界”中的全图常驻、全精度实时模拟（转录 Q5）已被 [ADR-0008](../adr/0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 取代；不变量中的格网规则继续有效，但只适用于体素引擎层。改写完成前，上述规则不能作为实现依据；其余规则继续有效。

> 决策出处：转录 Q1–Q9、Q166–Q175。

## 边界

本文定义世界空间、Voxel 逻辑单位、网格不变量、Voxel 的耐久与世界体素的结构规则。VoxelDefinition 的字段见 [02](02-definitions-and-content.md)；Composite 的内部网格、所有权与断裂见 [03](03-vehicles-buildings-and-designs.md)；Chunk Mesh 与渲染见 [07](07-rendering.md)。世界尺寸与 Chunk 尺寸是未冻结参数。

## 不变量

1. **一个 Voxel 永远占据一个 Cell。** 逻辑尺寸固定为 1 × 1 × 1。需要 2 × 1 × 1 的桌子时，用两个或更多 Voxel 构成 Composite，而不是创建跨 Cell Voxel。
2. **一个 Cell 最多一个 Voxel。** occupant count ∈ {0, 1}；多个可交互 Voxel 不允许重叠占据同一 Cell。
3. **空气不是 Voxel。** Cell 状态只有 Empty 或 Occupied(Voxel)，不存在必须实例化的 air Voxel。
4. **Orientation 严格正交。** Voxel 不允许任意角度旋转；领域模型支持完整的 24 种立方体正交朝向；Definition 可以限制可用方向：四向水平旋转、完整 24 向、不允许旋转。
5. **Occupancy 与 Collision 分离。** Voxel 逻辑上始终占据整个 Cell；实际 Collision Shape 可以是 None、Full Box、一个或少量局部碰撞 Box，且必须限制在自己的 Cell 内。
6. **结构连接只看 Grid。** Voxel 之间的结构连接完全根据六面邻接（±X、±Y、±Z）判断，不检查 Mesh 是否接触、Collider 是否接触、物理引擎是否认为发生碰撞。

## 规则

### 体素尺度

- 一个体素格边长 0.5 米。人物高 3 格，约 1.5 米。世界图中的米与场景中的格按这个比例换算。
- 车辆的五档尺寸按这个尺度定义，见 [03](03-vehicles-buildings-and-designs.md#五档尺寸)。

### 场景中的结构破损

核心模拟层只给出每辆车或每座建筑的结构耐久比例 r（当前值 ÷ 上限），以及战斗事件（命中了谁、哪个部件被毁），见 [03](03-vehicles-buildings-and-designs.md#损伤与战斗事件)。场景用四层效果表现：

1. **固定的破损顺序**：按“实例身份 + 蓝图版本”派生的随机键，为全部结构体素排一个移除顺序，偏向外壳，并按若干弹坑中心成团。r 每下降一段，就从顺序开头移除相应数量的体素，形成弹孔和缺口。移除量设上限（例如最多 40% 的结构体素），保证对象被摧毁前外形仍可辨认。修理时按相反顺序补回。顺序只取决于实例身份、蓝图版本和 r，所以重新打开场景时看到的破损相同。
2. **表面损伤**：即将被移除的体素和弹孔周围的体素，通过 shader 参数加上焦痕、裂纹和变暗，程度随 r 加深。
3. **状态特效**：r 低于阈值时持续冒烟（例如低于 50%），再低时起火（例如低于 25%），以及火花、漏液等。
4. **命中瞬间**：每个命中事件在下一批要移除的体素位置播放闪光、碎片飞溅和镜头抖动，然后移除这批体素。碎片可以使用 Godot 物理，只作表现。

被毁的部件按事件精确移除；受损但未被毁的部件用表面损伤表现。各项阈值和移除量上限在实测中调整。血条、状态图标属于界面，见[窗口与界面的设计和规划](https://github.com/odradekk/train/issues/207)。

### 线路场景中的铁轨

- 每条铁轨在场景中由三对轮轨组成，各档车辆使用其中一对，核心模拟层和调度不区分轨距：

| 轨距 | 宽度 | 使用的档位 |
| --- | --- | --- |
| 标准轨距 | 3 格（1.5 米） | 小型 |
| 宽轨 | 7 格（3.5 米） | 中型、大型 |
| 巨型轨距 | 15 格（7.5 米） | 超大型、泰坦 |

- 钢轨用网格表现，不占体素格；轨枕和道床用体素表示，道床宽约 21 格。
- 任何一条铁轨都可能有泰坦通过，相邻铁轨的中心距按最宽车辆加安全间隙取约 34 格（17 米）。
- 弯道半径、坡度等线形参数按这个尺度另定，由[地图](https://github.com/odradekk/train/issues/193)迷雾“观察与表现”确定。具体格数在场景实测时可以微调。

### 有限三维体素世界

- 世界是有限尺寸、完整三维、严格整数网格的 Voxel World；尺寸为创建世界时的配置参数，当前不冻结。
- 数据模型不限制为 Heightfield；未来自然支持洞穴、隧道、桥梁、地下设施、悬空结构、任意方向的体素破坏。Demo 的世界生成算法仍可以以较简单的地表地形为主。
- 当前已定规则是世界数据完整常驻内存、全图全精度实时模拟，远处不因不可见而降低模拟精度；"不加载"只表示不渲染（转录 Q5，见 [07](07-rendering.md) Visual Streaming）。Simulation LOD 仅作为延后评估的候选方向，不是已承诺的后续功能；若性能问题促使未来考虑引入，须先重审全图全精度模拟规则，模拟边界见 [04](04-deterministic-simulation.md)。

### 一切皆体素

- 游戏世界中能够实体化并参与游戏逻辑的对象统一建立在 Voxel 模型之上：地形、自然资源、铁轨、建筑零件、车轮、引擎、武器、装饰、人物或生物的结构组件、其他可交互实体。
- 不属于 Voxel：UI、粒子、临时视觉特效、摄像机、纯表现对象。
- Voxel 是逻辑空间单位，不是"只能拥有六面的立方体 Mesh"；可以使用六面 Cube 表现，也可以拥有专用 Custom Mesh。

### 耐久与破坏

- Demo 只有统一的 Durability：CurrentDurability 与 MaxDurability。CurrentDurability ≤ 0 时 Voxel 直接消失，视觉上用碎片、火花、烟尘表现。
- Demo 不建立 kinetic、fire、electric、corrosion 等 Damage Type Matrix；复杂损伤机制未来通过 Behavior 增加。
- 同一逻辑 Damage Event 内的破坏统一结算，见 [03](03-vehicles-buildings-and-designs.md) Damage Transaction。

### 世界体素的结构稳定性

- Demo 中 World-owned Voxel 不拥有通用结构支撑模拟：挖掉下面的体素，上面的普通 Voxel 可以悬空。
- 只有明确具有 GravityBehavior、FallingBehavior 的特殊 Voxel 才会下落。
- 新建及正常改装的设计要求六面连通。车辆和建筑在核心模拟层中不按连通部分拆分，见 [03](03-vehicles-buildings-and-designs.md#损伤与战斗事件)。

### 所有权

任意 Voxel Instance 同一时间只有一个空间所有者：World Grid 或 Composite。规则见 [03](03-vehicles-buildings-and-designs.md)。

## Demo 范围

有限 3D Voxel World；World Chunk 仅作空间分区，不承担生命周期（转录 Q30）；统一 Durability；无结构支撑。

## Demo 之后

World structural collapse、复杂重力系统、复杂 Damage Types、通用 Material System。

## 开放问题

- 已确认依据：代表性 World Size、负载、Chunk Size 及全图常驻内存的测量与用户冻结流程见[性能验收决议](https://github.com/odradekk/train/issues/98#issuecomment-5856117703)，不把当前小夹具规模当作容量保证。
- 地图迷雾（不立票）：Chunk 存储实现的具体优化；待测量暴露影响当前或下一切片的具体瓶颈时再提出决策问题。
