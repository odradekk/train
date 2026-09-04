# 桌面常驻窗口：Windows 与 Godot 平台能力

研究票：[调研桌面常驻窗口的 Windows 与 Godot 平台能力](https://github.com/odradekk/train/issues/27)

研究日期：2026-09-04

对象：`GDD+Technical Contract.md` §2、§71、§72、§73 定义的底部 Docked 无边框 3D 主窗口、Floating 模式、可选置顶、可选目标显示器、最小化停渲染但 Simulation 不停。运行时基线沿用 [Godot / Windows MVP 技术基线](godot-windows-mvp-baseline.md)：Godot `4.7.2-stable` .NET、C#、Windows x86_64、Compatibility 渲染器。引擎源码核对基于 `4.7.2-stable` tag。

## 结论

契约里的窗口行为在 Godot 4.7 .NET + Windows 上都能做到，但分成三类：

- **Godot 现成 API 就够**：无边框、Always-on-top、目标显示器选择、窗口位置与尺寸、原生 `HWND` 获取、失焦/遮挡时 Simulation 继续、最小化自动停渲染、限帧。不需要 P/Invoke。
- **Godot 完全没有，必须自己 P/Invoke**：**占用桌面工作区**。`DisplayServer` 只能*读*工作区（`screen_get_usable_rect`），没有任何预留接口，引擎 Windows 后端也完全没有 AppBar 代码。Docked 模式只能自己调 `SHAppBarMessage` 注册 AppBar。
- **Godot 有但语义受限，需要按限制设计产品**：点击不激活。`Window.unfocusable` 在 Windows 上就是 `WS_EX_NOACTIVATE`，并且引擎已在 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`，所以"点击不抢焦点、鼠标事件照收"开箱可用；代价是该窗口**永远收不到键盘输入**，且滚轮按 Win32 规范发给焦点窗口而不是悬停窗口。

由此得到三条对后续设计有约束力的判断：

1. Docked 的工作区预留是唯一必须写 Win32 互操作的部分，其余窗口行为都走 Godot API。互操作面可以压缩到 `SHAppBarMessage` + `GetMonitorInfo` + `MoveWindow` 这一小组调用。
2. AppBar 的 `ABN_POSCHANGED` 回调必须送到某个我们能拦到消息的 `HWND`。Godot 独占主窗口的窗口过程且不暴露原生消息钩子，因此只有三条路：子类化 Godot 自己的 `HWND`（推荐）、用自建的隐藏窗口当 AppBar 宿主、或放弃回调改为轮询。
3. Simulation 与窗口状态解耦（§71）在 Godot 里是默认行为，不需要额外代码：主循环无条件调用 `MainLoop::process()`，只有*渲染*受窗口状态门控。

## 事实、结论和建议

### 原生句柄与 P/Invoke 边界

**观察**：`DisplayServer.window_get_native_handle(handle_type, window_id)` 返回"供插件使用的内部结构指针"，在 Android、Linux、macOS 和 Windows 上实现。`HandleType.WINDOW_HANDLE = 1` 在 Windows 上就是该窗口的 `HWND`；`WINDOW_VIEW = 2`（`HDC`）与 `OPENGL_CONTEXT = 3`（`HGLRC` 或 ANGLE 的 `EGLContext`）标注为"仅 Compatibility 渲染器"。[DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst)

**观察**：本机 `Godot_v4.7.2-stable_mono_win64` 的 `GodotSharp.dll` 反射结果为 `Int64 WindowGetNativeHandle(HandleType, Int32)`，即 C# 侧返回 `long`，不是 `IntPtr`。（本机核对，非官方文档陈述。）

**观察**：`DisplayServer` 全部方法里只有 `window_get_native_handle` 涉及原生句柄；没有任何注册原生消息回调、替换窗口过程或转发 Win32 消息的接口。`window_set_window_event_callback` 只投递引擎自定义的 `WINDOW_EVENT_*`（鼠标进出、焦点、关闭请求、DPI 变化等），不是 Win32 消息。[DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst)

**结论**：C# 侧拿主窗口 `HWND` 的唯一正确写法是 `(nint)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, 0)`。只需要 `WindowHandle`；`WindowView`/`OpenglContext` 与 AppBar 无关，不要引入对渲染器的额外耦合。

**建议**：

- 把所有 Win32 调用集中在一个 `Platform/Windows/` 目录下的互操作类里，对外只暴露"设置 Dock 矩形 / 取消 Dock / 查询显示器"这种领域语义方法。
- .NET 8 用 `LibraryImportAttribute` 源生成互操作，不用 `DllImport`；`CallingConvention` 改用 `UnmanagedCallConvAttribute`。[P/Invoke source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)
- 句柄在 C# 里立刻转成 `nint`（`HWND`），不要以 `long` 在业务代码里流转。

### 无边框、置顶、显示器选择与几何

**观察**：`WINDOW_FLAG_BORDERLESS`（无原生标题栏与装饰）、`WINDOW_FLAG_ALWAYS_ON_TOP`（浮在所有其他窗口之上）、`WINDOW_FLAG_NO_FOCUS`（窗口不能获得焦点，"会忽略除鼠标点击外的所有输入"）三个标志都注明在 Windows 上已实现，全屏窗口会忽略前两个。对应的 `Window` 属性是 `borderless`、`always_on_top`、`unfocusable`。`always_on_top` 的文档补了一句：`transient` 打开时它不生效。[DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst) [Window class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_window.rst)

**观察**：显示器相关 API 齐全：`get_screen_count()`、`screen_get_position()`、`screen_get_size()`、`screen_get_usable_rect()`、`window_set_current_screen()`/`window_get_current_screen()`（Windows 已实现），并支持 `SCREEN_PRIMARY`、`SCREEN_OF_MAIN_WINDOW`、`SCREEN_WITH_MOUSE_FOCUS` 等特殊索引。窗口几何用 `window_set_position()`/`window_set_size()`，文档建议优先用 `Window.position`。[DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst)

**观察**：启动期同样的开关有项目设置：`display/window/size/borderless`、`display/window/size/always_on_top`、`display/window/size/no_focus`、`display/window/size/initial_position` 与 `initial_position_type`。[ProjectSettings class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_projectsettings.rst)

**观察**：Godot 的屏幕/窗口坐标不是 Win32 虚拟桌面坐标。`DisplayServerWindows::_get_screens_origin()` 取所有显示器矩形左上角的分量最小值，`screen_get_position()` 等一律返回 `Win32 坐标 − 该原点`。也就是说，当某个显示器排在主显示器左侧或上方时，Win32 给负坐标，而 Godot 把整个坐标系平移到非负。[display_server_windows.cpp:1333](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L1333)

**观察**：`Window.mode` 的 `MODE_MAXIMIZED` 描述为"占据除任务栏外的整个屏幕区域"，即引擎自身遵守工作区；`screen_get_usable_rect()` 返回"不被状态栏遮挡的屏幕部分"，Windows 上已实现。[Window class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_window.rst) [DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst)

**结论**：§2 与 §73 的无边框、占满目标显示器宽度、高度可调、默认主显示器、玩家可选显示器、Floating 可选置顶，全部只用 Godot API 即可，不需要 Win32。

**结论**：Godot 坐标与 Win32 坐标的原点差是一个静默错位来源。多显示器下混用两套坐标会把 Dock 条放到错误位置，而单显示器下永远不会暴露。

**建议**：

- AppBar 相关的几何计算全程留在 Win32 坐标系：用 `MonitorFromWindow`/`GetMonitorInfo` 取目标显示器的 `rcMonitor`/`rcWork`，用 `MoveWindow` 落位，不要把矩形往返 `Window.Position`。
- 需要在两套坐标间换算时，显式加减 `screen_get_position(screen)` 与 `GetMonitorInfo` 给出的 `rcMonitor` 之差，并把这条换算写成一个函数、加注释说明原点差的来源。
- Floating 模式的置顶用 `Window.AlwaysOnTop`，不要自己 `SetWindowPos`，避免与引擎的 z-order 维护打架。

### 桌面工作区预留：Godot 没有能力

**观察**：Godot 4.7 的 `DisplayServer` 中与工作区有关的只有只读的 `screen_get_usable_rect()`，没有任何设置或预留工作区的方法。[DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst)

**观察**：引擎 Windows 后端源码中不存在 `SHAppBarMessage`、`APPBARDATA` 或 `SPI_GETWORKAREA` 的任何调用；显示器信息一律走 `EnumDisplayMonitors` + `GetMonitorInfo`。[display_server_windows.cpp](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp)

**结论**：确认 Godot 不暴露工作区预留。§73 的 "Docked 占据 Desktop Work Area" 只能靠自己注册 Win32 AppBar 实现，这是本项目唯一强制的原生互操作点。

**建议**：把"是否占用工作区"作为 Docked/Floating 的唯一硬区别写进实现契约：Docked = 已注册 AppBar，Floating = 未注册 AppBar。模式切换 = 注册/注销，而不是只改窗口样式。

### AppBar 的注册、定位与注销序列

**观察**：应用通过发送 `ABM_NEW` 注册 appbar，并在其中提供一个应用自定义的消息标识符供系统回调；退出前必须发送 `ABM_REMOVE` 注销，注销后系统不再发通知、也不再为它保留屏幕区域。文档明确写"应用应总是在销毁 appbar 之前发送 `ABM_REMOVE`"。[Using Application Desktop Toolbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars)

**观察**：定位是两步。先用 `ABM_QUERYPOS` 提交建议的屏幕边与外接矩形，系统判断该区域是否已被任务栏或其他 appbar 占用并**仅通过矩形相减**调整，"不会努力保持矩形的初始尺寸"，所以调用后应用必须按自己的目标高度重新推导边界；再用 `ABM_SETPOS` 提交最终矩形，系统可能再次调整，应用应使用 `ABM_SETPOS` 返回的矩形调用 `MoveWindow` 落位。`ABM_QUERYPOS` 与 `ABM_SETPOS` 都固定返回 TRUE，真正的输出在 `rc` 里。[Using Application Desktop Toolbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars) [ABM_QUERYPOS](https://learn.microsoft.com/en-us/windows/win32/shell/abm-querypos) [ABM_SETPOS](https://learn.microsoft.com/en-us/windows/win32/shell/abm-setpos)

**观察**：`APPBARDATA` 为 `{ DWORD cbSize; HWND hWnd; UINT uCallbackMessage; UINT uEdge; RECT rc; LPARAM lParam; }`。`uEdge` 取 `ABE_BOTTOM`/`ABE_LEFT`/`ABE_RIGHT`/`ABE_TOP`；`rc` 在 `ABM_QUERYPOS`/`ABM_SETPOS`/`ABM_GETTASKBARPOS` 下是**屏幕坐标**下的外接矩形。`SHAppBarMessage` 的消息值为 `ABM_NEW=0x0`、`ABM_REMOVE=0x1`、`ABM_QUERYPOS=0x2`、`ABM_SETPOS=0x3`、`ABM_GETSTATE=0x4`、`ABM_GETTASKBARPOS=0x5`、`ABM_ACTIVATE=0x6`、`ABM_WINDOWPOSCHANGED=0x9`，返回值随消息而异。[APPBARDATA](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-appbardata) [SHAppBarMessage](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shappbarmessage)

**观察**：`SHAppBarMessage` 文档同时指出，要取得"任务栏和其他 appbar 都没占用的、你的应用可用的工作区"，应使用 `GetMonitorInfo`，而不是 `ABM_GETTASKBARPOS`。`MONITORINFO.rcWork` 就是该显示器的工作区矩形，以虚拟屏幕坐标表示，非主显示器时坐标可能为负。[SHAppBarMessage](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shappbarmessage) [MONITORINFO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-monitorinfo)

**观察**：appbar 收到 `WM_ACTIVATE` 时应发送 `ABM_ACTIVATE`，收到 `WM_WINDOWPOSCHANGED` 时应发送 `ABM_WINDOWPOSCHANGED`；文档说明这两条只用于让系统正确维护**同边自动隐藏 appbar** 的 z-order。[Using Application Desktop Toolbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars)

**结论**：Bottom Dock 的最小序列是固定的，与 Godot 无关：

```text
注册    SHAppBarMessage(ABM_NEW,      {cbSize, hWnd, uCallbackMessage})
定位 1  填 uEdge = ABE_BOTTOM, rc = 目标显示器 rcMonitor 底部 height 高的条带
定位 2  SHAppBarMessage(ABM_QUERYPOS, &abd)   → 按 abd.rc.bottom 重新推导 top
定位 3  SHAppBarMessage(ABM_SETPOS,   &abd)   → 系统批准的最终矩形
落位    MoveWindow(hWnd, abd.rc...)
注销    SHAppBarMessage(ABM_REMOVE,   {cbSize, hWnd})
```

定位三步在这些时机重跑：注册之后、收到 `ABN_POSCHANGED`、玩家改 Dock 高度、玩家换目标显示器、显示器拓扑变化。

**结论**：因为不做自动隐藏（§73 只要求 Bottom Dock），`ABM_SETAUTOHIDEBAR`、`ABM_ACTIVATE`、`ABM_WINDOWPOSCHANGED` 在首版都不是必需项。

**建议**：

- 注销必须覆盖所有退出路径：`NOTIFICATION_WM_CLOSE_REQUEST`、`NOTIFICATION_EXIT_TREE` 以及 `SceneTree` 退出，并且在切到 Floating 时也要注销。漏掉注销会让工作区在游戏退出后仍然被占。
- 高度按 `ABM_QUERYPOS` 返回值重算，不要假设系统保留了你请求的高度。
- 用 `GetMonitorInfo` 的 `rcMonitor` 而不是 `GetSystemMetrics(SM_CXSCREEN)` 计算条带宽度——微软示例用的是后者，只对主显示器成立。

### `ABN_POSCHANGED` 回调如何进入 Godot 主循环

**观察**：系统把通知以应用在 `ABM_NEW` 时指定的自定义消息号发送给 `abd.hWnd`，通知码放在 `wParam`。`ABN_POSCHANGED` 在任务栏尺寸/位置/可见性变化、同边新增或调整或移除其他 appbar 时到达，应用应据此重跑 `ABM_QUERYPOS` + `ABM_SETPOS` 并 `MoveWindow`。`ABN_FULLSCREENAPP` 在全屏应用启动或最后一个全屏应用关闭时到达，appbar 需要相应地掉到 z-order 底部再恢复。[Using Application Desktop Toolbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars)

**观察**：Godot 的 Windows 窗口全部由引擎用窗口类 `L"Engine"` 通过 `CreateWindowExW` 创建，窗口过程是 `DisplayServerWindows::WndProc`，脚本层没有任何介入点。样式变更走 `SetWindowLongPtr(GWL_STYLE/GWL_EXSTYLE)` + `SetWindowPos`，**不会重建 `HWND`**，因此主窗口句柄在其生命周期内稳定。[display_server_windows.cpp:7250](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L7250) [display_server_windows.cpp:2654](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L2654)

**观察**：`SetWindowSubclass` 安装的子类回调由"回调地址 + 调用方定义的子类 ID"唯一标识，通过 `RemoveWindowSubclass` 卸载；文档的唯一硬性警告是"不能用子类化辅助函数跨线程子类化窗口"。相比之下 `SetWindowLongPtr(GWLP_WNDPROC)` 是改窗口类关联的窗口过程来建立子类，未处理的消息必须自己用 `CallWindowProc` 传给上一个过程，形成过程链。[SetWindowSubclass](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass) [SetWindowLongPtrW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowlongptrw)

**观察**：Godot 的 Windows 应用清单声明了对 `Microsoft.Windows.Common-Controls` 6.0.0.0 的依赖，且启动时 `LoadLibraryW("comctl32.dll")` 并调用 `InitCommonControlsEx`。也就是说 comctl32 v6 的子类化辅助函数在 Godot 进程内可用。[godot.manifest](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/godot.manifest) [display_server_windows.cpp:7931](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L7931)

**观察**：message-only 窗口"不可见、没有 z-order、不能被枚举、不接收广播消息，只负责分发消息"，通过在 `CreateWindowEx` 的 `hWndParent` 传 `HWND_MESSAGE` 创建。[Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)

**结论**：三条可行路线，代价与风险不同。

| 方案 | 做法 | 优点 | 风险 |
|---|---|---|---|
| A 子类化（推荐） | 用 `SetWindowSubclass` 在 Godot 主窗口 `HWND` 上挂一个 C# `SUBCLASSPROC`，拦截自定义 appbar 消息，其余交 `DefSubclassProc` | 预留区直接绑定真正可见的窗口，shell 的窗口排列与全屏 z-order 语义正确；`HWND` 不会被引擎重建 | 必须在 Godot 主线程调用与卸载；委托必须被 GC 根持有；卸载时机必须早于窗口销毁 |
| B 隐藏宿主窗口 | 自建一个我们完全掌握窗口过程的顶层隐藏窗口作为 `abd.hWnd`，Godot 主窗口只跟随批准的矩形 | 完全不碰引擎的窗口过程 | 预留区绑定在一个用户看不见的窗口上；shell 是否正常接受从不可见的 appbar 宿主未经一手文档确认；`HWND_MESSAGE` 窗口没有 z-order，很可能不适合当宿主 |
| C 轮询 | 不注册回调消息，用 `_Process` 里的低频定时器重跑 `ABM_QUERYPOS`/`ABM_SETPOS` | 实现最简单，零消息拦截 | 收不到 `ABN_FULLSCREENAPP`，全屏应用出现时 z-order 处理缺失；任务栏变化到位置修正之间有可见延迟 |

**建议**：

- 首版按 A 实现，B 作为 A 被证伪时的退路，C 只作为兜底降级。三者对上层是同一个接口，切换不应影响调用方。
- 自定义回调消息号取 `WM_APP + n`，避免与引擎自用的窗口消息和定时器冲突。
- 子类过程只处理自己的消息号，其他一律 `DefSubclassProc`；处理逻辑本身只记录一个待处理请求，真正的 `ABM_QUERYPOS`/`ABM_SETPOS`/落位放到下一次 `_Process`，避免在窗口过程里做重活或回调进托管长逻辑。
- `SUBCLASSPROC` 委托必须由静态字段持有；只在主线程 `SetWindowSubclass`/`RemoveWindowSubclass`。

### 点击不激活与 Godot 输入

**观察**：`WS_EX_NOACTIVATE` 的定义是：用这个样式创建的顶层窗口在用户点击时不会成为前台窗口；系统在前台窗口最小化或关闭时也不会把它带到前台；**该窗口默认不出现在任务栏上**，要强制上任务栏需加 `WS_EX_APPWINDOW`。[Extended Window Styles](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)

**观察**：`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`（值 3）表示"不激活窗口，也不丢弃鼠标消息"。[WM_MOUSEACTIVATE](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate)

**观察**：Godot 的 `no_focus`（即 `Window.unfocusable`）在 Windows 上就落到这两件事上：窗口样式里 `if (p_no_activate_focus && !p_embed_child) r_style_ex |= WS_EX_TOPMOST | WS_EX_NOACTIVATE;`，窗口过程里 `case WM_MOUSEACTIVATE: if (no_focus || is_popup) return MA_NOACTIVATE;`（注释为 "Do not activate, but process mouse messages"），显示时用 `ShowWindow(SW_SHOWNA)`。[display_server_windows.cpp:2627](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L2627) [display_server_windows.cpp:5680](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L5680) [display_server_windows.cpp:1964](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L1964)

**观察**：Godot 主窗口的扩展样式里无条件带 `WS_EX_APPWINDOW`（非嵌入时），所以即使打开 `no_focus`，主窗口仍会出现在任务栏和 Alt-Tab 中。[display_server_windows.cpp:2572](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L2572)

**观察**：`WINDOW_FLAG_NO_FOCUS` 的类参考描述是"窗口不能获得焦点。无焦点窗口会忽略除鼠标点击外的所有输入"，而 `Window.unfocusable` 的属性描述是"窗口不能被聚焦也不能被交互，但仍可见"。两处措辞不一致，以 `DisplayServer` 的描述和上面的源码行为为准。[DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst) [Window class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_window.rst)

**观察**：`WM_MOUSEWHEEL` 按 Win32 规范"发送给焦点窗口"。Godot 的窗口过程把收到的 `WM_MOUSEWHEEL`/`WM_MOUSEHWHEEL` 直接翻成 `InputEventMouseButton` 的 `WHEEL_*`，没有任何"转发给悬停窗口"的补偿逻辑。[WM_MOUSEWHEEL](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel) [display_server_windows.cpp:6420](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L6420)

**结论**：Q19 选定的"不抢焦点"在 Godot 里是一行 `Window.Unfocusable = true`，不需要自己加 `WS_EX_NOACTIVATE`，也不会掉出任务栏。鼠标移动与点击照常进入 Godot 输入系统。

**结论**：代价是明确的：无焦点窗口拿不到键盘事件；滚轮是否到达取决于 Windows 的"悬停时滚动非活动窗口"这一系统设置，不是应用能保证的。因此 §67 的 Zoom 若只绑滚轮，在 Docked 无焦点状态下不可靠。

**结论**：Godot 的 `no_focus` 会连带加上 `WS_EX_TOPMOST`。这与 §73 "Docked 不依赖 Always-on-top"、"Floating 默认关闭 Topmost" 的意图存在张力，实际 z-order 还受随后 `SetWindowPos(HWND_TOPMOST 或 HWND_NOTOPMOST)` 影响，必须实测确认最终表现。[display_server_windows.cpp:2680](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L2680) [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)

**建议**：

- 用 `Window.Unfocusable`，不要自己改扩展样式。自己 `SetWindowLongPtr` 加的位会在引擎下一次 `_update_window_style()` 时被整体覆盖。
- 相机的 Yaw/Pitch/Zoom 至少提供一条不依赖键盘、也不依赖滚轮的鼠标拖拽通路。
- 需要键盘的功能（管理 UI、设计器）放到另一个可获焦点的窗口，与 §2 "其他管理功能使用独立窗口"一致。
- "是否可由玩家显式获取焦点"是产品决策，本票不定；技术上运行时切换 `Unfocusable` 是可行的。

### 多窗口（非嵌入的 Window 节点）

**观察**：`display/window/subwindows/embed_subwindows` 默认 `true`（单窗口模式，子窗口作为控件嵌在主窗口内）；设为 `false` 才会为每个子窗口创建独立的操作系统窗口，可以移出主窗口并使用原生窗口装饰。文档指出单窗口模式可能更快，因为不必为每个 popup 和 tooltip 创建独立窗口。[ProjectSettings class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_projectsettings.rst) [Viewport class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_viewport.rst)

**观察**：`Window` 的多项能力被文档明确限定为"只对原生窗口有效，即主窗口以及主视口关闭 `gui_embed_subwindows` 时的 `Window` 派生节点"，包括 `Window.mode` 和 `titlebar_changed` 信号。子窗口位置在多窗口模式下是相对最左屏幕左上角的全局坐标；单窗口模式下则相对主窗口。[Window class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_window.rst)

**观察**：Windows 后端为每个窗口单独 `CreateWindowExW`，主窗口带 `WS_EX_APPWINDOW`（上任务栏与 Alt-Tab），子窗口不带。无边框窗口使用 `WS_POPUP`（源码注释指出过去用的是 `WS_EX_TOOLWINDOW`）。[display_server_windows.cpp:2572](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L2572)

**观察**：`WINDOW_FLAG_POPUP` 要求必须设置 transient parent，并且该标志在窗口可见时不能修改；popup 窗口独占接收全部输入而不从父窗口抢走焦点，在点击窗口外或应用切换时自动关闭。[DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst)

**观察**：Compatibility 渲染器在 Windows 上的原生 GL 管理器对所有窗口共用一个 GL display，源码注释直书 `// find display NYI, only 1 supported so far`；每个窗口有自己的 `HDC`，切换窗口时 `wglMakeCurrent(win.hDC, disp.hRC)`。像素格式只配置一次，其 alpha 位数取决于 `OS::is_layered_allowed()`。[gl_manager_windows_native.cpp:287](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/gl_manager_windows_native.cpp#L287)

**结论**：§2 要求的"管理 UI 用独立窗口"在 Windows 上可行，前提是把 `display/window/subwindows/embed_subwindows` 设为 `false`。这是全局设置，会同时把 popup、tooltip、对话框都变成原生窗口，因此是一次有性能取舍的全局选择。

**结论**：已知限制集中在三点：`always_on_top` 与 `transient` 互斥；popup 标志在窗口可见时不可改且必须有 transient parent；Compatibility 下多窗口共享单一 GL 上下文，每帧按窗口切换 current。

**建议**：

- 管理窗口用独立的 `Window` 节点 + `embed_subwindows = false`，不要另起进程。
- 管理窗口不要把 transient parent 设为主窗口，否则主窗口的置顶状态会通过 `_is_always_on_top_recursive` 传染给它。
- 首版只开一个管理窗口，避免同时验证多窗口与 Compatibility 上下文切换的性能表现。

### 失焦、遮挡、最小化与限帧

**观察**：`Main::iteration()` **无条件**调用 `OS::get_singleton()->get_main_loop()->process(...)`，其后才是渲染。渲染是否发生取决于 `wants_present = (can_any_window_draw() || has_additional_outputs()) && RenderingServer::is_render_loop_enabled()`。[main.cpp:5062](https://github.com/godotengine/godot/blob/4.7.2-stable/main/main.cpp#L5062) [main.cpp:5081](https://github.com/godotengine/godot/blob/4.7.2-stable/main/main.cpp#L5081)

**观察**：Windows 后端的 `window_can_draw()` 实现是 `return !wd.minimized;`，`can_any_window_draw()` 是"存在任一未最小化的窗口"。整份 Windows 显示服务器源码中没有任何遮挡（occlusion）检测。[display_server_windows.cpp:3123](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L3123)

**观察**：每帧末尾 `Main::iteration()` 调用 `OS::add_frame_delay(DisplayServer::window_can_draw(), ...)`。`OS::add_frame_delay` 里，`is_in_low_processor_usage_mode() || !p_can_draw` 任一成立就把动态延时设为 `low_processor_usage_mode_sleep_usec`；`max_fps > 0` 时再取 `max(dynamic_delay, 1000000 / max_fps)`。[main.cpp:5178](https://github.com/godotengine/godot/blob/4.7.2-stable/main/main.cpp#L5178) [os.cpp:708](https://github.com/godotengine/godot/blob/4.7.2-stable/core/os/os.cpp#L708)

**观察**：`application/run/low_processor_mode_sleep_usec` 默认 `6900`（引擎注释写明"大约 144 FPS"）；`low_processor_mode` 默认 `false`，其语义是"只在需要时才重绘屏幕"，文档明确说明它面向编辑器和移动应用，"对大多数游戏，因为屏幕每帧都要重绘，建议保持关闭"。[ProjectSettings class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_projectsettings.rst) [OS class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_os.rst)

**观察**：`Engine.max_fps`（运行时可写）与 `application/run/max_fps`（默认 0 = 不限）是主要限帧手段；`display/window/vsync/vsync_mode` 为 Enabled/Adaptive 时优先，上限不超过显示器刷新率。`application/run/frame_delay_msec` 强加固定帧间延时，文档说"多数情况下应优先用 `max_fps`，它更精确"。[Engine class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_engine.rst) [ProjectSettings class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_projectsettings.rst)

**观察**：`RenderingServer.render_loop_enabled = false` 时"完全禁用渲染，但引擎逻辑仍在处理"，可用 `force_draw()` 手动出帧；命令行 `--disable-render-loop` 等效。[RenderingServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_renderingserver.rst) [main.cpp:1951](https://github.com/godotengine/godot/blob/4.7.2-stable/main/main.cpp#L1951)

**观察**：`_Process()` 的调用频率取决于帧率；`_PhysicsProcess()` 默认按固定频率运行。[Idle and Physics Processing](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/idle_and_physics_processing.rst)

**观察**：失焦有两组通知：`NOTIFICATION_WM_WINDOW_FOCUS_IN/OUT`（本引擎实例某个窗口的焦点变化，含窗口间切换）与 `NOTIFICATION_APPLICATION_FOCUS_IN/OUT`（整个应用与桌面或第三方应用之间的焦点切换），桌面与移动平台均已实现。`Window` 另有 `focus_entered`/`focus_exited` 信号。[Node class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_node.rst) [Window class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_window.rst)

**观察**：用户拖动或缩放窗口时，Windows 进入模态循环。Godot 在 `WM_ENTERSIZEMOVE` 里用 `SetTimer(..., USER_TIMER_MINIMUM, ...)` 起定时器，在 `WM_TIMER` 中调用 `Main::iteration()`，在 `WM_EXITSIZEMOVE` 里 `KillTimer`。也就是说拖窗期间主循环仍在跑，但节奏被定时器限制（`USER_TIMER_MINIMUM` 为 10 毫秒）。[display_server_windows.cpp:6749](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L6749)

**结论**：逐条回答 §71：

| 状态 | `_Process` / `_PhysicsProcess` | 渲染 | 自动限帧 |
|---|---|---|---|
| 失焦 | 照常运行 | 照常全速 | 无 |
| 被其他窗口遮挡 | 照常运行 | 照常全速（引擎不检测遮挡） | 无 |
| 最小化 | 照常运行 | 跳过（`can_any_window_draw()` 为假） | 有，`low_processor_mode_sleep_usec`（默认约 145 Hz）自动生效 |

**结论**：§71 的 "Simulation 与窗口状态严格解耦" 在 Godot 中是默认行为，不需要任何额外代码；真正需要注意的是反方向——最小化时"Rendering 可以停止或显著降低"引擎已经做了，而"遮挡时省电"引擎没做。

**结论**：`_Process` 频率随帧率变化，因此 §25 的确定性 Simulation 绝不能把 `_Process` 的 `delta` 当作时钟。任何一档限帧都会直接改变 `_Process` 的调用次数。

**建议**：

- `SimulationClock` 由 `_PhysicsProcess` 的固定步长或自持累加器驱动，`_Process` 只负责表现层插值。这样限帧、最小化、拖窗节流都不影响模拟结果。
- 桌面常驻场景默认给 `Engine.MaxFps` 一个保守值（Docked 常态是长时间挂机），并允许玩家在设置里调整。这是省电的主要旋钮。
- 不要打开 `low_processor_mode`：3D 火车持续运动意味着每帧都有变化，`has_changed()` 恒为真，省不了电，反而引入"只在变化时重绘"的额外判断。
- 需要"遮挡时降帧"时自行实现：监听 `NOTIFICATION_APPLICATION_FOCUS_OUT` 降低 `Engine.MaxFps`，`FOCUS_IN` 时恢复。真正的遮挡检测需要额外 Win32 手段，超出本票范围。
- 最小化若要比默认的约 145 Hz 更省，用 `RenderingServer.RenderLoopEnabled` 或调大 `OS.LowProcessorUsageModeSleepUsec`，两者都不影响 `_Process`。

### Compatibility 渲染器与 DPI

**观察**：`HandleType.WINDOW_VIEW`（`HDC`）与 `OPENGL_CONTEXT`（`HGLRC`，ANGLE 下为 `EGLContext`）标注为"仅 Compatibility 渲染器"，而 `WINDOW_HANDLE`（`HWND`）无此限制。[DisplayServer class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_displayserver.rst)

**观察**：`is_window_transparency_available()` 在非 RD（即 Compatibility）路径下直接返回 `OS::is_layered_allowed()`；GL 像素格式的颜色位与 alpha 位在启动时按 `is_layered_allowed()` 一次性决定，对所有窗口生效。[display_server_windows.cpp:5306](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L5306) [gl_manager_windows_native.cpp](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/gl_manager_windows_native.cpp)

**观察**：Godot 在 `is_hidpi_allowed()` 为真时调用 `SetProcessDpiAwareness(PROCESS_SYSTEM_DPI_AWARE)`，即**系统 DPI 感知**，不是 per-monitor 感知。`display/window/dpi/allow_hidpi` 默认 `true`，文档指出关掉它会走低 DPI 回退，"会导致窗口显示模糊或像素化，并可能引发各种窗口管理缺陷"。[display_server_windows.cpp:7927](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/windows/display_server_windows.cpp#L7927) [ProjectSettings class reference](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_projectsettings.rst)

**结论**：Compatibility 对 AppBar 方案本身没有影响——所需的只有 `HWND`，与渲染器无关。它的影响集中在多窗口（单一共享 GL 上下文、每帧切换 current）与透明（像素格式全局一次性决定）。

**结论**：系统 DPI 感知是多显示器 Dock 的实质风险。当玩家选择的目标显示器与主显示器缩放比例不同时，系统会对本进程的坐标做 DPI 虚拟化，而 shell 本身是 per-monitor 感知的，两边对同一个矩形的理解可能不一致，Dock 条的高度和位置可能偏差。这条必须实测。

**建议**：

- 首版把"跨不同缩放比例的显示器 Dock"列为已知限制并实测，而不是先写补偿代码。
- 不要为了 Dock 去改 `allow_hidpi`；关掉它会引入文档明说的窗口管理缺陷。
- 首版不使用 per-pixel transparency，避免像素格式与多窗口叠加带来的额外变量。

## 建议的最小实现路径

分五步，每步都可以独立验证，前一步不通不进入下一步。

1. **纯 Godot 的窗口形态**。用 `Window.Borderless`、`Window.Unfocusable`、`Window.CurrentScreen`、`Window.Position`/`Size` 做出一个底部条形无边框窗口，不注册 AppBar。验证：无边框、覆盖目标显示器宽度、点击不抢焦点、鼠标事件仍到达、任务栏仍有条目。
2. **确认 `HWND` 通路**。`(nint)DisplayServer.WindowGetNativeHandle(HandleType.WindowHandle, 0)`，用 `GetMonitorInfo` 打印目标显示器的 `rcMonitor`/`rcWork`，与 Godot 的 `screen_get_position`/`screen_get_usable_rect` 对照，确认坐标原点差。
3. **AppBar 注册、定位与注销**（先不接回调）。实现 `ABM_NEW` → `ABM_QUERYPOS` → `ABM_SETPOS` → `MoveWindow` → `ABM_REMOVE`。验证：最大化其他程序时它们让出底部条带；退出后工作区完整归还。
4. **接回调**。按方案 A 用 `SetWindowSubclass` 接自定义消息，处理 `ABN_POSCHANGED`（重跑定位）与 `ABN_FULLSCREENAPP`（调 z-order）。验证：改任务栏位置或自动隐藏、插拔显示器、启动一个全屏程序。
5. **Docked ↔ Floating 切换**。Floating = `ABM_REMOVE` + 恢复自由几何 + 按玩家选项设 `AlwaysOnTop`；Docked = 反向。验证：来回切换十次工作区不泄漏、不重复注册。

代码边界建议与 §74 一致：一个不继承 `Node` 的 `WindowsAppBar` 负责全部 P/Invoke 与 Win32 几何；一个 Godot 侧的适配节点负责在合适的通知里调用它并转发玩家设置。领域层不感知窗口。

## 风险清单

按影响排序。

1. **工作区泄漏**。未发送 `ABM_REMOVE` 就退出（崩溃、强杀、漏掉某条退出路径），底部条带会继续被系统预留。必须覆盖所有退出路径，并考虑启动时对残留注册做一次自愈。
2. **系统 DPI 感知下的混合缩放多显示器**。Godot 只声明 `PROCESS_SYSTEM_DPI_AWARE`，目标显示器缩放与主显示器不同时，Dock 矩形可能错位或高度不符。需要实测，且没有干净的应用侧修法（改 DPI 感知级别属于改引擎）。
3. **`no_focus` 连带 `WS_EX_TOPMOST`**。与 §73 的置顶策略意图冲突，最终 z-order 还受引擎后续 `SetWindowPos` 影响，行为需实测确认。
4. **自设扩展样式会被覆盖**。任何绕过 Godot 直接 `SetWindowLongPtr(GWL_EXSTYLE)` 的位，都会在引擎下一次 `_update_window_style()`（改 borderless、always_on_top、mode、resizable 等时触发）被整体重写。只用 Godot 的标志，不要自己叠样式。
5. **子类化的生命周期**。`SetWindowSubclass` 不能跨线程；委托必须防 GC；必须在窗口销毁前 `RemoveWindowSubclass`。做错会在退出时崩溃或产生难查的消息丢失。
6. **无焦点窗口的输入缺口**。收不到键盘；滚轮取决于系统"悬停滚动非活动窗口"设置。相机操作与管理 UI 的入口设计必须绕开这两点。
7. **遮挡不省电**。引擎不检测遮挡，被完全挡住时仍全速渲染。桌面常驻场景下这是长期功耗的主要来源，需要自己按失焦或可见性做降帧策略。
8. **多显示器 AppBar 未经一手文档确认**（见下）。若二级显示器不支持工作区预留，"玩家可选显示器 + Docked" 的组合就需要产品层退让。
9. **坐标原点差**。Godot 坐标与 Win32 虚拟桌面坐标相差 `_get_screens_origin()`，只在有显示器位于主显示器左侧或上方时暴露，单显示器开发期完全不会出现。
10. **模态循环期间的节流**。拖窗或缩放期间主循环由 10 毫秒定时器泵送，其他模态循环（原生对话框等）不在此保护范围内。Simulation 用固定步长时钟即可免疫。

## 剩余不确定性

以下未能在一手资料中确认，也未做代码验证（本票明确不做 spike）：

- **二级显示器上的 AppBar 工作区预留**。`APPBARDATA.rc` 是屏幕坐标、`ABM_GETAUTOHIDEBAREX`/`ABM_SETAUTOHIDEBAREX` 明确支持"特定显示器的特定边"，都指向多显示器可用；但微软文档没有一句直接说明 `ABM_SETPOS` 会预留非主显示器的工作区，而 "Using Application Desktop Toolbars" 的示例用的是只对主显示器成立的 `GetSystemMetrics(SM_CXSCREEN)`。需实测。
- **隐藏窗口能否作为 AppBar 宿主**（方案 B）。`HWND_MESSAGE` 窗口"没有 z-order、不能被枚举"，几乎肯定不适合；一个从不可见的普通顶层窗口是否被 shell 正常接受，无一手结论。
- **Godot `no_focus` 窗口的最终 z-order**。样式里加了 `WS_EX_TOPMOST`，但 `_update_window_style()` 随后按 `always_on_top` 决定 `HWND_TOPMOST`/`HWND_NOTOPMOST`，两者叠加的结果需要实测。
- **`SetWindowSubclass` 与 Godot `"Engine"` 窗口过程共存的实际表现**。理论上同进程同线程子类化是合法的，但引擎在窗口过程里有线程锁与重入 `Main::iteration()` 的逻辑，需实测确认不会死锁或重入。
- **混合 DPI 下 Dock 条的实际误差量级**。
- 全部结论基于文档与 `4.7.2-stable` 源码阅读，没有在真实桌面上运行过任何 AppBar 代码。
