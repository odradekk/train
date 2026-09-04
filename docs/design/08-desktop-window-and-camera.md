# 08 桌面窗口与相机

> 来源：原稿 §2、§67–73；转录 Q18–Q20、Q154–Q165；研究 [`docs/research/desktop-dock-window.md`](../research/desktop-dock-window.md)（票 #27）。

## 边界

本文定义主窗口的桌面形态、窗口与 Simulation 生命周期、Pause、Docked 与 Floating 模式、相机。Simulation 时钟本身见 [04](04-deterministic-simulation.md)；产品形态的整体描述见 [00](00-product-and-core-loop.md)。

## 不变量

- Simulation 与窗口状态严格解耦：主窗口失焦、被遮挡、最小化、打开管理 UI 均不停止 Simulation。
- 只有显式 Pause 才停止 SimulationClock。
- 管理 UI 是同一 World Model 的 View，不隐式改变 3D 视图（转录 Q165）。

## 规则

### 窗口与 Simulation 生命周期（§71）

- 3D 主窗口是 Primary Window，辅助 UI 为 Secondary Window。
- 以下情况 Simulation 均继续：主窗口失焦、用户正在使用 IDE、Floating View 被其他窗口遮挡、打开 Management UI、主窗口最小化。主窗口最小化时 Rendering 可以停止或显著降低，但 Simulation 不停止。
- 首版关闭主 3D 窗口视为退出游戏；托盘与后台模式后补（转录 Q154）。

### Pause（§72）

- Pause 时仍然允许 UI、Camera、Window、Design 操作、Rendering。
- 首版没有 Offline Progress：关闭游戏即 Simulation stops。未来可作为独立 Feature 实现 Offline Progress 或 Bulk Simulation。

### Docked 与 Floating（§73）

- **Docked**：首版 Bottom only；占满目标 Monitor Width；Height adjustable；占据 Desktop Work Area。转录 Q18 选择 Windows AppBar 式真正占用工作区，否决 Always-on-top Overlay（留作可选）；转录 Q19 要求不抢焦点；转录 Q20 默认主显示器可选，窗口可拖动。左右竖条不合横向构图，Top Dock 日后易加（转录 Q160）。
- **Floating**：任意移动；合理范围调整尺寸；不预留 Desktop Work Area；Always-on-top 用户可选，默认关闭 Topmost。
- 研究 #27 的事实：Godot 没有工作区预留 API，Docked 须自注册 Win32 AppBar 并子类化 Godot 的 HWND 接收位置通知；不抢焦点靠 `Window.Unfocusable` 开箱可用，代价是无键盘输入；失焦、遮挡、最小化时 `_Process` 照常运行，只有最小化会门控渲染，遮挡没有任何检测。

### Camera（§67）

- 主 3D View 使用 Perspective Camera。Camera Focus 永远绑定 Locomotive 或某个 Carriage，不支持普通自由飞行。
- 允许 360° Horizontal Yaw、受限 Vertical Pitch、Distance Zoom。Zoom 只改变 Camera Distance，FOV 基本固定。

### Camera Follow（§68）

Camera Orientation = Train Heading + Player Orbit Offset。火车转弯时 Camera 平滑跟随；玩家改变的是相对于列车的观察位置。

### Camera Target Switch（§69）

玩家在 Locomotive、Carriage #1、Carriage #7 之间切换时使用平滑 Transition，不会瞬间 Teleport。如果当前 Focus Target 被摧毁，使用明确 fallback 选择同一 Train 中的有效目标；具体优先级通过实际 UI 设计确定。

### Camera Collision（§70）

首版不采用传统第三人称 Spring Arm；Camera 不因为普通建筑或地形持续自动 Zoom In/Out。未来如果遮挡问题明显，可考虑 Obstacle Fade、Camera framing correction。

## 首版范围

Primary 3D Window、Bottom Dock、Floating、Display selection、Focus behavior、Perspective Camera、Orbit、Zoom、target switching、minimized rendering suspension（原 §81 Phase 7）。

## 明确延后

Overlay、Top 与左右 Dock、托盘与后台模式、Offline Progress、倍速 UI（转录 Q18、Q154、Q158、Q160）。

## 开放问题

- [补齐首版范围缺口：破坏触发源、玩家 Blueprint 含义、Faction 与窗口行为](https://github.com/odradekk/train/issues/30)：失焦、遮挡与最小化时的渲染策略与帧率上限；Docked 模式采用哪条实现路径。研究 #27 未能核实：二级显示器上 AppBar 是否真的预留工作区；`SetWindowSubclass` 与引擎窗口过程的共存表现；混合 DPI 下 Dock 条的误差。
- 地图迷雾：相机焦点目标被摧毁时的回退优先级（转录 Q162）。
- 未冻结参数：Camera FOV、Zoom Min/Max、Window Default Height（[roadmap](roadmap.md)）。
