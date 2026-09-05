# 06 铁路

> 决策出处：转录 Q96–Q125、Q144–Q153；研究 [#28](../research/grid-rail-curve-slope-precedents.md)；[#32](https://github.com/odradekk/train/issues/32) 的二维与 Three.js 原型及后续确认取代单格内转向、默认 1:1 坡和邻接推导曲线的旧方案。

## 边界

本文定义铁路的 Domain：轨道体素与拓扑、RouteState、逻辑图、运营层、施工、净空、桥隧、列车运动、多接触点约束、碰撞、脱落。轨道视觉（TrackRenderer）见 [07](07-rendering.md)；相机跟随见 [08](08-desktop-window-and-camera.md)。

## 不变量

- Physical Rail State（轨道体素、连续几何及明确连接）是铁路 Runtime Truth；Logical Rail Graph 是派生索引，不允许长期保留不存在的连接。
- RailPosition 是列车权威位置，World Transform 是派生结果。
- 世界生成器、玩家和 AI 最终统一调用 RailConstructionPlan。

## 规则

### 设计地位

铁路与火车的机械运行是游戏核心视觉体验之一。即使首版铁路控制系统被简化，也不能把以下问题视为可以永久接受的临时表现：火车突然转向、轨道接缝明显、车厢切角、坡道姿态跳变、火车穿过错误道岔、长车厢明显脱离轨迹。铁路拥有独立的 Domain 与 Presentation 系统。

### Rail Voxel

- 首版只有一种基础 `core:rail`。长期采用混合模型：Rail Definition = 轨道的种类、属性与能力；Track Topology = 当前实例的空间连接。
- 未来可以新增 heavy rail、high-speed rail 等；但直线、曲线、坡道、道岔、交叉不作为独立 Definition。

### Track Topology

- 不使用不断膨胀的简单 Shape Enum 作为长期核心。保留 Track Ports + Internal Connections 表达明确连接，但不再以单格正交端口或面中点规定连续曲线的形状。
- 首版是直线、连续水平弯道及带坡顶坡底过渡的缓坡。总方向可改变 90°，但不存在瞬时直角转向，也不让列车沿体素阶梯逐格转向。
- 两段轨道相邻或空间接近不代表连通；连接必须由物理轨段的明确连接决定，并具有一致的接续几何。交叉、道岔与多轨结构后续专门设计。

### 连续几何与物理状态

- 先设计连续线路，再派生轨道占格与空间连接登记。空间不足时调整线路或拒绝生成，不缩小到单格急弯，也不只在表现层假平滑。
- 物理轨段保存曲线参数及端点连接，轨道体素关联所属轨段。仅凭占格列表无法唯一恢复曲线；几何是物理状态的一部分，不是可任意重新猜测的渲染缓存，见 [ADR-0006](../adr/0006-preserve-physical-rail-geometry-and-local-breaks.md)。
- 渲染网格、行驶路径查询、弧长采样表和压缩路网由同一物理状态派生。读档恢复原有形状；保存与重建遵守 [09](09-persistence.md)，不保存 Godot Mesh 作为权威数据。
- 水平弯道平滑进入、退出曲率；坡道平滑改变倾角，坡顶坡底需要实际过渡空间。不再默认 1:1 坡，也不把 gentle_slope 作为观感不好时才补的选项。
- 原型采用曲率渐变段连接圆弧、倾角渐变段连接直坡；具体数值算法与采样精度在实施时确定，须遵守 [04](04-deterministic-simulation.md)。不能把穿过阶梯状 Port 中点的任意样条当成已满足半径与净空约束。

### 轨道占格

- 按钢轨、轨枕等实际轨道结构沿曲线形成的实体体积登记占用，不能只登记中心线经过的格子。被占用的轨道格关联其支撑的曲线区间；占格与区间关系在生成时建立。
- 车辆通行净空单独检查，不把整片净空登记为轨道体素。桥梁等支撑结构仍与轨道正交。
- 首版不允许两条独立轨道共占一格；生成器须调整线路或拒绝方案。轨段的技术切分不应人为制造独立线路或额外连接。
- 占格与区间映射、边界相交的数值处理属于实施验证内容；视觉装饰不能独立决定占格或连通性。

### RouteState

区分 TrackTopology（理论上可以怎样连接）与 RouteState（当前实际允许怎样连接）。未来道岔因此可以拥有真实 Runtime State、动画、故障和控制逻辑。

### Physical Rail 与 Logical Rail Network

- 破坏一格，仅令依赖该格的曲线区间不可通行，不因存储时将曲线划成很长的物理轨段而整段失效。
- 两侧完好部分保持原位置和曲率，不重新平滑，也不跨缺口自动连接；渲染须显示实际缺口，不能继续绘制完整铁轨。
- 原曲线参数可用于定位剩余部分，但不能证明缺失区间仍有轨道。有效通行区间必须结合当前轨道体素判断。
- Physical Rail change → mark rail network dirty → incremental graph update；派生图未完成更新时也不能授权跨越实际缺口。列车已在断口附近的处理及位置重映射由 #33 确定。

### Compressed Rail Graph

- 不会把每个 Rail Voxel 当 Graph Node。只有重要位置作为 RailControlPoint，首版：Station、Junction、Endpoint；未来：Signal、Block Boundary、Special Track Device。
- 连续普通轨道压缩成 Edge：Control Point → Compressed Edge → Control Point。Edge 记录或引用实际 Physical Rail Path。

### 站点接轨接口

- 聚居地 Layout 中的 RailSocket 描述站点的接轨位置与方向，一个站点可声明多个接口；它不等于物理轨道的 Track Port，也不直接生成铁路连接或 RailControlPoint。
- 物理轨段的明确连接与当前有效区间决定实际物理连通性，Station 是压缩图中的重要位置。站点存在、有接轨接口、已接入铁路网络是不同事实；具体归属见 [05](05-world-generation-and-settlements.md)。
- 多个 RailSocket 不引入首版多站台运营或复杂调度要求。

### Rail Operational Layer

- 物理轨道描述几何可能性；单向、封锁、信号、Reservation 等属于 Operational Rail Layer。例如 Physical A ↔ B，Operational A → B allowed、B → A forbidden。
- 首版默认所有轨道双向。架构预留单向、信号、Block、Reservation、Occupancy、调度。

### Rail Construction

RailConstructionPlan（连续几何、连接与占格方案）→ Validate → RailEditTransaction → Physical Rail State Changes → Rail Graph Update。几何、连接与体素修改原子提交。首版只有 Generator 使用；未来加入玩家铺轨、AI 铺轨、地形破坏施工时不建立另一套铁路修改规则。

### 铁路净空

- 游戏存在统一的 Track Gauge 与 Loading Gauge / Clearance Envelope，具体数值由测试确定。
- 所有合规车辆都应能通过正常生成且完整的线路。首版不设仅允许小车或大车的线路等级；尺寸档位不等于线路兼容等级。
- 玩家设计的车辆必须满足合法 Rail Contact、净空限制、车辆几何约束。RailConstructionPlan 同样验证整条线路的车辆 Swept Clearance，而不只检查 Rail Cell 本身。
- 静态 Building Composite 的实际体素占用与碰撞纳入统一查询：轨道不能与建筑体素占同格，车身经过的净空也必须检查。建筑修改或破坏后查询随之更新；登记不复制体素或改变所有权。

### 单节车辆尺寸

单位为体素格；每个车头或车厢 Composite 单独声明档位，不限制整列长度。尺寸是允许包络的上限，可以不填满，不能自动升档。

| 档位 | 长 | 宽 | 高 |
| --- | --- | --- | --- |
| 小型 | 8 | 5 | 6 |
| 中型 | 12 | 7 | 6 |
| 大型 | 16 | 9 | 8 |
| 超大型 | 20 | 11 | 8 |
| 泰坦 | 24 | 13 | 10 |

- 所有车体体素、附加模块与实体装饰都必须在声明包络内；任一方向越界即非法，改装整体拒绝并保留原车辆状态，见 [03](03-composite-and-behavior.md)。
- 包络相对车辆与接触点基准固定，不只比较可任意平移的包围盒尺寸。原型纵向以两接触点中点为基准，横向对称限制为 ±宽/2；满宽车体侧移同样越界。底部高度与接触点的最终规则仍需机械约束验证。
- 尺寸须支持可进入的内部空间，而非只供外挂模块。原型的一格厚外壳与内部尺度是布局验证，不代表角色、门洞、楼梯或可达性已实现；正式设计器与相关玩法仍延后。
- 尺寸合规只是车辆合法性的必要条件，还须满足接触点、车钩、净空及车体不相交等机械约束。档内任意接触点布置不因此自动合法。

### #32 验证结论与边界

- 用户确认二维观感及 Three.js 简单体素三维观感，五档尺寸（包括泰坦）正式确定。三个原型归档于独立分支，资产指针见 [#32](https://github.com/odradekk/train/issues/32)，不作为正式产品代码。
- 当前起始参数：水平半径 192 格、缓和长度 160 格、坡角 10°、左右各 8 格净空、顶部净空 12 格。它们是已通过当前原型的候选参数，不是数学最小安全值，也不是现实铁路标准。原型反向弯的单弯转角为 45°，缓和长度实际限制到不超过 R×π/4（约 150.8 格）。
- 二维固定参数复核覆盖水平弯、反向弯、坡道三个独立案例 × 25 种满尺寸有序双车组合 × 321 个位置，共 24,075 个姿态样本，未发现超净空、侵轨、车体相交或连接缺失；固定轴距与车钩长度误差小于 0.000000004 格。采样通过不等于连续区间的数学保证。
- Three.js 验证了空间尺度、内部展示与刚性车体姿态；三个运行案例仍各处于一个平面，不构成水平弯与坡道叠加的验证。显示只做平移旋转，不能缩放、剪切或弯曲车体来掩盖约束错误。
- 原型接触点间距为车长减 2、前后各悬伸 1、车钩长 1；轨距、轮组、底部高度等也只是实验配置，不自动成为正式规格。
- 实施阶段须补齐实际轨道占格与局部断轨、存档恢复、档内允许设计的机械约束、弯坡叠加、多节编组、倒车、多轴约束、连续扫掠与 Godot 集成验证。未验证的线路组合不默认开放给生成器；原型不证明所有合规车辆已经能通过所有线路。

### 桥梁和隧道

- Bridge 与 Tunnel 与 Rail Definition 正交：Bridge Structure + Rail Voxels，而不是 bridge_rail；隧道是 Terrain excavation + Tunnel structure + Rail。桥梁可以没有铁路，铁路也可以不在桥梁上。
- 首版世界生成器不自动生成复杂桥梁和隧道。

### Rail Movement

- 列车是 Path-constrained / Kinematic Entity，而不是 RigidBody 驱动（转录 Q144：RigidBody 会脱轨抖动、穿透、破坏确定性）。
- RailPosition 是权威位置状态，描述沿轨进度与方向；World Transform 是派生状态。归属、轨道引用及更新规则由 #33 确定，本文不预先选择派生 Edge 或物理轨段作为持久引用。

### 多接触点车辆约束

- 车辆不使用单个中心点切线决定姿态。Composite Definition 定义 Rail Contact / Bogie Anchors，首版至少 Front Contact 与 Rear Contact，分别计算对应 Track Path Point；车体位置与姿态由多个接触点共同决定。这是铁路视觉核心要求。
- 前后接触点须保持车体中的固定空间距离，不能直接用相同数值的轨道弧长差代替；沿弯道两者不同。车体始终刚性运动，车钩连接同时受约束。具体位置归属与沿线求解由 #33 接续。

### 碰撞

事故碰撞触发伤害的玩法留到后续版本；首版通过 [03](03-composite-and-behavior.md) 的调试伤害操作验证损伤与断裂。以下为后续事故系统的边界，不构成首版实现要求。

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

物理轨段连续几何、明确连接、实体占格与局部断轨；直线、连续水平弯与平滑缓坡；五档单节车辆包络；compressed graph、RailPosition、multi-contact carriage movement（roadmap Phase 6）；所有轨道双向；只有 Generator 铺轨。

## 明确延后

玩家铁路施工 UI、AI 铁路施工、复杂桥梁与隧道生成、交叉与道岔结构、Signal、Block System、Reservation、Interlocking、复杂道岔控制、单双线运营规则、复杂车辆交通调度。

## 开放问题

- [定义 RailPosition 的归属、引用、断轨处理与渲染插值](https://github.com/odradekk/train/issues/33)：RailPosition 归 Train Assembly、每节 Composite 还是每个接触点；基于物理轨段、有效区间与派生压缩图的引用及重映射；断轨附近已在行驶的列车如何处理（转录 Q110）；Simulation Tick 与渲染帧之间如何插值。以本文 #32 决定为前提，不再局限于“压缩 Edge 或 Cell 加格内进度”的旧二选一。
- 未冻结参数：Rail Gauge、Loading Gauge（[roadmap](roadmap.md)）。
- 地图迷雾：列车 Assembly 与耦合、多列车。
