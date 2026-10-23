# Godot 资产端口

`App/Assets/` 实现 `IContentFiles`、`IAssetInspector` 和从修订字节创建的资源缓存。生产宿主已通过 `App/Composition/` 装配这些端口，由 App 每帧泵送并按退出顺序释放；#319 负责模型最终材质。旧 `GodotContentSource` 已删除。

## 文件与线程

在 Godot 主线程创建 `GodotMainThreadDispatcher` 和 `ContentFiles`，每帧调用 `Pump()`。`Invoke<T>(Func<T>)` 在主线程直接执行；后台调用入队并等待自己的结果。主线程不能同步等待后台加载任务。文件枚举、原始字节读取、PNG 尺寸检查和 GLB JSON 预检在调用线程完成；GLB 解码，以及贴图、网格、材质等运行资源的创建和释放仍由 dispatcher 在主线程协调。哈希由 Content 计算，缓存使用其不透明的 `AssetRevision`。

`ContentFiles(GodotMainThreadDispatcher, IReadOnlyDictionary<ContentRoot, ContentRootLocation>)` 接收构造方提供的根登记表：`ContentRootLocation.Disk(absoluteDirectory)` 或 `ContentRootLocation.Resource("res://...")`。只有 `ContentFiles` 在构造时用 `OS.HasFeature("template")` 区分资源根的实现：[导出模板具有 template 特征，编辑器二进制没有](https://docs.godotengine.org/en/4.7/tutorials/export/feature_tags.html)。源码运行把资源根通过 `ProjectSettings.GlobalizePath` 解析成绝对路径，复用 `DiskContentFiles`；导出使用 PCK 的 `DirAccess`／`FileAccess`。显式磁盘根始终使用磁盘实现；未登记的根属于调用错误。磁盘根本身及根以下路径各段都拒绝链接／reparse point，枚举保留 File／Directory／Link。读取最多消费 `maximumBytes + 1` 字节，返回不可变副本，区分 Missing、LimitExceeded、LinkedPath 和 Inaccessible。

线程划分依据是 Godot 4.7 文档与 4.7.2 源码，升级引擎时需要重新核对：

- [资源线程说明](https://docs.godotengine.org/en/4.7/tutorials/performance/thread_safe_apis.html#resources)禁止多线程修改同一资源。PNG 每次调用独占一个 `Image`，在同一线程创建、`LoadPngFromBuffer`、取尺寸并释放；[PNG 解码器](https://github.com/godotengine/godot/blob/4.7.2-stable/drivers/png/png_driver_common.cpp#L62-L125)使用私有解码状态和像素缓冲，不创建 GPU 纹理。不得跨线程共享这个可变实例。
- [PCK 文件实现](https://github.com/godotengine/godot/blob/4.7.2-stable/core/io/file_access_pack.cpp#L443-L629)每次打开持有独立文件句柄、位置和目录遍历列表。加载期间只读已挂载的 PCK 索引，不挂载、替换或清空资源包；文件和目录实例不跨调用共享。打开、读取和取错误都在同一调用线程完成；[FileAccess](https://docs.godotengine.org/en/4.7/classes/class_fileaccess.html#class-fileaccess-method-get-open-error)和[DirAccess](https://docs.godotengine.org/en/4.7/classes/class_diraccess.html#class-diraccess-method-get-open-error)的打开错误按线程记录。
- 原生 [DirAccessWindows.change_dir](https://github.com/godotengine/godot/blob/4.7.2-stable/drivers/windows/dir_access_windows.cpp#L188-L223)会临时改变进程工作目录，构造器又在锁外读取它，不能据此证明源码目录枚举的线程安全，所以源码运行使用 .NET 磁盘实现。Godot 原生枚举默认不列出隐藏项，磁盘实现保留它们；当前内置根已核对两者文件名和类型一致，没有为此增加过滤。

真实引擎测试验证源码运行时的资源枚举、读取和 PNG 检查在工作线程上不经 `Pump()` 完成，GLB 仍需泵送。测试使用编辑器二进制，不覆盖 PCK 分支；后者由 Windows Release 导出中的实际读档对照测量验证，包括恢复检查点、诊断和内容配对。

PNG 和 PCK 的直接调用通过 dispatcher 的 `InvokeOnCallingThread` 登记生命周期，执行仍在调用线程，不入队、不等帧。调用在锁内检查关闭状态并增加进行中计数，在 `finally` 中减少计数；每次调用结束前已释放自己的 Godot 对象。停止泵送时必须调用 dispatcher 的 `Dispose()`：它先拒绝新调用、取消排队请求，再等待进行中的直接调用归零。等待期间释放锁，已有调用不依赖主线程。`App._ExitTree` 在 Godot 的语言和资源清理之前关闭 dispatcher，避免引擎退出时处置仍在加载线程使用的对象；关闭后的直接调用抛 `ObjectDisposedException`，由 Content 的准备边界处理。停止接收新加载后，宿主继续泵送已有调用并释放资源租约，再关闭 dispatcher。不得先停泵、同步等待仍需主线程的资源释放；主线程已有的租约可直接释放。缓存的 `Dispose()` 拒绝新获取，但不夺走已有租约。

## 解码事实

PNG 使用真实 `Image.LoadPngFromBuffer`，只报告尺寸。GLB 先用 .NET 校验头、块边界并解析 JSON；任何非 `data:` buffer URI 都在调用 Godot 前返回 `UnsupportedContent`。外部图片走 flag 32，保留几何解码并报告 `HasExternalImages`。尺寸、网格数量、透明方式是否合格，由 Content 判断。

Godot 4.7.2 的 flag 32 跳过 images／samplers／textures／materials，但仍执行 `_parse_meshes`；`_parse_buffers` 不受该 flag 影响。正常路径使用 `EmbedAsUncompressed`，不提取图片到磁盘。损坏内嵌图片可能仍让 `AppendFromBuffer` 返回 `Ok`，所以还检查 `GLTFState.GetImages()` 的数量、空项和空图片，报告 `CannotDecode`。依据是 [4.7.2 解析流程](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/gltf/gltf_document.cpp#L6785)，并有损坏 PNG 的真实引擎夹具。

所有表面透明方式只取原始 JSON 的 `materials[].alphaMode`，缺省 Opaque。Godot `_parse_meshes` 按 primitives 顺序逐个 `add_surface`，因此同一 mesh 的 primitive 与网格表面一一对应；实现另核对数量。多实例按节点顺序展开，每个实例保留自己的节点与父节点变换。检查包围盒由实际顶点求出，单位为米。

## 资源与所有权

内部调用入口：

```csharp
AssetLease<ImageTexture> AcquireFaceTexture(ContentAsset asset);
AssetLease<ModelAsset> AcquireModel(ContentAsset asset);
```

调用方传入 Content 已校验的资产。缓存键为“修订 + 用途”，当前用途只有生成 mipmap 的面贴图和模型。相同键共享资源，每份租约 `Dispose()` 只释放一次引用，最后一个引用释放时才销毁 Godot 资源；租约释放后不能访问 `Value`。Godot 资源由缓存拥有，消费者只持租约，不自行销毁网格或贴图。

`ModelAsset.Mesh` 烘焙节点和父节点变换，然后只乘一次 2，把米换成格，不做轴转换。负行列式变换同时补偿三角面绕序和切线方向，保留 Godot 解码后的正面。`Surfaces` 与展开后的网格表面按序对应，保留基础贴图、线性颜色系数、透明方式和 alpha cutoff。它不构建最终材质。

## Keep File 与验证

每个新增 `builtin/**` PNG／GLB 都必须提交 `.import`，且 `[remap]` 中 `importer="keep"`。统一入口在资产或导入配置变化时选择项目边界检查，由 C# `Train.ContractChecks structure` 扫描全部内置资产；缺失或非 keep 都失败。

验证入口和资产导出探针的范围见[统一测试入口](run-tests.md#选择)。宿主隔离已接入独立检查，生产资源端口已装配；完整的真实四模块联调仍归阶段验收。
