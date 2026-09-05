# 07 渲染

> 决策出处：转录 Q126–Q143、Q179。

## 边界

本文定义表现层策略：三路 Voxel Presentation、Chunk Mesh、Custom Static Mesh、Specialized Renderer 与 TrackRenderer、Composite 渲染、Dirty 重建、Visual Streaming、LOD、Render Origin、Collision Adapter。所有内容都是 World State 的派生缓存，不改变 Simulation。相机见 [08](08-desktop-window-and-camera.md)。

## 不变量

- Rendering 与 Collider 都是 World State 的派生缓存；World Model 不保存 Godot CollisionShape 作为事实。
- LOD 永远只改变 Presentation，不改变 Simulation。
- Composite 移动时只更新根 Transform，绝不因为移动重建内部 Mesh。
- Rebase 只移动 Visual Proxy；World Voxel Coordinate、RailPosition、聚居地位置、Save State、Seed、Simulation 永远不变。

## 规则

### 总体策略

领域世界和表现路径分开。Voxel Presentation 分为三路：Cube、CustomStaticMesh、Specialized。

### Cube Voxel Rendering

- 首版：Chunk → Face Culling → Chunk Surface Mesh，只生成暴露面。不使用 one MeshInstance per voxel。
- 后期如果性能需要，Greedy Meshing 作为 ChunkMesher 的可替换优化；Greedy Meshing 不改变 World 与 Chunk 语义。

### Custom Static Mesh

- Custom Mesh Voxel 按 Chunk + Mesh + Material 进行局部批处理或实例化。首版只要求 LOD0；未来 Custom Mesh LOD 属于 Presentation Asset Metadata，而不属于 Gameplay Definition。
- 普通 Custom Mesh 的视觉主体应基本限制在自己的 Cell 内；需要真正跨 Cell 的视觉结构进入 Specialized Renderer。

### Specialized Renderer

需要领域专用视觉的 Voxel 使用独立 Renderer。首要例子是 TrackRenderer：Rail 仍然参与体素占用与破坏，但渲染网格从物理轨段保存的连续几何及当前有效部分派生，与列车行驶路径使用同一事实来源，见 [06](06-railway.md)。物理曲线参数不是表现缓存；网格与采样缓存可以丢弃重建。

不能只在渲染时平滑阶梯状轨道，或缩放、剪切、弯曲车体来掩盖运动约束错误。轨道破坏须显示实际缺口，剩余完好部分不重新拟合或移位；未来道岔、交叉与多轨结构另行设计。

### Track Render Section

TrackRenderer 不为整个几公里的 Edge 建一个巨大 Mesh，而是拆成局部 TrackRenderSection，按 Chunk Boundary、Control Point、Junction 等自然边界切分。局部轨道变化只重建受影响的 Section。

### Composite Rendering

- Composite-owned Voxel 不进入 World Chunk Mesh。无论 Composite 是否移动，首版统一 CompositeVisual { Cube Batch, Custom Mesh Batch, Specialized Visuals }。
- 移动时 update root Transform only。只有 Damage、Refit、Voxel Layout Change 才标记内部 Visual Dirty 并批量重建。
- 未来可以针对大量静态 Composite 增加 Presentation-only Baking 优化。

### 列车插值

- 采用 [06](06-railway.md) 的沿轨插值：在两个已完成模拟状态之间沿实际经过的物理路径求参考位置，再统一求解编组接触点与车钩约束，得到刚性车体姿态。不能独立直线插值各车世界坐标，否则会切角或破坏连接约束。
- 不外推未发生的运动，正常运行接受最多一个模拟步的显示延迟。显示结果不回写 RailPosition、速度或其他权威状态；Godot 变换只是派生表示。
- 编组断开、活动车头换端或轨道支撑失效时，终止旧插值并从已提交的事件后状态重新建立显示关系，不跨新旧参考点或不同编组插值。
- 相机与车体使用同一份显示姿态，具体跟随语义见 [08](08-desktop-window-and-camera.md)。

### Dirty Rendering

- 世界变化不逐 Voxel 立即重建 Mesh：World changes → mark affected Chunk dirty → coalesce → one rebuild。
- Chunk 边界修改要同时考虑相邻 Chunk 的暴露面变化。首版可以同步生成 Mesh，出现实际卡顿后再考虑异步 Meshing。

### Visual Streaming

- World Data 完整常驻内存（转录 Q5 的刻意选择）。"不加载"主要表示不构建或不保留当前区域的视觉表示。
- 采用 Visual Residency + Frustum Visibility。Resident 区域使用 Load Radius < Unload Radius 形成 hysteresis，避免边界反复 Load/Unload。
- 根据 Train route + velocity 做 directional prefetch；不根据 Camera 朝向决定主要预取方向。

### LOD

- 首版不实现真正的 Voxel Geometry LOD，依靠 Residency、Frustum Culling、Distance Culling、Custom Mesh LOD0。
- 后期性能需要时再增加 Greedy Meshing、Custom Mesh LOD、Voxel Geometry LOD。

### Render Origin

- Domain 使用绝对世界坐标，例如 WorldVoxelCoord = (183421, 42, 91822)。Godot Presentation 使用局部 Render Origin：RenderPosition = AbsolutePosition − RenderOrigin。火车移动到远处后可以重新 Rebase RenderOrigin。
- 全图常驻内存与 RenderOrigin Rebase 并存是刻意选择：转录 Q179 否决了用 Godot 双精度构建承载大世界的方案，Rebase 是在单精度表现层保持精度的手段，不表示世界需要流式加载。
- 首版逻辑单位 1 Voxel Unit = 1 Godot Unit，但概念上保持解耦。

### Collision Rendering / Adapter

World Voxel Data → Chunk Collider Builder → Godot Collision。Voxel 被修改后标记 Collision Cache dirty。

## 首版范围

Chunk Rendering（Face Culling）、Custom Static Mesh batch、AssetResolver、Dirty Chunk、TrackRenderer、Dynamic Composite Rendering、Visual Streaming（roadmap Phase 2、Phase 6）。

## 明确延后

Greedy Meshing、Voxel Geometry LOD、Custom Mesh LOD、异步 Meshing、静态 Composite 的 Presentation-only Baking、Simulation LOD。

## 开放问题

- 未冻结参数：Chunk Size、Render Residency Radius、Unload Radius、Directional Prefetch Distance、Meshing Threshold、Custom Mesh LOD Distance、Voxel Geometry LOD Distance（[roadmap](roadmap.md)）。
- 地图迷雾：Chunk 存储形态与全图常驻的内存预算。
