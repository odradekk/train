# TextureStorage RID 退出泄漏诊断（#491）

## 结论与范围

**本轮未复现原始 TextureStorage／RD Texture／ObjectDB 退出泄漏。** 普通 follow-8x 为 0/32，verbose follow-8x 为 0/58；连同其他情景，共 109 个有效测量进程及 8 个启动对照。原始对象身份和根因仍未知，与 #477 天空改动的关联未被证实，也不能据此认定问题已修复。旧输入在本轮产品上不兼容，因此这些结果不能直接代表原版本、原输入的发生率。

本工单只诊断，不修复，按 [批准计划及决定 1–8](https://github.com/odradekk/train/issues/491#issuecomment-6190091359) 完成有效测量上限 109 次。失败和因协调窗口中止的运行另列，不计入分母。

本报告服务于阶段 2 验收 [#299](https://github.com/odradekk/train/issues/299)，只提供当前导出版退出行为的诊断证据，不构成阶段联调或性能验收。

## 原始事件与可比性

原始阳性来自 #477 合并提交 `e9f0961d8999f561e14d01cf83e0049194455b92`。原始目录：

```text
D:/Code/train-validation-archive/issue-298-20261015T114702/
  TestResults/issue-298-e9f0961/measurement/follow-8x/attempt-1/follow-8x-1/
```

`process.json` 记录 exit 0、无超时、清理成功、耗时 45.528 秒；`report.json` 为 completed。`process.log` 记录：

```text
ERROR: 4 RID allocations of type 'N10RendererRD14TextureStorage7TextureE' were leaked at exit.
WARNING: 8 RIDs of type "Texture" were leaked.
WARNING: 4 ObjectDB instances were leaked at exit (run with `--verbose` for details).
```

ObjectDB 告警数量为 4。这些原生退出诊断只在 `process.log` 中，`godot.log` 没有相应记录。因此退出码 0、completed 和 Godot 文件日志无 ERROR 均不足以排除该问题。日志没有 verbose 对象身份，无法把数量对应到资源、节点或 C# 调用点。前两轮共 150 个进程未出现、第三轮 9 个中 1 个出现，是 [#298 验收评论](https://github.com/odradekk/train/issues/298#issuecomment-6174311596) 的历史结论，本工单没有重新逐一审计前 150 次日志。

捕获器先分别收完 stdout、stderr 再拼接；原生日志没有时间戳。不能从跨流行序推断精确退出时序或竞争。测量代码先报告再 Quit，引擎随后清理，足以确认需要检查退出阶段，尚不足以确定原因。

原输入按归档 identity 的 SHA-256，从 Git 的 `e9f0961:examples/basic-through/save.json` 精确恢复。当前产品加载失败，报告 `host_restore_failed`、`Rejected`、`unsupported_format`，字段 `/core_state_format_version`。该次是兼容性试验，没有有效采样。按批准决定改用当前示例，两个输入不混合统计。

`e9f0961..b0d499e` 之间 Rendering 全目录、WorldSceneView、WorldSceneController、Measurement、GodotAssetDecoder 没有变更，`App._ExitTree` 的释放顺序未变，但新增了 `GoodsIconCache` 子节点；新增了经济 UI、GoodsIconCache 和离屏图标路径，录制格式与示例世界也已变化。本轮复测覆盖 b0d499e 产品路径，但不是原版本、原输入的完全重演；当前的 0/N 不能直接估计原条件发生率，也不能证明这段历史已经修复问题。

## 产品、输入与环境身份

测量与对照固定使用实施开始时的 `b0d499e1fb031261b8e4cf8266bd27e98f6bc5f8`。Release 导出时源码干净，输出为本工作树 `TestResults/issue-491/release-build/`。构建、导入、导出分别为 20.305、19.097、25.466 秒，均 exit 0；工具日志中的异常见后文，不能把 exit 0 描述为日志全净。

| 身份 | SHA-256 / Git blob |
| --- | --- |
| Release `train.exe` | `a630030c3990c54523dbae0bb7db3b8d3873a58c3f9e88e15002e036aeac7e6c` |
| Release `train.pck` | `44e4efdab5e777000b948126e9f23aff3506aecd47255f827c9825fa14e396cf` |
| Release `Train.dll` | `25295e65aa2f82d3ee10deda6238a9d630ff988a0e7dc1ace0d89da19a3c229a` |
| Debug `train.exe` | `98d520450cd87dd11afe867d9c9b7e0e521eb6815f555ddecfb3b6908c26c447` |
| Debug `train.pck` | `44e4efdab5e777000b948126e9f23aff3506aecd47255f827c9825fa14e396cf` |
| Debug `Train.dll` | `155087df83dc19472e40805aa80abd2f3adaecd9a50f8839571b5dc7b6ab888a` |
| #298 原输入 | `9d849d8d8abdb75dcae365b42939f27bf290397fc6fc36eb3b096b304827a46d` |
| 本轮 `examples/basic-through/save.json` | `a9b5aae8baa9f63a157fc50a267a85a97879f3033edc2f83ff8c90f100c7dfd0` |
| 测量 public-api blob | `7906f4d3bde054bd1935b4de19d52abd72d013e7` |
| b0d499e 原版 `scripts/measure.ps1` | `028c399a4573930c5b035d97604e22a17c409906f0f7b4acfbea77100b7070ff` |
| b0d499e 原版 `scripts/export-measurement.ps1` | `28b69ef758114a5b3877e26efa2109971fd150aafb13bcea4457f11303c1b5ba` |

交付分支的示例已随主线更新。重放本轮固定 b0d499e 导出须使用归档的 `inputs/current-b0d499e.save.json`，不能把交付分支的新示例传给旧导出；下文 `examples/basic-through/save.json` 指测量当时的 b0d499e 文件。Debug 配置为 ExportDebug，源码同为干净 b0d499e。两个输入的原始字节分别保留在 `inputs/historical-e9f0961.save.json`、`inputs/current-b0d499e.save.json`。本轮测量的 CoreInputId 为 `0F04DB44EA9AF31F6444AC9BF3716D1196702FEDEE81F2A9CBFCA07D498E5286`，world seed 1。

环境为 Windows 10.0.26300 x64、Ryzen 9 5900HX（16 逻辑核）、RTX 3080 Laptop GPU、NVIDIA 32.0.15.9649；Godot 4.7.2 stable mono official `ed1daf0bf`，Vulkan 1.4.329、Forward+，1920×1080、VSync 开、MSAA4X、SDFGI/TAA 关；.NET 10.0.12、SDK 10.0.112、PowerShell 7.6.6。环境字段以真实游戏报告和 `environment.json` 为准。同机存在其他任务负载；每次保留 `external-load.jsonl`，内容是总 CPU 和 Godot*/dotnet 进程快照，不覆盖所有进程、GPU 或窗口状态，不能用于完整负载归因；本工单不比较帧时间或评价性能。

测量身份与交付身份分开：测量绑定 `b0d499e`，交付分支在合并前已同步 master，具体提交见 PR 和完成评论。整组契约基线为 `9bafcee0a12d3da20bbb0809708899681b13ce47`，public-api blob 为 `32236034a41867535c124f1d56604cd38e0c4185`。本工单不改公开面或存档／内容格式。上述 b0d499e 结果按验收条件 1 保留，未用新主线重新导出或混入统计；后续主线已改变 Core／Content、Scenes 和导出优化设置，不能把本轮结果当作该新 master 的运行验证。

构建优化也需区分：对保留的 b0d499e Release 导出只读检查 `DebuggableAttribute`，`Train.dll` 不含 `DisableOptimizations`，Core／Content／Scenes 及其三个 Contracts 依赖均含该标志。这里的 Release 指导出模板和 ExportRelease 配置，并不表示所有托管依赖均已优化；七个程序集的标志和哈希在 `b0-export-debuggable-metadata.json`。新主线 #512 已为 ExportRelease 增加 `Optimize=true`，该变更不回写本轮导出，本工单也不据两版推断退出泄漏的变化。

## 运行方法与结果

普通组调用原 `measure.ps1`，每批固定 3 个串行游戏进程；原入口维持 5 秒预热、至少 30 秒采样、录制及报告后退出。因窗口中止和首次显示超时，按决定 7、8 补足有效次数。verbose 组使用同一导出和相同游戏参数，只增加 `--verbose`，每次新目录、串行启动。verbose 的额外日志可能改变时序，还可能改变 Vulkan 实例配置：[固定引擎源码](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/drivers/vulkan/rendering_context_driver_vulkan.cpp#L450) 在非 DEV 构建中根据 verbose 请求可选 `VK_EXT_debug_utils`；可用时安装[创建期 callback 与实例 debug messenger](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/drivers/vulkan/rendering_context_driver_vulkan.cpp#L727)。`--verbose` 本身不直接启用 Godot 的 [validation layers](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/drivers/vulkan/rendering_context_driver_vulkan.cpp#L876)。本轮已见 callback 输出 loader ERROR，普通组未输出不能证明底层 manifest 问题不存在。两组因此分别统计，不能只把差异描述为输出量。

```powershell
pwsh -NoProfile -File scripts/export-measurement.ps1 -OutputDirectory TestResults/issue-491/release-build
pwsh -NoProfile -File scripts/measure.ps1 -ExecutablePath <release-build/train.exe> -InputPath examples/basic-through/save.json -OutputDirectory <新的批次目录> -Scenario <follow-8x|follow-1x|node-8x|map-8x>
```

verbose 直接启动的产品参数如下；路径均为绝对路径，`--verbose` 位于 `--` 前。每次的 `command.json` 保存实际参数和身份，未修改测量入口。

```text
train.exe --windowed --resolution 1920x1080 --log-file <run/godot.log> --verbose --
  --measure=follow-8x --measure-input <save.json>
  --measure-output <run/report.json> --measure-identity <run/identity.json>
```

所有导入、导出、运行均在子进程中隔离 APPDATA 和 LOCALAPPDATA。每个测量有效样本要求退出码 0、无外部超时、清理成功、completed、ExportRelease、非空真实绘制帧、`final_performance=false`。扫描器对每次 `process.log` 和 `godot.log` 不区分大小写搜索 `ERROR`、`leaked`、`Leaked instance`，原始匹配行写入 `log-scan.json`；表中原生 ERROR 按 `ERROR:` 等行首诊断计数，`error_count=0` 和方法名中的 error 不当作错误；泄漏计数指泄漏关键词诊断，不是运行中内存分析。兼容性失败、中止、编辑器、headless、Debug 真窗口和历史扫描器自检分别分类。

扫描器用原阳性日志副本核验，正确识别 exit 0 的 TextureStorage 泄漏；该副本在 `detector-selfcheck/`，不计入本轮统计。两个日志缺一，不记为已经扫描的无泄漏样本。最终已对全部 109 份有效报告另核验 Release、真实帧、scope、构建／输入／产物哈希及环境字段，结果为 `final-runtime-validation.json`；直接启动包装器的完成判断较原入口简单，因此未只依赖它的 exit 0。

| 普通组 | 有效进程 | 目标泄漏 | 泄漏关键词命中／原生 ERROR | 0/N 的条件 95% 上限 |
| --- | ---: | ---: | ---: | ---: |
| follow-8x | 32 | 0 | 0／0 | 8.94% |
| follow-1x | 7 | 0 | 0／0 | 34.82% |
| node-8x | 6 | 0 | 0／0 | 39.30% |
| map-8x | 6 | 0 | 0／0 | 39.30% |

普通组共 51 个有效进程；另有一次协调窗口中止、一次内部超时。`normal-report-validation.json` 逐份核验真实帧、配置、scope 及固定构建和输入。

| verbose 组 | 有效进程 | 目标泄漏 | 泄漏关键词命中／原生 ERROR | 0/N 的条件 95% 上限 |
| --- | ---: | ---: | ---: | ---: |
| follow-8x | 58 | 0 | 0／58 | 5.03% |

58 次均完成，未出现新失败；每次两份日志各有下文列出的 3 条相同 loader ERROR。“原生 ERROR”列计进程数，同一诊断在两个日志的重复记录不重复计进程。follow-8x 合计 90 个有效进程，分组为 0/32 与 0/58；普通组与 verbose 组共 109 个有效测量进程。有效游戏进程墙钟累计普通组 3629.651 秒、verbose 组 3976.998 秒，不含入口／采样器开销、暂停及排除样本，不作为性能比较。

若样本独立同分布，0/N 的单侧 95% 二项上限为 `1 - 0.05^(1/N)`。这是假设条件下的发生率上限，不是根因排除概率；并发负载、不同输入及 verbose 的时序变化限制了外推，普通组和 verbose 组不合并给出一个无条件上限。

### 对照与覆盖边界

同一 Release 执行两次原 `scripts/checks/run-export-smoke.ps1`，每次 empty/mod 两个进程。Debug 使用 #298 的 ExportDebug build、recovery import、Windows Desktop export-debug 配方；headless 启动参数相同，另按计划增加 3 次有窗口启动：

```text
train.exe --headless --max-fps 60 --quit-after 1200 --log-file <run/godot.log>
train.exe --windowed --resolution 1920x1080 --quit-after 1200 --log-file <run/godot.log> --verbose
```

headless 使用 dummy 渲染器，不创建 RendererRD TextureStorage。其成功只能说明对应启动／冒烟路径，没有能力反证本工单这类泄漏。依据固定官方版本的 [DisplayServerHeadless::create_func](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/servers/display/display_server_headless.cpp#L38) 与 [RasterizerDummy](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/servers/rendering/dummy/rasterizer_dummy.h#L60)。有窗口 Debug 覆盖真实 RendererRD 启动退出，但未执行 follow-8x 测量路径。原 smoke 不写逐进程 process.json，其成功由入口的退出码、超时、清理和行为断言确认；汇总对此明确标为入口派生结论。

| 对照 | 有效进程 | 目标／泄漏关键词命中 | 原生 ERROR |
| --- | ---: | ---: | --- |
| Release headless smoke（两轮 empty/mod） | 4 | 0／0 | 无 |
| Debug headless（#298 配方） | 1 | 0／0 | 无 |
| Debug windowed verbose | 3 | 0／0 | 每次 3 条 Vulkan loader ERROR |

两轮 smoke 入口分别 11.586、10.991 秒，headless Debug 21.380 秒；有窗口 Debug 分别 11.619、11.408、12.200 秒，均 exit 0、无外层超时且清理成功。Debug 导出 build/import/export 为 18.077/7.784/21.099 秒，均 exit 0；文件身份见 `debug-build/identity.json`。

有窗口 Debug 和 Release verbose 的 loader ERROR 明确报告无法打开 `C:/ProgramData/obs-studio-hook/obs-vulkan64.json`、`obs-vulkan32.json` 和 `C:/Program Files/Rockstar Games/Social Club/SocialClubVulkanLayer.json`；另有 GalaxyOverlayVkLayer 命名 WARNING。它们与 TextureStorage 退出泄漏是不同诊断，不能据此声称已确认两者无关或导致原事件。`vulkan-loader-context.json` 记录这三个路径当前不存在。没有改机器配置或屏蔽这些输出；不能把这 3 次对照或 verbose 样本报告为无原生 ERROR。

### 排除样本与异常

1. `frequency/follow-8x/batch-02/follow-8x-3` 因协调者窗口中止，记录为“因窗口中止”，排除统计。其余窗口在进程结束后停批，没有新增中止样本；暂停期间只读代码和整理日志。
2. `frequency/follow-1x/batch-01/follow-1x-2` 是首次显示内部等待超时，**原因未解释**。first-load 成功（4.036 秒），scene-first-display 等待 120.110 秒失败；进程 131.381 秒后 exit 1，外部 timed_out=false，不是协调窗口中止，没有有效采样。两份日志没有窗口最小化、焦点或绘制状态记录；会话 JSONL 仅有 application_ready（12:37:06.4406483Z）。负载日志覆盖 12:37:00.312–12:39:11.277Z，共 94 个 CPU 样本，最小／中位／最大 45/93/100%，没有窗口状态或 GPU 记录。高 CPU 不能证明超时原因。
3. 旧输入兼容性失败另列，不进入当前示例分母。

[#478 的报告](follow-8x-frame-time-analysis.md) 中，明确最小化的另一次运行有“测量窗口被最小化”异常；绘制停止后触发 120 秒超时的运行缺少连续窗口／焦点证据，触发原因仍未知。不能以表现类似认定本轮也最小化。当前 `App/App.cs` 的焦点处理不记录日志；`MeasurementRun.cs` 的最小化检查位于 FramePostDraw 回调中，缺少异常也不能反证窗口未最小化。首次显示未完成还可能是尚无可显示帧，当前证据不能区分。按决定 8 保留异常，不改生产代码、脚本或预算。

### 导入、导出工具进程自身的异常

Release 首次导入日志记录缺失 `.fontdata`、Theme.tres 依赖加载/解析失败；Release 和 Debug 的导入均记录 `GodotTools.HotReloadAssemblyWatcher.RestartTimer` 的 Timer 不在 SceneTree 错误。该调用栈来自 Godot C# 编辑器工具，作为编辑器导入异常单列。

两次导出进程的 process.log 均有 495 个 ObjectDB 实例泄漏 WARNING 和 10 resources still in use ERROR；对应导入/导出步骤都 exit 0。它们在扫描 JSON 中归类 editor，不能混入 51 个普通游戏样本、verbose 组或启动对照，也不能声称导出工具日志全净。游戏日志是否有同类问题按各次实际扫描单独报告。

## 资源创建与退出释放盘点

以下是 `b0d499e` 的静态事实，覆盖 C# 显式创建与取得的纹理及包装对象，不穷尽引擎内部资源图，不能替代泄漏对象身份。路径行号绑定该提交。

| 创建／取得位置 | 持有与释放 | 诊断边界 |
| --- | --- | --- |
| `App/Assets/AssetResourceCache.cs:46–54` PNG ImageTexture | Image 用 using；缓存租约持有托管引用，材质持有原生引用；最后租约在 `:78–86` Dispose | Cache.Dispose 只停止新增获取，不夺走有效租约；已有 engine 测试检查该合同，不检查整进程退出 RID |
| `App/Assets/GodotAssetDecoder.cs:25–30,119–142` GLTF 图片和基色 mipmap ImageTexture | 转移到 ModelAsset；`AssetResourceCache.cs:23–27` 去重 Dispose，未转移图片及 State/Document 由 decoder 清理 | `:118` 取得的 GltfTexture 包装对象无显式 Dispose；尚无身份说明它关联原泄漏 |
| `Ui/MacroMap.cs:269–301` 地图符号 ImageTexture | `_symbols` 持有；Image 用 using，退出调用 ReleaseSymbols，逐项 Dispose | 尚无实际退出对象清单证明所有退役已发生 |
| `Ui/GoodsIconCache.cs:132–140,196–215` SubViewport 与读回 ImageTexture | Capture 释放 preview、display、viewport、Environment；Clear 先清控件纹理，再退租约或 Dispose 输出纹理 | GetTexture 中间包装对象无显式 Dispose；此路径在原事件后才加入，不能解释原版本事件 |
| `Ui/WorldSceneView.cs:32,41–60,151–159` 两组 Sky／Environment／材质 | View.Free 后 Dispose Environment、Sky、Material；游戏 Shader 由 GD.Load 取得，Material 持有原生引用 | 引擎管理天空内部纹理；Shader 包装引用和具体退出顺序仍可核查，尚无泄漏关联 |
| `App/Measurement/MeasurementRun.cs:202` Viewport.GetTexture().GetSize | 临时取得 ViewportTexture 包装引用，无显式 Dispose | [官方 getter](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/scene/main/viewport.cpp#L1416) 返回既有 default_texture，不能称每次新建原生纹理 |
| `MeasurementRun.cs:272` 截图读回 | Image 用 using，ViewportTexture 包装对象无显式 Dispose | follow-8x 不走该截图分支，不能用此调用解释原阳性 |

正常退出顺序：`MeasurementRun.cs:153–168` 等录制关闭、写报告、Quit；`App/App.cs:219–236` 依次释放 measurement、Development、Recording、Saves、WorldScenes、Host、dispatcher、logger。`WorldSceneController.cs:301–336` 先断开 Environment、释放 SceneDisplay、异步关闭纯 .NET 场景会话，再释放 View 和资源缓存。`SceneDisplay.cs:143–161` 退役节点与 PreparedVisual，`SceneResources.cs:73–80` 先 Free 节点，再逆序释放所持资源，材质先于纹理租约释放。异步会话关闭不等待，但仅凭此不能推导 Godot 纹理遗留。UI 缓存依赖各自 `_ExitTree`，本工单没有确认这些回调与 App 父节点退出的精确相对顺序。

未在所查 App、Ui、Rendering C# 发现 Texture2DArray 创建。`scene_material.gdshader` 的 base_texture 由 SceneResources 设置；`sky.gdshader` 和 `water.gdshader` 没有纹理 uniform。#477 改了天空材质并设置 FogSkyAffect=0，未改变释放代码；没有身份证据便不能默认归因天空，也不能只因无 uniform 就排除引擎内部天空纹理。

## 引擎源码事实与推断边界

固定官方提交 `ed1daf0bf001b61586d9930840f2f1394092c079`：

- [GodotObject.Dispose／终结器](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/modules/mono/glue/GodotSharp/GodotSharp/Core/GodotObject.base.cs#L98) 进入 native disposal；[RefCounted 桥](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/modules/mono/glue/runtime_interop.cpp#L167) 释放 managed 持有的引用，仍有其他原生引用时不会立即删除资源。
- [DisposablesTracker](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/modules/mono/glue/GodotSharp/GodotSharp/Core/DisposablesTracker.cs#L25) 使用短 WeakReference，只同步 Dispose 遍历时能取得的对象。因此“有 tracker”不是所有 wrapper 已完成清理的证明；“有未显式 Dispose 的 getter”也不是泄漏证明。
- [正常退出](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/main/main.cpp#L5216) 在销毁 RenderingServer 前结束脚本语言，ObjectDB cleanup 位于更后的 core 注销。沿 [CSharpLanguage::finalize](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/modules/mono/csharp_script.cpp#L140) 到 [GDMono 析构](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/modules/mono/mono_gd/gd_mono.cpp#L839)，未发现全局等待 managed 终结器完成的显式步骤；热重载的 UnloadPlugin 有 GC 等待，不能套用到正常退出。未检查 CLR 内部最终关闭行为，也未捕获本次实际竞争，故只能保留时序候选。
- [TextureStorage 二维纹理初始化](https://github.com/godotengine/godot/blob/ed1daf0bf001b61586d9930840f2f1394092c079/servers/rendering/renderer_rd/storage_rd/texture_storage.cpp#L1041) 可为一个 owner 创建主 RD texture 和 sRGB shared RD texture。4:8 数量比例与这类路径相容，不能据此识别具体纹理、证明因果或计算独立显存泄漏量。

## 候选原因、修复方向与代价

尚未确认根因。下表按可验证的持有／释放机制列候选，不作概率排名；代价是取得身份后调查及最小项目改动的工程估计，不包括上游引擎修复排期，本工单不实施。

| 候选 | 依据与尚缺证据 | 可能的修复方向与估计 |
| --- | --- | --- |
| 纹理退出时仍被材质、节点或其他资源持有 | 当前 ImageTexture 的显式所有者有释放路径，尚未发现正常路径确定缺失的 Dispose；需要 verbose 类名/ID，再用创建、租约、退役记录确认实际持有者 | 修正已证实的持有和退役链，约 1–2 个工程日 |
| 临时 RefCounted 包装引用与退出清理时序 | GetTexture／GltfTexture 有未显式 Dispose 包装对象，tracker 保证有限；没有实例关联或运行时序证明，尚不能把该 getter 与四个实例建立对应关系 | 确认共享包装对象所有权后，对应引用采用确定性释放；项目内约 0.5–2 日，若需引擎最小复现另约 2–5 日 |
| Sky／Environment 引擎内部纹理退出顺序 | #477 与原事件时间相邻，但无纹理 uniform、无身份关联；引擎仍有天空内部纹理 | 只有阳性身份指向天空或仍无法归属时，分别只改变天空材质、FogSkyAffect 做 A/B，再按证实的 owner 调整退役；项目内约 1–3 日，上游另估 |

新增 GoodsIconCache 和 UI 字体持有不能解释原版本事件，但影响当前资源图，已纳入盘点。正常所有者路径、终结器路径和内部天空资源目前都不能被证实或排除。

建议本轮停止继续重复当前输入，也不据这些静态候选改天空或释放顺序。当前没有阳性身份指导，本轮未做天空／Fog A/B 或生产插桩；没有基线阳性的 0/N 变体不足以归因原事件。与 #477 的关系仍未定。

若再次自然出现同类诊断，值得继续追查：先保留两个日志及 verbose 的实例 ID、类名、资源路径／节点，再把身份对应到上述创建和退役路径。若必须回答原事件根因，单独恢复 e9f0961 与原输入，比继续当前新输入更有区分力；进一步插桩和天空／Fog 单变量实验仍应在独立副本完成。现有证据只覆盖退出诊断，本工单没有验证运行中资源增长或玩家可见影响，不以未复现证明这些方面正常。

## 验证与证据索引

基线 `dotnet build Train.csproj` 成功，0 警告、0 错误，34.623 秒。`pwsh -NoProfile -File scripts/test.ps1 -Project godot -Layer fast,integration` 首次 244 个用例通过，但入口因本工单运行期间新建 scratch 脚本触发源码快照检查而失败；保留原结果，不记为入口通过。停止写文件后同命令重跑通过：156 fast、88 integration，入口 60.585 秒。两次目录分别为 `TestResults/fast-integration-20261015T104355876-b0d499e/`、`TestResults/fast-integration-20261015T104540109-b0d499e/`。这是 b0d499e 的基线结果，不声称在报告提交重新执行了这 244 个测试。

交付 head 的工程构建、按改动选择的检查及 Windows routine 结果记录在本工单完成评论和对应 CI；上述 244 个用例及本轮运行结果仍绑定 b0d499e，未移作新主线的实测结果。

本工单按派工用 CLI 导出和运行，未启动常驻 MCP 编辑器、未通过 9083 进行 MCP 交互，也没有 Computer Use、人工视觉质量或交互验收。临时诊断驱动与扫描器只在忽略目录，未改 canonical 脚本；未创建生产源码插桩副本或临时生产补丁。

以下均相对于本工作树 `TestResults/issue-491/`，合并归档由协调者复制到 `D:/Code/train-validation-archive/issue-491-<时间>/`，保留目录结构：

| 证据 | 路径 |
| --- | --- |
| 环境、导出文件及脚本哈希 | `environment.json`、`build-identities.json`、`release-build/export-summary.json` |
| 输入原件、选择及兼容性试验 | `inputs/`、`compatibility/historical/` |
| 每次普通／verbose 报告、两个日志、进程结果和负载 | `frequency/`、`verbose/` |
| 首次显示超时分类 | `frequency/follow-1x/batch-01/follow-1x-2/failure-classification.json` |
| 冒烟、Debug 导出及启动 | `smoke/`、`debug-build/`、`debug-headless/`、`debug-window/` |
| 逐次扫描、完整身份核验和阳性自检 | `log-scan.json`、`final-runtime-validation.json`、`detector-selfcheck/result.json` |
| 托管优化元数据及交付同步 | `b0-export-debuggable-metadata.json`、`merge-master.json` |
| 窗口停机与唯一强制中止 | `window-*.json`、`frequency/follow-8x/batch-02/follow-8x-3/interruption.json` |

原始日志不进入 Git；PR 只含本报告，不改生产代码、测试、测量脚本、预算或契约。
