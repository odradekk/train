# Godot 资产端口

`App/Assets/` 实现 `IContentFiles`、`IAssetInspector` 和从修订字节创建的资源缓存。它们尚未接入生产宿主；#317 负责装配、每帧泵送和退出顺序，#319 负责模型最终材质。旧 `GodotContentSource` 不使用这些端口。

## 文件与线程

在 Godot 主线程创建 `GodotMainThreadDispatcher`，每帧调用 `Pump()`。`Invoke<T>(Func<T>)` 在主线程直接执行；后台调用入队并等待自己的结果。主线程不能同步等待后台加载任务。纯 .NET 磁盘 I/O 和 GLB JSON 预检留在调用线程；Godot 文件、图片与 GLB 解码、资源创建和释放经 dispatcher 串行执行。哈希由 Content 计算，缓存使用其不透明的 `AssetRevision`。

`ContentFiles(GodotMainThreadDispatcher, IReadOnlyDictionary<ContentRoot, ContentRootLocation>)` 接收构造方提供的根登记表：`ContentRootLocation.Disk(absoluteDirectory)` 或 `ContentRootLocation.Resource("res://...")`。端口不判断开发／导出环境；未登记的根属于调用错误。磁盘根的祖先与路径各段都拒绝链接／reparse point，枚举保留 File／Directory／Link。读取最多消费 `maximumBytes + 1` 字节，返回不可变副本，区分 Missing、LimitExceeded、LinkedPath 和 Inaccessible。

停止泵送时必须调用 dispatcher 的 `Dispose()`：未开始的请求被取消，后台等待者结束；新请求被拒绝，已开始的请求照常完成。停止接收新加载后，宿主继续泵送已有调用并释放资源租约，再关闭 dispatcher。不得先停泵、同步等待仍需主线程的资源释放；主线程已有的租约可直接释放。缓存的 `Dispose()` 拒绝新获取，但不夺走已有租约。

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

`ModelAsset.Mesh` 烘焙节点和父节点变换，然后只乘一次 2，把米换成格，不做轴转换。负行列式变换同时补偿三角面绕序和切线方向，保留 Godot 解码后的正面。`Surfaces` 与展开后的网格表面按序对应，保留基础贴图、线性颜色系数、透明方式和 alpha cutoff。它不构建最终材质。非对称 JSON＋BIN 夹具从构造参数独立推出米制包围盒，并核对网格 AABB 为其两倍、+X／+Z 顶点没有翻转。

## Keep File 与验证

每个新增 `builtin/**` PNG／GLB 都必须提交 `.import`，且 `[remap]` 中 `importer="keep"`。统一入口在资产或导入配置变化时选择项目边界检查，由 `common-checks.ps1` 扫描全部内置资产；缺失或非 keep 都失败。

真实文件断言复用 `ContentFilesScenario`；Godot 行为进入现有 `Tests/Engine`。`scripts/test.ps1 -Layer engine -ExportWindows` 另构建专用 `Tests/AssetExport`：以链接方式编译同一份 `App/Assets/`，仅引用 Core／Content 契约，从空输出目录构建、导出并运行。它核对 PCK 与 Mod 原始字节、原始文件名、故障、实际编译输入／引用及加载程序集；同时核对真实游戏 PCK 的内置 PNG 字节和源文件排除。该项目被游戏的 `Tests/**` 导出排除规则覆盖。完整宿主隔离、真实四模块联调及生产资源装配仍由后续票验证。
