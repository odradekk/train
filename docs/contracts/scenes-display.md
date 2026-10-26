# 体素引擎：显示数据与拾取

本页记录显示数据与拾取，与[契约目录](scenes.md)一起使用。

## 显示更新与资源

`PresentCandidate(prepared)` 只供宿主预先准备跟随线路的下一节点视觉资源，不返回显示帧，也不参与 `Present` 的网格基准 D。无节点候选时返回 `Success(null)`；`prepared=null` 返回候选全部对象、`Base=null`，否则返回相对该回执的增量。宿主完整准备后才回传 `SceneUpdate.Frame`，未完成时继续传旧回执，已提供的更新保持到回执或候选撤销。无变化时可返回同一帧身份的空增量。外会话回执违反前提并抛 ArgumentException；已失效的本会话回执取得完整集合。候选撤销或换目标后，新候选使用不复用的 Space，宿主据此释放旧候选独有资源。

候选 `SceneUpdate` 的 Effects、Environment 内集合均为空，ShiftExistingRoots 为零；宿主只消费 Upserts／Removed 中的 SceneVisual，不接入对象姿态、雾、拾取或特效。候选升为当前节点时沿用 Space 与对象身份，正式 `Present` 仍按 D 返回完整更新，宿主按正式更新的姿态及视觉参数接入已准备资源。候选构建失败只使该候选返回 Failure，当前场景与核心继续运行；会话 Faulted 仍遵循会话故障规则。所有操作沿用世界会话的串行调用与不可变值所有权。

`ISceneFrame` 是可保留的不可变显示边界，包含同一时刻的姿态、拾取、空间映射和显示更新。帧身份统一使用 `Update.Frame`。`Present` 的 `displayedFrame` 必须是已经完整接入 Godot 的最后一帧 D，作为网格基准；不能传仅计算或准备过的帧。以 D 请求的准备期间姿态、相机和拾取使用最新返回帧 P，不使 P 成为网格基准；传 null 时保持旧画面和相机直到提交，样本仍使用 D。`Update.Base=null` 唯一表示完整替换：首次、传 null 或空间变更时返回该帧的全部对象。增量更新的 Base 必须等于 D 的 `Update.Frame`。

Godot 按以下语义准备并一次接入资源更新：完整替换时移除旧场景；增量相对 D 给现存根加 `ShiftExistingRoots`，再删除 Removed、加入或替换 Upserts，最后替换 Environment。准备期间已显示对象可先按 P 更新姿态；锁定活动更新帧 C 的 Base、Session、Request、Space 变化时取消准备，否则完成 C 后提交，并在提交同帧重施最新 P 姿态，不能重复叠加平移，规则见[Godot 契约](godot.md#显示帧相机与输入)。Upserts、Environment、效果和调试线已经使用新原点；父子关系无环，每个存活对象的父对象必须存在。移除包含不再存在的子对象，Godot 不猜测级联业务语义。同一更新内 ID 不重复且不同时出现在删除与更新中。

浮动原点平移不重建网格；运动只更新姿态并复用原不可变几何。`FindPose` 返回合成到当前根空间的显示姿态，相机与渲染共用它。`SceneCameraSample` 携带焦点所在的来源帧、焦点和必填的 `TerrainViewRadiusCells`；后者为场景格单位的有限正值，以焦点为中心保守覆盖实际相机完整投影包络的水平半径，不能用远裁剪平面距离代替，窗口宽高比或投影变化时必须重算。以 D 请求的准备期间为 P 和 P 根坐标中的焦点，传 null 时仍为 D 和 D 根坐标中的焦点，不要求来源帧等于网格基准 D。体素负责转到稳定空间并沿线路定位。线路 `Location` 帧没有同空间相机样本时，`Origin` 为请求点在线路空间中的位置。`Locate` 给出业务地点，`TerrainHeight` 返回相对本帧原点的精确地形格高度，尚无几何映射时为 Missing。`DisplayTerrainHeight(x, z)` 使用同一帧根空间的局部 X／Z 格坐标，返回相对本帧原点的显示地形格高度：优先精确地形，其次该帧实际可见的远景，结果为 `Lookup<double>`，有覆盖时返回 Found，两者均未覆盖时返回 Missing。输入坐标必须有限，否则抛 ArgumentException。该查询仅用于显示和相机防穿地；远景不能提供业务地点或交互资格，也不能改变 Ready、Locate 或 Resolve 的结果。

世界请求的 `SceneFrameRequest.PickRay` 为可选 `ScenePickRaySample`，携带最近一次实际传给 `ISceneFrame.Pick` 的帧（以 D 请求的准备期间为 P，传 null 时仍为 D）和射线；命中、未命中及 NotReady 都可提供样本。射线使用来源帧根坐标，不依赖 `Present` 的 displayedFrame，因此完整替换时仍可提供。仅开启 PickRay 且来源与新帧的 Session、Request、Space 都相同时，DebugLines 中输出一条射线：起点为来源 Origin 加射线 Origin 再减新帧 Origin，方向不变，长度为 MaximumDistanceCells；不额外执行拾取，不按命中距离截短。未开启、无样本或旧 Request／Space 时不输出射线。外会话样本或非法射线在修改会话前抛 ArgumentException；数值沿用有限、单位方向和正有限长度的射线前提。宿主在目标请求、会话或显示空间切换时清空旧样本。样本及调试线不改变 Pick 的只读语义，已返回帧仍不可变。

`NodePanBounds` 由体素给出节点场景完整的水平相机范围，分量为本帧根空间的 X、Z 格坐标，随浮动原点一起换算；不是当前已生成网格的包围盒。节点的 Partial／Ready 帧必须提供，Loading 尚未确定时可为 null；非节点帧为 null。Godot 只限制节点自由环绕的焦点，同空间后续帧范围缺失时保留焦点，不猜边界；跟随相机继续使用显示对象姿态。此字段由[Godot 宿主契约](https://github.com/odradekk/train/issues/290)补齐已有的相机规则，不冻结具体场景尺寸。

`NodeOverview` 是节点小地图使用的不可变几何概况，坐标与同帧 `NodePanBounds` 相同：`Vector2.X` 为根空间 X，`Vector2.Y` 为根空间 Z，单位为格，随浮动原点换算。节点的 Partial／Ready 帧必须提供，Loading 尚未确定时可为 null；非节点帧为 null。概况覆盖完整节点，不按当前已构建网格筛选，也不包含核心地块占用。`SceneNodeOverview.Lair` 是生物聚落占地的四角，按周长顺序，约定与 `SceneOverviewPlot.Corners` 相同。矩形覆盖已摆放模型的占格与平整区域。没有已放置占地时为空数组，包括聚居地、空节点、内容缺失和模型没有占地。一个节点至多一处。

`SceneOverviewPlot.TemplatePlotNumber` 对应核心 `PlotView.TemplatePlotNumber`，`Size` 保留内容布局中的地块尺寸类别，宿主不据此重算轮廓；`Corners` 是按周长顺序排列的四角，可直接绘制多边形。`SceneNodeOverview.Tracks` 的每项是一条铁轨中心折线，包含入口、站线、旁路线和回转线，与 `Centerline` 调试输出取相同路线，不含停车线；读取不要求开启调试图层。空节点和生物聚落的 `Plots` 为空，仍提供铁路 `Tracks`。生物聚落节点的 `Lair` 为上述四角；聚居地节点和空节点的 `Lair` 为空数组。同一布局与 `Origin` 的帧复用概况实例，调用方可按引用变化更新底图；不同原点下的概况不得混用。此契约变更依据 [#580 批准计划](https://github.com/odradekk/train/issues/580#issuecomment-6243568667)；生物聚落占地依据 [#682](https://github.com/odradekk/train/issues/682)。

Base 不匹配或适配失败时不应用资源更新。资源尚未准备好时，不部分接入新资源，也不把 P 设为 displayedFrame，但允许按上述规则更新旧几何姿态、相机和拾取；新对象等待资源就绪。下一次仍以最后完整接入帧 D 请求；必要时传 null 获取完整替换。绑定切换后旧画面失去交互资格，即使为过渡暂时保留，也不能继续拾取。GPU 资源的创建、原子接入和退役由 Godot 契约落实。

显示值及其全部集合元素不可变，调用方可以跨更新或关闭保留，不需要归还缓冲；不再使用时释放引用，由 GC 回收。关闭会话不会释放 Godot 的纹理、网格或材质。双方可共享不可变值，不要求每帧深拷贝全部世界；不公开租借期限、引用计数或区块释放协议。

## 网格、模型与效果数据

| 数据 | 格式与责任 |
| --- | --- |
| `SceneMesh` | 对象局部格坐标，顶点法线归一化，三角形索引合法且个数为三的倍数，从外侧看逆时针；Godot 转换引擎绕序 |
| `SceneVertex` | `readonly record struct`，字段及顺序保持 Position、Normal、TextureUv、FaceUv、ExposedEdges、RelativeHeight、Damage；TextureUv 为已计算的贴图坐标，可超出 `[0,1]`；FaceUv、外露边标志、相对高度、损伤供着色器使用，高度与损伤比例在 `[0,1]` |
| `SceneMaterial` | 面贴图只含资产修订、材质类别及可选涂装；钢轨、轨枕、水面和施工表现使用项目自带材质 |
| `SceneVisual.Model` | 一格一个原始模型引用，材质次序沿用内容元数据；对象 Pose 已包含格放置和绕格中心的朝向。Godot 只将模型米坐标换为格，不额外适配尺寸。相对高度平面把模型局部格坐标换成所属对象高度比例，用于积灰 |
| `Wheelset` | 体素给出位置、轴向、尺寸和转角，Godot 提供轮对模型；轴为对象局部 X，转角为弧度，尺寸为正数格 |
| `SceneLight` | 位于所属对象的局部原点，额外灯位使用子对象；范围为正数格，损伤比例 `[0,1]`；颜色与随机键交给 Godot |
| `PersistentSceneEffect` | 当前状态派生的烟、火等，位置相对对象，强度 `[0,1]`；对象移除同时结束其持续效果 |
| `SceneEffectCue` | 实际新事件产生的一次性效果，位置相对帧原点，稳定 ID 防重复；可带来自内容的碎片碰撞类别，不含 GPU 粒子资源 |
| `TerrainCollisionPatch` | 近似地形高度场，X 最快的行优先数组，长度 Width×Depth；高程相对 Patch.Origin，间距正数格。只供相机之外的碎片等表现碰撞，不做逐体素刚体 |

立方体贴图按体素的 24 种局部朝向选面：局部 +Y、-Y 分别用 Top、Bottom；局部 +Z 在 Front 与 Side 的 `AssetRevision` 不同时用 Front，其余面用 Side。侧面和 Front 的图像上方朝局部 +Y，顶面、底面朝局部 +Z。`TextureUv` 的 `(0,0)` 是图像左上角，U 沿「图像上方 × 外法线」增大，V 沿图像上方的反方向增大，从面外看图像不镜像。

Top、Bottom、Side 沿贴图轴以对象网格的整数坐标作向下取整的取模，按跨度 1／2／4／8 选取图中的子块；负坐标沿用同一规则。Front 与 Side 的资产修订不同时，Front 使用跨度 1；相同时视为没有 Front，局部 +Z 使用 Side 并参与平铺。内容侧在缺省 Front 时须保持 Front = Side。单格面仍使用原坐标；合并矩形从首格的整数取模相位连续增加 `TextureUv`，可超出 `[0,1]`，由宿主重复采样衔接，不能先将远坐标转成低精度浮点再取模。

按 [#388 批准决定](https://github.com/odradekk/train/issues/388#issuecomment-6241371082)，七种标准形状均在局部 `[0,1]³` 内，并使用同一套 24 朝向：

| 形状 | 局部几何 | 完全覆盖的格面 |
| --- | --- | --- |
| `cube` | 满格 | 六面 |
| `half` | 贴底，高 1/2 | 底面 |
| `plate` | 贴底，厚 1/8 | 底面 |
| `wedge` | `y ≤ 1 − z`，斜面朝 +Y、+Z | 底面、背面 −Z |
| `corner-wedge` | `y ≤ min(1 − x, 1 − z)`，斜面朝 +X、+Z | 底面 |
| `beam` | 沿 Y 贯穿、居中的 1/4 × 1/4 截面 | 无 |
| `cylinder` | 沿 Y、高 1、半径 1/2，12 边平面侧壁 | 无 |

形状按局部面法线最接近的轴选 Top、Bottom、Side 或 Front。Y 与其他轴平局时取 Y（顶面或底面）；水平 X／Z 平局取 X（侧面）。Front 仅用于严格最接近局部 +Z 的面，且仍遵守 Front 与 Side 修订不同才启用、跨度固定为 1 的规则。每个面的贴图轴和跨度沿用所选立方体面，用顶点的对象网格坐标计算 `TextureUv`，使相接的立方体与形状贴图连续。`FaceUv` 是顶点投影到该格面正方形的坐标；`ExposedEdges` 按立方体规则标记正方形周围邻格为空的边，不标记内部三角化边。

只有相邻形状旋转后完全覆盖相对格面时，才剔除本格位于格边界上的面。模型、缺失内容和部分覆盖面均不遮挡；部分覆盖与部分覆盖之间不裁剪或额外剔除。

每个模型格输出所在对象的一个子 `SceneObject`，稳定键为父键加 `:model:` 和 `LocalAt`。Pose 为格放置和绕格中心的朝向，再加旋转后的 `(0.5, 0, 0.5)` 底面中心偏移。`Materials` 原样保持 `SurfaceMaterials` 次序，`Paint` 取该格涂装；`Surface` 与所属对象几何相同。`RelativeHeightPlane` 将模型局部格坐标（不是米）经旋转和放置变换为所属对象网格 Bounds 最低至最高 Y 的高度比例。

已开启磨损的车辆和建筑按实例类别与数值生成稳定种子：9 字节输入首字节分别为 1（车辆）或 2（建筑），后 8 字节为 ID 的小端编码；SHA-256 前 8 字节按小端读取。同一实例的嵌套对象共用种子，移动或重建不改变；线路场景模型使用稳定摆放身份。只读预览与编辑没有实例身份，种子固定为 0。此次不扩大磨损开启范围：节点场景模型目前随地形关闭磨损，与设计 07 的要求仍有已知差异。

[#495 批准决定](https://github.com/odradekk/train/issues/495#issuecomment-6195218048)将 `SceneVertex` 从 `sealed record` 改为上述值类型，`SceneMesh` 的 `ImmutableArray` 及跨更新保留所有权不变。无磨损合并矩形的四边在单位格点分段，以中心扇形三角化；1×1 面保留四顶点、两三角形。`TextureUv`、`FaceUv` 和 `RelativeHeight` 在矩形内仿射插值，四角 `FaceUv` 为 0／1，`ExposedEdges` 为 None；`FaceUv` 与 `ExposedEdges` 仅在 `WearEnabled=true` 时参与着色，有磨损面保持逐格坐标与外露边。合并范围和不可合并条件见[区块面合并](../design/01-voxel-engine-and-scenes.md#区块面合并)。

体素只生成标准形状、铁路等自己的几何，不接收像素或导入模型几何。模型纹理、材质、磨损图案、粒子实现和缺失标记外观归 Godot，见[资产交接](README.md#表现资产的交接)。

体素保存最近两个相邻已完成模拟步的输入；同一步的命令更新当前边界，跨步缺失时只用当前值，不伪造中间运动。`Interpolation` 为 `[0,1]`，只在相邻边界插值，不外推未来；`SimulationSeconds` 是本帧对应的模拟表现时间。节点等离散转换不能把不同空间的坐标直接插值。

车辆、炮塔、生物、烟火和碎片均跟随这个时间，暂停时固定；相机和 UI 不受影响。Godot 将该时间用于自身粒子/碎片，不借真实帧增量继续播放世界效果；倍速下仍遵守[真实时间闪烁上限](../art/06-wear-damage-and-motion.md#特效的强度上限)。瞬时效果只来自收到且处于已观察场景中的事件，资源准备重试使用相同 ID；错过的旧事件、场景关闭期间的事件或已经结束的效果不补演。持续状态可从当前查询重建。效果按所属会话和 Space 清理，不跨场景残留。

## 拾取与命中有效性

`ISceneFrame.Pick` 使用该帧的网格与对象姿态，射线、命中点、法线和距离都在该帧根空间。Cell、Face 则属于命中对象自身格网，NestedObjectPath 指向嵌套对象；DDA 始终按完整格，与楔形、空心模型或视觉凹槽无关。距离最近的可拾取格优先；先进入未构建区域时返回 NotReady，不把它当透明空白。射线先命中可见远景地形时同样返回 NotReady，不能穿过远景继续拾取后方对象，也不能将远景作为业务地形命中。

钢轨、轨枕、水面及粒子不是可拾取体素，不阻挡射线；铁路施工仍从地图或面板指定线路与断点。缺失单格保留原格占位；缺失功能复合体占格未知，只在放置点显示标记，通过诊断定位，标记不拦截 DDA。

世界命中携带核心对象、设计及部件身份；地形命中携带业务地点。编辑命中携带编辑会话、修订及内部元素身份，不把编辑位置当成核心 PartId。`Resolve` 在当前世界输入下核对请求、空间、对象存在性与设计/部件是否仍适用；普通模拟推进本身不让所有命中失效，已毁部件、旧设计或旧观察请求则明确拒绝。它不执行任何核心命令，也不是之后命令的业务许可；宿主仍走正式命令入口。

旧帧在会话关闭后仍可用于历史显示或只读拾取，不能重新取得当前交互资格。编辑选择前，宿主核对命中的 EditSessionId 和 EditRevision 与 Read 相同；Apply 还会核对预览基准，不能只靠 UI 按钮拦截。
