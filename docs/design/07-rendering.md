# 07 渲染

> **所属层**：体素引擎层与渲染层。
>
> **修订中**：由[地图](https://github.com/odradekk/train/issues/193)迷雾“观察与表现”改写：“Visual Streaming”“Render Origin”依赖全图常驻、全精度模拟（转录 Q5），已被 [ADR-0008](../adr/0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 取代；“LOD”中关于 Simulation LOD 的规则、“Track Render Section”的几何来源和“列车插值”的位置来源，要按局部场景改写；“本阶段表现验收契约”交给[地图](https://github.com/odradekk/train/issues/193)迷雾“新阶段”。三路表现、Dirty Rendering 和渲染作为派生缓存等规则继续有效。改写完成前，上述规则不能作为实现依据；其余规则继续有效。

> 决策出处：转录 Q126–Q143、Q179。

## 边界

本文定义表现层策略：三路 Voxel Presentation、Chunk Mesh、Custom Static Mesh、Specialized Renderer 与 TrackRenderer、Composite 渲染、Dirty 重建、Visual Streaming、LOD、Render Origin、Collision Adapter。所有内容都是 World State 的派生缓存，不改变 Simulation。相机见 [08](08-window-and-ui.md)。

## 不变量

- Rendering 与 Collider 都是 World State 的派生缓存；World Model 不保存 Godot CollisionShape 作为事实。
- 本文的表现层 LOD 只改变 Presentation，不改变 Simulation；Simulation LOD 是另需设计决策的模拟策略，不属于渲染优化。
- Composite 移动时只更新根 Transform，绝不因为移动重建内部 Mesh。
- Rebase 统一转换局部显示、相机及 Godot 碰撞/查询适配坐标；World Voxel Coordinate、RailPosition、聚居地位置、Save State、Seed、Simulation 永远不变。

## 规则

### 总体策略

领域世界和表现路径分开。Voxel Presentation 分为三路：Cube、CustomStaticMesh、Specialized。

### 本阶段表现验收契约

[三路体素表现、视觉驻留与渲染原点切换的验收契约](https://github.com/odradekk/train/issues/107)已确认 Q1–Q10。以下是本阶段目标契约，决策完成不表示实现或验收已经通过。

- 三路表现使用少量可长期复用的代表内容，并在同一场景共存。Cube 覆盖地形、建筑及车辆方块；CustomStaticMesh 至少包含一个朝向明显、带空隙的非对称模型，分别用于世界体素、建筑和车辆；Specialized 以真实铁路为代表，覆盖三维弯坡、道岔和损伤缺口。验证旋转、批处理、碰撞独立性和所有权转移，不以正式美术完成度作为本阶段门槛。
- 视觉驻留以当前观察范围为主。自由视角准备相机周边；跟随列车时结合被跟随列车的路径和速度向前预取。远处未被观察的列车继续完整模拟，但不强制保留其周边视觉；仅在面板选中远处对象不加载整片区域，执行聚焦或跟随后才准备目标区域。转动镜头不替代沿列车路径预取。
- 渲染原点由主观察位置驱动：自由模式依据相机，跟随模式依据正在观察的车辆，其他远处列车不独立触发切换。相机、地形、建筑、车辆、轨道和调试标记统一转换，世界坐标及模拟状态保持不变；暂停时移动相机也可触发切换。具体距离和性能阈值留待测量。
- 世界编辑器的近距离聚焦/跟随切换平滑过渡；远距离切换先准备目标区域，再短暂淡出/淡入，不要求飞越并加载整段路程。准备期间保留原视图并显示准备状态，不隐式改变模拟运行或暂停状态；失败保留原视图并报告原因，连续请求以最后一次为准。
- CustomStaticMesh 本阶段支持静态模型、基础颜色与贴图、正常光照和阴影，以及不透明和镂空裁剪材质。资产原点、尺寸和方向须有统一约定，零件按定义允许的正交朝向放置。半透明玻璃、骨骼动画、模型内部机械动画和任意自定义着色器不作为完成门槛；车辆整体运动不属于模型内部动画。
- 局部重建是必达要求：世界体素变化只更新受影响 Chunk 及必要邻块；修改单个建筑或车辆可重建该对象内部表现，但不能重建所有组合体；铁路变化只更新受影响局部区段。单纯移动组合体或切换渲染原点不重建内部几何。同批修改合并；未驻留区域变化不为此创建视觉，再次进入时从最新权威状态恢复。建筑残块转移与收回不能出现重复显示、残留碰撞或命中错误所有者。耗时及每帧处理量由性能验收确定。
- 驻留与可见性分开：进入加载范围后准备视觉，离开更大的卸载范围才释放；转身离开视锥只停止绘制，不立即卸载。判定使用实际空间范围而非仅对象中心，覆盖长列车、跨区建筑和同一受损建筑的多个分离部分，不强制特定索引或拆批方式。沿路径预取随换端、改道更新，不要求提前加载全部岔路。
- 遮面剔除只在能够确认相邻面完全被遮住时进行。非满格 Custom Mesh 和镂空材质默认不遮住整个邻面；不能从占用或碰撞盒推断视觉完全遮挡。本阶段不要求精确复杂遮挡算法。
- 模型、材质和专用表现绑定进入正式内容校验。缺失、损坏、不支持的必需资产，以及不存在或不适用的专用渲染绑定须明确报错；热重载失败保留旧内容及有效表现，不部分换新。首次加载沿用 [02](02-definitions-and-content.md) 的内容包校验规则，不把错误对象静默替换为 Cube 或隐藏后当作加载成功。纯表现更新不改变碰撞、身份、铁路连接或模拟结果。
- 必须同时提供自动检查与真实 Godot 人工观察；检查三路共存及修改、跨区观察、远处修改返回、近/远坐标与运行/暂停下的 Rebase，以及存档恢复。保持同一模拟输入时，改变相机、驻留范围或渲染原点不能改变领域结果。反复加载/卸载不能积累失效对象或持续泄漏资源；自动检查不替代闪烁、跳变、接缝和明显卡顿的人工观察。

#### 可重复验收矩阵

验收配置与操作步骤使用自动测试配置和普通示例存档（见 [09](09-persistence.md#来源示例维护与模式兼容)），不再新增永久专用场景入口。下表规定可观察结果，具体夹具规模、阈值及执行步骤由相应实施规格落定。

| 场景 | 必须观察到的结果 |
| --- | --- |
| 同区混合三路表现 | Cube、朝向明显且带空隙的 CustomStaticMesh 与正式轨道专用表现共存；世界、建筑、车辆分别使用适用的批次，没有逐体素独立 MeshInstance 或重复所有权显示。 |
| 原点、尺寸、朝向、材质与遮面 | 同一代表模型在世界、建筑、车辆中按统一原点与尺寸落位，无偏移或缩放差异，包括旋转与批处理之后；覆盖定义允许的正交朝向、不透明与镂空裁剪、基础贴图/光照/阴影。从模型孔隙能看见真实邻面，不按占格或碰撞盒误剔除。 |
| 世界边界、单对象及局部线路修改 | 同批修改合并，必要邻块一起刷新；只重建受影响 Chunk、组合体或轨道区段，无关对象不重建。纯运动不重建组合体内部几何。 |
| 损伤、修复及所有权转移 | 按 [03](03-vehicles-buildings-and-designs.md) 区分建筑与车辆断裂规则；建筑多核心岛仍显示为同一失效建筑，原位残块转世界及修复收回无重影、残留碰撞或错误命中归属。 |
| 热重载成功与失败 | 合法资产更新应用到受影响表现，身份、物理语义和模拟状态不变；非法候选明确失败，保留旧有效内容和表现，不部分替换或隐藏对象。非驻留区域返回后使用当前有效内容。 |
| 驻留边界来回移动及原地转身 | 加载/卸载范围不同，边界附近不反复创建释放；离开视锥不等于卸载。长列车、跨区建筑和分离部分不会因中心在范围外漏显。 |
| 自由观察、多列车切换及路径变化 | 相机附近和当前跟随列车的沿途区域准备正确；远处列车继续模拟且不强制各自保留视觉；选择不隐式聚焦，换端/改道更新预取，镜头朝向不替代列车路径。 |
| 远距离目标切换及失败 | 原视图保留到目标区域准备完成后再淡出/淡入；不要求加载两地之间全部区域，不隐式暂停。失败保留原视图，较早请求不能覆盖最后请求。 |
| 未驻留区域修改后返回 | 在远处执行正式修改、损伤、修复或内容更新，期间不为此创建视觉；返回后直接反映最新权威状态，无过期网格或碰撞。 |
| 近原点/远坐标、运行/暂停及连续 Rebase | 在合法世界边界内用相同局部布局与动作对照；相机、三路视觉、组合体姿态、碰撞命中、选择及调试标记保持一致，无额外可见跳变；Rebase 不重建内部几何。 |
| 观察与模拟隔离 | 相同初始状态、有效语义内容和模拟步输入，改变相机路径、驻留范围或 Rebase 时机，领域结果一致；运行计划、速度、轨道位置、ID 和断裂/故障状态不受影响。 |
| 保存恢复与重复进入 | 恢复权威状态后重建当前所需表现与碰撞，不复用过期缓存；保持实际损伤及轨道缺口。反复加载/卸载和目标切换不累积失效对象或持续泄漏。 |

近/远坐标对照用于证明显示精度与状态隔离，不代表已承诺某个超大世界容量。领域自动检查覆盖不变性、局部失效范围和所有权；真实 Godot 检查覆盖资产加载、批次、坐标换算、碰撞命中与生命周期；人工检查记录运行环境、配置、步骤及观感结果，不能把静态截图或 headless 通过当作动态观感通过。

#### 性能测量衔接

[性能验收](https://github.com/odradekk/train/issues/98)须消费上述正确性矩阵，确定代表规模、测量环境及门槛。记录驻留 Chunk/组合体/轨道区段数量、加载/释放次数、各类实际重建数量及耗时、目标区域准备耗时、Rebase 耗时、帧耗时和资源/内存随重复操作的变化；通过这些证据辨别局部更新是否退化成全图或全对象重建。

加载/卸载半径、沿路径预取距离、近/远切换界限、Rebase 触发与防抖条件、单帧重建预算、目标区域准备时间及长期资源稳定门槛均待实测，不在本文虚构数值。驻留/原点切换不能为达到性能门槛暂停远处模拟或改变运行规则。资产文件格式和具体绑定字段随实施明确并文档化，不预建通用资源或渲染框架。

### Cube Voxel Rendering

- Demo：Chunk → Face Culling → Chunk Surface Mesh，只生成未被视觉完全遮挡的面；是否占格或有碰撞盒不等于完全遮挡，见上文验收契约。不使用 one MeshInstance per voxel。
- 后期如果性能需要，Greedy Meshing 作为 ChunkMesher 的可替换优化；Greedy Meshing 不改变 World 与 Chunk 语义。

### Custom Static Mesh

- Custom Mesh Voxel 按 Chunk + Mesh + Material 进行局部批处理或实例化。Demo 只要求 LOD0；未来 Custom Mesh LOD 属于 Presentation Asset Metadata，而不属于 Gameplay Definition。
- 普通 Custom Mesh 的视觉主体应基本限制在自己的 Cell 内；需要真正跨 Cell 的视觉结构进入 Specialized Renderer。

### Specialized Renderer

需要领域专用视觉的 Voxel 使用独立 Renderer。首要例子是 TrackRenderer：Rail 仍然参与体素占用与破坏，但渲染网格从物理轨段保存的连续几何及当前有效部分派生，与列车行驶路径使用同一事实来源，见 [06](06-railway.md)。物理曲线参数不是表现缓存；网格与采样缓存可以丢弃重建。

不能只在渲染时平滑阶梯状轨道，或缩放、剪切、弯曲车体来掩盖运动约束错误。轨道破坏须显示实际缺口，剩余完好部分不重新拟合或移位。本阶段弯坡与水平基本道岔的几何边界见 [06 的验证契约](https://github.com/odradekk/train/blob/62c40d2/docs/design/06-railway.md#本阶段几何与车辆验证契约)；其拓扑与切换规则由网络运行决策确定，表现不能独立发明连接。

### Track Render Section

TrackRenderer 不为整个几公里的 Edge 建一个巨大 Mesh，而是拆成局部 TrackRenderSection，按 Chunk Boundary、Control Point、Junction 等自然边界切分。局部轨道变化只重建受影响的 Section。

### Composite Rendering

- Composite-owned Voxel 不进入 World Chunk Mesh。无论 Composite 是否移动，Demo 统一使用 CompositeVisual { Cube Batch, Custom Mesh Batch, Specialized Visuals }。
- 移动时 update root Transform only。Damage、Refit、Voxel Layout Change 及引用的表现资产变化标记受影响对象的内部 Visual Dirty 并批量更新；单个对象改变不能触发全部组合体重建。
- 未来可以针对大量静态 Composite 增加 Presentation-only Baking 优化。

### 列车插值

运动、接触点与车钩约束，以及断开、换端或支撑失效后的插值重建，统一遵守 [06 的沿轨渲染插值](https://github.com/odradekk/train/blob/62c40d2/docs/design/06-railway.md#沿轨渲染插值)；编组与停车状态由 [06 的运动规则](https://github.com/odradekk/train/blob/62c40d2/docs/design/06-railway.md#rail-movement) 决定。本节只定义显示层契约。

- 在相邻两个已完成模拟状态之间取插值时间，不外推未发生的运动，正常运行接受最多一个模拟步的显示延迟。
- 显示结果不回写 RailPosition、速度或其他权威状态；Godot 变换只是派生表示。
- 相机与车体使用所选车辆的同一份显示位置和车体朝向，不直接跟随跳动的 Tick 位置，也不以行驶方向代替车体朝向；目标选择与具体跟随语义见 [08](08-window-and-ui.md)。
- 真实 Godot 观感检查覆盖 [06 验证矩阵](https://github.com/odradekk/train/blob/62c40d2/docs/design/06-railway.md#本阶段几何与车辆验证契约)中的三维弯坡、混编、较长编组和事件后接续，检查刚性车体、车钩、轨道接缝与相机连续性。原型采样或 headless 检查通过不替代用户观感；几何失败不得由显示层修补后当作通过。

### Dirty Rendering

- 世界变化不逐 Voxel 立即重建 Mesh：World changes → mark affected Chunk dirty → coalesce → one rebuild。
- Chunk 边界修改要同时考虑相邻 Chunk 的暴露面变化。Demo 可以同步生成 Mesh，出现实际卡顿后再考虑异步 Meshing。

### Visual Streaming

- 遵守 [01](01-voxel-engine-and-scenes.md) 与 [04](04-deterministic-simulation.md) 的当前已定规则：World Data 完整常驻内存，全图全精度实时模拟（转录 Q5）。"不加载"只表示不构建或不保留当前区域的视觉表示，不暂停或降低该区域的模拟精度。
- 采用 Visual Residency + Frustum Visibility。Resident 区域使用 Load Radius < Unload Radius 形成 hysteresis，避免边界反复 Load/Unload。
- 当前跟随列车时根据其 route + velocity 做 directional prefetch，随换端/改道更新；不根据 Camera 朝向决定主要预取方向。自由视角准备相机周边，其他远处列车不各自要求驻留区域，见上文验收契约。

### LOD

- Demo 不实现真正的 Voxel Geometry LOD，依靠 Residency、Frustum Culling、Distance Culling、Custom Mesh LOD0。
- 后期性能需要时再增加 Greedy Meshing、Custom Mesh LOD、Voxel Geometry LOD。

### Render Origin

- Domain 使用绝对世界坐标，例如 WorldVoxelCoord = (183421, 42, 91822)。Godot Presentation 使用局部 Render Origin：RenderPosition = AbsolutePosition − RenderOrigin。由上文主观察位置规则触发 Rebase RenderOrigin，不能由各辆远处列车竞争驱动。
- 全图常驻内存与 RenderOrigin Rebase 并存是刻意选择：转录 Q179 否决了用 Godot 双精度构建承载大世界的方案，Rebase 是在单精度表现层保持精度的手段，不表示世界需要流式加载。
- Demo 的逻辑单位 1 Voxel Unit = 1 Godot Unit，但概念上保持解耦。

### Collision Rendering / Adapter

World Voxel Data → Chunk Collider Builder → Godot Collision。Voxel 被修改后标记 Collision Cache dirty。静态建筑的真实所有权与碰撞语义见 [01](01-voxel-engine-and-scenes.md) 和 [03](03-vehicles-buildings-and-designs.md)。

Rebase 时已有 Godot 碰撞表示、射线/命中查询与可见对象使用一致的局部坐标转换，命中结果仍能还原为绝对世界位置及真实 owner/part。没有某区域的视觉或 Godot 代理，不能让领域占用、净空或列车防护查询漏掉该区域对象；这些规则继续读取权威世界。进入区域或读档后从最新权威状态恢复必要缓存，不把过期碰撞当作当前命中结果。

## Demo 范围

Chunk Rendering（Face Culling）、Custom Static Mesh batch、AssetResolver、Dirty Chunk、TrackRenderer、Dynamic Composite Rendering、Visual Streaming。

## Demo 之后

Greedy Meshing、Voxel Geometry LOD、Custom Mesh LOD、异步 Meshing、静态 Composite 的 Presentation-only Baking；本阶段不以半透明玻璃、骨骼动画、模型内部机械动画、任意自定义着色器或正式美术完成度作为验收门槛。

## 开放问题

- 已确认依据：本阶段跨区域观察、修改/重建与长期运行的渲染性能、内存测量及用户冻结流程见[性能验收决议](https://github.com/odradekk/train/issues/98#issuecomment-5856117703)。
- 本阶段表现规则已由上文验收契约确定；Chunk Size、驻留/卸载半径、路径预取距离、Rebase 触发条件、重建预算和 Meshing Threshold 等数值须实测后提交用户确认，不能把方法决议当作数值已经冻结或实现已经通过。
- 地图迷雾（不立票）：本阶段之外的 Custom Mesh LOD Distance、Voxel Geometry LOD Distance、额外批处理或存储优化；实测表明需要选入后再评估，不预先承诺。
