# 美术设计文档

本目录规定游戏的美术风格和资产制作规则，是这两方面的权威文档：画面整体是什么基调，车辆、建筑、地形和界面怎么画，资产按什么规格制作、怎样检查。本目录由地图[美术设计文档：整理初稿，建成 docs/art](https://github.com/odradekk/train/issues/239)建立，起点是 2026-10-08 美术讨论的整理稿。

## 与设计文档的关系

- [设计文档](../design/README.md)是游戏的设计契约。占格、朝向、内容格式与表现绑定、渲染路径、核心状态等技术规则由设计文档维护；本目录引用这些规则，不重述。
- 本目录在这些技术规则之内规定画法、规格和检查。美术规则需要改动技术规则时，同时修改设计文档的对应章节。
- 规则写成后，资产制作和代码改动另开实施工单，不因本目录发布而扩大当前切片的实现范围。

## 文档地图

| 文件 | 内容 | 状态 |
| --- | --- | --- |
| [00-direction.md](00-direction.md) | 视觉基调、关键词、技术时代感、不采用的方向 | 已写 |
| `01-construction-language.md` | 用途与造型、尺度与子网格、构件和 Shape、接缝与倒角 | 由[非立方体形状与构件语言](https://github.com/odradekk/train/issues/245)编写 |
| `02-materials-and-textures.md` | 材质、贴图密度、映射、细节层次 | 由[立方体体素的面贴图形式](https://github.com/odradekk/train/issues/243)编写，[首个切片的体素面贴图规格](https://github.com/odradekk/train/issues/246)补“本阶段” |
| `03-color-and-paint.md` | 默认色彩、玩家涂装 | [首个切片的体素面贴图规格](https://github.com/odradekk/train/issues/246)补“本阶段”；其余待拆票 |
| `04-world-assets.md` | 建筑、地形、自然物、铁路 | 由[首个切片的场景美术规格](https://github.com/odradekk/train/issues/247)编写 |
| `05-lighting-and-readability.md` | 光照、构图、可读性 | 由[Compatibility 渲染器下的画面目标](https://github.com/odradekk/train/issues/242)编写 |
| `06-wear-damage-and-motion.md` | 磨损、损伤、动画、特效 | 待拆票 |
| `07-icons-and-ui.md` | 2D 图标、宏观地图、界面视觉 | 由[宏观地图、界面与图标的视觉风格](https://github.com/odradekk/train/issues/244)编写 |
| `08-asset-pipeline-and-review.md` | 制作流程、交付物、样机与检查 | 待拆票 |

每篇的固定小节：边界、不变量、规则、本阶段、Demo 范围、Demo 之后、开放问题；没有内容的小节写“无”。“本阶段”写首个切片可以直接制作的规格，首个切片的范围见 [00 的本阶段范围](../design/00-product-and-core-loop.md#本阶段范围)。

## 出处

- 2026-10-08 美术讨论的决定以“美术 Qn”引用[美术讨论决定记录](../research/art-grilling-decisions.md)。这些决定的美术意图默认有效；与当前设计文档或代码冲突的条目，由地图中对应的票重新决定。
- 之后的决定引用解决它的票的名称和链接，不用 §n 引用章节。被废弃的规则直接删除，历史留在 git、已关闭的票和决定记录中。
- 概念图、原型和验证报告见[美术参考与原型证据](../../archive/prototypes/art-reference-v1/README.md)。它们只证明各自观察到的范围：概念图说明方向，原型说明当时的实验效果，都不代表正式资产、Godot 实现或性能已经通过。

## 术语

由[美术文档的术语](https://github.com/odradekk/train/issues/241)填写。在此之前沿用 [CONTEXT.md](../../CONTEXT.md) 和[设计文档](../design/README.md)的用词。设计文档和代码也会用到的词写进 CONTEXT.md；只在美术制作中使用的词在这里定义。
