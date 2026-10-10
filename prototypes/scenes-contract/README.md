# 体素契约消费样例

这是[体素引擎层公开契约：场景、拾取与编辑会话](https://github.com/odradekk/train/issues/289)的调用结构样例，只引用 `Contracts/Scenes`（及其核心、内容契约引用），不引用生产项目。没有场景实现、模拟替身或行为测试。编译成功只证明类型和调用能组合，不证明几何、后台取消、帧差分或保存行为正确。

世界宿主先以同一完整边界的核心视图与内容调用 `WorldConsumer.Open`，成功后持有返回会话并创建消费器。调用 `RequestView` 取得观察请求，再串行调用 `Update` 交入后续固定边界及新事件。`WorldConsumer.Update` 以 `Update.Base=null` 识别完整替换；增量只在 Base 等于实际显示帧的 `Update.Frame` 时接入，基准不匹配时以 `Present(request, null)` 请求一次完整替换，并再次检查结果。`Present` 的 `displayedFrame` 只在 Godot 完整接入后更新；恢复失败、资源仍在准备或接入失败时保留上一显示帧。射线也使用该显示帧的浮动原点，命中后通过 `Resolve` 核对当前交互资格。关闭时使用 `await using` 或等待 `DisposeAsync`，不提前丢弃关闭任务。

编辑宿主先用内容的 `DesignEvaluation` 打开会话，并以 `using` 管理消费器。`Edit` 对同一份预览询问确认，再应用；`Evaluate` 固定捕获，交内容校验，并用捕获的修订标识接入结果。`CaptureObject` 提取嵌套对象用于另存，不要求宿主解读布局。`SaveDraft` 只保存草稿，经内容准备和宿主真实提交成功后调用 `MarkSaved`，允许保留编辑会话。发布有效蓝图先调用 `CaptureAndClose`，再由宿主用返回草稿准备和提交蓝图；嵌套对象也先捕获并关闭再发布。宿主提交端口负责更新活动内容。样例保留关闭后的不可变帧价值，但关闭消费器后不得继续调用交互方法。

`ISceneDisplay` 和 `IContentCommit` 只有边界声明，没有实现。实际适配器必须兑现注释中的成功语义；不能用恒定返回 `true` 验证契约。保存目标成功与随后 `MarkSaved` 接受是两个结果；方法返回 `false` 不表示目标一定未提交。

`ReadRotationMetadata` 只示范同步读取会话当前评估中的设备转动元数据，保留修订和已知／未知区别；null 表示没有当前评估。正式覆盖线框由体素通过既有 `Present → ISceneFrame.DebugLines` 交出，Godot 直接绘制。这个读取样例不实现选区匹配、坐标转换或绘制，也不证明嵌套转动和实际覆盖层正确。

显示调用成功但返回 `Readiness.Failed` 仍是构建失败，不能接入该帧。调用方收到调用失败、Failed 帧或资源故障后立即禁止旧帧交互，并按 [Godot 故障入口](../../docs/contracts/godot.md#界面命令与诊断)处理；这里没有实现完整宿主的返回地图或保留编辑草稿流程。

`DesignIconConsumer` 先读取货物的显示设计，再调用 `FindDesign`、`PreviewDesign` 和 `Present`；只读预览通过 `using` 释放，不创建编辑或撤销状态。没有显示设计的货物交由已有图标入口处理。该样例只证明调用可组合，不验证网格算法。

`TemplateConsumer` 把固定模板草稿交给 `PreviewTemplate`，完整接入后才记为实际显示预览。拾取同时核对内容世代及表单修订，再使用返回的草稿数组下标；表单修改后不能把旧预览下标解释为新条目。

节点自由环绕用 `ConstrainNodeFocus` 读取实际显示帧的 `NodePanBounds` 限制水平焦点；还没有范围时保持当前焦点。调用方先按浮动原点变化转换焦点，不能把别的帧坐标传入，也不能从已生成的部分网格估算完整范围。

在仓库根目录使用锁定 SDK 和 PowerShell 7 验证：

```powershell
dotnet --version
dotnet build prototypes/scenes-contract/Train.Scenes.ContractSamples.csproj
```
