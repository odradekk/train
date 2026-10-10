# Godot 宿主契约消费样例

这是[Godot 层公开契约：宿主适配、资源与交互接入](https://github.com/odradekk/train/issues/290)的可编译调用样例，只引用三个契约项目和 Godot 4.7.2。没有生产宿主、文件发布、业务规则、完整渲染器或第四个契约程序集。

`HostComposition` 接收装配好的三个工厂与内容宿主端口。`HostConsumer` 展示候选准备、场景创建、同一核心视图读取地图／面板／报价、同步 `Submit(command, source)` 和分发返回事件；推进一次只执行一个模拟步，通知策略在下一步前落实。新局／读档候选激活、缺失确认、内容候选、活动组切换和关闭仍须按跨层协议执行，样例不执行完整协调流程。宿主帧驱动、暂停来源记账和保存路径等内部方案见[宿主设计](../../docs/godot-host-design.md)。

`WorldDisplayConsumer` 将实际显示帧交回 `Present`，相机与拾取也使用该帧。仅在 `IFrameDisplay.TryInstall` 确认整帧成功接入后更新基准。`Failure`、`Faulted`、`Success` 内 `Readiness.Failed` 以及接入异常都会停止样例的旧帧交互；调用方接收结果及诊断或异常后隐藏场景、返回地图并关闭会话。这个样例内接口没有实现，不能用恒定成功的替身证明真实资源接入。编辑故障应保留工作草稿供保存、退出或重试，未在此样例实现编辑恢复流程。

`ResourceConsumer` 展示普通 `SceneMesh` 的顶点、法线、UV 和索引转换，以及固定 PNG 字节解码后创建纹理。它不接入材质、磨损、完整模型或缓存。场景格坐标直接成为 Godot 坐标，一格为一个 Godot 单位、物理意义为 0.5 米；GLB 原始米坐标才乘 2。契约的外侧逆时针三角形交换两个索引，适配 Godot 的顺时针正面。纹理与网格由调用方持有、释放，资源方法按本次宿主规范在 Godot 主线程调用。

API 依据：[ArrayMesh](https://docs.godotengine.org/en/4.7/classes/class_arraymesh.html)、[Image](https://docs.godotengine.org/en/4.7/classes/class_image.html)、[ImageTexture](https://docs.godotengine.org/en/4.7/classes/class_imagetexture.html)。绕序转换保留轴方向；相机节点的默认朝前方向不改变内容的 +Z 正面约定。

在仓库根目录使用锁定 SDK 验证：

```powershell
dotnet --version
dotnet build prototypes/godot-contract/Train.Godot.ContractSamples.csproj
```

编译只证明签名和 Godot C# API 可组合，不证明生产模块隔离、共享契约行为、GPU 渲染、资源退役或视觉正确；没有启动游戏或执行真实引擎验证。
