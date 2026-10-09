# 美术参考与原型证据（2026-10-08）

本目录保存 2026-10-08 美术讨论留下的概念图、两个 Three.js 原型和原型验证报告，由[迁移美术初稿并建立 docs/art 骨架](https://github.com/odradekk/train/issues/240)从整理稿 `TRAIN-Art-Design-v1.0.html` 迁入。它们是讨论证据，不是正式资产、Godot 实现或验收结果。现行美术规则见[美术设计文档](../../../docs/art/README.md)，讨论中的决定见[美术讨论决定记录](../../../docs/research/art-grilling-decisions.md)。

## 三种资料的用途

| 资料 | 用途 | 不能证明 |
| --- | --- | --- |
| 已认可的概念图 | 现代重装、近未来工业、尺度与气氛的目标 | 几何是否合法、精确包络、玩法、性能 |
| 北境重载线原型及其截图 | 整体材质气氛、轮廓与构图参考 | 画面中的对象都由正式体素定义构成 |
| Voxel Art Testbed 002 原型、截图与报告 | 可编辑构件、复用、映射、涂装和实验操作的验证 | 正式 Godot 实现、当前车辆尺度、完整的战斗和损伤模型、规模下的帧率 |

用户初步验收的是 Testbed 002 当时的效果。画面中的界面、背景布景、实验型号名和试验数值都不是规范。

## 原型

两个原型都是单文件 HTML，内嵌 Three.js 运行库，可直接用浏览器打开。文件按原字节保存，SHA-256 与整理稿记录的来源指纹一致。整理稿另记录了两份源码 ZIP 的哈希，ZIP 本身没有随初稿提供。

| 文件 | 内容 |
| --- | --- |
| [北境重载线](northern-corridor.prototype.html) | 页面标题“北境重载线 · Three.js 美术验证”，早于 Testbed 002 的美术场景原型。整理稿称参考图 C 为“前一版 Three.js 场景的程序截图”（Art Lab 001），与本文件的对应关系没有记录 |
| [Voxel Art Testbed 002](voxel-art-testbed-002.prototype.html) | 可编辑的体素构件实验台：军用、民用、工业维护建筑三个样机和构件台 |

## 参考图

整理稿只内嵌了阅读用的压缩图，原始 PNG 没有随初稿提供。下列 WebP 从整理稿中原样取出，与原始 PNG 不是同一份字节；原始 PNG 的哈希见下文来源文件指纹。

| 图 | 文件 | 说明 |
| --- | --- | --- |
| A 现代装甲列车与工业站场 | [concept-armored-train.webp](images/concept-armored-train.webp) | 讨论中生成的概念图，方向已被接受。参考现代铁路身份、厚重装甲、重型轮组、克制的照明与基础设施的尺度关系；不照抄每一片污迹，也不把永久雪景当作世界观 |
| B 泰坦长编组 | [concept-titan-convoy.webp](images/concept-titan-convoy.webp) | 讨论中生成的概念图。参考编组的延展、重复的车体与功能上装的节奏；不从图中反推车宽、轨距、人物尺寸或武器数值 |
| C 北境重载线整体气氛 | [art-lab-001-scene.webp](images/art-lab-001-scene.webp) | 前一版 Three.js 场景的程序截图，作为整体观感参考，不作为正式资产结构的唯一依据 |
| D Testbed 002 军用样机 | [testbed-military.webp](images/testbed-military.webp) | 检查装甲、观察窗、轮组、设备和实际格子编辑 |
| E Testbed 002 民用样机 | [testbed-civil.webp](images/testbed-civil.webp) | 检查共用资产怎样形成民用工业用途，而不是小一档的军用车 |
| F Testbed 002 工业维护建筑 | [testbed-depot.webp](images/testbed-depot.webp) | 检查梁柱、薄板、混凝土与设备构件能否在车辆和建筑之间复用 |
| G Testbed 002 构件台 | [testbed-catalog.webp](images/testbed-catalog.webp) | `lab:` 定义是实验目录；35 个形状、37 个构件定义是原型当时的数量，不是正式目录的要求 |

## 验证报告

[Testbed 002 浏览器验证报告](testbed-002-validation.md)记录 52 项检查全部通过，运行环境为 Chromium 144、WebGL 2、ANGLE SwiftShader 软件渲染。报告由原型制作时提供，迁入时没有重新运行，也不证明用户设备的帧率、正式核心模型或 Godot 实现。

## 使用限制

- T-24、C-16、D-24 是实验夹具的名称。T-24 的 24×13×10 格来自旧版泰坦包络；现行尺寸见 [03](../../../docs/design/03-vehicles-buildings-and-designs.md)。不能把旧样机整体放大后继续当作一格一块的布局。
- Testbed 的 300 CSS px 条形视口只用于低屏幕覆盖率的压力测试；桌面停靠已从产品规划删除。
- Testbed 的实验 JSON、撤销、逐格 Runtime ID 和“耐久归零即删除”只属于原型，不是正式的存档、身份或结构耐久模型。

## 来源文件指纹

整理稿记录的原文件 SHA-256。“本目录”一列说明这份文件在本目录中是否以原字节保存。

| 原文件 | 本目录 | SHA-256 |
| --- | --- | --- |
| 冰雪要塞的装甲列车.png | 压缩图 A | `e7826548ce102ed2debb4839e9f9f00b927235562845860f7f8f69735053f7ad` |
| 雪域巨构_装甲战争列车跨越峡谷.png | 压缩图 B | `5561764b5b80e4eea68be0713e6a64d85972fab8f77d91b8ca3641c518a9eafe` |
| TRAIN-Scene-Preview.png | 压缩图 C | `eb30777e3f8dc059f2faa5c71546095078ab5d01ba7dc90bb627e74cd6e99e4f` |
| TRAIN-Testbed-Preview.png | 压缩图 D | `7cdf3b069b0ce920ab864621e7510ebfcadfa9e31c95d271dafe37b662b078ba` |
| TRAIN-Testbed-Civil.png | 压缩图 E | `57d57dd7cb8fdf65076516b81456c386f8a415a6e59ae0a177f33dd18c2117f7` |
| TRAIN-Testbed-Depot.png | 压缩图 F | `43c6acfd20930eda3ba1169373601f5ecb151229ef37d748fa78e4991d62722e` |
| TRAIN-Testbed-Catalog.png | 压缩图 G | `8c426f9905c4db4c1e7e05847341a0c38279e9ba863687543e9f122c14979959` |
| TRAIN-Testbed-Validation.md | 由整理稿转写为 `testbed-002-validation.md` | `0123723abbf3c81a75c8bdea422be2d672373cc11f735f2ce2e8021ca69e850e` |
| TRAIN-Art-Decisions-Q231-Q240.md | 由整理稿转写进[美术讨论决定记录](../../../docs/research/art-grilling-decisions.md#末轮记录q231q240) | `3f2b8f1a331bb786613803028fc3627aca764436710db9a9270ae6b120c76d2e` |
| TRAIN-Northern-Corridor.html | 原字节：`northern-corridor.prototype.html` | `56c74cd3a10886e058a7bbe7b449b97a936d97f548db672b05a52b29252c8d4c` |
| TRAIN-Northern-Corridor-source.zip | 没有 | `e2be1c9c2576e51110f1a5d3e9e1f9dd9d9ac92212faa3bb95bd128eb2f29c8d` |
| TRAIN-Voxel-Art-Testbed.html | 原字节：`voxel-art-testbed-002.prototype.html` | `a5779cbdaefb2d193ed19f6b39d21957b3c47f5b1b5ecf0df4ce32ec226665ea` |
| TRAIN-Voxel-Art-Testbed-source.zip | 没有 | `ea703f43686a8319511f00fc981621ae0646b94483b4872baaf53279cc50ae05` |
