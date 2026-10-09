# 体素引擎与 Playground 设计原型归档

本目录保存规划阶段使用的独立原型与原始测量资料，不属于正式游戏实现，也不是正式引擎、生成器或性能验收结果。原型文件按原始内容归档，后续领域规则以已确认的[设计文档](../../docs/design/README.md)和[分期规格](../../docs/design/voxel-engine-stage-spec.md)为准。

| 资料 | 用途 | 原始提交与决策 |
| --- | --- | --- |
| [铁路几何比较](rail-geometry.prototype.html) | 早期二维线路与几何取舍 | `b7a8391`；[铁路几何决策](https://github.com/odradekk/train/issues/32) |
| [车辆包络](rail-envelope.prototype.html) | 五档车辆尺寸与早期包络观感 | `b7a8391`；[铁路几何决策](https://github.com/odradekk/train/issues/32) |
| [Three.js 三维铁路](rail-3d.prototype.html) | 早期简单体素三维观感 | `b7a8391`；[铁路几何决策](https://github.com/odradekk/train/issues/32) |
| [Playground 流程](playground-workflow.prototype.html) | 选择、聚焦/跟随、暂停、故障与保存切换的交互讨论 | `9876889`；[流程决议](https://github.com/odradekk/train/issues/96#issuecomment-5778442326) |
| [三维铁路机械](rail-mechanics-108.prototype.html)及[测量数据](rail-mechanics-108.measurements.json) | 连续弯坡、混编与首批机械参数的有界实验 | `2f0698a`；[参数决议](https://github.com/odradekk/train/issues/108#issuecomment-5793791612) |
| [世界编辑器交互](world-editor-interaction.prototype.html) | 分层选择、操作预览、原子拒绝、有限撤销和铁路/运行计划流程；三轮流程获用户确认 | 随本轮规格原样归档；[交互决议](https://github.com/odradekk/train/issues/140#issuecomment-5894620802) |
| [宏观地图与界面风格](macro-map-ui-style.prototype.html) | 宏观地图、面板、状态色、物品图标外框的统一样稿，供继续打磨；规则以[美术 07](../../docs/art/07-icons-and-ui.md)为准 | [视觉风格决议](https://github.com/odradekk/train/issues/244) |
| [美术参考与原型证据](art-reference-v1/README.md) | 2026-10-08 美术讨论的概念图、北境重载线与 Voxel Art Testbed 002 原型及其验证报告 | 由[迁移美术初稿并建立 docs/art 骨架](https://github.com/odradekk/train/issues/240)归档；[美术讨论决定记录](../../docs/research/art-grilling-decisions.md) |

原型分支的原始提交历史已合入主分支；旧提交地址仍可用于核对当时资料。早期参数、示例地图、模拟故障、离散采样及原型操作认可，不得代替现行契约要求的正式连续安全、真实体素占格、保存恢复、Godot 集成及目标实机验收。

HTML 原型可独立打开，含外部模块的原型仍依赖其原有网络资源；本次归档不改写原型、升级依赖或补作视觉验收。此目录通过 `.gdignore` 排除正式 Godot 资源导入。

更早的贸易玩法原型已经位于[历史 MVP 归档](../mvp/README.md)，保留原路径与证据，不在此复制或改写。
