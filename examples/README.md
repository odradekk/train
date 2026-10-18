# 示例存档与配方

`basic-through` 是游戏内基础示例；另外四个经济示例只供开发与阶段验收，不进入 Windows 导出的 PCK。所有配方都加载已提交的正式 `builtin/core`，通过公开 Core 命令推进真实模拟，不重跑内容生成器或修改存档内部状态。

| 目录 | 场景与检查 | 制作／检查配置 |
| --- | --- | --- |
| `basic-through` | 基础铁路贯通，200 步 | Debug |
| `starting-trade` | 种子 40、两列小型起始列车，分线运输主食、燃料和结构金属，运行 4 游戏小时 | Release |
| `storage-tight` | 列车 1 驶往林业镇 5，依次买入木材加工品和结构金属，各以 2400 为目标；总容量 1260 截断第二笔成交，10000 步 | Debug |
| `commission-branches` | 两列起始列车满载采购后，首笔小型货车委托受理，第二笔真实缺货且不扣款 | Debug |
| `lease-expiry` | 租用 1 点仓储、存入主食，越过一天租期后卸货被拒、取货成功，5,184,001 步 | Release |

这些配方共用种子 40、`core:basic-through-world` 与 `stage1-world-v1`，存档元数据固定。默认车队由一列机车加三辆货车和一列机车加两辆货车、一辆武装车组成；采购后仍可委托小型货车的经济条件已由 #387 的正式配方补验，阶段 #300 复核。

## 基础贯通

`basic-through/recipe.json` 固定生成配置身份、种子、生成器版本、存档元数据、命令位置和目标位置。它使用仓库已提交的 `builtin/core`，执行 `train.replace_run_plan`、`train.start` 和 `train.stop`。命令名和计划结构沿用核心输入格式 2：`plan` 包含 `loop` 与 `stops`，基础例子使用 `{kind:"waypoint",node}`。命令裁决和模拟推进均由真实 Core 工厂执行。

在仓库根目录构建并运行：

```powershell
dotnet build Tools/Cli/Train.Cli.csproj
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll run . examples/basic-through/recipe.json TestResults/run.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll run . examples/basic-through/recipe.json TestResults/repeated.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll compare TestResults/run.json TestResults/repeated.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll replay . examples/basic-through/recipe.json TestResults/replayed.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll make . examples/basic-through/recipe.json TestResults/remade.save.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll check . examples/basic-through/recipe.json examples/basic-through/save.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll save . examples/basic-through/recipe.json TestResults/prefix.save.json 150
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll resume . examples/basic-through/recipe.json TestResults/prefix.save.json TestResults/resumed.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll compare TestResults/run.json TestResults/resumed.json
```

输出目录须已存在，制作存档要求目标文件不存在。`run` 输出模拟位置、权威检查点、`CoreInputId` 及运行身份；`compare` 先核对 `CoreInputId` 和当前构建／运行环境，再由核心比较检查点，失败返回非零退出码并报告首个差异位置与字段。它也支持从连续运行中已有的检查点位置开始比较恢复运行的完整后缀，上例以 150:0 为保存边界。`replay` 使用本次运行的录制及内容视图逐检查点核对，不读写复现包。

工具只加载内置内容。PNG 端口支持当前资产使用的 8 位 RGBA、非交错编码，校验签名、块 CRC、头部、解压长度；无效或不支持的 PNG 返回 `CannotDecode`，GLB 返回 `UnsupportedContent`。这不替代真实 Godot 资产解码或视觉验证。

`basic-through/save.json` 是推进到 200:0 的普通格式 6 存档，当前核心载荷为格式 20。`make` 推进到配方目标后保存，`save` 在指定步的序号 0 边界保存；该位置的命令由 `resume` 执行。保存同时捕获核心状态与内容依赖，UUID、名称和保存时间取配方固定值。容器编解码、临时文件读回校验及发布直接链接宿主同份源码；恢复依次读取容器、解码核心和内容、准备恢复内容、准备核心、激活后继续配方。

Godot 开发构建（编辑器运行和调试导出）的主菜单提供“从示例开始”，发布构建隐藏此入口。游戏从 `res://examples/basic-through/save.json` 读取同一份普通存档，经过正常恢复流程打开暂停的宏观地图，不执行配方。恢复后的对局使用新 UUID，沿用名称“基础贯通小世界”，标记为未保存；第一次手动或自动保存才写入新 UUID 的用户存档目录，原示例保持不变。

发布导出的 `--smoke` 从 PCK 验证示例恢复、新身份、200:0、暂停和未保存状态，并检查菜单入口隐藏；不保存副本或读回。副本保存由宿主测试在临时目录使用真实文件 I/O 验证；调试导出的菜单入口另做人工验证。这些证据不替代 #298 的四模块阶段验收。

`check` 按配方重新运行，并与普通存档恢复后的检查点比较。比较前只显式核对检查点之外的 `CoreInputId`：运行取当前内容视图的身份，恢复取存档捕获的 `InputIdentity`，不同则报“配方或内容已变，需要重新制作”。种子、生成器版本、生成内容修订和配置已包含在存档 `world.source` 中，交给核心检查点比较报告首个差异字段，例如 `/world/source/world_seed`。示例一致性以检查点摘要为准，容器头部或内容载荷的无关变化不要求字节一致；正常恢复只需要 `save.json`。工具保持两个模块载荷不透明。

已提交示例的重新制作比较仅在 Windows 测试，同机跨进程比较在所有平台测试。示例一致性检查使用当前已提交的内置内容，不重跑内容制备脚本，也不宣称跨构建或跨平台确定性。篡改测试只改临时副本，已提交文件保持原样。

## 经济配方与查询

配方 `format_version` 仍为 1，新字段可省略。`checkpoint_steps` 是严格递增、不超过目标步的整数数组，各项表示 `step:0`；它补充保存恢复比较所需的边界。查询与权威检查点在同一位置采样，包括起点、推进到命令的边界、命令执行后、补充边界和终点。`starting-trade` 每 54000 步（15 游戏分钟）列一个检查点，直到 864000 步，包含 216000、432000、648000、864000 四个小时点。这些边界也用于在近期报告保留上限内核对两列车的报告连续性；其他配方不隐式按时间采样。

Station 的形状为 `{kind:"station",node,dwell_steps,goods_rules}`，省略后两项分别表示 0 和空规则。每条规则为 `{action,good,local_inventory,target_quantity}`；Buy 动作为 `{kind:"buy",maximum_unit_price:null}`，Sell 为 `{kind:"sell",minimum_unit_price:null}`，限价也可填非负整数。货物使用 `{kind:"voxel",definition:"core:fuel"}`。数组保留业务顺序，核心按实际需求、库存、钱包和容量裁决。

新增配方命令如下，均带已有的 `at` 位置，名称沿用核心输入格式 2：

| kind | 其他字段 | 使用者 |
| --- | --- | --- |
| `lease.acquire` | `request:{kind:"rent",settlement,capacity,duration_steps}`, `maximum_payment` | 租约示例、短测试 |
| `inventory.unload_goods`／`inventory.load_goods` | `train,settlement,local_inventory,good,quantity` | 租约示例、短测试 |
| `market.buy_vehicle` | `seller,vehicle,maximum_payment` | 仅测试专用短配方 |
| `construction.commission_vehicle_work` | `facility,blueprint,sources,maximum_payment` | 委托成功与材料不足分支 |

CLI 委托目前只支持制造车辆。`facility` 是执行施工的建筑编号，能力固定为 `Construction`；`blueprint` 是内置包中的车辆蓝图身份，由内容模块交出完整设计输入。`sources` 是按消费顺序排列的库存编号数组，必须显式给出，空数组表示整份 BOM 都从当地 NPC 补购。`maximum_payment` 为非负整数。任务优先级为 0；改装和修理的 CLI 配方入口尚未增加，核心改装和修理由模块用例验证。

租期必须是正式规则一天（5,184,000 步）的整数倍；配方不能借缩短租期加速验证。不支持本票未使用的续租、卖车、手动货物买卖或自动装卸。这些是 CLI 配方能力，没有新增游戏控制台命令，单命令手册不适用。

`run`／`replay`／`resume` 的 JSON 另外包含：

- `samples[]`：位置（节点位置包含可空 `entered_from`、`entered_track`、`next_edge`、`next_track`，与 Core 公开位置一致）、钱包、全局未归属委托工钱，全部列车的位置／活动／库存身份／当前运行累计／近期报告，全部聚居地的库存身份、待售车辆与委托任务编号，以及按任务编号查询的状态、工作进度、冻结工作量、投入货物和阻塞，全部租约的到期状态，以及这些对象引用的库存容量、已用、可用、权限和全部货物。数据直接来自公开查询；报告与当前累计保留 Core 给出的收付款、净现金流、运量、载运率和未完成规则，不重算。近期报告有核心规则规定的保留上限。
- `command_receipts[]`：配方中的零基 `command_index`、`kind`、`decision`（accepted／rejected）、`problems[{code,field,related_identities}]`、`quoted_total`。现车购买使用执行前的公开 `QuoteVehicle.Total`，车辆委托使用 `GetJobQuote.TotalPayment`；启动条件不足时为空，单纯资金不足时仍有已计算总额。其他命令为 null；报价不预留交易条件。
- `samples[].settlements[]` 另含公开查询中的 `market_goods`（库存、补货目标、留用、可售／可收量及阻塞）、`facilities`（流程、状态、实际工作速率、限制、输出缓冲、潜在消耗及阻塞）、`residents` 和 `population_needs`。潜在消耗不是实际消耗。
- `goods_transfers[]`、`production_completions[]`、`production_state_changes[]` 保留命令提交和推进返回的三类公开事件，含实际模拟位置和事件序号。成交记录含列车、聚居地、货物、数量及实际金额；生产完成量包含仍在输出缓冲中的货物。
- `samples[].unassigned_service_payments` 是全局未归属委托工钱，`samples[].commission_jobs[]` 是委托任务详情，`samples[].settlements[].commission_jobs[]` 是当地任务编号。所有问题项（含 `blockers`、`reasons` 和 `incomplete_reasons`）均保留 `code`、`field`、`related_identities`。

需要按小时调查起步贸易时，只运行一次 Release `run`，再用 Python 3 标准库脚本汇总：

```powershell
dotnet build Tools/Cli/Train.Cli.csproj -c Release
New-Item -ItemType Directory -Force TestResults/trade-diagnostics | Out-Null
dotnet Tools/Cli/bin/Release/net10.0/Train.Cli.dll run . examples/starting-trade/recipe.json TestResults/trade-diagnostics/run.json
python -X utf8 Tools/Cli/summarize_starting_trade.py TestResults/trade-diagnostics/run.json TestResults/trade-diagnostics/summary
```

脚本只接受完整四小时 `starting-trade` 输出，写出逐笔成交和小时列车、市场、设施、人口需求及设施状态变化 CSV。按实际事件位置划分小时，以成交金额除以数量计算实际均价，无成交时标“无成交”，不计算定价公式。实际消费由库存、完成产量、输出缓冲和转运收支守恒推导，包含居民、批次投入及燃料的合计，不拆分用途。脚本逐小时核对成交净额与钱包变化，并跨采样去重运行报告、检查报告连续性，将每列车的报告收付款与成交累计核对；失败返回非零。该入口不适用于带租金或现车买卖的配方。

现金流按同一运行中钱包余额的差计算，不统计成交笔数。起步例子只使用两列初始列车和货物买卖，不卖车、不租仓、不委托。两列车分别在 0、1800 步启动，每站停靠 300 步，按下表循环。买入目标表示车上该货物的目标存量，已有货物只补到目标；卖出目标均为 0，实际成交受目的地可收量限制。

| 列车 | 路线与货物 | 买入目标 |
| --- | --- | --- |
| 1 | 农业镇 14 买主食，依次在城市 2、林业镇 5 和 8 卖出，再回 14 | 主食 40 |
| 2 | 14 买主食，13、12 卖主食；12 买燃料，13 卖燃料并买结构金属；2 卖结构金属和燃料，5、8、14 卖燃料 | 主食 20、燃料 10、结构金属 100 |

配方保留原有货物买入目标，均不超过列车 1 的 1260 和列车 2 的 1080 容量。农业镇的主食和油田的燃料持续有余量，矿镇的结构金属供应城市生产；买方开局已经备货，后续人口与生产消耗逐步形成可收需求。车辆车间的模板初始结构金属也满足城市生产，减少列车 2 可卖出的数量，配方不假定每圈都能卖完或每列车都盈利。本轮四小时库存和逐列现金流的一次性复核记在 #340，数值仍待阶段试玩确认。核对末尾钱包、各小时余额差，以及每列车已结束报告和末尾当前运行的收付款、运量及剩余货物；跨采样重复出现的报告按列车及结束位置去重，并检查报告时间连续性，不能把有限的近期报告直接当作全部累计。

容量例子在 8000 步记录进站过程的查询，10000 步核对列车 1 两种货物的总量等于容量、`available == 0`，第二条规则为 `insufficient_capacity / Train.Inventory.Capacity`，且卖方结构金属仍有剩余、钱包余额为正。结构金属来自车辆车间的模板一次性初始库存；两种货物合计可售量超过车队容量，使第二笔成交由容量截断。租约例子在到期前关闭买入规则，停靠等待；到期后的卸货回执为 `access_denied / LocalInventory`，随后取货回执为 accepted，货物与权限由同位置查询核对。

## 委托成功与缺货分支

`commission-branches/recipe.json` 使用种子 40 和两列正式起始列车。列车 1 前往矿镇 13 采购结构金属，列车 2 在农业镇 14 采购主食后前往油田 12 采购燃料；各地点只采购一次，不循环，也不消耗农业镇用于委托的结构金属库存。采购在第 7841 步完成，提交委托前两列车均满载。

| 列车 | 采购地点与货物 | 实际数量 | 实际货款 | 最终载货量／容量 |
| --- | --- | --- | --- | --- |
| 1 | 矿镇 13，结构金属 | 1260 | 17,640 | 1260／1260 |
| 2 | 农业镇 14，主食 | 395 | 3,704 | 两笔合计 1080／1080 |
| 2 | 油田 12，燃料 | 685 | 2,740 | 同上 |

列车 2 两笔货款合计 6,444；全部采购共支付 24,084，钱包由 813,500 减至 789,416。

在 `10000:0`，向农业镇（节点 14、聚居地 6、施工建筑 37）提交 `core:small-freight-wagon` 制造委托。材料来源为空，完整 BOM 从当地 NPC 补购，总付款上限为 Int64 最大值。公开报价为 43,734，由材料款 20,726 和工钱 23,008 组成。首单受理后，在 `10000:1` 查询到钱包余额 745,682、全局未归属服务支出 23,008，委托任务 1 为 `Working`，工作进度为 0、冻结总工作量为 11,504，投入货物为 16 份转向架、2 份仓储设备和 1,420 份结构金属。这补验了 #387 验收条件 4：正式起始车队满载采购后仍可受理指定小型货车委托；阶段 #300 继续复核。

随后在 `10000:1` 再提交相同委托，因当地材料不足而以 `insufficient_goods / Sources` 拒绝。`related_identities` 按顺序列出 `voxel:core:bogie`、`voxel:core:structural-metal`；#348 新增的建设设备包含额外仓储设备，第二单不再缺这一项。拒绝后的 `10000:2` 与 `10000:1` 全部查询样本仅位置不同：钱包、全部库存、委托任务和工钱保持不变，确认拒绝没有部分扣款或消费。

日常用例严格重放真实配方，已提交存档由统一示例检查核对并确认源文件保持不变；通用委托保存恢复另由 Core 跨进程用例覆盖。“前面材料可用、最后一项不足仍完全不扣”的场景另由 Core 受控用例覆盖。示例使用正式内容，不注入货物；成功受理不等于任务已经完工，也不替代 #300 阶段联调。

## 自有建设的内容准备

本节是正式内容的准备步骤，不是已执行成功的建城配方或存档。建城任务待 #342、建筑建造待 #343 用真实命令验证；断轨分支仍归 #344，完整路径归 #301 联调。

### 运输与建筑材料

建城只从一列列车扣料：小站点需要 1696 件建筑材料和 2 个站台设备，基础工棚需要 412 件建筑材料和 1 个基础吊机，合计 2111 件。开局两列车分别只有 1260、1080 容量，不能单独承运。可在同一非敌对聚居地、停车且货舱为空时先解编两列车，再用 `FormTrain` 将原有 8 辆车编成一列（两台机车、五辆货车、一辆武装车）。无需买车或委托造车，合编容量为 2340，装齐建城材料后尚余 229。

当前规则下该编组具有控制部件，总牵引力 1440 kN；空车质量约 316.152 吨，建城货物约 97.096 吨，装齐后的加速度约 3.485 m/s²，高于 0.15 的出发下限。编组总长 208 米，小站点的站台段长 96 米；当前核心按中心位置运行、按站台数量预约，没有列车长度不得超过站台段的准入限制。已用 `33d06b17` 的核心规则和本票正式内容，在种子 40 新局中核对解编、合编、矿镇停靠，以及完整 BOM 的实际购买和满载运行；这不表示建城命令已通过。

默认模板中只有矿业镇的建材厂启用建筑材料生产，种子 40 的矿镇在节点 13。建材厂每批消耗 4 件石料、产出 4 件建筑材料，30 work、1 work/s，名义产率为 480 件/游戏小时；开局按两小时潜在产量预存 960 件。买够建城所需的 2108 件，还需累积 `2108－960＝1148` 件，即 287 批，至少 `287×30＝8610` 秒（2:23:30）的满速工作，另有批次启动和离散步开销；这不是无条件等待时限。三条默认生产线最初合计耗电 4，矿镇燃料不会自动补货，必须从油田运输燃料，保持采石、建材生产和可用仓储。导电金属、聚合物等 #586 上游缺口不直接参与石料到建筑材料这条链。

种子 40 的实测路线先从油田购买 32 件燃料，再到矿镇自动卖出。保持默认 NPC 计划、不注入货物，到开局后 2:23:48 的采样时刻，矿镇已累积 2108 件建材并余 26 件燃料；列车到站后实际等待 2:20:50。随后买齐建城 BOM，货舱占用 2111/2340，钱包仍有 796518。满载列车沿矿镇 13 → 节点 12 → 空节点 11（边 15、14）行驶 91 秒，携全部材料进入目标节点。这些时刻包含行车和生产，不能当作所有种子的固定等候时间。空节点须从已有铁路可达的节点中选取，地图上的任意空节点未必通车；在空节点停车后提交建城命令的部分仍待 #342 验证。

### 一次性设备与路径

#340 的模板一次性车辆材料继续保留；#348 给六份 NPC 模板各加一套建设设备。设备用完不补货，持续供货待 #586 决定后接入。两条路径共用建城套件和小仓库，设备数量逐货物取较大需求，不把两条完整路径相加：

| 设备 | 正式蓝图 BOM 来源 | 每套数量 |
| --- | --- | ---: |
| 站台设备 | 小站点 | 2 |
| 基础吊机 | 基础工棚 | 1 |
| 仓储设备 | 小仓库 | 80 |
| 发电设备 | 发电站，仅开采路径需要 | 1 |
| 机床 | 农场 | 1 |
| 钻头 | 开采场 | 1 |

模板按货物合并库存，仓储设备为原车辆材料 2 件加建设设备 80 件，共 82 件；只购买建设所需的 80 件，仍给原委托保留 2 件。设备的 NPC 补货目标为 0，按价格下限加买入价差出售已有货物；这不提供免费物资或持续补货。

平原／丘陵选择「建城 → 小仓库 → 农场 → 启用谷物种植 → 装货运输」。谷物种植不耗电，不需要先建发电站。森林、荒漠、山地选择「建城 → 小仓库 → 发电站 → 开采场 → 启用当地资源允许的开采流程 → 装货运输」，需另行运入燃料。每条路径的建筑均不超过基础工棚的中地块施工上限。

建城任务只消耗起始 BOM、按固定工期完成；之后内置施工不耗电，只占劳动力。小仓库还需 412 件建筑材料和 80 件仓储设备，不能把全部后续建设物资挤进首趟剩余的 229 容量，应在建城后分趟运送。新城库存为空、人口立即消费；尽快建设仓库并转入主食、衣物等生活物资，谷物产出本身不是居民口粮，也没有免费口粮或免消费期。后续工地表现、材料扣除、生产和装卸的完整衔接仍待 #342／#343 与 #301 验证。

## 重制与长验证

当前核心状态格式为 20：#332 的车辆改装、拆解使用格式 16，#342 的冻结建城任务使用格式 17，#541 的节点进出铁轨使用格式 18，#343 将施工目标改为实例并增加地块后使用格式 19，#344 增加铁路扩建与修复任务及按边自动修复来源后使用格式 20，命令输入和重放外壳仍为格式 2；格式 1–19 的示例存档和重放起点须重新制作，不能手工修改生成载荷。断轨修复的公开配方待 #622 提供设生产计划后再补，本目录暂不收录。

同一份存档使用同一种构建配置制作和检查。日常测试只运行短经济命令、现车、容量和委托例子。本次车辆内容迭代中，`starting-trade` 只做 `make` 和一次正常读取；`lease-expiry` 通过 Linux runner 制作并执行长验证。长例不能放进日常集成层。

[长示例脚本](../scripts/checks/run-long-example.ps1)构建一次 Release，然后并行执行三组：A 连续运行并严格重放；B 保存退出后由独立进程恢复；C 制作普通存档再从零按配方检查。保存边界为起步贸易 **432000**、租约到期 **5183999**。`replay` 的输出作为连续基准，最后比较恢复后的完整检查点后缀，不额外运行一次 `run`。脚本创建独立运行目录，每条命令完成即保留退出码、耗时和日志，任何失败或未执行均非零退出。runner 触发与下载方法见[远端检查](../docs/run-tests.md#远端检查)。

`basic-through`、`storage-tight`、`commission-branches` 和 `starting-trade` 的存档在 Windows 制作；`lease-expiry` 在 Linux runner 制作。`check` 在制作平台使用同种构建配置比较；Linux 重制不能验证 Windows 存档的一致性，反之亦然。基础例子的命令形式见[基础贯通](#基础贯通)，长示例使用表中 Release 配置。同机跨进程比较、保存恢复和本次录制的严格重放在两种平台都有效，不宣称跨平台确定性。

容量例子的普通存档用 Debug 制作，Windows 日常用例通过 `check` 对照已提交的 `storage-tight/save.json`，同时确认源文件保持不变。验收证据仍以具体构建、命令、实际耗时和日志为准；这些 CLI 检查不提供 Godot 界面、目标窗口交互或阶段 #299 四模块验收证据。
