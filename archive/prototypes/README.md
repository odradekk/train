# 体素引擎与 Playground 设计原型归档

本目录保存规划阶段使用的独立原型与原始测量资料，不属于正式游戏实现，也不是正式引擎、生成器或性能验收结果。原型文件按原始内容归档，后续领域规则以已确认的[设计文档](../../docs/design/README.md)和[分期规格](../../docs/design/voxel-engine-stage-spec.md)为准。

| 资料 | 用途 | 原始提交与决策 |
| --- | --- | --- |
| [铁路几何比较](rail-geometry.prototype.html) | 早期二维线路与几何取舍 | `b7a8391`；[铁路几何决策](https://github.com/odradekk/train/issues/32) |
| [车辆包络](rail-envelope.prototype.html) | 五档车辆尺寸与早期包络观感 | `b7a8391`；[铁路几何决策](https://github.com/odradekk/train/issues/32) |
| [Three.js 三维铁路](rail-3d.prototype.html) | 早期简单体素三维观感 | `b7a8391`；[铁路几何决策](https://github.com/odradekk/train/issues/32) |
| [Playground 流程](playground-workflow.prototype.html) | 选择、聚焦/跟随、暂停、故障与保存切换的交互讨论 | `9876889`；[流程决议](https://github.com/odradekk/train/issues/96#issuecomment-5778442326) |
| [三维铁路机械](rail-mechanics-108.prototype.html)及[测量数据](rail-mechanics-108.measurements.json) | 连续弯坡、混编与首批机械参数的有界实验 | `2f0698a`；[参数决议](https://github.com/odradekk/train/issues/108#issuecomment-5793791612) |

原型分支的原始提交历史已合入主分支；旧提交地址仍可用于核对当时资料。早期参数、示例地图、模拟故障、离散采样及原型操作认可，不得代替现行契约要求的正式连续安全、真实体素占格、保存恢复、Godot 集成及目标实机验收。

HTML 原型可独立打开，含外部模块的原型仍依赖其原有网络资源；本次归档不改写原型、升级依赖或补作视觉验收。此目录通过 `.gdignore` 排除正式 Godot 资源导入。

更早的贸易玩法原型已经位于[历史 MVP 归档](../mvp/README.md)，保留原路径与证据，不在此复制或改写。
