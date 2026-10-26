# Render with Forward+

Status: proposed

## Problem

Compatibility 是 MVP 阶段为了覆盖最广的硬件选的，当时只需要几块占位方盒。美术方向要求物理上可信的材质和自然光照（[00 视觉定位](../../../../docs/art/00-direction.md)），而 Godot 4.7 的 Compatibility 只有低动态范围（RGBA8），没有运行时全局光照、屏幕空间反射、TAA 和贴花，SSAO 和辉光也只有简化版。它能显示烘焙的光照贴图，但线路场景在运行时生成，没法预先烘焙。

## Proposal

2026-10-09 经[Compatibility 渲染器下的画面目标](https://github.com/odradekk/train/issues/242)确认，属于地图[美术设计文档：整理初稿，建成 docs/art](https://github.com/odradekk/train/issues/239)。

游戏改用 Godot 的 Forward+ 渲染器，不再使用 Compatibility。目标配置的 GTX 1650 满足 Forward+ 的硬件要求（Vulkan 或 D3D12）。

### 规则与推论

- 体验目标改为“Forward+ 渲染器、默认画质”下 60 FPS（[00 的本阶段性能目标](../../../../docs/design/00-product-and-core-loop.md#本阶段性能目标)），默认画质只开必要效果，较重的效果做成画质选项；具体清单见 [05 光照与可读性](../../../../docs/art/05-lighting-and-readability.md)。实测由性能测量的实施工单负责。
- 着色器里的颜色贴图一律写 `source_color`；Forward+ 要求如此，以后换渲染器也不会出错。

## Alternatives considered

**保持 Compatibility，以它的能力为画面上限。** 它的开销最低，但写实的光照和材质做不出来。

**首个切片先用 Compatibility，以后再切换。** 两种渲染器的颜色范围、辉光和 SSAO 都不同，切换后灯光和材质要重调一遍；渲染层还没有实现，现在切换代价最低。

## Acceptance criteria

项目使用 Godot 的 Forward+ 渲染器；着色器里的颜色贴图都写 `source_color`，SDFGI 默认不开。

## Risks

- Forward+ 的基础开销最高。
- 没有 Vulkan 或 D3D12 的机器，Godot 会自动退回 Compatibility 运行，画面降级。
- SDFGI 按世界坐标分区，浮动原点平移时可能整片重算，默认不开，先实测。
