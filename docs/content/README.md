# 内容定义标准

这里是内容包和各种内容定义的格式标准：字段说明、JSON Schema 和一个示例内容包。规则的来由见设计文档 [02](../design/02-definitions-and-content.md)、[03](../design/03-vehicles-buildings-and-designs.md) 和 ADR-0011 到 ADR-0015；字段怎样写，以这里为准。

`ContentFactory` 的 Initial 加载已支持 manifest v2、voxel v3、无嵌套的车辆／建筑／场景模型蓝图 v3、地貌 v1、聚居地类型 v3、聚居地模板 v2、五类规则和生成配置 v2（含 NPC 初始化参数），并使用宿主文件与资产检查端口提供的固定字节和事实。工厂提供新局输入、铁路表现参数及按最终所有者固定的模板副本和布局查询；模板的 `founding`、`inventory`、`production_plans` 非空时以 `unsupported_format` 拒绝，流程、生物聚落、嵌套、大型设备与生物仍待后续任务，完整示例包尚不能加载。当前范围的捕获、恢复、重载和复现已接入，详见[内容契约](../contracts/content.md)；游戏宿主已通过 `App/Composition/` 接入真实工厂，旧内容加载路径在 [#452](https://github.com/odradekk/train/issues/452) 退役。当前目标是完整 Demo；聚居地类型版本 3 已移除临时供需、恢复量和站台数，保留的临时市场表及其 Schema、样例仅是历史记录。实施按[工单重组](https://github.com/odradekk/train/issues/294)获准的六阶段方案推进，各阶段交付当期所需的真实内容与验证配方；全部正式内容在第六阶段核对，不能要求早期任务提前完成全目录。

实施验收仍要求加载器能完整加载更新后的 Demo 示例，且示例通过对应 Schema 校验；一个包里只要有一份定义无效，整个包都无效。不能以加载旧的首切片子集或让临时样例通过 Schema 代替完整 Demo 内容验收。

| 文件 | 内容 |
| --- | --- |
| [demo-catalog.md](demo-catalog.md) | 完整 Demo 要制作的正式内容、测试用内容和美术资产清单 |
| [manifest.md](manifest.md) | 内容包的 `mod.json` |
| [voxel.md](voxel.md) | 体素定义 `<local id>.voxel.json` |
| [blueprint.md](blueprint.md) | 蓝图 `<local id>.blueprint.json`，以及被嵌套的复合体和存档共用的设计对象 |
| [settlement-type.md](settlement-type.md) | 聚居地类型 `<local id>.settlement-type.json` |
| [population-rule.md](population-rule.md) | 人口规则 `<local id>.population-rule.json` |
| [market-rule.md](market-rule.md) | 市场规则 `<local id>.market-rule.json` |
| [construction-rule.md](construction-rule.md) | 施工规则 `<local id>.construction-rule.json` |
| [railway-rule.md](railway-rule.md) | 铁路规则 `<local id>.railway-rule.json` |
| [combat-rule.md](combat-rule.md) | 战斗规则 `<local id>.combat-rule.json` |
| [lair-type.md](lair-type.md) | 生物聚落类型 `<local id>.lair-type.json` |
| [generation-profile.md](generation-profile.md) | 世界生成配置 `<local id>.generation-profile.json` |
| [terrain-type.md](terrain-type.md) | 地貌类型 `<local id>.terrain-type.json` |
| [market-table.md](market-table.md) | 市场表 `<local id>.market-table.json`，首个切片的临时格式 |
| [process.md](process.md) | 流程 `<local id>.process.json`：生产、开采、回收 |
| [settlement-template.md](settlement-template.md) | 聚居地模板 `<local id>.settlement-template.json`，以及从世界生成到聚居地出现在节点上的整条链 |
| [schemas/](schemas/) | JSON Schema（draft 2020-12），一种文件一个，共用的类型在 `common.schema.json` |
| [examples/](examples/) | 示例：内容包 `example-pack`、依赖它的 `example-addon`，以及用户内容库里的一个玩家蓝图 |
| [tools/make_placeholder_pngs.py](tools/make_placeholder_pngs.py) | 按定义生成占位 PNG |
| [tools/validate_examples.py](tools/validate_examples.py) | 用 Schema 校验全部示例，需要 Python 包 `jsonschema` |

这个目录里有 `.gdignore`，Godot 编辑器不会导入这里的图片。

## 基础贯通正式内容

`builtin/core/resources/` 当前提供基础贯通小世界的正式输入，由 [generate_small_world.py](../../scripts/content/generate_small_world.py) 独占生成：三种轨距的小型／中型／超大型机车、小站点和站台设备、农业镇类型与八地块模板、平原与丘陵及桥隧模型、五类规则和 `core:basic-through-world` 生成配置。PNG 的唯一可编辑源也是该脚本，附带确定 UID 的 Keep File `.import`；改图后须重制。

第一阶段风格样稿覆盖 `ground`、`subsoil`、`rock` 和小型机车使用的五种货物体素：`structural-metal`、`bogie`、`propulsion-equipment`、`control-equipment`、`storage-equipment`。面贴图每格 32 px，图标 128×128、透明底；土层图与车辆底面放在 `voxels/shared/` 复用。小型机车采用驾驶室、机器间、两侧检修走廊和端部平台的外形，轮对格位与体素数量沿用原内容。中型、超大型蓝图保持原模板，共用的体素表现会随本批素材更新；其余资产仍为占位。本批样稿不代表完整 Demo 美术完成，画面待用户阶段试玩确认。

这是基础贯通小世界，**不是完整 Demo 默认开局**。配置使用非零噪声和成环线路，起始车队每种轨距各一台机车；数值另起，尚未完成经济配平、全目录制作或正式美术。`lair_ratio=0`，不启用生产计划、自动库存和现车初始化。含生物聚落的预算未测量：生物聚落会占用节点，影响起点检查、初始铁路和选路，需要在内容接入后重测。

在仓库根目录重制和检查：

```powershell
python scripts/content/generate_small_world.py
python scripts/content/generate_small_world.py --check
python scripts/content/generate_small_world.py --output-dir TestResults/content-reproduction
pwsh -NoProfile -File scripts/test.ps1 -Project content -Layer fast,integration
```

`--check` 比较全部文件清单与字节并校验 Schema；目录中多出、缺少或手改任何文件都会失败。生成时也拒绝多余文件，避免覆盖未归脚本所有的内容。修改正式内容应修改生成脚本，再重制；每份 JSON 必须在现有 1 MiB 单文件上限内。新 PNG 的 `.import` 已设置 `importer="keep"`，无头导入后应保持不变。

真实内容加载测试只验证 Content 的解析、引用和设计投影。世界测量及固定种子的节点／线路定位记录见 [#372](https://github.com/odradekk/train/issues/372) 测量评论；三维弯坡、轮轨表现与看图确认归 #298，依赖 PR #431 合并。游戏已不加载 `builtin/legacy-host/`；该目录仅保留资产导出探针与 `ContentPortsTests` 使用的 PNG 夹具。宿主已在 #424 接入真实工厂，导出规则清理归 #377。

## 内容包的结构

```text
<包目录>/
├── mod.json
└── resources/
    ├── voxels/
    │   ├── <local id>.voxel.json
    │   ├── <local id>/            这份定义自己的资产
    │   └── shared/                几份体素定义共用的资产
    ├── blueprints/
    ├── processes/
    ├── settlement-types/
    ├── settlement-templates/
    ├── rules/
    ├── railway-rules/
    ├── combat-rules/
    ├── lair-types/
    ├── generation-profiles/
    ├── terrain-types/
    └── markets/                历史临时市场表
```

- Mod 放在 `user://mods/<包目录>/`。内置的 `core` 包放在 `builtin/core/`，没有 `mod.json`，版本就是游戏版本。
- 加入或移除一份定义，就是加入或删除它的文件和同名文件夹。

## 通用规则

- **一个文件一份定义**：文件名是 `<local id>.<种类>.json`，根对象就是这份定义。文件里的 `id` 必须和文件名一致，不一致就报错。
- **严格 JSON**：未知字段、重复字段、注释和尾随逗号都无效。包里任何一份定义有错，整个包都不加载。
- **`schema_version`**：每种文件各自编号。当前 manifest、聚居地模板、世界生成配置和历史市场表是 2，聚居地类型、体素定义和蓝图是 3，流程、人口/市场/施工/铁路/战斗规则、生物聚落类型与地貌类型是 1。
- **local id**：1–64 个字符，只用小写英文字母、数字、下划线和连字符，以字母或数字开头；不能是 `shared`，它是共用资产文件夹的名字。约定用连字符分隔单词，例如 `diesel-engine`。
- **规则身份**：人口、市场、施工、铁路和战斗五类规则共用一个 `RuleDefinitionId` 空间；同一包内不同规则种类的 local id 也不能重复。
- **引用**：同一个包里的定义直接写 local id；其他包的定义写 `<命名空间>:<local id>`，命名空间是 `core` 或那个包的 UUID，例如 `core:grain`。引用了其他包的定义，就必须在 manifest 里声明依赖那个包。写本包自己的命名空间也可以，等同 local id。
- **资产路径**：
  - 不带斜杠的文件名，指这份定义自己的同名文件夹，例如 `"top.png"`；
  - 以 `shared/` 开头的路径，指同类定义共用的文件夹，例如 `"shared/vehicle-underside.png"`；
  - 其他写法都无效。
  - 文件名只用小写英文字母、数字、下划线和连字符。
  - 资产文件只能是 PNG 和 `.glb`。`.blend` 等源文件不放进包里（[资产访问与导入](https://github.com/odradekk/train/issues/286)）。
- **行为**：类型由游戏代码提供，内容只选类型、填参数。行为以类型为键写成对象：核心参数写在 `simulation.behaviors`，表现参数写在 `appearance.behaviors`。
- **标签**：取值由游戏代码提供，写了未知标签整个包无效。
- **阶段**：字段表的“阶段”列保留原先首个切片、Demo、Demo 之后的划分；当前实施目标统一见 [00](../design/00-product-and-core-loop.md#本阶段范围)。首切片已有且仍有效的字段纳入 Demo，明确废止的临时字段仅保留为历史，不因阶段标签继续实现。标签不是六个开发阶段的任务划分，也不表示当前加载器已支持；实际交付顺序由[工单重组](https://github.com/odradekk/train/issues/294)确定，Demo 之后不自动纳入。

## Schema 和加载器的分工

Schema 只检查单个文件的结构和取值范围。下面这些由加载器检查，各格式说明里列出了它自己的规则：

- `id` 和文件名一致；
- 引用的定义存在，跨包引用的包已声明为依赖；
- 资产文件存在，图片和模型的格式、尺寸符合要求；
- 涉及几个字段的规则，例如碰撞盒不超出所在的格。

## 示例包

- `example-pack` 依赖 `core`，示例引用的定义都写在包里，不依赖尚未制作的正式内置定义。
- `example-addon` 依赖 `example-pack`，用完整标识引用它的体素、蓝图和聚居地类型，演示包依赖和跨包引用。
- `content-library/` 是用户内容库里的一个玩家蓝图。

新增的六份生活货物供人口规则引用，使用已有共享占位贴图；不是完整食品生产链或正式美术。数值只用来演示写法，不是正式设计。

改了示例里的定义后，补齐缺少的占位图：

```powershell
python docs/content/tools/make_placeholder_pngs.py docs/content/examples/example-pack
```

已有的图默认跳过，加 `--force` 才重新生成。

改了 Schema 或示例后，校验一遍：

```powershell
python docs/content/tools/validate_examples.py
```

它只检查单个文件的结构；跨文件的引用和设计合法性由加载器和实施工单的测试检查。
