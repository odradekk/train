# 08 窗口与界面

> **所属层**：渲染层。
>
> **修订中**：全文由[窗口与界面的设计和规划](https://github.com/odradekk/train/issues/207)改写。“Docked 与 Floating”“Demo 的 Docked 实现与验收”等桌面停靠规则已由[产品定位、玩法支柱与 Demo 范围](https://github.com/odradekk/train/issues/194)删除；“世界编辑器的观察与模拟控制”同时属于[地图](https://github.com/odradekk/train/issues/193)迷雾“编辑器的去向”。改写完成前，上述规则不能作为实现依据；其余规则继续有效。

> 决策出处：转录 Q18–Q20、Q154–Q165；研究 [`docs/research/desktop-dock-window.md`](../research/desktop-dock-window.md)（票 #27）；2026-09-05 [#30](https://github.com/odradekk/train/issues/30) 确认渲染策略、AppBar 路径与验收要求。

## 边界

本文定义主窗口的桌面形态、窗口与 Simulation 生命周期、Pause、Docked 与 Floating 模式、相机。Simulation 时钟本身见 [04](04-deterministic-simulation.md)；产品形态的整体描述见 [00](00-product-and-core-loop.md)。

## 不变量

- Simulation 与窗口状态严格解耦：主窗口失焦、被遮挡、最小化、打开管理 UI 均不停止 Simulation。
- 只有显式 Pause 才停止 SimulationClock。
- 管理 UI 是同一 World Model 的 View，不隐式改变 3D 视图（转录 Q165）。

## 规则

### 窗口与 Simulation 生命周期

- 3D 主窗口是 Primary Window，辅助 UI 为 Secondary Window。
- 以下情况 Simulation 均继续：主窗口失焦、用户正在使用 IDE、Floating View 被其他窗口遮挡、打开 Management UI、主窗口最小化。主窗口最小化时停止该窗口的渲染，但 Simulation 不停止；仍可见的辅助窗口继续正常渲染。
- Demo 中关闭主 3D 窗口视为退出游戏；托盘与后台模式后补（转录 Q154）。

### Demo 渲染策略

- 失焦时正常渲染，保证用户操作其他软件时仍可观察列车。
- 暂不专门检测遮挡，被遮挡时沿用正常渲染策略。
- 主窗口最小化时停止其渲染，恢复后继续渲染。
- 提供可配置的渲染帧率上限，默认值由性能测试确定。限帧与窗口状态不得改变模拟时间推进规则。

### Pause

- Pause 时仍然允许 UI、Camera、Window、Design 操作、Rendering。
- Demo 没有 Offline Progress：关闭游戏即 Simulation stops。未来可作为独立 Feature 实现 Offline Progress 或 Bulk Simulation。

### Docked 与 Floating

- **Docked**：Demo 只支持 Bottom；占满目标 Monitor Width；Height adjustable；占据 Desktop Work Area。转录 Q18 选择 Windows AppBar 式真正占用工作区，否决 Always-on-top Overlay（留作可选）；转录 Q19 要求不抢焦点；转录 Q20 默认主显示器可选，窗口可拖动。左右竖条不合横向构图，Top Dock 日后易加（转录 Q160）。
- **Floating**：任意移动；合理范围调整尺寸；不预留 Desktop Work Area；Always-on-top 用户可选，默认关闭 Topmost。
- 研究 #27 的事实：Godot 没有工作区预留 API，Docked 须自注册 Win32 AppBar 并子类化 Godot 的 HWND 接收位置通知；不抢焦点靠 `Window.Unfocusable` 开箱可用，代价是无键盘输入；失焦、遮挡、最小化时 `_Process` 照常运行，只有最小化会门控渲染，遮挡没有任何检测。

### Demo 的 Docked 实现与验收

- 采用 Windows AppBar 预留工作区，通过 `SetWindowSubclass` 在 Godot 主窗口的 HWND 上接收系统通知；其他窗口功能使用 Godot API。平台互操作与世界模型隔离。
- 必须验收工作区预留与释放、Docked/Floating 来回切换、多显示器与混合 DPI，以及窗口消息处理和 Godot 的共存表现；这些窗口平台行为的验证方式在实现桌面窗口时确定。
- 上述场景尚未经实际运行验证；选定实现路径不代表兼容性已经得到证明。具体复现步骤和通过标准在对应切片 spec 中明确。

### Camera

- 正式游戏主 3D View 使用 Perspective Camera。Camera Focus 绑定 Locomotive 或某个 Carriage，不支持普通自由飞行；世界编辑器的观察方式见下文专节。
- 允许 360° Horizontal Yaw、受限 Vertical Pitch、Distance Zoom。Zoom 只改变 Camera Distance，FOV 基本固定。

### Camera Follow

下列车辆跟随规则也适用于世界编辑器的跟随模式，不限制其独立自由观察模式。

Camera Orientation = Selected Vehicle Displayed Body Heading + Player Orbit Offset。火车转弯时 Camera 平滑跟随；玩家改变的是相对于所选车辆的观察位置。

相机读取 [07](07-rendering.md) 中与车体一致的显示位置与朝向，不直接跟随跳动的模拟步位置，不用速度方向代替车体朝向。#33 的双车头换端不自动切换相机目标，也不因活动车头或行驶方向改变让镜头旋转 180°；玩家仍可显式切换观察车辆。

### Camera Target Switch

玩家在 Locomotive、Carriage #1、Carriage #7 之间切换时使用平滑 Transition，不会瞬间 Teleport。如果当前 Focus Target 被摧毁，使用明确 fallback 选择同一 Train 中的有效目标；具体优先级通过实际 UI 设计确定。

### Camera Collision

Demo 不采用传统第三人称 Spring Arm；Camera 不因为普通建筑或地形持续自动 Zoom In/Out。未来如果遮挡问题明显，可考虑 Obstacle Fade、Camera framing correction。

### 世界编辑器的观察与模拟控制

[项目级世界编辑器的操作流程](https://github.com/odradekk/train/issues/96)已确认 Q1–Q10。用户认可流程原型，不代表正式引擎实现通过验收。

- 世界编辑器提供 3D 自由观察、聚焦选中对象和跟随指定车辆。对象列表/检查面板的选择不自动改变镜头；选中后可直接发起聚焦或跟随。可以切回自由视角检查地形、建筑、线路和远处故障，不要求世界中先有车辆。
- 世界编辑器的近距离目标切换使用平滑过渡；远距离目标先准备其区域，再短暂淡出/淡入，不沿途加载整段路程。准备期间保留原视图并显示状态，不改变模拟运行/暂停状态；失败保留原视图并报告原因，连续切换以最后一次请求为准。该调试规则是上文普通平滑目标切换的远距离例外，见 [表现验收契约](07-rendering.md#本阶段表现验收契约)。
- 世界编辑器中对象仍存在就保留选择和跟随，编组拆分不自动换到活动车头；目标彻底销毁时清除失效引用并提示，镜头保持当前位置、转为自由视角，不跳到其他列车。上文正式游戏的目标回退不适用于此调试规则。
- 常用调试操作提供可视入口，控制台承担精确参数与批量操作；两者调用同一套正式逻辑，保留可追查的操作结果，不引入正式玩家建造 UI。工作区、选择与输入、各类创作工具及运行诊断见 [15](15-world-editor.md)。
- 新建空白世界、生成世界、打开示例或存档后，世界编辑器默认暂停自动推进；固定测试配置只用于自动测试和制作示例，不作为人工入口，见 [09](09-persistence.md#世界编辑器存档工作流)。恢复时仍保留原速度、运行计划及故障状态；进入暂停不等于对所有列车执行停车。通过显式运行、暂停、单步、指定步数或[有限条件暂停](15-world-editor.md#有限条件暂停)控制模拟。
- 打开面板、查询、切换镜头均不隐式暂停。修改、伤害和修复可在暂停时执行，也可在运行时提交到明确模拟步；沿用正式修改与故障恢复规则，不因调试入口而绕过验证。确实只能在暂停时执行的操作明确提示，由用户暂停后再提交，不把所有编辑一律限制为暂停。
- 运行、暂停和步进作用于当前世界：模拟造成的变化直接保留，暂停不自动还原，保存包含运行结果。试验后回退须明确重新加载之前的存档；隔离复现或测试使用独立会话，不改变当前编辑世界。编辑撤销不回退模拟时间。

## Demo 范围

Primary 3D Window、Bottom Dock（Windows AppBar）、Floating、Display selection、Focus behavior、Perspective Camera、Orbit、Zoom、target switching、minimized rendering suspension、可配置渲染帧率上限。

以上桌面窗口能力属于 Demo。本阶段只要求上文世界编辑器的观察与模拟控制，并用普通窗口做性能验收；Bottom Dock、AppBar、多显示器及其验证不阻塞本阶段完成，见 [00](00-product-and-core-loop.md#本阶段范围)。

倍速控制属于 Demo。倍速档位与规则见 [04](04-deterministic-simulation.md#时钟与倍速)，界面由[窗口与界面的设计和规划](https://github.com/odradekk/train/issues/207)设计。

## Demo 之后

专门遮挡检测；Overlay、Top 与左右 Dock、托盘与后台模式、Offline Progress（转录 Q18、Q154、Q160）。

## 开放问题

- 地图迷雾（不立票）：正式游戏相机焦点目标被摧毁时的回退优先级（转录 Q162）；待对应游戏交互切片再评估。世界编辑器的目标失效规则已在上文确定。
- 地图迷雾（不立票）：Camera FOV、Zoom Min/Max、Window Default Height、默认渲染帧率上限调优；窗口能力属于 Demo，不阻塞本阶段，实现桌面窗口时再调优。
