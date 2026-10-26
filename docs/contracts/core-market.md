# 核心模拟层：市场与交易

本页记录市场与交易，与[契约目录](core.md)一起使用。

## 市场报价与手动交易

#384 按[批准决定 D1–D9](https://github.com/odradekk/train/issues/384#issuecomment-6194386767)实现本节。玩法公式见[市场与交易](../design/19-markets-and-trading.md)，本节明确报价、执行及当前阶段边界。没有新增持久字段：钱包、货物、实例所有权、设计与耐久和唯一停车归属沿用现有编码；核心状态格式为 23，命令和重放格式仍为 2。目标、留货、价格、待售列表和名额不另存。#528 已接入三个经济初始化配置，生成初始库存和有限现车；初始化规则见上文“[阶段一生成器](core-lifecycle.md#阶段一生成器)”。现车也可由玩家卖给当地形成。自动规则和报告见下文“[自动交易与运行报告](core-recording.md#自动交易与运行报告)”，委托采购见上文“[NPC 车辆委托与施工报价](core-construction.md#npc-车辆委托与施工报价)”，真实经济内容及四模块交接归 #299。

## 备货与查询

每次查询或交易只计算一次全城潜在消耗。`Running`、`WaitingForInputs`、`OutputBlocked` 计划计入；停用、零能力、条件不满足、锁定、流程缺失不计入。假设原料和燃料齐全、产出不阻塞，按 Priority、BuildingId、CapabilityKind 顺序共用完好发电能力和当前有效劳动容量。每秒批次数为潜在工作速率除以当前流程工作量，原料率为批次数乘每批数量，燃料率为功率消耗除以燃料能量；燃料沿规范货物身份选择。实际批次、库存和资源仲裁不因查询推进。

容量预算复用仓储划分，扣除有效租约完整额度及到期租库实际占位，依次安排基本生活、生产、改善。生产第一轮试算完整最低组合：当前批次原料已投入，不重复备料，燃料按冻结系数乘剩余工作量；无当前批次时保障下一完整批次。组合放不下就跳过，试算不消耗预算或共享燃料余量。第二轮仅处理最低组合成功的计划，补当前流程时长目标超出已安排组合的部分，按整数目标比例及最大余数压缩，平局按稳定货物身份。全城燃料余量只抵扣一次，整件燃料的富余供后项使用。

目标函数的库存输入明确为主食和加工食品两个数量。只有食品需要随候选库存重算目标；其他货物的目标在一次查询或交易中固定，可售量为 `max(Stock-T,0)`，可收量为 `max(T-Stock,0)` 再受真实入库容量限制。居民目标计算和区间探测共用 `PopulationStockTargets.FoodTargets`，返回基本食品的两个整数需求 K。固定另一食品库存时，K 的两个分量各自单调，同一 K 的库存集合为连续区间；相同 K 决定相同的居民分配、生产预算和完整目标。数量计算在该区间内直接求连续可成交数量，计价只在跨区间时重算目标，金额仍逐件精确取整。边界搜索只探测 K，不假定最终目标单调；非有限探测值退回单点。K 变化但最终目标因压缩或合并恰巧相同的平台仍保守逐件处理。

`Market.Goods` 是当前普通货物目录、世界保留的组装体货物与当地库存货物的并集，按规范身份排序，保留已有缺定义货物。字段含义如下：

| 字段 | 含义 |
| --- | --- |
| `Stock` | 聚居地自身库存的实际数量，不含租库、消费物理余量或未来产物 |
| `RestockTarget`、`ReservedForUse` | 同一个整数补货／出售保留目标，不是额外存储预留，也不是净采购缺口 |
| `SellableQuantity` | 不考虑具体列车的连续可售数量；动态食品沿逐件当前目标判断 |
| `ReceivableQuantity` | 不考虑具体列车的连续可收数量，同时受本地真实 `Available` 限制 |
| `Blockers` | 自有 `invalid_state / Settlement`、敌对 `access_denied / LocalInventory`、缺规则 `content_missing / MarketRule`、缺货物定义 `content_missing / Good`；无用途或没有超额库存由数量 0 表达 |

缺市场规则时目标为 0 占位且附缺失原因，可售／可收为 0，不表示真实需求为零。自有聚居地仍计算装载警告所需的目标。目标变化不删除货物，入库始终另查真实空间和预留。

## 货物报价与执行

`QuoteTrade` 覆盖 Buy、Sell、Load、Unload。输入须有非零身份、合法货物、正数量和有效动作，否则抛 `ArgumentException`；命令价格条件须非负，也在取得编号前检查。报价不锁货、锁价或锁容量，执行重新裁决。

`CurrentlyExecutableQuantity` 不超过请求数量，表示当前可执行的连续前缀。资格失败为 0；数量或容量不足时给可执行前缀。Buy 足量物理条件满足但资金不足时给可支付前缀。手动命令仍整单接受或拒绝，不按该字段自动部分成交。Buy/Sell 的 `Total` 仅在完整请求数量、留货和容量条件满足后计算，钱包不足仍提供完整总价；其余拒绝时为空。Load/Unload 的 `Total` 始终为 0。`Blockers` 为按执行顺序得到的首个拒绝，成功为空。

Load/Unload 沿用“库存与租约”的检查顺序。合法自有装载若扩大转移后补货缺口，`ShortageWarnings` 给出 `restock_shortage / LocalInventory.Goods` 及货物身份，不阻止装载。租库无本地需求，不套用留货。缺市场规则不阻止合法手动自有装卸；目标不可计算时不伪造短缺警告。

Buy/Sell 在前提检查后依次判断：

| 顺序 | 条件和拒绝（代码 / 字段） |
| --- | --- |
| 1 | 对象存在：`object_missing / Train`、`Settlement`、`LocalInventory` |
| 2 | 列车全部车辆玩家自有：`not_owned / Train` |
| 3 | 列车位于当地节点：`train_not_at_settlement / Train.Position` |
| 4 | LocalInventory 是该聚居地直接拥有的库存；租库和其他库存拒绝 `invalid_state / LocalInventory`；自有聚居地拒绝 `invalid_state / Settlement` |
| 5 | 非敌对：`access_denied / LocalInventory` |
| 6 | 市场规则存在：`content_missing / MarketRule` |
| 7 | Station 停留中、S 停稳、速度 0、持站台预约：`platform_required / Train.Position` |
| 8 | 货物定义完整：`content_missing / Good` |
| 9 | 来源足量：`insufficient_goods / Quantity`；Buy 的 NPC 留货不足同码；Sell 的 NPC 用途缺口不足为 `insufficient_demand / Quantity` |
| 10 | 入库与全城预留空间足够：`insufficient_capacity / Train.Inventory.Capacity` 或 `LocalInventory.Capacity` |
| 11 | 总额条件满足：`price_condition_failed / MaximumPayment` 或 `MinimumReceipt` |
| 12 | Buy 钱包足够：`insufficient_money / Wallet` |

数量裁决先受真实来源数量和容量约束，超量请求在计价循环前拒绝。动态食品的供需沿候选库存路径逐件判断，任何一步失败整单拒绝。成功原子转移双方库存和钱包，发一个 `GoodsTransferred`，携带实际动作、数量和金额；拒绝无成交事件，业务世界保持原值。

货币中间运算使用 `BigInteger` 的整数分子／分母。中点保存为 `x2=成交前库存+成交后库存`，先 clamp 倍率，再乘价差，逐件 ceil 付款或 floor 收款、最低 1，最后累加。T=0 时不收购，出售按 `V·L·(1+s)` ceil，不除以零。参数直接使用万分比整数，未转换为 double。单件和总额转换、库存与钱包更新检查溢出；意外溢出走内部故障，不新增金额业务拒绝。报价故障返回 `CoreResult.Faulted`，不冻结活动会话；提交故障沿既有冻结语义。

**计价 T 的端点约定（D3）**：NPC 库存 s 与 s−1 之间的同一条库存边，买卖都使用低库存端 s−1 的补货目标计价；能否成交仍按该件成交前的真实目标判断。动态食品沿候选库存序列判断资格及计价目标，可证明目标相同的区间共用结果，潜在消耗仍复用本次计算。拆单和合单经过相同库存序列并逐件取整；反向同边基础价一致，付款向上、收款向下及非负价差保证原路买入卖回不获利。条件不满足的反向操作照常拒绝。

## 现车

`VehiclesForSale` 是当地 NPC 拥有的停放车辆，损伤、缺定义或建造能力失效不使车辆消失。`VehiclePurchaseCapacities` 对非敌对 NPC 列出五个尺寸档位：`ForSale` 为真实待售数，`Limit` 为当前配置上限（未配置为 0），`Available` 为有该档有效建造能力时的 `max(0,Limit-ForSale)`，否则为 0。自有聚居地返回空待售和空名额数组；敌对聚居地保留待售数和配置上限，各档 Available 为 0；缺市场规则时名额数组为空，并由 Market.Rule 明确报告缺失。上限降低不删车，买走释放名额。

报价与执行依次检查聚居地／车辆存在（`object_missing / Seller`、`Buyer` 或 `Vehicle`），关系（自有 `invalid_state`，敌对 `access_denied`，字段为 Seller/Buyer），方向所有权（Buy `not_for_sale / Vehicle`，Sell `not_owned / Vehicle`），同地（`vehicles_not_colocated / Vehicle.Location`），已解编（`vehicle_in_train / Vehicle.Details.Train`），定义完整（`content_missing / Vehicle`），市场规则（`content_missing / MarketRule`）。Sell 再检查 #501 的有效施工部件车辆档位（`capability_zero / Buyer`）和名额（`insufficient_capacity / Buyer.VehiclePurchaseCapacity`）。最后检查总限价及 Buy 钱包，代码和字段沿用货物交易。

清空货物由既有 `DisbandTrain` 的非空货物／预留拒绝保证；未编组车辆没有单独库存。`SellVehicle` 在关系和所有权检查前拒绝任一施工锁，返回 `instance_locked / Vehicle`；锁由活动改装、修理或拆解任务派生。

估值 A 按 `StructuralBaseValue × 有效结构耐久/结构上限 + Σ PartProjection.BaseValue × 有效部件耐久/部件上限` 精确汇总，有效耐久取历史绝对值与当前上限的较小值，被毁部件贡献 0，不重复计算部件对结构耐久的贡献。各项整数分数先汇总、再乘整车万分比倍率，付款只 ceil 一次，收款只 floor 一次，无最低 1。报价条件不足返回 Total 空；限价或钱包不足仍提供已算出的总价。成交只改变钱包与原实例所有权，发 `VehicleTraded`，车辆仍在原地、原停车列表、未编组，设计与损伤不变。

四种新增命令仍用输入格式 2，规范输出字段顺序为：

| kind | 字段顺序 |
| --- | --- |
| `market.buy_goods` | `input_format_version, kind, train, settlement, local_inventory, good, quantity, maximum_payment` |
| `market.sell_goods` | `input_format_version, kind, train, settlement, local_inventory, good, quantity, minimum_receipt` |
| `market.buy_vehicle` | `input_format_version, kind, seller, vehicle, maximum_payment` |
| `market.sell_vehicle` | `input_format_version, kind, buyer, vehicle, minimum_receipt` |

身份为非零 ulong，数量为正 long，金额条件为非负 long，good 沿用装卸的三种规范货物编码。解码接受字段换序，拒绝缺失、多余、重复字段及非法数值，返回相应 JSON Pointer 的 `invalid_state`；不支持的版本或种类为 `unsupported_format`。成功和正常拒绝都进入录制，普通恢复与严格重放沿同份状态和命令实现执行。
