# 示例存档与配方

`basic-through` 是游戏内基础示例；另外三个经济示例只供开发与阶段验收，不进入 Windows 导出的 PCK。所有配方都加载已提交的正式 `builtin/core`，通过公开 Core 命令推进真实模拟，不重跑内容生成器或修改存档内部状态。

| 目录 | 场景与检查 | 制作／检查配置 |
| --- | --- | --- |
| `basic-through` | 基础铁路贯通，200 步 | Debug |
| `starting-trade` | 种子 40、两列小型起始列车，分线运输主食、燃料和结构金属，运行 4 游戏小时 | Release |
| `storage-tight` | 列车 1 驶往林业镇 5，依次买入木材加工品和原木，各以 2400 为目标；总容量 1260 截断第二笔成交，4000 步 | Debug |
| `lease-expiry` | 租用 1 点仓储、存入主食，越过一天租期后卸货被拒、取货成功，5,184,001 步 | Release |

四个配方共用种子 40、`core:basic-through-world` 与 `stage1-world-v1`，存档元数据固定。默认车队由一列机车加三辆货车和一列机车加两辆货车、一辆武装车组成；采购后仍可委托小型货车的经济条件留待 #387 补验。

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

`basic-through/save.json` 是推进到 200:0 的普通格式 6 存档。`make` 推进到配方目标后保存，`save` 在指定步的序号 0 边界保存；该位置的命令由 `resume` 执行。保存同时捕获核心状态与内容依赖，UUID、名称和保存时间取配方固定值。容器编解码、临时文件读回校验及发布直接链接宿主同份源码；恢复依次读取容器、解码核心和内容、准备恢复内容、准备核心、激活后继续配方。

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

租期必须是正式规则一天（5,184,000 步）的整数倍；配方不能借缩短租期加速验证。不支持本票未使用的续租、卖车、手动货物买卖或自动装卸。这些是 CLI 配方能力，没有新增游戏控制台命令，单命令手册不适用。

`run`／`replay`／`resume` 的 JSON 另外包含：

- `samples[]`：位置、钱包，全部列车的位置／活动／库存身份／当前运行累计／近期报告，全部聚居地的库存身份与待售车辆，全部租约的到期状态，以及这些对象引用的库存容量、已用、可用、权限和全部货物。数据直接来自公开查询；报告与当前累计保留 Core 给出的收付款、净现金流、运量、载运率和未完成规则，不重算。近期报告有核心规则规定的保留上限。
- `command_receipts[]`：配方中的零基 `command_index`、`kind`、`decision`（accepted／rejected）、`problems[{code,field}]`、`quoted_total`。只有现车购买提供执行前的公开报价总额，其他命令为 null；报价不预留交易条件。

现金流按同一运行中钱包余额的差计算，不统计成交笔数。起步例子只使用两列初始列车和货物买卖，不卖车、不租仓、不委托。两列车分别在 0、1800 步启动，每站停靠 300 步，按下表循环。买入目标表示车上该货物的目标存量，已有货物只补到目标；卖出目标均为 0，实际成交受目的地可收量限制。

| 列车 | 路线与货物 | 买入目标 |
| --- | --- | --- |
| 1 | 农业镇 14 买主食，依次在城市 2、林业镇 5 和 8 卖出，再回 14 | 主食 40 |
| 2 | 14 买主食，13、12 卖主食；12 买燃料，13 卖燃料并买结构金属；2 卖结构金属和燃料，5、8、14 卖燃料 | 主食 20、燃料 10、结构金属 100 |

配方保留原有货物买入目标，均不超过列车 1 的 1260 和列车 2 的 1080 容量。农业镇的主食和油田的燃料持续有余量，矿镇的结构金属供应城市生产；买方开局已经备货，后续人口与生产消耗逐步形成可收需求。配方不假定所有货物每圈都能卖完。本次车辆内容迭代只重新制作并正常加载长示例，四小时现金流复核留给阶段验收。届时核对末尾钱包是否高于起始余额、各小时余额差，以及每列车已结束报告和末尾当前运行的收付款、运量及剩余货物。跨采样重复出现的报告按列车及结束位置去重，并检查报告时间连续性，不能把有限的近期报告直接当作全部累计。

容量例子在 3600 步记录查询，4000 步核对列车 1 两种货物的总量等于容量、`available == 0`，第二条规则为 `insufficient_capacity / Train.Inventory.Capacity`，且卖方原木仍有剩余、钱包余额为正。两种货物合计可售量超过车队容量，使第二笔成交由容量截断。租约例子在到期前关闭买入规则，停靠等待；到期后的卸货回执为 `access_denied / LocalInventory`，随后取货回执为 accepted，货物与权限由同位置查询核对。

## 重制与长验证

同一份存档使用同一种构建配置制作和检查。日常测试只运行短经济命令、现车和容量例子。本次车辆内容迭代的长示例只做 `make` 和一次正常加载；起步四小时与租约一天的完整验证留给阶段验收，不能放进 450 秒的集成层。

[长示例脚本](../scripts/checks/run-long-example.ps1)构建一次 Release，然后并行执行三组：A 连续运行并严格重放；B 保存退出后由独立进程恢复；C 制作普通存档再从零按配方检查。保存边界为起步贸易 **432000**、租约到期 **5183999**。`replay` 的输出作为连续基准，最后比较恢复后的完整检查点后缀，不额外运行一次 `run`。脚本创建独立运行目录，每条命令完成即保留退出码、耗时和日志，任何失败或未执行均非零退出。runner 触发与下载方法见[远端检查](../docs/run-tests.md#远端检查)。

Linux 上的 `check` 只比较本次 Linux 运行重新制作的存档，不能验证已提交 Windows 存档的重制一致性。对已提交 `save.json` 的 `check` 继续在 Windows 使用对应构建配置执行；基础例子的命令形式见[基础贯通](#基础贯通)，长示例使用表中 Release 配置。同机跨进程比较、保存恢复和本次录制的严格重放在两种平台都有效，不宣称跨平台确定性。

容量例子的普通存档用 Debug 制作，Windows 日常用例通过 `check` 对照已提交的 `storage-tight/save.json`，同时确认源文件保持不变。验收证据仍以具体构建、命令、实际耗时和日志为准；这些 CLI 检查不提供 Godot 界面、目标窗口交互或阶段 #299 四模块验收证据。
