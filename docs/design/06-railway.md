# 06 铁路

> 决策出处：转录 Q96–Q125、Q144–Q153；研究 [`docs/research/grid-rail-curve-slope-precedents.md`](../research/grid-rail-curve-slope-precedents.md)（票 #28）。

## 边界

本文定义铁路的 Domain：轨道体素与拓扑、RouteState、逻辑图、运营层、施工、净空、桥隧、列车运动、多接触点约束、碰撞、脱落。轨道视觉（TrackRenderer）见 [07](07-rendering.md)；相机跟随见 [08](08-desktop-window-and-camera.md)。

## 不变量

- Physical Rail Voxel 是铁路 Runtime Truth；Logical Rail Graph 是派生索引，不允许长期保留不存在的连接。
- RailPosition 是列车权威位置，World Transform 是派生结果。
- 世界生成器、玩家和 AI 最终统一调用 RailConstructionPlan。

## 规则

### 设计地位

铁路与火车的机械运行是游戏核心视觉体验之一。即使首版铁路控制系统被简化，也不能把以下问题视为可以永久接受的临时表现：火车突然转向、轨道接缝明显、车厢切角、坡道姿态跳变、火车穿过错误道岔、长车厢明显脱离轨迹。铁路拥有独立的 Domain 与 Presentation 系统。

### Rail Voxel

- 首版只有一种基础 `core:rail`。长期采用混合模型：Rail Definition = 轨道的种类、属性与能力；Track Topology = 当前实例的空间连接。
- 未来可以新增 heavy rail、high-speed rail 等；但直线、曲线、坡道、道岔、交叉不作为独立 Definition。

### Track Topology

- 不使用不断膨胀的简单 Shape Enum 作为长期核心。轨道采用 Track Ports + Internal Connections。
- Straight：Ports West、East，Connection West ↔ East。Curve：Ports North、East，Connection North ↔ East，即单格内转向（转录 Q96–Q125）。Crossing：N ↔ S、E ↔ W。Switch：可能的 West ↔ East、West ↔ North。
- 首版实际只需要 Straight、Curve、标准坡轨。坡轨首版为 1:1，一格升一格（转录 Q101）；视觉过陡再加 gentle_slope。
- 普通轨道的形状由邻接推导并缓存，岔路显式声明（转录 Q97）。

### RouteState

区分 TrackTopology（理论上可以怎样连接）与 RouteState（当前实际允许怎样连接）。未来道岔因此可以拥有真实 Runtime State、动画、故障和控制逻辑。

### Physical Rail 与 Logical Rail Network

轨道被破坏时：Physical Rail change → mark rail network dirty → incremental graph update。

### Compressed Rail Graph

- 不会把每个 Rail Voxel 当 Graph Node。只有重要位置作为 RailControlPoint，首版：Station、Junction、Endpoint；未来：Signal、Block Boundary、Special Track Device。
- 连续普通轨道压缩成 Edge：Control Point → Compressed Edge → Control Point。Edge 记录或引用实际 Physical Rail Path。

### Rail Operational Layer

- 物理轨道描述几何可能性；单向、封锁、信号、Reservation 等属于 Operational Rail Layer。例如 Physical A ↔ B，Operational A → B allowed、B → A forbidden。
- 首版默认所有轨道双向。架构预留单向、信号、Block、Reservation、Occupancy、调度。

### Rail Construction

RailConstructionPlan → Validate → RailEditTransaction → World Voxel Changes → Rail Graph Update。首版只有 Generator 使用；未来加入玩家铺轨、AI 铺轨、地形破坏施工时不建立另一套铁路修改规则。

### 铁路净空

- 游戏存在统一的 Track Gauge 与 Loading Gauge / Clearance Envelope，具体数值由测试确定。
- 玩家设计的车辆必须满足合法 Rail Contact、净空限制、车辆几何约束。RailConstructionPlan 同样验证整条线路的车辆 Swept Clearance，而不只检查 Rail Cell 本身。

### 桥梁和隧道

- Bridge 与 Tunnel 与 Rail Definition 正交：Bridge Structure + Rail Voxels，而不是 bridge_rail；隧道是 Terrain excavation + Tunnel structure + Rail。桥梁可以没有铁路，铁路也可以不在桥梁上。
- 首版世界生成器不自动生成复杂桥梁和隧道。

### Rail Movement

- 列车是 Path-constrained / Kinematic Entity，而不是 RigidBody 驱动（转录 Q144：RigidBody 会脱轨抖动、穿透、破坏确定性）。
- 权威状态 RailPosition { Rail Path / Edge, DistanceAlongPath, Direction, future lane/track info }；World Transform 是派生状态。

### 多接触点车辆约束

- 车辆不使用单个中心点切线决定姿态。Composite Definition 定义 Rail Contact / Bogie Anchors，首版至少 Front Contact 与 Rear Contact，分别计算对应 Track Path Point；车体位置与姿态由多个接触点共同决定。这是铁路视觉核心要求。

### 碰撞

- 正常列车运动不使用刚体物理求解。发生事故：Movement → Swept Collision Detection → Collision Event → Deterministic Collision Resolver → Damage Transaction。
- Godot Physics 可以辅助检测，但不成为正常列车位置和 Gameplay Damage 的权威来源。视觉层可以自由播放非确定性的粒子碎片，只要不改变 Simulation 结果。

### 动态 Voxel 脱落

移动 Composite 中的 Voxel 或 Fragment 脱落后，需要从连续空间回到整数 World Grid，采用逻辑 Snap + 视觉动画补偿。对整个 Fragment：

1. 获取当前连续 World Transform；
2. Rotation Snap 到最近正交方向；
3. Anchor Snap 到 World Grid；
4. 根据 Local Grid 得到全部目标 Cell；
5. 检查冲突；
6. 在有限半径内按确定性顺序寻找完整可放置位置；
7. 成功后整体 ownership transfer；
8. 如果无法完整放置则销毁；
9. Presentation 播放自然的坠落或碎裂动画。

不逐 Voxel 独立 round。

## 首版范围

Rail Ports 与 Internal Connections、Straight、Curve、Standard Slope、compressed graph、continuous path、RailPosition、multi-contact carriage movement（roadmap Phase 6）；所有轨道双向；只有 Generator 铺轨。

## 明确延后

玩家铁路施工 UI、AI 铁路施工、复杂桥梁与隧道生成、Signal、Block System、Reservation、Interlocking、复杂道岔控制、单双线运营规则、复杂车辆交通调度。

## 开放问题

- [原型化单格拓扑下的连续轨道几何：弯道半径与坡顶平滑](https://github.com/odradekk/train/issues/32)：TrackRenderer 与列车路径如何从单格拓扑派生连续几何（每格独立圆弧还是跨格平滑）；1:1 坡的坡顶坡底过渡与前后接触点姿态；最小可接受形态；是否需要 gentle_slope。研究 #28 指出先例中单格 90° 弯与 1:1 坡属极端，可迁移的是"最小半径约为接触点间距 3 倍"的比例、连接点落整数格而曲线中段自由、坡顶单独做过渡段。
- [定义 RailPosition 的归属、引用、断轨处理与渲染插值](https://github.com/odradekk/train/issues/33)：RailPosition 归 Train Assembly、每节 Composite 还是每个接触点；引用派生的压缩图 Edge 还是物理轨道 Cell 加格内进度，图增量重建时如何重映射；断轨附近已在行驶的列车如何处理（转录 Q110）；Simulation Tick 与渲染帧之间如何插值。
- 未冻结参数：Rail Gauge、Loading Gauge（[roadmap](roadmap.md)）。
- 地图迷雾：列车 Assembly 与耦合、多列车。
