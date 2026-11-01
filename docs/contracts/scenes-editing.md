# 体素引擎：编辑与预览

本页记录编辑与预览，与[契约目录](scenes.md)一起使用。

## 设计编辑会话

`OpenEditor` 接收固定有效内容与 Complete 或 Partial 评估；宿主保证初始评估由这份内容产生。无需合法设计，Partial 始终具有承载已知布局、元素和锁的 Scene，但 Scene.Design 可以没有规范哈希。Failed 没有可解释草稿则拒绝。打开时建立自己的工作副本，初始无未保存修改；新建草稿是否需要先提示命名由宿主决定。一个会话始终使用该份有效内容，[结束编辑后再发布](editing.md#编辑会话与保存边界)，不提供会话内内容重绑定。

`Read` 返回不可变 EditState，`Capture` 返回整份固定草稿与 EditStamp；后续修改不影响它们。Elements 是当前固定修订的可定位元素清单，内部 ID 跨不改身份的操作保持，删除后不复用；诊断路径和数组下标只对应本修订。体素实现按原缺失引用维护锁，不等待迟到的完整内容评估才执行锁定。

`Select` 替换选区；`Enter` 只能进入可编辑的组装体/场景模型副本，null 回根。封闭功能复合体不能进入。切换层级清空选区，宿主丢弃旧悬停预览；选取、相机、平面、剖切均不修改草稿修订、脏状态或撤销栈。框选遇到封闭复合体按整件处理。

所有操作坐标属于当前进入对象。Properties 改该对象文字与同类别参数，不偷偷转换设计类别。PlaceNested 使用内容公开的副本或封闭引用；体素解析自己的工作布局进行编辑，但不重做内容哈希、BOM 或完整合法性。CaptureObject 导出可编辑嵌套副本在自身坐标中的独立草稿；它不改变宿主设计，不给封闭引用解封，也不能绕过缺失项的复制锁。另存蓝图仍须完整校验，并在结束编辑后由内容发布。

格范围包含两端；Move/Copy 的 Offset 是整数格偏移。RotateSelection 绕正轴按右手规则旋转，QuarterTurns 只接受 1–3，其他值返回 invalid_operation；Pivot 指定格的中心。格的朝向与旋转复合，None 体素保持 PxPy，复合结果不在 FourWay 允许集合时报告该格冲突，整笔拒绝；嵌套件的朝向同样复合。移动、旋转和复制的坐标超出 int 范围时返回失败，不回绕。Stroke.Samples 是每次立方笔刷的最小角格，覆盖各点起始的 BrushWidth³ 格后合并去重；宿主连续采样，不把离散采样间未覆盖的格隐式连线。笔刷预览与实际提交共用这些定义。

## 预览、提交与撤销

`Preview(EditOperation)` 是悬停查询，按调用时的草稿、选区和当前进入对象计算结果，不修改草稿，也不提交或保留待应用的操作。返回值的 Basis 标明计算所用的 EditStamp；宿主丢弃过期结果，并在输入参数、选区或进入对象改变后重新查询。提交无需先调用 Preview。

CoveredCells 是去重后的覆盖格数，AffectedCells 是实际将改变的格数，SkippedCells 是空格、当前对象已有的格或无变化等不适用格数。Add 跳过当前对象已有的格；Remove、Paint、Replace 跳过空格；这些规则不能绕过锁定或嵌套占格冲突。冲突导致整笔拒绝，不能计为跳过后部分提交。笔刷宽度最多 16，Cells 操作的单次覆盖最多 5000 格；上限按覆盖格数计算，即使其中大部分会跳过也不放宽。选区操作不设此覆盖上限，具体范围定义见[编辑规则](../design/15-design-editor-and-tools.md#编辑)。未知占格时 OccupancyComplete=false，不能声称已完成完整冲突检查。

宿主在悬停处绘制半透明范围：有冲突或 AffectedCells=0 时显示红色并说明原因，其他情况显示绿色。Preview 返回 Failure 时，宿主按输入中可表示的范围绘制红色覆盖并显示失败原因。绿色只表示本次操作可以改变草稿，不代表设计已通过完整合法性检查；占格未知仍须明确提示。

Conflicts 提供具体格或元素及原因。HighlightCells 在当前进入对象坐标中表示 RotateSelection 第 1 到第 QuarterTurns 个离散姿态的占格并集、放置/替换/移动/复制的目标覆盖，其余为操作范围；旋转的冲突与锁只按最终姿态检查，中间姿态只展示。Godot 画覆盖层，不自行算扫掠。工作平面与包络分别来自 EditViewOptions 和内容评估，Godot 可按这些公开几何值画线框。

Cells.Add 只对实际新增的格检查内容评估中的 Scene.AllowedEnvelope，超出时报告 outside_envelope，整笔拒绝。包络来自打开会话的初始评估，或最近一次 AcceptEvaluation 接受的评估；只有该评估草稿的 Specification 与当前整份草稿的 Specification 深层内容相同时才使用。当前对象中的格先转换到根对象坐标再检查。草稿修改清空 CurrentEvaluation 后，只要 Specification 仍相同，该包络仍有效；Specification 不匹配或 AllowedEnvelope=null 时不做包络检查。MoveSelection、CopySelection、RotateSelection 及其他操作不新增这项约束。内容层仍负责完整设计合法性，草稿暂时不连通或不合法仍可保存。

设备 rotation 行为的全角度范围与冲突格读取当前评估的 Scene.RotationSweeps，不能用 HighlightCells 代替。按 NestedItemIndices 匹配选中对象（空路径为根），体素用已有层级放置与本帧原点变换生成覆盖线框，经 Present 返回帧的 DebugLines 交出；这些编辑线框不要求开启开发调试图层。Godot 直接绘制帧中线框，不转换嵌套设计坐标或重算内容合法性几何。SweptCells 与 ConflictCells 的 Known/Unknown 分别显示，未知提示读取 CurrentEvaluation，不能将未知冲突显示为完整无冲突。AcceptEvaluation 拒绝的迟到结果不能更新这些覆盖层；草稿改变后随 CurrentEvaluation 清空，不能保留旧范围冒充当前结果。

`Apply(EditOperation)` 按调用时的草稿、选区和当前进入对象重新检查并同步提交操作，不接收 Preview 的返回值。改变选区或进入对象后，Apply 使用新的选择和坐标空间；此前查过哪份预览不影响提交。操作无需二次确认，超过单次覆盖上限或存在已知冲突时整笔拒绝，草稿、修订与撤销不变。未知引用保留并锁定，父级、选区、移动、旋转、复制、替换等均不能间接改变它；允许编辑其他已知部分不意味着未知处被推定为空。

宿主将单格点击直接交给 Apply；笔刷从按下到松开累计 Stroke，松开后提交一笔。长方体第一击确定角 A，第二击确定角 B 并提交：每个轴分别取两角的最小值和最大值，添加时再把最大 Y 增加高度减一，高度至少为 1；移除、涂装和替换使用两角范围，不叠加高度。选区操作和属性按钮也直接提交各自的 EditOperation，不设“应用预览”步骤。

一次成功修改整笔入栈，草稿修订增加；无变化操作不制造修订或历史。Undo/Redo 同样产生新的编辑修订，恢复对应工作值，不能使旧异步结果重新有效；新编辑清空重做。撤销不限步数但只活在本会话中，不进存档。体素内部用不可变共享或其他实现控制成本，不把存储结构列入契约。

PaintSelection／ShapeSelection 递归处理选中的可编辑副本；功能复合体只整件改 Paint，形状操作把其占格计入 Skipped。覆盖格去重计入 Covered，实际改值的格计入 Affected，空格、未变化或不支持目标形状的格计入 Skipped。单格／笔刷／Box 的 Paint／Shape 只改当前对象自己的格，落在其他嵌套对象占格上时报告 occupancy_conflict，不能部分提交。复制后仍选择原元素；空选区的预览影响 0 格，Apply 不产生历史。

## 校验与保存完成

宿主 Capture 后把 Draft 交给固定内容的 EvaluateDesign，可在后台运行；回到协调线程调用 AcceptEvaluation。宿主负责把原请求标识与结果成对保留，并保证计算使用的内容世代等于 EditStamp.Content；DesignEvaluation 本身不携带来源证明，不能把另一份内容的结果重新贴上当前标识。会话核对请求中的会话、修订、内容世代及结果草稿与当前值相符才接入，返回 true。草稿匹配必须递归比较全部字段及集合中的内容，不能用默认 record 的 == 或 Equals 代替深层内容比较；迟到或关闭返回 false，不覆盖当前诊断、缺失锁或按钮状态。每次草稿变化后 CurrentEvaluation=null，原结果不能冒充当前结果；旧内容评估故障按原请求报告，不污染新请求。

MarkSaved 只在对应保存目标成功后调用：本局草稿内容记录已提交就成功，用户/包草稿须写盘成功。它确认捕获版本，而不是宣布游戏已经存盘；期间新增修改仍脏，旧会话通知返回 false。撤销恢复到已确认保存的同一工作值时可以清除编辑脏标记，不依赖业务设计哈希。保存失败不调用它，保留活动草稿和旧记录。

设计保存、蓝图发布及核心提交由内容/Godot 负责，体素没有文件写入或核心提交方法。关闭前的保存/放弃/取消由宿主执行；Dispose 不自动保存，立即使预览、校验接入及修改入口失效，重复 Dispose 无副作用，保留的快照和帧仍不可变。

## 只读设计预览

图标与设计缩略图使用 `PreviewDesign(content, initial)`，无需打开编辑会话。宿主保证评估来自这份固定内容；与 OpenEditor 一样，Complete 或 Partial 可以预览，Failed 没有可解释草稿则拒绝。返回 `IDesignPreview`，只提供内容世代、`Present` 和 `Dispose`，不建立撤销、编辑修订、选区或脏状态。

`Present` 使用与世界、编辑及模板预览相同的体素网格生成规则，返回时间为零的完整替换帧。缺失引用保留已知部分和明确诊断，不把预览成功当成设计合法。这个入口只承诺固定设计显示，不新增通用预览框架；网格、材质引用、坐标和不可变值责任沿用本页的[显示契约](scenes-display.md#显示更新与资源)。调用同步完成，不启动需要宿主轮询的后台任务。Dispose 结束预览资格，重复关闭无副作用，关闭后的 Present 抛 ObjectDisposedException；已返回帧仍可保留，Godot 自行持有和释放渲染资源。

货物图标按 `FindGood(good)` 的 `DisplayDesign` 调用 `FindDesign`，再创建只读预览并 `Present`。宿主无需读取布局或重新选择同构成的设计；无显示设计的货物走其已有图标入口。可编译调用见消费样例的 `DesignIconConsumer`。

## 开发模板预览

聚居地模板由 Godot 持有 `SettlementTemplateDraft` 表单；PreviewTemplate 接收固定草稿、修订及内容，生成场景模型、已知预建建筑与地块显示资料。它不需要创建虚假核心世界，不修改表单，不拥有第二套撤销或发布流程。可在后台调用，仅结果仍匹配该表单修订和内容时接入。

ITemplatePreview.Frame 是完整替换帧；Plots 提供地块局部范围及在预览空间中的姿态。DraftIndex 使用固定数组下标，草稿里重复的地块编号不会错误定位另一个条目。PickPlot 单独拾取地块占地，最近者优先、等距离按下标；普通体素拾取仍用 Frame.Pick。Godot 根据拾取结果修改公开表单字段，再请求预览，不自行遍历场景模型布局或摆放建筑几何。

缺失模型或建筑时返回已知部分与明确诊断，不虚构引用的内容；无法解释的输入返回 Failure。预览成功不代表模板合法，完整规则及发布校验仍归内容。结果为不可变值，无需 Dispose；旧结果只可保留展示，不能覆盖更新的表单。
