# 系统与工种标签

issue 标签分四个维度，每个维度一个前缀，互不重叠：类型（`enhancement`、`bug`、`documentation`、`wayfinder:*`）、分诊状态（见 [triage-labels.md](triage-labels.md)）、系统（`area:`）和工种（`discipline:`）。前两个维度保持现状，本文规定后两个。

## 规则

- 创建或分诊 issue 时，打 1–2 个 `area:` 标签和 1 个 `discipline:` 标签。
- `area:` 只选主要改动所在的系统，顺带触及的系统不打。需要 3 个以上 area，或主要产出同时有代码和美术资产时，把 issue 拆小。
- 跟踪类 issue 不打这两类标签：`wayfinder:map`，以及只用来汇总子 issue 的父 issue。
- 新系统或新工种出现第一张 issue 时再建标签，名称取自设计文档主题，并更新本文的表。标签用 `gh label edit <旧名> --name <新名>` 改名，已打标签的 issue 会跟着更新。

## area：系统

`area:` 描述 issue 所属系统，不参与测试选择；测试按层和项目选择，见[统一测试入口](../run-tests.md)。

| 标签 | 范围 | 设计文档 |
| --- | --- | --- |
| `area:content` | 内容定义、体素定义、蓝图与设计属性、内容加载与重载 | 02 |
| `area:simulation` | 模拟时间、命令、确定性、录制与复现 | 04 |
| `area:world` | 世界图与世界生成 | 05 |
| `area:railway` | 铁路与列车运行 | 06 |
| `area:trade` | 市场、交易规则、资金、现车买卖与运行报告 | 19 |
| `area:saves` | 存档格式、读档、示例存档 | 09 |
| `area:production` | 资源、开采、库存、生产流程与运行 | 10–13 |
| `area:construction` | 建造、改装、维修与拆解任务 | 14 |
| `area:editor` | 设计编辑器、创作模式与调试模式 | 15 |
| `area:settlements` | 聚居地、人口、劳动力与聚居地服务 | 16 |
| `area:combat` | 战斗、世界生物、聚落袭击与征服 | 17 |
| `area:scenes` | 体素引擎层：区块、网格数据、拾取、线路场景、节点场景、场景切换 | 01 |
| `area:rendering` | Godot 渲染：网格上传、相机、材质 | 07 |
| `area:ui` | 窗口、宏观地图、面板、菜单 | 08 |
| `area:tooling` | 控制台、日志、测试运行器、CI、长测与性能测量、导出脚本 | — |

## discipline：工种

按 issue 的主要产出选择。

| 标签 | 主要产出 |
| --- | --- |
| `discipline:code` | 代码、测试、脚本、内容数据 |
| `discipline:art` | 美术资产：体素模型、贴图、图标、界面视觉稿 |
| `discipline:design` | 设计决定、设计文档、数值。wayfinder 的 grilling、research、prototype 票属于这一类 |

界面的交互与实现打 `area:ui` 和 `discipline:code`；界面的视觉资产打 `area:ui` 和 `discipline:art`。
