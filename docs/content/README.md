# 内容定义标准

这里是内容包和各种内容定义的格式标准：字段说明、JSON Schema 和一个示例内容包。规则的来由见设计文档 [02](../design/02-definitions-and-content.md)、[03](../design/03-vehicles-buildings-and-designs.md) 和 ADR-0011 到 ADR-0015；字段怎样写，以这里为准。

`ContentFactory` 的 Initial 加载已支持 manifest v2、voxel v3、车辆／建筑／组装体／功能复合体／场景模型蓝图 v3、流程 v1（生产、开采、回收）、地貌 v1、聚居地类型 v3、聚居地模板 v2、五类规则和生成配置 v2（含 NPC 初始化参数），并使用宿主文件与资产检查端口提供的固定字节和事实。工厂提供新局输入、流程核心投影与显示查询、铁路表现参数及按最终所有者固定的模板副本和布局查询；租赁参数归市场规则。模板的 `founding`、`inventory`、`production_plans` 已支持解析、引用与约束校验及核心投影；特殊库存支持 voxel、功能复合体和组装体货物。NPC 初始化执行仍由核心负责，生物聚落与生物仍待后续任务，完整示例包尚不能加载。当前范围的捕获、恢复、重载和复现已接入，详见[内容契约](../contracts/content.md)；游戏宿主已通过 `App/Composition/` 接入真实工厂，旧内容加载路径在 [#452](https://github.com/odradekk/train/issues/452) 退役。当前目标是完整 Demo；聚居地类型版本 3 已移除临时供需、恢复量和站台数，保留的临时市场表及其 Schema、样例仅是历史记录。实施按[工单重组](https://github.com/odradekk/train/issues/294)获准的六阶段方案推进，各阶段交付当期所需的真实内容与验证配方；全部正式内容在第六阶段核对，不能要求早期任务提前完成全目录。

当前 builtin 提供五类聚居地类型、模板及场景模型，每份模板至少八个地块和两个站点，并包含所需建筑与吊车。地貌包含 plains、hills、forest、desert、mountains，生成阈值为示例验证临时值；五种地貌分别绑定草地、干草地、林地、沙和碎石，荒漠下层为沙、岩层为砂岩，其他地貌复用土与灰岩。内置正式货物与流程、资源生成参数和模板生产计划由 #379 交付；模板特殊库存包含 #340 的一次性小型车辆材料和 #348 的一次性建设设备，NPC 初始化由 #528 的核心生成器执行，内置配置已启用，动态供应链验收归 #299；#390 已提供大型设备与规模样机的真实内置输入，设备制造入库仍待 #343 与 #300 验证。这些内容定义不表示四模块联调已通过。

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

## 经济运输正式内容

`builtin/core/resources/` 当前提供经济运输切片的正式输入：覆盖三种轨距、五档尺寸的 11 份车辆蓝图、小站点和站台设备、五类聚居地与六份模板、五种地貌及桥隧模型、五类规则和 `core:basic-through-world` 生成配置。除下述手绘图标外，JSON 和 PNG 由 [generate_small_world.py](../../scripts/content/generate_small_world.py) 生成，该脚本是生成贴图的唯一可编辑源。所有 PNG 的 Keep File `.import` 由脚本生成，UID 由路径确定。

可涂体素的默认色也只在该脚本维护：面图使用中性纹样，内置蓝图的调色板补默认 `paint`，已有涂装保持。Scenes 直接放置的可涂原料另生成到 `Scenes/BuiltInResourcePaints.g.cs`，当前只有原油资源堆；`--check` 同时核对该文件。指定 `--output-dir` 时，这份 C# 文件写入指定目录，不改工作树的 Scenes 源码。图标保留原色，面图的 Alpha 按[美术 03](../art/03-color-and-paint.md#玩家涂装)表示可涂权重。

自然与铁路素材由同一脚本生成：13 种基础场景体素（包含路灯和道床碎石，不含站台边缘）、三种树、两种岩石、1–3 轨桥隧和两类工地。工地另复用三种专用场景体素 `site-fence`、`road-barrier`、`rail-stack`；轨枕堆和灯柱复用混凝土。场景体素不新增物品图标，必填的 `display.icon` 复用 128 px 面图或原有共用图标。面图保持 32 px/格，土层图放在 `voxels/shared/` 复用。道床由铁路规则的必填 `appearance.ballast_voxel` 指定，不随地貌岩层变化。

森林装饰密度为每平方米 0.002 株，三种树权重为 2/2/1；丘陵与山地每平方米 0.0007 块岩石，两种岩石权重为 3/1。现有摆放不按坡度筛选。工地尚未接入施工或断轨状态，灯光效果、三轨桥隧的真实流程留给后续 #345／#344／#305 验证；六份聚居地底景归 #361。画面由用户在阶段试玩中确认。

小型机车及其四种货物体素 `bogie`、`propulsion-equipment`、`control-equipment`、`storage-equipment` 继续使用第一阶段风格样稿。图标 128×128、透明底，车辆底面放在 `voxels/shared/` 复用。小型机车保留驾驶室、机器间、两侧检修走廊和端部平台，移除为维持旧价值填入的内部结构金属。其余车型按机车、货车和武装车角色生成；货车的载量、武装车的静态武器都来自实际部件，不代表战斗已实现。#390 已新增大型柴油机 `large-diesel-engine` 和重炮塔 `heavy-turret` 两种功能复合体，并在原 id 下更新大型机车 `large-locomotive`、泰坦武装重型车辆 `titan-armed-vehicle` 和重型车辆工厂 `heavy-vehicle-factory` 三份样机及龙门吊 `gantry-crane`。泰坦展开约 25,000 格，含两台大型柴油机、四座重炮塔和不少于 60 个部件；货舱保持六个，总容量 1,080，其余部件以静态机枪补足。炮塔的 `rotation` 只声明表现转动，战斗行为待后续验证；外观修整归 #391，设备制造入库归 #343，并由 #300 验证完整流程。默认开局和既有蓝图 id 不变。#359 已补齐十二种原料，#393 已补齐十六种工业与建造货物的正式美术；结构金属沿用中性面图与跨度 4，替换为手绘图标；其余资产仍待对应美术任务，不代表完整 Demo 美术已完成。

这是经济运输验证世界，**不是完整 Demo 默认开局**。配置使用非零噪声和成环线路，起始车队按清单改为两列小型列车：A 为机车加 3 辆货车，B 为机车、2 辆货车和 1 辆武装车。起始资金按两列车的载量与小型货车组成价值估算；采购后是否足够委托建造由 #387 用核心报价补验。正式经济目录包含 46 种货物和 63 个流程（38 生产、8 开采、17 回收），模板包含真实生产计划。检查器直接读取正式 JSON，分别检查生产与回收关系、人口消费品的静态可达性、两条燃料路线的整批原料需求与余量，以及真实蓝图设备支持的满速供电和劳动估算；燃料批次的产出能量须高于自身及直接上游开采的耗电，炼油联产品不分摊单品成本。模板报告给出本地吞吐和燃料能量收支，以及跨模板进口和未被启用计划供应的中间品，静态全包可达不表示 NPC 能动态闭合运行。正式美术仍未完成。`lair_ratio=0`；NPC 按核心补货目标初始化输入原料和生活物资，预存 2 小时潜在产出，并按车间档位和市场名额生成 1–2 辆现车，容量不足时由核心按比例补入。这不表示动态供应链或真实起步贸易已验收。含生物聚落的预算未测量：生物聚落会占用节点，影响起点检查、初始铁路和选路，需要在内容接入后重测。

生活需要初值按最大人口消耗基准农场四分之一产量推导，当前每种需要为每人每小时 1/6 件；基本、生产、改善备货分别为 4、2、1 小时。仓库蓝图的规模预算只在生成器中计算，是建筑规模估算，不是核心补货目标。`small-warehouse` 当前含 80 个容量 180 的货舱，总容量 14,400；玩家也能建造，NPC 按 50% 比例最多可出租 7,200，实际出租仍受空位限制。它比原蓝图多用 79 件货舱，改变仓储、建造成本和租赁玩法，均待用户试玩确认。`large-warehouse` 的件数是该推导结果的 2 倍，当前 160 个货舱、总容量 28,800，是待试玩确认的初值。`large-station` 有 4 个站台部件。两份建筑的仓储设备和站台设备只来自一次性备货，持续供货待 #586；现有模板没有 `large_station` 地块。车辆货舱沿用同一设备定义，起始资金按实际车队载量估算。

按 [#348 用户决定](https://github.com/odradekk/train/issues/348#issuecomment-6246841664)，内置施工规则 `basic-construction.power_per_work` 为 0，施工只占劳动力，包含 NPC 委托造车；`labor_per_work` 仍为 0.25。生产流程的耗电不变。六份 NPC 模板各一次性备一套建设设备，数量从最终蓝图 BOM 推导：站台设备 2、基础吊机 1、仓储设备 80、发电设备 1、机床 1、钻头 1；覆盖建城后建小仓库，再走农场或发电站加开采场的路径。与 #340 的小型车辆材料按货物相加，每种货物只保留一条；仓储设备共 82 件，只买建设所需的 80 件时还剩 2 件，够原委托用；NPC 不为委托预留。这些设备用完不补货，持续供货待 #586 上游货源决定后接入。新城仍只建小站点和基础工棚，库存为空；运输、建材来源和待联调部分见[建设路径](../../examples/README.md#自有建设的内容准备)。

地貌分布以平原为主，丘陵、森林次之，荒漠、山地较少；阈值和通行数值都是待试玩确认的初值，固定种子的实际占比记录在 #545 验收评论中。平原的绕行系数为 1.12，森林、荒漠为 1.25，丘陵为 1.40，山地为 1.65；前四种地貌允许最多 2 轨，山地最多 1 轨，全局上限仍为 2。用逐级增加的绕行长度区分通行难度，山地再限制扩轨容量；不增加地貌对建设、速度或成本的额外规则。

海拔噪声范围为 [0,18]，湿度为 [0,1]。平原覆盖全部海拔，湿度 [0.2,0.75]、起伏不超过 1.4 米，优先留下中等湿度的平缓地区；森林保留湿度不低于 0.65 的其余地区，荒漠限于湿度不高于 0.2 的极干地区。其余海拔不低于 11 的点归山地，剩余点归丘陵。相比旧阈值，平原不再因海拔达到 10 就消失，山地也不会覆盖所有高湿森林。森林、荒漠、山地的起伏上限分别取 18、19、20 米，覆盖当前噪声的全部可能起伏，并以不同上限确定交集处的选择顺序；丘陵覆盖完整海拔、湿度范围，起伏上限取最大有限 double，兜底所有合法起伏。五种上限互不相同，闭区间边界不会产生并列匹配。

核心沿整条边每 60 米采样（含端点），绕行系数取所有采样点的最大值，铁轨上限取最小值。因此只要任一采样点是山地，整条边就按 1.65 倍直线距离、最多 1 轨生成，即使该边的主要地貌显示为平原；节点山地占比不能代替单轨边占比。调整时同时统计各种地貌的节点数、边数、单轨边数和各绕行档位的边数。

在仓库根目录重制和检查：

```powershell
python scripts/content/generate_small_world.py
python scripts/content/generate_small_world.py --check
python scripts/content/check_economy.py
python scripts/content/generate_small_world.py --output-dir TestResults/content-reproduction
pwsh -NoProfile -File scripts/test.ps1 -Project content -Layer fast,integration
```

`--check` 核对全部文件清单、比较生成文件字节、校验 Schema，并调用独立经济检查器；缺少文件、生成文件字节不同或出现未登记文件均失败。生成模式也拒绝未登记文件。修改生成内容应改脚本后重制；每份 JSON 必须在现有 1 MiB 单文件上限内。PNG 的 `.import` 为 `importer="keep"`，无头导入后应保持不变。使用空的 `--output-dir` 重制时，需另行提供手绘清单中的源 PNG 才能通过该目录的 `--check`；生成器不会复制它们。

### 手绘货物图标

手绘清单是生成器 `GOODS_ART` 推出的 `HAND_DRAWN_ICONS`，列出 `builtin/core/resources/voxels/<id>/icon.png`。这些图由图像模型生成，整理后的最终 PNG 本身就是唯一源，不在 `art-source/` 留副本，也不提交整理脚本。后续直接编辑内容目录内的 PNG，在运行中的 Debug 构建中用 `reload` 或 Ctrl+Shift+R 检查。贴图仍修改生成脚本后重制，不能用图标代替面贴图。

生成器保留手绘 PNG，`--check` 在缺图时失败；对应的 Keep File `.png.import` 仍由生成器生成。新增 #393、#394 图标时在 `GOODS_ART` 登记，沿用下述提示词、整理步骤和同用途规则。

创作模式维护的文件在生成器 `HAND_DRAWN_ICONS` 旁的 `AUTHORED_FILES` 登记（初始为空，由 #391 填写）。每项与 `HAND_DRAWN_ICONS` 一样，是相对 `resources/` 的精确文件路径，可登记 JSON、PNG、GLB 和 `.import`，例如 `voxels/stone.voxel.json`；关联文件须逐一登记，不按目录或扩展名放行。登记后以创作模式写入的磁盘文件为准，生成器不生成、不覆盖这些文件，生成规则变化也不会更新它们。`--check` 检查登记文件存在，JSON 使用实际磁盘字节做 Schema 校验，跳过生成字节比较；未登记的多余文件仍失败。向空的 `--output-dir` 重制时也需自行提供全部登记文件。

共用提示词模板：

> 为《Train》绘制单个「{货物名称}」物品图标。近未来工业题材的克制手绘 2D 插画，四分之三视角，光从左上方来，深色外轮廓。构图遵循「{组别统一画法}」，物品为「{本项物品形状与包装}」，固有色与块形为「{本项特征}」。只画物品，居中，四周留边，透明背景；无文字、数量、外框、底板、水印或场景。缩小后仍以大形和固有色辨识。

同用途资产沿用同一种构图：六种固体矿物与化石原料均为居中的低矮货堆，主堆与前方散落物的位置一致，用固有色、块形和粒度区分；硅砂为平滑砂堆。原油用储油桶；农畜产品用常见包装与内容物；原木成捆，纤维压成方包。工业批次的金属锭坯统一低矮堆叠；燃料、化工原料和原油统一桶形、视角与占比；三种部件统一视角、占比和紧凑摆放，面图使用工业外壳；结构块、混凝土块和铁轨统一低矮堆叠或成捆构图。锯材区别原木，卷布区别纤维包，连续建材区别预制混凝土块。燃料与化工原料的桶盖、箍筋和桶身均为可涂中性纹样，只有一条／两条识别带保色。连续建材跨度 4，不画包装；预制块跨度 1，保留块缝。顶面的锯材与卷布画竖放端面、钢轨画对称截面，避免单向平铺。后续批次先确定各用途的统一画法，再套用逐项特征。

模型输出只整理一次：检查透明度，裁去透明空边并清除背景残留；保留透明度，使用预乘透明度重采样，等比缩放到最大边约 100 px；居中放入透明的 128×128 画布，保存为最终 `icon.png`。检查浅底、深底上的白边与残色。再放进美术 07 的外框（`#e3e7ea`→`#c2c9d0` 渐变底、`#6b7580` 边框、物品区域占边长的 78%），以 32、48、64 px 检查辨识，并在真实游戏 UI 查看。实际模型、逐项提示词、整理步骤和截图记录在工单完成评论。

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
- **`schema_version`**：每种文件各自编号。当前 manifest、聚居地模板、世界生成配置、铁路规则、市场规则和历史市场表是 2，聚居地类型、体素定义和蓝图是 3，流程、人口/施工/战斗规则、生物聚落类型与地貌类型是 1。
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
- #501 的 `construction` 必填 `max_plot_size`（仅 `small`、`medium`、`large`、`giant`），`max_size_class` 可选，缺省不能造车；体素和蓝图 Schema 仍为版本 3，旧施工行为缺少新字段时严格拒绝。当时普通内容载荷与复现内容载荷均升为版本 4；#502 因市场规则新增必填字段将复现内容载荷升为 5，普通内容载荷保持 4，详见[内容契约](../contracts/content.md#保存与严格复现)。二者独立于外部定义 Schema。基础吊机体素及车间吊机、龙门吊功能复合体的格式样例见示例包；`builtin/core` 已提供引用这些部件的基础工棚、车辆车间与重型车辆工厂；#390 已交付正式龙门吊、大型柴油机、重炮塔和三份规模样机的内容输入，设备制造入库仍待 #343 与 #300 验证。
- 建筑与地块的六档适配、包络以及模板地块的重叠和场景范围，见[地块与建筑](../design/16-settlements.md#地块与建筑)和[模板加载规则](settlement-template.md#加载器检查的规则)。蓝图 Schema 保持版本 3，模板保持版本 2；旧 `station` 地块取值被未知取值诊断拒绝。

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
