# 01 体素世界与网格不变量

> 决策出处：转录 Q1–Q9、Q166–Q175。

## 边界

本文定义世界空间、Voxel 逻辑单位、网格不变量、Voxel 的耐久与世界体素的结构规则。VoxelDefinition 的字段见 [02](02-definitions-and-content.md)；Composite 的内部网格、所有权与断裂见 [03](03-composite-and-behavior.md)；Chunk Mesh 与渲染见 [07](07-rendering.md)。世界尺寸与 Chunk 尺寸是未冻结参数（[roadmap](roadmap.md)）。

## 不变量

1. **一个 Voxel 永远占据一个 Cell。** 逻辑尺寸固定为 1 × 1 × 1。需要 2 × 1 × 1 的桌子时，用两个或更多 Voxel 构成 Composite，而不是创建跨 Cell Voxel。
2. **一个 Cell 最多一个 Voxel。** occupant count ∈ {0, 1}；多个可交互 Voxel 不允许重叠占据同一 Cell。
3. **空气不是 Voxel。** Cell 状态只有 Empty 或 Occupied(Voxel)，不存在必须实例化的 air Voxel。
4. **Orientation 严格正交。** Voxel 不允许任意角度旋转；领域模型支持完整的 24 种立方体正交朝向；Definition 可以限制可用方向：四向水平旋转、完整 24 向、不允许旋转。
5. **Occupancy 与 Collision 分离。** Voxel 逻辑上始终占据整个 Cell；实际 Collision Shape 可以是 None、Full Box、一个或少量局部碰撞 Box，且必须限制在自己的 Cell 内。
6. **结构连接只看 Grid。** Voxel 之间的结构连接完全根据六面邻接（±X、±Y、±Z）判断，不检查 Mesh 是否接触、Collider 是否接触、物理引擎是否认为发生碰撞。

## 规则

### 有限三维体素世界

- 世界是有限尺寸、完整三维、严格整数网格的 Voxel World；尺寸为创建世界时的配置参数，当前不冻结。
- 数据模型不限制为 Heightfield；未来自然支持洞穴、隧道、桥梁、地下设施、悬空结构、任意方向的体素破坏。首版世界生成算法仍可以以较简单的地表地形为主。
- 世界数据完整常驻内存，远处也全精度实时模拟；"不加载"只表示不渲染（转录 Q5，见 [07](07-rendering.md) Visual Streaming）。

### 一切皆体素

- 游戏世界中能够实体化并参与游戏逻辑的对象统一建立在 Voxel 模型之上：地形、自然资源、铁轨、建筑零件、车轮、引擎、武器、装饰、人物或生物的结构组件、其他可交互实体。
- 不属于 Voxel：UI、粒子、临时视觉特效、摄像机、纯表现对象。
- Voxel 是逻辑空间单位，不是"只能拥有六面的立方体 Mesh"；可以使用六面 Cube 表现，也可以拥有专用 Custom Mesh。

### 耐久与破坏

- 首版只有统一的 Durability：CurrentDurability 与 MaxDurability。CurrentDurability ≤ 0 时 Voxel 直接消失，视觉上用碎片、火花、烟尘表现。
- 首版不建立 kinetic、fire、electric、corrosion 等 Damage Type Matrix；复杂损伤机制未来通过 Behavior 增加。
- 同一逻辑 Damage Event 内的破坏统一结算，见 [03](03-composite-and-behavior.md) Damage Transaction。

### 世界体素的结构稳定性

- 首版 World-owned Voxel 不拥有通用结构支撑模拟：挖掉下面的体素，上面的普通 Voxel 可以悬空。
- 只有明确具有 GravityBehavior、FallingBehavior 的特殊 Voxel 才会下落。
- Composite 继续使用完整六面连通与 Fragmentation（[03](03-composite-and-behavior.md)）。

### 所有权

任意 Voxel Instance 同一时间只有一个空间所有者：World Grid 或 Composite。规则见 [03](03-composite-and-behavior.md)。

静态 Building Composite 的实际体素占用登记到世界网格查询中；登记是指向 Composite 内体素的引用，不创建 World-owned 副本，不改变所有权。该格不能再放置其他地形或轨道体素；建筑修改与破坏后同步更新占用，见 [05](05-world-generation-and-settlements.md)。

## 首版范围

有限 3D Voxel World；World Chunk 仅作空间分区，不承担生命周期（转录 Q30）；统一 Durability；无结构支撑。

## 明确延后

World structural collapse、复杂重力系统、复杂 Damage Types、通用 Material System。

## 开放问题

- Chunk Size、World Size：未冻结参数（[roadmap](roadmap.md)）；Chunk 存储形态与全图常驻的内存预算在地图迷雾中。
