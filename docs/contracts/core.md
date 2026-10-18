# 核心模拟层公开契约

本页落实[核心模拟层公开契约：命令、查询、事件与复现](https://github.com/odradekk/train/issues/287)，并经[契约审计修订](https://github.com/odradekk/train/issues/296)收窄，与[公共约定](conventions.md)、[跨层事务](transactions.md)和[编辑协议](editing.md)一起使用。玩法以设计文档为准，本页说明调用方法及其责任。

## 施工能力输入与执行交接（#501）

`PartCapability.Construction(double WorkPerSecond, PlotSize MaximumBuildingPlotSize, SizeClass? MaximumVehicleSize)` 的三个参数均无默认值；调用方显式传 `null` 表示没有车辆施工能力。`WorkPerSecond` 为正有限值，建筑上限只接受 `Small`、`Medium`、`Large`、`Giant`，Core 输入校验拒绝站点档位与非法枚举；非空车辆上限必须是有效的车辆档位。能力来自完好部件，工作速率相加，两个上限分别取完好施工部件的最大值；不新增无调用方的公开汇总接口。

- #343：在建筑建造、改装、修理的命令边界检查目标设计的地块档位不超过设施建筑上限；小站点按大、大站点按巨型归档。改装／修理范围与站点归档按批准计划记录，阶段验收时待用户确认。
- #331：已在自有车辆建造、修理的命令边界检查设施车辆上限；车辆改装归 #332。`null` 一律拒绝，非空时目标车辆档位不得超过上限。不能把存在施工工作速率解释为能够造车。
- 拆解和制造大型设备只要求施工能力，不检查上述档位。#501 交付表达、输入校验、持久化和交接；大型设备制造归 #343，拆解归 #332。

## 交付状态与入口

已提供独立的 [Train.Core.Contracts](../../Contracts/Core/Train.Core.Contracts.csproj) 声明和[可编译消费样例](../../prototypes/core-contract/ConsumerExamples.cs)。当前实现了核心工厂、候选与会话生命周期、阶段一小世界生成、同步内容发布、列车编组及计划运行、方向锁与跟车、对向公平、站台排队、断点掉头及 `DebugAddRailBreak`、不可变世界查询，以及当前世界状态的捕获、编码、解码、普通恢复、默认录制、起点确认与裁剪、固定故障片段、严格重放和检查点比较。其他领域行为仍待交付；模块证据不代表 #298 的真实四模块联调或完整 Demo 已通过，调用样例和旧原型也不构成生产行为证据。

| 声明 | 调用方使用的内容 |
| --- | --- |
| [Values.cs](../../Contracts/Core/Values.cs) | 强类型身份、位置、金额、万分比、带类型的货物和对象引用、查询缺失和结果 |
| [ContentInputs.cs](../../Contracts/Core/ContentInputs.cs) | 内容提供的核心投影、设计及返还归属、改装差额、各项规则、生成与开局输入 |
| [Commands.cs](../../Contracts/Core/Commands.cs) | 封闭命令目录、任务和租约请求、命令回执 |
| [Queries.cs](../../Contracts/Core/Queries.cs) | 同一边界的读视图、业务详情、报价与预览 |
| [Events.cs](../../Contracts/Core/Events.cs) | 本次调用的有序领域事件和删除后仍可用的表现资料 |
| [Lifecycle.cs](../../Contracts/Core/Lifecycle.cs) | 工厂、会话、内容候选、捕获、录制与严格重放 |

新入口为 [Train.Core.CoreFactory.Create()](../../Core/CoreFactory.cs)，返回 `ICoreFactory`；实现类型均为 `internal`，不接收外部计算阶段、规则、随机源或编码器。生产宿主由 `App/Composition/` 调用真实工厂，其余代码只接收契约接口；调用位置由结构检查验证。工厂公开面纳入检查仍归 #406 等整组基线工作；[公开面基线](public-api.txt)仍只有已确认的契约声明。

当前已实现的成员：

- `PrepareNew` 及候选的 `View`、`References`、`AdaptationReport`、`MissingReferences`、单次 `Activate` 和 `Dispose`；生成失败不替换活动会话。
- 会话的 `Read`、`Submit`、`Advance` 和 `Dispose`。实现 `PublishContent`、五个铁路命令和 `DebugAddRailBreak`，推进包含列车运行及线路、站台调度；`PublishContent` 在比较身份前使用与新局相同的内容投影校验，未初始化数组等输入错误在事务前抛 `ArgumentException`，不取得命令编号。身份相同时在事务前抛 `ArgumentException`；合法投影身份不同时，在同步事务内检查世界引用并适配保存的设计、衍生货物、当前定义依赖和节点站台数，具体拒绝与兼容规则见下文“内容候选与同步提交”。铁路货物查询和出发检查从当前货物目录识别缺失，恢复候选的缺失标注见下文“当前载荷”。
- 读视图的 `Position`、`Revision`、`InputIdentity` 和 `Fault`。修订在每个 Completed 命令、完成步骤及新步骤故障时各增加一次；NotAccepted、查询和零步推进不增加。修订已达 `ulong.MaxValue` 时，故障仍写入并冻结，修订保持最大值；正常路径的递增仍使用 checked 算术。
- `World`、`FindNode`、`FindEdge`、`FindLocation`、`FindInstance`、`FindTrain`，覆盖生成的图、占据者、资源、实例和运行列车；车辆位置随所属列车派生，轨道查询列出实际在轨列车。
- `FindInventory`、`FindLease`、`QuoteLease`、`LoadGoods`、`UnloadGoods`、`AcquireLease` 及租约到期，按 [#325 的批准计划](https://github.com/odradekk/train/issues/325#issuecomment-6172428536)实现。库存预留参与容量和持久化；当前车辆制造与修理不产生返还预留；产生和释放预留归 #332 等后续任务，具体规则见下文“库存与租约”。
- #384 接入 `QuoteTrade` 的四种动作、`BuyGoods`／`SellGoods`、`QuoteVehicle` 和 `BuyVehicle`／`SellVehicle`，以及补货目标、两轮备货、潜在消耗和现车名额；具体语义见下文“市场报价与手动交易”。#528 接入按配置计算的 NPC 初始库存和有限现车，见下文“阶段一生成器”。
- #385 接入运行计划的自动货物规则、停靠重评、运行累计、近期报告及 `AutomaticTradeEvaluated`、`RunReported`；规则顺序、部分成交、时序与持久化见下文“自动交易与运行报告”。
- `FindSettlement` 与 `Economy` 按 [#486 的批准计划](https://github.com/odradekk/train/issues/486#issuecomment-6190044442)接入已有状态：聚居地库存身份、地块、停放车辆、站台与占用、租约、仓储划分、加工、回收和开采设施、电力、人口、钱包和当前规则状态。尚未实现及暂定字段逐项见下文“聚居地与经济查询”。
- `FindFacility` 的 `Production` 键、模板中的加工／回收计划、批次推进、输出缓冲及生产事件，按 [#381 的批准计划](https://github.com/odradekk/train/issues/381#issuecomment-6183793182)实现，具体范围见下文“加工与回收”。
- `Extraction` 设施、开采批次、节点资源原地恢复与全局再生，按 [#383 的批准决定](https://github.com/odradekk/train/issues/383#issuecomment-6188092446)实现，使用同一批次运行时与资源仲裁；参数、时序及事件见下文“节点资源”。
- #331 接入 `StartOwnedJob` 的 `BuildVehicle` 和车辆 `Repair`、`FindJob`、施工事件与修理锁；施工与生产、开采、人口共用单步资源仲裁，具体范围见下文“自有车辆施工”。
- #387 接入两种资金来源的车辆制造／修理 `GetJobQuote`、`CommissionVehicleWork`、原子补购收费及按设施 FIFO 委托队列；全局工钱、查询、事件和保存重放见下文“NPC 车辆委托与施工报价”。
- `Capture`、`DecodeState` 和 `PrepareRestore`，覆盖当前真实世界状态；格式与恢复规则见下文“当前载荷”。公开 `PrepareNew` 路径有共享 `CaptureScenario` 及跨进程生成、保存恢复测试。
- `ReadRecording`、`CaptureRecordingStart`、`ConfirmRecordingStart`、`PrepareReplay` 和 `CompareCheckpoints`，覆盖当前完整世界状态、`PublishContent`、五个铁路命令和 `DebugAddRailBreak`，包含正常业务拒绝。运行在线路上、停留及调度中的录制通过生产重放路径逐检查点比较。
- #382 将人口消费与结算接入 `Advance`：六类生活需求、主食优先与加工食品共享满足、整数取用后的物理余量、分类加权、人口变化余量和最低一人。每次消费全部完成后判断各项短缺，仅在状态变化时发 `PopulationShortageChanged`；结算发 `PopulationSettled`，从多于一人降至一人时发 `PopulationMinimumReached`。停用与恢复使用 `PopulationAvailabilityChanged`，停用期间不消费、不累计周期、不判断短缺，劳动力为零。

下表列出尚未实现的行为。尚未实现的独立成员直接抛 `NotImplementedException`，不以业务失败、故障结果或缺失查询代替。未实现的命令在取得编号和进入事务捕获之前抛出；异常消息只注明成员或命令种类。

| 未实现成员 | 交付责任 |
| --- | --- |
| `NpcCommission` 下改装的 `GetJobQuote`、`CommissionVehicleWork` | #332；车辆制造／修理及其两种资金来源的报价已由 #387 接入 |
| `FindFacility` 的 `Construction` 键、`SetJobPriority` | 后续施工查询与调度任务 |
| `OwnInventory` 下建筑建造、大型设备制造、建筑修理的 `GetJobQuote` 及对应的 `StartOwnedJob` | #343 |
| `OwnInventory` 下改装、拆解的 `GetJobQuote` 及对应的 `StartOwnedJob` | #332 |
| 其余命令、其他 `Find*`、报价和预览 | 后续经济运输、制造与编辑、自有建设、战斗与生物领域任务 |

旧 `Train.Core.Simulation` 及宿主入口已删除。`RandomKey`、`KeyedRandom`、`SplitMix64` 原算法迁到 `Core/World/`，作为内部实现保留；固定向量测试及独立 Python 参考工具位于 `Tests/Core/World/`。会话始终持有世界状态；内部会话测试也使用真实生成状态，拒绝与事件由内部测试体供给。列车运行、调度及后续领域任务须将各自新增权威字段补入捕获、恢复和检查点，不能把初始世界往返当作完整玩法恢复证据。

#380 已实现内部建筑能力计算、聚居地共用燃料能量余量和单步资源仲裁。存储、发电、生产、开采、施工共用有效部件判断；设施请求在基本消费与改善消费之间，按 Priority、BuildingId、CapabilityKind 单次排序处理完整批次输入或联合用电／用工请求。只从聚居地自有库存取用货物和燃料，闲置发电不耗燃料，已取出的能量余量不随燃料规格更新而重算。#381 与 #382 已将真实设施请求、人口消费与劳动力接入 `Advance` 的同一次资源仲裁；模块测试不表示完整经济流程已运行。交接逐项如下：

- #381 已接入真实批次输入、最大有用工作速率和批次推进，不在步内重启。
- #382 的 `PopulationOperations.Begin` 在消费到期时生成基本消费请求并提供人数乘当前有效劳动比例的容量；`PopulationConsumption.PrepareImprovement` 使用基本消费实际授予量生成改善请求，`PopulationOperations.Complete` 在三段消费后回写状态与事件。基本消费预估与仲裁读取同一份库存。同一步每座聚居地最多调用一次 `SettlementResources.Resolve`，无到期消费及设施请求时跳过仲裁。人口请求和真实设施请求进入这同一次调用，使用人口提供的真实劳动容量；生产结果与人口结果保留在同一个提交世界中。本步产出下一步才可消费，期末人口变化下一步才影响请求和劳动力分配。
- #486 已将已有权威状态接入 `FindSettlement` 及其 `PowerPoolView`、人口和仓储查询；加工、回收和开采设施详情与 `FindFacility` 使用同一查询实现。
- #384 已接入潜在消耗、两轮备货及最低组合的共用燃料抵扣。潜在计算复用 `SettlementResources.AllocateWork` 的电力／劳动分配，实际运行仍由 `Resolve` 扣料和提交；查询不消费库存或燃料。
- #299 验证上述真实模块路径及此前阶段路径；本票的内部仲裁、保存恢复与隔离证据不能替代阶段联调。

#382 的居民备货计算为内部 `PopulationStockTargets.Calculate`。调用方传入实际本期规则（尚未开始时为当前规则，停用时传空）、人数、市场基本／改善备货时长、主食与加工食品各自的库存数量、物理消费余量、需求类别与该类容量预算，返回按货物身份排序的整数目标。只有这两种替代食品的现货影响目标，其余现货只参与报价和可成交数量。目标包含应保留的现货，不是净采购缺口；加工食品共享基础与改善覆盖，物理余量抵扣后再逐项向上取整，预算不足时以标准库 `BigInteger` 保存整数目标及比例中间量，按整数目标比例和最大余数分配，最终数量转换为容量可表达的整数；平局按稳定货物身份。#384 的 `MarketStockTargets` 按基本、生产、改善顺序提供预算并合并目标；基本目标已经压缩时，后续预算为零。该内部入口及市场的模块证据不代替 #299 真实联调。

#486 的人口查询接入使用已保存的 `SettlementState.Population` 与 `PopulationState`：本期规则及修订、有效步数、累计应需与满足量、六项短缺状态、上次可用性以及最近结算。周期进度保存 `ElapsedSteps` 与 `LastAdvancedStep` 时间锚点；可用时在当前边界加上距锚点的步数，停用时保持不动，恢复后重新锚定。非消费步不重建人口数组；应需与满足量仅在消费步同时累加同一区间的实际计算值，不从总步数反推累计值。消费物理余量和人口变化余量只用于模拟及持久化，不进入查询。当前有效劳动比例立即生效，本期消费、权重和增长参数保持冻结；可用性须结合当前投影及本期必需引用判断，上次可用性也用于冻结进度。`LastSettlement` 只保留最近一条；查询接入后 `RecentSettlements` 返回同一条，尚无结算时为空数组。结算原因按变化率的符号判断，整数变化为零也可以是增长或短缺流失；负变化率且结算后为一人时为 `MinimumReached`。

### 聚居地与经济查询

`FindSettlement` 对零编号抛 `ArgumentException`，不存在的非零编号返回 `Lookup.Missing`。地块保留运行身份及模板本地编号；普通地块允许除站点外的建筑种类，站点地块仅允许站点。`Market.Storage` 复用库存与租约的统一容量划分，租约列表包含尚未删除的到期租约。查询只读取当前读视图，不推进消费、生产、租约或统计。

`Power.GenerationCapacity` 是当前有效发电部件的能力之和，`FuelEnergyRemaining` 是已保存的燃料能量余量。`Demand`、`ActualSupply` 是各设施上一步保存结果之和，不因当前内容发布而重算。`Population.AllocatedLabor` 同样汇总各设施上一步保存的 `LastAllocatedLabor`，完成批次也保留最后一步分配；下一无工作请求步骤归零。`AvailableLabor` 按当前人数乘当前有效人口规则劳动比例计算，人口停用时为零；`IdleLabor = max(0, AvailableLabor - AllocatedLabor)`。当前劳动容量下降后，上一边界实际分配可以暂时高于当前容量。

人口周期尚未开始时返回 `NotStarted`；已有周期但尚无消费统计时，满足率为空、趋势为 `Stable`。消费后满足率来自同一周期的累计应需与满足量，零应需项按已满足处理；趋势按冻结规则的变化率判断，负变化率且人数为一时为 `AtMinimum`。缺当前规则或本期必需引用时返回 `Disabled` 并保留周期。当前规则修订与本期执行修订分开提供，不伪造尚未发生的结算。

`Economy.Wallet` 读取真实钱包。市场、人口、施工的 `RuleStatusView` 返回本局所选身份及当前发布修订；当前定义不存在或已标缺失时修订为空，并给出 `content_missing`。以下字段按 #486 批准的阶段范围处理，后续工单须接入其新增状态：

| 字段 | 当前取值与后续责任 |
| --- | --- |
| `VehiclesForSale` | 当地 NPC 拥有的实际停放车辆，按身份排序；来源包括世界生成的初始现车和玩家卖给当地的原实例 |
| `Surrendered` | 当前没有投降机制，为 `false`；#352 接入投降状态 |
| `CommissionJobs`、`Economy.UnassignedServicePayments` | #387 返回当地活动委托的任务编号（按受理顺序），并累计全局实际已付委托工钱；材料补购款、租金及各列车运行账目不计入该累计 |
| `Market.Goods`、`VehiclePurchaseCapacities`、设施 `PotentialConsumption` | #384 从当前真实库存、需求、能力和市场规则派生；字段定义见下文市场部分 |
| `Economy.RecentRunReports` | #385 接入全局近期运行报告，按生成顺序返回；与列车查询共用同一份历史，包含已解编列车的报告 |
| `Facilities` 中的施工设施 | 当前返回已实现的加工、回收和开采设施；施工详情查询仍待后票，未列出不表示不存在施工能力或活动施工任务；活动任务由 `FindJob` 查询 |
| `ParkingCapacity` | `int.MaxValue` 表示 Demo 不限停放数量，不表示已实现有限停车容量 |
| `MoraleRatio` | 暂定为满值 `1`；士气权威状态及初始值由 #352 接入 |
| `Engagement` | 范围按聚居地默认取 `All`，优先级暂取 `ArmedFirst`；#350 接入设置命令和状态并确定优先级 |

上述暂定停车、士气和交战值仅由查询给出，不加入持久状态；这部分结果不构成后续战斗行为的验证证据。

### 建城与玩家聚居地

#342 实现 `FoundSettlement`、`PreviewSettlement`、`FindJob` 的建城分支及 `SettlementFounded`。世界生成与建城共用类型和模板选择规则；建城从当前固定投影选择，以世界种子、节点身份和独立用途键生成随机数，不消耗新局生成的随机序列。

- `PreviewSettlement(NodeId)` 与受理共用节点、内容及身份可用性校验，返回起始人口、生活货物每秒预计用量、精确 BOM、冻结工期及阻止原因。预览不指定材料列车；提交时另查该列车的所有权、施工锁、位置、速度与完整材料。生活物资预计量按基本食物优先使用主食、改善饮食使用加工食品计算。
- 人口不可用时复用人口模块的停用判断，只有预览返回 `CoreResult.Failure`，问题为 `content_missing` 并指向缺失规则或货物；不伪装成零需求，也不增加建城拒绝条件。
- 材料列车归玩家所有、位置是目标 `TrainPosition.Node` 且速度为零即可，不限制节点内阶段，不要求站台。占据节点返回 `node_occupied / Node`，已有建城预留返回 `busy / Node.FoundingJob`。材料不足返回 `insufficient_goods / MaterialSource.Inventory`，列出缺货身份；正常拒绝不改变库存、节点或任何实体编号来源。
- 接受时一次扣除完整起始建筑 BOM，从全世界共用来源取得 `JobId` 并预留最终 `SettlementId`；编号耗尽返回 `identity_exhausted`，仍按 Completed 拒绝进入录制。`Created` 只列已经创建的任务，`SettlementCopies` 立即列出预留所有者、源模板及地块号/尺寸。宿主按既有协议完成内容配对后才通知、保存或推进。
- 任务冻结玩家业务模板、类型身份、受理时人口下限、工期和已投入材料，不锁定或继续依赖材料列车，不消耗施工能力、电力或劳动力。只保留 `founding` 建筑，不授予 NPC 预建建筑、库存或生产计划。源模板更新不改任务；普通恢复时即使类型已缺失，完工仍使用冻结人口。
- 恰好推进满冻结工期时创建预留身份的玩家聚居地及其起始建筑，库存为空，人口从成立后开始统计。站台数复用建筑能力计算，节点尺度变化复用列车适配；清除节点任务标记并发出一次 `SettlementFounded`。进行中的副本、设计、类型与任务规则从受理起进入引用清单、保存、检查点和严格重放。建筑后续建设归 #343，界面归 #346，真实四模块交接归 #301。

建城命令沿用输入格式 2，规范字段顺序为 `{"input_format_version":2,"kind":"settlement.found","node":1,"material_source":1}`；两个身份均为非零 `ulong`，解码允许重排，拒绝缺失、未知、重复及非法字段。不新增控制台命令。

### 自有车辆施工

[#331 的批准计划](https://github.com/odradekk/train/issues/331#issuecomment-6233893871)只实现 `StartOwnedJob(JobRequest.BuildVehicle)` 和 `StartOwnedJob(JobRequest.Repair)` 的车辆目标。设施必须位于玩家自有聚居地，且建筑归玩家所有、未被摧毁。车辆上限按当前设计中定义未缺失的施工部件判断资格，不看部件损伤；上限为 `null` 或低于目标档位拒绝。实际工作速率和档位能力只计完好部件。每个施工能力最多一个任务，已占用返回 `busy`，不排队。

制造请求的 `DesignInput.InputIdentity` 必须等于当前内容身份；目标须为车辆、`CanConstruct`，且设计及 BOM 货物不缺定义。接受后完整目标设计与 `AssemblyGoods` 才进入世界设计目录。修理目标可以是当地停放车辆，也可以是位于设施节点、`Activity == Stopped` 的玩家列车中的车辆，不要求解编。修理只处理有效定义：被毁部件各消耗一件 `RepairGood`；结构货物按数量乘结构耐久损失比例逐种向上取整；受损未毁部件只计 `RepairWorkPerDamagedPart`。损失用保存耐久与当前上限的较小值计算。缺定义部件和结构货物不消耗、不计工作量，部件耐久记录原样保留；没有可修工作量时拒绝。

`Sources` 是显式消费顺序，空数组表示没有来源，只能用于无需货物的请求。允许设施所在聚居地库存和当地可访问的玩家列车库存；列车来源复用 `LoadGoods` 的归属、同节点、修理锁、`Servicing`、`Docked`、零速及站台预约检查。重复、不存在或不可访问来源定位到 `Sources[i]`。受理先完成全部校验和材料分配，再原子扣料、分配任务编号并占用设施；任何拒绝都不扣料、不占能力。

任务冻结目标设计身份、总工作量、电力／劳动系数、规则身份和启动修订，保存实际材料来源及投入。已有实例与任务的规格来自世界保存的设计，普通读档沿用内嵌设计，规格适配通过 `PublishContent` 完成。`PublishContent` 不允许删除活动任务必需定义；规则更新不重算冻结的总工作量与系数。任务通过 `SettlementResources.Resolve` 与生产、开采及人口同次仲裁；资源不足或完好部件能力不足为 `ResourceBlocked`，保留进度，能力不足的 `Limits` 含 `Capability`。设施被毁、失去归属或设计资格，以及目标实例消失或被毁，使任务失败并释放设施。制造完成后创建当地停放车辆；修理完成后有效部件和结构耐久恢复到当前上限。接受发 `ConstructionStarted`，状态变化发 `ConstructionStateChanged`，完成再发 `ConstructionCompleted`。完成或失败立即移除任务，之后 `FindJob` 返回 `Missing`，结果从事件读取。

`FindInstance` 的 `UnderRepair` 从活动修理任务派生，不另存锁。锁拒绝同一车辆的第二个施工任务、所在列车的 `StartTrain`、货物买卖与装卸，以及 `SellVehicle`；编组、解编允许。稳定拒绝码如下，字段与相关身份补充定位：

| 代码 | 原因 |
| --- | --- |
| `object_missing`、`not_owned`、`instance_destroyed` | 设施或目标不存在、非玩家所有、已被摧毁 |
| `invalid_design`、`content_changed`、`content_missing` | 目标不能制造、输入身份过期、必需定义缺失 |
| `conditions_not_met`、`capability_zero`、`busy` | 设施车辆档位资格不符、完好施工能力不足、能力已占用 |
| `instance_locked` | 车辆正在修理，或列车包含正在修理的车辆 |
| `train_not_stationary_at_node`、`vehicles_not_colocated` | 修理目标列车未在当地停止，或停放车辆不在当地 |
| `duplicate_source`、`access_denied`、`insufficient_goods` | 来源重复、库存不允许访问、总材料不足 |
| `train_not_at_settlement`、`platform_required` | 列车来源不在当地，或不满足停站与站台访问条件 |
| `invalid_state`、`identity_exhausted` | 无可修工作量，或任务编号耗尽 |

运行中的阻塞还使用 `power_unavailable`、`labor_unavailable`。`FindFacility(Construction)`、`SetJobPriority`、建筑施工、大型设备制造、改装（含委托报价与命令，归 #332）和拆解均未实现；未支持命令／请求在取得命令编号前抛 `NotImplementedException`。本票不增加取消、退款或暂停。

### NPC 车辆委托与施工报价

[#387 的批准计划](https://github.com/odradekk/train/issues/387#issuecomment-6239791107)接入 `CommissionVehicleWork` 的车辆制造和修理，以及 `GetJobQuote` 的 `OwnInventory`、`NpcCommission` 两种资金来源。两条命令和报价共用需求、来源分配及拒绝计算；报价不预留货物、资金或队列位置，提交重新裁决。报价内部故障返回该读视图位置的 `CoreResult.Faulted`，不冻结活动会话。

两种资金来源均只实现 `BuildVehicle` 和车辆 `Repair`。`Refit` 仍抛 `NotImplementedException`，由 #332 接入同一委托流程。委托的 `BuildBuilding`、`BuildEquipment`、`Dismantle` 和建筑 `Repair` 不合法，抛 `ArgumentException`。非法资金来源、零身份、未初始化来源数组和负 `MaximumPayment` 也在取得命令编号前抛 `ArgumentException`。

业务检查按以下顺序执行，报价和命令返回相同代码与字段：

1. 设施所在聚居地存在（`object_missing / Facility`）；委托位于自有聚居地时 `invalid_state / Facility`，敌对时 `access_denied / Facility`。
2. 制造的输入身份、设计种类、可制造性及必需定义；修理的目标存在、玩家归属、修理锁、当地停车位置和结构未毁，沿用上节的代码和字段。
3. 设施存在、归属、结构未毁、档位资格和完好施工能力；自有任务检查设施空闲，委托允许排队。设施及目标其余拒绝沿用上节。
4. 施工规则存在（`content_missing / ConstructionRule`），委托还须有市场规则（`content_missing / MarketRule`）；计算工作量及材料，无可修工作量时 `invalid_state / Instance.Durability`。
5. 按数组顺序检查每个来源的重复、存在和访问权限。自有任务沿用当地自有库存／当地列车来源；委托仅允许当地玩家列车库存及该聚居地的玩家租用库存，到期租约仍可取出。列车来源复用装载的归属、同节点、停站、站台、零速及修理锁检查。NPC 库存不能作为玩家来源，返回 `access_denied / Sources[i]`；其他来源拒绝也定位到 `Sources[i]`。
6. 先分配玩家来源，再按货物稳定身份顺序从当地真实可售库存补购缺额，复用市场留货及逐件边际价格。两种资金来源均检查全部货物后才报告缺料；`insufficient_goods / Sources` 的 `RelatedIdentities` 按货物键的 Ordinal 顺序列出所有缺货身份，编码与 `restock_shortage` 相同。补购直接投入施工，不经列车货舱，不受玩家货舱容量限制；不会先买半份材料，也不建立等待供货的任务。
7. 任务编号可用；计算材料款、工钱和总额后依次检查总限价（`price_condition_failed / MaximumPayment`）和钱包（`insufficient_money / Wallet`）。

`RequiredGoods` 是整份需求，`SourceConsumption` 仅为玩家来源，`NpcPurchases` 是缺额补购。拒绝时，`SourceConsumption` 和 `NpcPurchases` 只包含到失败检查为止算出的部分分配，仅供诊断参考，不能据此消费材料或执行补购。`OwnInventory` 的补购为空，`MaterialPayment` 与 `ServicePayment` 为零。启动条件不足时 `TotalPayment` 为空；仅限价或钱包不足时仍保留已算出的总额。工钱为冻结总工作量乘市场规则的 `CommissionMoneyPerWork`，无最低费用。接受时一次提交全部玩家材料消费、NPC 补购、总付款、工钱累计及任务建立，发 `ConstructionStarted`，携带资金来源和两项实际付款。材料款只影响钱包及事件；只有工钱计入 `Economy.UnassignedServicePayments`，不计入任何列车运行的 `ServicePayments`。

每个 `FacilityKey` 以 `JobId` 派生严格 FIFO，不保存第二套队列。队首未结束时，后续任务为 `Queued`，进度和速率为零，不请求资源，即使队首为 `ResourceBlocked` 也不跳队。不同设施可并行；请求 `Priority` 仅参与当地资源仲裁，不影响队列顺序。队首完成或失败后，下一步才处理后继任务，开工通过既有 `ConstructionStateChanged` 可见。排队任务同样检查设施被毁、失去归属或资格、目标消失或被毁的失败条件，不退款。修理从受理起取得锁，排队期间也禁止第二个任务、运行和贸易。

已受理委托沿用保存的投入、工作量和电力／劳动系数。合法内容更新不重扣材料、不重收工钱、不回算历史付款；后续报价使用更新后的规则。保存恢复、检查点和严格重放包括资金来源、队列状态与全局工钱累计。普通世界仅保留接受的设计，录制仍保留所有 Completed 命令（包括拒绝）携带的完整设计输入及依赖。

### 加工与回收

计划直接读取每座聚居地已保存的 `Template.ProductionPlans`，按地块的 `TemplateNumber` 定位建筑；没有第二份运行计划。源模板更新不改变该副本。`SetProductionPlan` 仍未实现，继续抛 `NotImplementedException`；玩家计划编辑归后续自有建设任务。本票不新增控制台命令。

`FindFacility` 支持 `CapabilityKind.Production`（加工与回收）及 `Extraction`（开采）。建筑不在聚居地地块上，或既无该能力的计划、设计也没有声明该能力时返回 `Missing`。设备损坏或缺定义造成有效能力为零，不会使仍有计划或声明能力的设施消失。`Construction` 键仍待后票，抛出注明 `FindFacility` 和能力种类的 `NotImplementedException`。`PotentialConsumption` 由全城同一次潜在电力／劳动分配换算为每秒原料与燃料消耗；与 `FindSettlement.Facilities` 使用同一计算。

状态按流程缺失、停用、能力为零、不满足条件、出料阻塞、等待原料、运行中的顺序判断。锁定目前没有权威来源，由 #332、#343 接入，不能据当前七态的模块证据宣称八态完整通过。停用只阻止启动新批次；当前流程的 Tag／地貌要求不再满足时，状态为“不满足条件”，已有批次仍按冻结规格完成。能力为零或流程缺失时保留批次进度。流程补回后继续，不重新扣原料。

启动步只原子投入完整原料并建立批次，下一步才工作。工作请求上限取当前有效能力与剩余工作量所需速率的较小值，使用冻结的工作量和电力／劳动系数。生产推进接收 #382 人口换算提供的劳动容量，与基本／改善消费在同一次 `SettlementResources.Resolve` 中仲裁；人口规则或本期必需货物缺失时，人口停用并提供零劳动力。

完成产出和已有缓冲在批次请求仲裁之后按计划优先级、建筑身份入库，同一批多输出按货物身份的 Ordinal 顺序。使用扣除租约和预留后的真实可用容量，不扩大超容；本步完成或步初缓冲非空的设施不在本步启动下一批。生产能力失效不阻止已完成货物的内部转入。仅冻结产出或缓冲货物定义缺失时，仍按件数转入库存并保留身份；已有库存规则将其标为缺失且排除原料和燃料使用。流程本身缺失则暂停执行，不用冻结参数替代缺失定义。

`SettlementState.ProductionFacilities` 按 `FacilityKey`（建筑身份、能力种类）保存 `Batch`、`OutputBuffer` 和上一步的 `LastActualWorkPerSecond`、`LastLimits`、`LastPowerDemand`、`LastActualSupply`、`LastAllocatedLabor`。同一建筑的加工和开采计划独立运行，共同竞争电力、劳动力与输出容量；相同优先级、建筑身份的请求及入库按能力种类排序。批次保存流程、启动版本、冻结产出、总工作量、两项系数和进度。请求功率为本步最大有用速率乘冻结电力系数，实际供电及实际劳动占用为获分配速率乘对应冻结系数；无工作请求时结果归零／空。`ActualWorkPerSecond` 和 `Limits` 直接返回上一步结果，能力、主状态和规格差异按当前内容派生。内容发布不重算上一步结果；读档、旧读视图和检查点保留它们。电力与实际劳动占用查询汇总每座聚居地全部设施的上一步结果，包括开采；施工另保存聚居地上一步汇总，以保留任务完工移除后的读数。

### 节点资源

开采使用本节点的储量作为批次投入，不读取库存中的同种货物。启动时经同一次 `SettlementResources.Resolve` 按优先级、建筑身份、能力种类原子扣除完整 `Extracts`，下一步才工作；冻结产出为同种同量货物。设施标签、地貌、有效开采能力、缺失流程、停用、供电、劳动力与 OutputBuffer 沿用普通批次规则。资源不足时等待完整一批，不部分开采。不可再生项扣到零时删除，可再生项保留零储量；设施和计划保留。

可再生项使用自己已保存的 `ResourceRenewal.Renewable(Maximum, UnitsPerSecond)`。每步按 `UnitsPerSecond / 60` 加入小数余量，累计出的整量恢复储量；恢复量最多为步初距离上限的差额，到达上限时丢弃余量。恢复在本步开采扣量之后提交，因此本步恢复量下一步才可开采；步初已满的项不会因本步开采而取得恢复量。非零余量按节点、货物身份保存于 `resource_recovery`，参与检查点及严格重放，其货物依赖由已有节点资源引用覆盖。普通恢复和内容发布不重算上限、速率或余量。

不可再生项的参数来自 `world.source` 中已解析的资源生成配置。`resource_regenerations` 按货物保存生成时全世界该货物的初始储量合计及下一次抽取序号；初始总量使用 `Int128`，不以当前剩余量重算，其货物依赖由已有生成配置引用覆盖。内容发布和普通恢复不改变这些参数。周期步数为 `max(1, ceil(RegenerationIntervalSeconds × 60))`，乘积按一次 `double` 运算后向上取整；周期超过 `ulong` 可表达步数时，在可表达的会话时间内不会到期。各货物的整数检查进度从已保存的完成步数与自身周期派生，不另存进度。步初完成步数为 `n` 时，恰在 `(n + 1) % 周期步数 == 0` 的步骤检查，读取步初的全世界剩余量与资源存在性；恢复后继续同一周期。

剩余量严格低于初始总量乘触发比例时，每个检查周期最多新增一项。阈值比较使用输入比例的精确二进制值和整数储量，不把大整数合计转成 `double`；初始量为零或比例为零时不触发。候选是生成表允许地貌、步初没有该资源的节点，占据者不限，按 `NodeId` 升序排列。新增量取配置的闭区间，不再次判定初始生成的出现概率。无候选时不抽取、不推进序号，下个周期继续检查。抽到零时不新增、不发事件，但消耗序号；下个周期继续检查。新储量下一步才可开采，世界图结构和地貌不变。没有扣量、没有未满可再生项、没有到期检查的空步直接保留已提交的世界记录和节点数组；只有储量实际变化的节点才重建。

随机沿用 `KeyedRandom` 算法版本 1。选节点的用途是 `resource.regeneration.node.v1`，抽储量的用途是 `resource.regeneration.reserve.v1`；两者的 `WorldSeed` 为世界种子，分量依次为货物标识 UTF-8 字节数、每个字节各自扩为 `ulong`、该货物抽取序号。序号从零开始，有候选且实际抽取时递增。编码及混合遵循已有 `KeyedRandom` 小端编码；节点索引和储量均用已有整数区间映射。固定向量见 `ResourceStateTests`。

资源事件在生产／人口提交后、生产状态变化事件前发出。同一步先发全部 `ResourceExhausted`，再发全部 `ResourceRecovered`，各组按节点身份、货物身份的 Ordinal 顺序；最后发 `ResourceRegenerated`，按货物身份的 Ordinal 顺序。不可再生项扣到零并删除时发 `ResourceExhausted(Renewable=false)`；可再生项扣到零时发 `ResourceExhausted(Renewable=true)`。`ResourceRecovered` 只在恢复提交将零储量变为正数时发出，`Quantity` 是本步恢复后的储量；其他恢复步及单纯到达上限不发事件。同一步允许先耗尽再恢复，两事件按上述顺序发出。`ResourceRegenerated.Quantity` 是实际新增量。查询不会推进周期、改变储量或发事件，旧读视图保持不变。

本票只提供核心内部情景的模块与独立进程证据，不新增 builtin 开采内容；真实内容装配及四模块交接仍由阶段二 #299 验证。本票未新增命令，控制台手册不适用。

## 所有权与数值

核心负责完整世界的推进、业务裁决、权威状态、确定性记录与重放。内容负责解析、设计合法性、哈希、BOM、部件编号和对应关系；核心不读布局、形状、涂装、显示文本或文件。体素从查询重建场景；宿主负责运行调度、暂停、文件、完整存档和复现包、事件分发及诊断展示。

普通数据是深度不可变值。所有 `ImmutableArray` 必须已初始化，包括合法的空数组；不得包含可变的元素对象。接口对象只能由对应工厂或会话取得，测试替身实现同一接口。普通输入记录有公开构造方式，不依赖实现构造器、反射或友元。

| 量 | 范围、单位和解释 |
| --- | --- |
| 世界身份 | 非零 `ulong`，不同领域有不同类型；销毁后不复用。接口不公开分配器计数 |
| 部件编号 | 非负 `int`，含义由内容的规范遍历决定；核心不按布局重新编号 |
| 定义身份 | 规范化的 `namespace:local-id`；生产与开采使用生产流程身份，回收使用回收流程身份，由 `ProcessRef` 区分 |
| 设计、构成、语义与核心输入身份 | 分别表示自己的身份，不互换；哈希值是规范大写 SHA-256 文本。`CoreInputId` 标识当前外部定义的核心语义，不包含玩家设计目录、本局库或模板实例副本，不等于内容世代 |
| 金额 | `Money(long)`，非负最小货币单位；价格中间运算不经过 `double`。意外溢出按内部故障处理，不设金额越界业务分支 |
| 价格参数 | `BasisPoints(int)`，10000 为 1 倍；敏感度非负，倍率为正且下限不大于上限，价差在 0–9999。无公开分数运算器 |
| 数量、耐久、伤害、总工作量 | 计数及累计值用整数；数量非负、BOM 项正数、总工作量正数。历史耐久可以高于当前上限，内部原样保存；查询只给出有效值和上限 |
| 连续量 | 有限 `double`：米、秒、吨、千牛、工作量进度、速率、电量和劳动力。比例按字段限制在 0–1；不接收 NaN、无穷值 |
| 时间 | 模拟步为整数，60 步/模拟秒。无现实时间戳驱动玩法；正时长不能使用零 |
| 优先级和顺序 | `Priority` 数字越小越先；相同时按设计规定的稳定设施身份、能力种类仲裁。来源、编组、停靠点与自动规则按数组顺序解释 |

`CoreContentProjection` 的目录按类型和规范身份排序，身份不得重复。`MissingReferences` 表示已知缺失，不能用零属性谎称完整；缺失项仍保留原身份。设计中的 `ReturnUnits` 明确大型设备与其部件、结构的归属，返还整件时不得同时返还其内部货物。有效性与差额由内容计算；核心结合当前实例损伤决定返还和实际消耗。

`Identity` 只覆盖外部定义、规则、源模板及它们的核心语义引用；设计派生的缺失清单不参与此身份。`Designs`／`DesignGoods` 是新局所需种子设计、普通读档或重载所需已引用设计与衍生组装体货物的计算资料，不是本局／用户蓝图库，也不参与 `Identity`。固定外部输入下，同一设计哈希或组装体身份的计算结果必须一致；不能用目录增删改变已有身份的规格。源定义变化即使设计哈希不变也须改变 `Identity` 并重算引用设计。新局实际使用的设计和货物进入核心起点，不靠这个身份从当前磁盘补取。

`DesignInput` 把固定外部输入身份、设计业务投影及本设计和嵌套设备所需的全部衍生组装体货物一起交给命令。核心接受建造／改装／立即应用时保存需要的投影，不要求先将蓝图加入 `CoreContentProjection.Designs`，也不改变 `CoreInputId`。过时的 `InputIdentity` 返回 `content_changed`；同一身份下相互矛盾的投影属于调用前提错误。定义型货物从当前外部目录取得，不能借 `AssemblyGoods` 修改定义。

`DesignKind` 分为车辆、建筑、大型设备，公共质量、部件和 BOM 不重复。`InstanceDetails` 分为车辆与建筑；车辆未编组时其 Train 可空。类别不再由一组可空字段推断。

`PlotSize` 为 `Small`、`Medium`、`Large`、`Giant`、`SmallStation`、`LargeStation` 六档，包络与适配规则见[地块与建筑](../design/16-settlements.md#地块与建筑)。核心在新局预建与恢复校验时检查建筑档位能否放进目标地块；旧 `Station` 已移除，旧载荷不能沿用其枚举数值读取。

`ProcessProjection` 共享版本、工作量、电力与劳动力系数、设施标签和地貌要求；`ProcessDetails` 将生产、开采、回收的强类型身份及货物资料分别封闭。开采只有 `Extracts`，其产出隐含为同种货物、同样数量；生产和回收分别持有输入输出。三种流程均可要求地貌，`AllowedTerrainTypes` 为空表示没有地貌要求。

`ResourceGenerationEntry` 保持扁平配置。`Renewable=true` 使用 `RecoveryUnitsPerSecond` 原地恢复至该项生成时的初始储量上限；不可再生资源按 `RegenerationIntervalSeconds` 检查全世界该货物的剩余总储量，低于初始总量乘 `GlobalRegenerationRemainingReserveRatio` 时新增节点资源。触发比例为有限 `double`，范围 [0,1]；新增储量沿用 `MinimumReserve`、`MaximumReserve`，候选与随机规则遵循[资源与开采](../design/10-resources-and-extraction.md#再生事件)。参数由内容提供，不在契约中冻结配平值。

`SettlementTemplates` 是按外部定义身份唯一的当前源目录。世界生成或建城选定模板后，核心在聚居地或建城任务内部直接保存自己的模板业务数据，包含地块、起始建筑、库存及生产计划，不公开全模板或全局副本哈希目录。内容按聚居地实例身份保存对应场景模型设计和地块布局副本；外部模板更新不改已有聚居地或进行中的建城任务。`SettlementView.SourceTemplateId` 仅用于可选来源追溯；`FoundingJobView` 返回预留的最终 `ReservedSettlementId` 与 `SourceTemplateId`。`PlotView.TemplatePlotNumber` 对应内容副本的本地地块号，不能从运行时 `PlotId` 或数组顺序猜测布局。

总工作量与工作系数为整数，进行中的 `ProgressWork` 保留小数。工钱费率是整数金额/工作量；租金费率是整数金额/容量/模拟小时，期限用模拟步。逐件货物、整车、服务费的结算以及两轮备货算法统一见[市场规则](../design/19-markets-and-trading.md)和[潜在消耗](../design/13-production-runtime-and-facilities.md#备货用的潜在消耗)，调用方不得复算。

## 创建、恢复与销毁

### 阶段一生成器

[#309 的批准计划](https://github.com/odradekk/train/issues/309#issuecomment-6134702996)落实为内部 [WorldGenerator](../../Core/World/WorldGenerator.cs)，只接受生成器版本 `stage1-world-v1`。七阶段依次生成地貌、节点、非交叉连通图、占据者、资源、初始铁路及有效起点。无模板的聚居地类型不参选；优先从偏好当前节点地貌或没有地貌偏好的类型中按权重选择，若没有这样的类型，则从全部有模板的类型中选择。类型和模板按规范身份排序后抽取，人口取类型闭区间。模板业务字段复制到聚居地内，特殊库存和生产计划进入初始状态；生物聚落仅保存类型、节点和身份，不生成生物群。地貌特征身份固定为 `lake`、`river`。

初始铁路只用铁轨数范围有效且不经过生物聚落的线路。先检查包含全部聚居地的连通分量能否满足环数与可达空节点数，再按格子 `(y,x)` 顺序尝试删除空节点；仅在诱导子图仍连通、环数及空节点数仍足够时删除，重复整轮直到没有删除。每轮检查计入预算，结果为“包含意义上最少”：去掉任一空节点都会破坏条件，不保证全局最少。精确枚举所有空节点子集的搜索呈指数增长，已在目标规模耗尽预算，因此采用批准决定 12 的删点规则。剩余集合上按长度、身份做 Kruskal，再按随机分数增加成环边；初始铁轨数取配置、线路铁轨上限与 `[1,2]` 的交集。

起点验证非敌对、站点、已建铁路可达的非敌对聚居地和空节点数量，以及是否排除相邻生物聚落。`FullyLoadedPurchaseRoundsPerTrain`、`RequiredAffordableCommissionDesign` 只检查取值、车辆设计种类和引用，不评估经济条件；生成成功不证明经济或战斗开局可玩。初始车队按输入顺序在起点创建，车辆归玩家、满耐久，列车停着且不占站台，`EnteredFrom` 为空。实例和库存不另存位置，查询从所属列车或聚居地派生。

NPC 初始化按 [#528 的批准决定](https://github.com/odradekk/train/issues/528#issuecomment-6198284367)作用于全部非玩家聚居地，包括敌对聚居地。先保留模板特殊库存；`FillInputsAndLivingTargets=true` 时，按当时库存计算一次运行时市场补货目标并补缺口，包含既有食品替代、生产两轮备货与共享容量预算。补货缺口超过真实剩余空间时，按整数缺口同比分配，先向下取整，再按最大余数分配余量，同余数按稳定货物身份顺序；不删除已有库存，不因空间不足重试。

随后按 `OutputReserveHours` 预存产出：设施筛选和潜在速率与市场补货相同，采用 `ProductionQueries.Potential` 中 `Running`、`WaitingForInputs`、`OutputBlocked` 的设施，按货物汇总潜在产量后向上取整，扣除该货物已有库存，只补不足。产出缺口共用补货之后的真实剩余空间，容量不足时沿用上述整数分配。很大的预存时长使用大整数保留比例，避免中间乘积溢出。预存是开局直接放入库存，不推进模拟、不执行生产批次、不消耗原料或扣除节点资源。

`MaximumForSaleVehicles>0` 时，有有效车辆车间的 NPC 聚居地生成满耐久待售实例，所有权等于当地关系，车辆停放当地且未编组。候选先按车间支持档位和市场规则 `VehiclePurchaseLimits.MaximumForSale` 的剩余名额筛选，再按 DesignHash 排序，有放回等概率抽取；每辆占用对应档位名额。可生成名额是车间支持且有候选的各档位上限之和，数量在 `[MinimumForSaleVehicles, min(MaximumForSaleVehicles, 可生成名额)]` 内均匀抽取。没有车间时为零辆；有车间但名额低于最低数量时，立即返回 `config_invalid`，字段为 `Configuration.NpcInitialization.MinimumForSaleVehicles`，不做约束重试。敌对关系的交易访问限制不影响初始生成，也不使用运行时的可收购数作为生成名额。

初始化不是搜索，不增加候选或搜索工作量计数。三项关闭时保留原生成路径、随机键和预算结果；生成器版本仍为 `stage1-world-v1`，核心状态格式为 16，命令格式仍为 2。新增库存和车辆使用既有权威状态、引用、捕获、恢复和重放协议，不新增持久字段。

违反契约取值范围的输入（包括空地貌带）抛 `ArgumentException`；没有带模板的聚居地类型在重试前返回 `config_invalid`；初始铁轨范围与 `[1,2]` 没有交集时，在 `configuration` 阶段返回 `config_invalid`，不消耗候选数或搜索工作量：`MinimumInitialTracks>2` 指向 `Configuration.InitialRailway.MinimumInitialTracks`，`MaximumInitialTracks<1` 指向 `Configuration.InitialRailway.MaximumInitialTracks`；不支持的版本、缺少引用或无法唯一匹配的地貌带返回配置失败。失败用一条 `CoreProblem("generation_failed", 字段路径, null, [类别, 阶段, 候选身份, 世界重试序号, 已用候选数, 已用搜索工作量])` 表达，六项都是字符串，计数使用不变区域十进制；类别为 `config_invalid`、`constraint_unsatisfied`、`budget_exhausted`。同步入口没有中断类别，也没有成功报告字段。预算跨整次准备累计，起点 BFS 与相邻生物聚落的线路扫描也计入搜索工作量；进入阶段时清空候选身份。配置失败和预算耗尽立即返回，约束失败有界重试，失败不消耗正式身份。最大阶段／世界重试数表示首次之外的次数。

诊断的阶段和候选身份使用下表格式；格子坐标为生成时的非负整数，尚未分配正式节点编号。阶段开始及整阶段失败 `World.Nodes`、`World.Connected` 时，候选身份为空字符串。地貌在节点和线路采样期间求值，因此诊断没有单独的地貌阶段；地貌随机键仍使用阶段编号 1。

| 诊断阶段 | 对应生成阶段编号 | 候选身份 |
| --- | --- | --- |
| `configuration` | 生成前的校验与规范化，不使用随机键 | 空字符串 |
| `nodes` | 2；地貌采样使用 1 | `cell:x,y` |
| `edges` | 3；地貌采样使用 1 | `cell:x1,y1/cell:x2,y2` |
| `occupants` | 4 | `cell:x,y` |
| `resources` | 5 | `cell:x,y:namespace:good-id` |
| `railway` | 6 | 删除空节点时为 `rail:remove:cell:x,y`；尚未检查具体候选时为空字符串 |
| `validation` | 7 | `cell:x,y`；尚未检查起点时为空字符串 |
| `initialization` | 生成后的 NPC 初始化，不计搜索预算 | 聚居地所在的 `cell:x,y` |

随机使用 `KeyedRandom` 版本 1：`RandomKey(WorldSeed, "world.stage1.v1.<用途>", [阶段编号, 世界重试序号, 阶段重试序号, 候选分量…])`。无序输入规范排序，车队和编组保持输入顺序；不以正式编号或枚举次序作为候选身份。各用途的候选分量如下，阶段 3 的图算法没有随机键。

| 阶段 | 用途后缀 | 阶段重试序号与候选分量 |
| --- | --- | --- |
| 1 | `noise.elevation`、`noise.moisture`、`noise.river` | 0；八度尺度的 double 位模式、格点 x/y 的 ZigZag 编码 |
| 2 | `node.presence`、`node.x`、`node.y` | 节点重试序号；格子 x/y |
| 4 | `occupant.rank`、`settlement.relation`、`settlement.type`、`lair.type` | 占据者重试序号；格子 x/y |
| 4 | `settlement.template`、`settlement.population` | 占据者重试序号；格子 x/y、类型身份的 UTF-8 字节数及逐字节数值 |
| 5 | `resource.presence`、`resource.reserve` | 0；格子 x/y、资源货物身份的 UTF-8 字节数及逐字节数值 |
| 6 | `rail.cycle`、`rail.tracks` | 0；按端点身份排列的两端格子 x/y |
| 7 | `start.rank` | 0；格子 x/y |

NPC 现车使用独立的 `KeyedRandom` 版本 1，不消费上述阶段的随机流。聚居地身份采用其生成格子坐标：数量键为 `RandomKey(WorldSeed, "world.stage1.v1.npc.vehicle.count", [x, y, 0])`；第 i 辆候选选择键为 `RandomKey(WorldSeed, "world.stage1.v1.npc.vehicle.choice", [x, y, i])`，i 从 0 起。键不依赖正式编号、集合枚举顺序或初始化的调用次序。

新局调用 `PrepareNew`，输入固定种子、生成器版本、完整解析配置、开局资料和核心内容投影。成功返回独立候选；阶段约束不满足或预算耗尽返回阶段、相关字段和失败原因。失败不会替换活动会话。配置中的平衡值来自内容，不以声明中的字段为默认数值或生成算法实现。

普通读档先 `DecodeState`，从 `ICoreCapture.References` 得到设计、定义以及冻结执行资料所需引用。宿主交给内容解析，再用固定候选投影调用 `PrepareRestore`。核心校验并适配状态，不重跑世界生成，不让宿主解析 JSON 查引用。候选的缺失清单与适配报告用于宿主确认；确认只适用于该固定候选。缺失确认不能绕过格式、状态一致性或内容合法性检查。

引用中的 `FrozenRevision` 标明冻结资料的来源，实际冻结参数保存在核心载荷中；普通读档不因此要求安装旧定义。严格重放才要求包内的原输入，不能把两条路径混为一谈。

宿主准备完整活动组并完成必要确认后调用一次 `Activate`。未激活候选的 `Dispose` 放弃候选；激活后会话所有权移交调用方，销毁候选不再销毁会话。会话 `Dispose` 终止写入口、释放自身持有的录制资料；已返回的值、读视图、捕获与导出资料仍有效。重复释放无副作用；对已释放会话调用操作或重复激活是编程错误。

`CoreSessionOptions.RetainedRecordingStarts` 必须至少为 1，在选项进入核心时检查；`PrepareNew` 与 `PrepareRestore` 使用同一选项校验，对非正数抛 `ArgumentOutOfRangeException`，并把宿主给定的数量传入候选。没有关闭录制或无限保留的特殊数值。数量由宿主明确传入，实际容量与起点请求间隔仍由 #322 测量并提交用户确认。

核心对象与内容投影都是托管数据，没有借用数组、引擎资源或文件句柄。宿主仍须按跨层协议处理旧组资源与迟到结果。新局/读档成功后的暂停、技术操作锁及编辑期间暂停由宿主执行，不成为核心命令。

## 读视图、查询与事件

`Read()` 固定最近完整边界，返回位置、持久修订、核心输入身份及故障状态。所有查询和报价都基于这个视图；推进或命令不会改写旧视图。可在其他线程读取保留视图，不要求每次复制整个世界。实现可以共享不可变结构，但不能返回内部可变世界对象。

| 消费者 | 查询方式与结果 |
| --- | --- |
| 宏观地图 | `World`：地图范围、节点/线路、关系、地貌、资源、站台、断点及施工、列车、生物群和战斗标记 |
| 地点场景 | `FindLocation`：整个节点或整条线路上的对象身份；线路距离不作为相等匹配条件，再从同一视图取得详情 |
| 车队与跟随 | `FindTrain`：有序车辆、权威位置、方向、速度、下一站、运行状态与阻止原因；相邻步读视图用于插值 |
| 设计与损伤 | `FindInstance`：设计及构成身份、有效耐久和上限、部件编号、缺失内容、可组合的施工/战斗锁定 |
| 聚居地、生产与库存 | `FindSettlement`、`FindFacility`、`FindInventory`、`FindLease`：统一物理容量、真实访问权、八种生产状态、冻结批次、输出缓冲及供电/劳动力 |
| 人口 | 当前规则、尚未开始/进行中/缺失停用、本期执行规则身份和版本、统计进度、需求与满足率、趋势及最近结算；未结算不伪造零报告 |
| 施工与建城 | `FindJob` 返回施工、线路或建城的明确种类；建城和线路任务不伪造施工设施 |
| 战斗 | `FindCombat`、`FindCreatureGroup`、`FindCreature`、`FindLair`：地点、身份、参战关系、撤退进度、个体生命值等 |
| 交易及经营 | 货物/现车/租约/任务报价，以及当前运行累计与近期净现金流报告；不是利润表 |

`FindInstance` 返回的 `PartDamageView.DefinitionMissing` 与列车能力、库存容量共用部件缺定义规则，每次查询都结合保存设计的缺失标记与当前读视图的内容投影判断。普通恢复不将查询标记写回保存设计，也不改写部件耐久记录。

`Lookup.Missing` 表示对象已不存在，不自动换选另一个对象。UI 名称与设计展示资料按内容身份向内容读取。人口查询的当前劳动力已由核心计算；本期执行版本不等于当前发布版本，调用方不能从历史规则重算劳动力。消费发生在统计结算之前，满足率和周期摘要来自同一已提交边界，见[人口规则](../design/18-population-and-labor.md)。

查询只公开调用所需结果，不公开消费／人口余量、调度倒计时、完整冻结规则或批次功耗与劳动系数。批次版本、冻结产出、总量与进度、`CurrentSpecificationDiffers` 及仓储容量划分有明确业务用途，继续公开。内部规则用模块测试或推进后的可观察结果验证，不为测试增加普通查询字段；存档仍完整保留内部权威状态。

场景从 `WorldSummary.WorldSeed` 取得当前世界的原始种子，普通读档与严格重放均保留它，不要求宿主解析核心载荷。`WorldSummary.RailwayRule` 是本局所选铁路规则身份，体素据此查询内容的曲线半径与坡度资料，不从规则目录猜选项。`SimulationTime` 公开既定的每秒 60 步及每步秒数，这是构建常量，不是可配置的另一套时钟。以上查询和下述列车显示状态由[体素公开契约](https://github.com/odradekk/train/issues/289)补齐。

`TrainView.PlatformQueueEnteredStep` 为当前有效站台申请首次入队的模拟步，可空。仅当列车位于节点、尚未进入 Departing、当前计划需要友方站台、尚未持有站台预约且已有申请队龄时非空；此条件与查询中的 `platform_wait` 阻塞一致，包含尚在 Approaching 的申请者。站台分配依次按该值、`TrainId.Value` 升序决定；不同入口共享同一节点的站台申请顺序。出线等待、已获站台及未申请列车返回空。该字段投影已有存档队龄，不新增权威状态或改变存档格式。

`TrainPosition.Node(NodeId Id, EdgeId? EnteredFrom, EdgeId? NextEdge, NodeTravelPhase Phase, double DistanceMeters, int? PlatformNumber)` 是节点内唯一权威位置；`NodeTravelPhase` 为 `Approaching / Waiting / Docked / Departing`。删除 `NodeTransitView` 和 `TrainView.NodeTransit`，内部也不另存转场。`EnteredFrom` 为实际进入线路，无进入历史时为空；`NextEdge` 为已决定的下一线路，不承诺预留铁轨，体素只能消费它而不重算路径。`DistanceMeters` 为中心沿节点路径的米数，`PlatformNumber` 为可空预约站台号。`NodeView` 仅增加 `ApproachLengthMeters`、`PlatformLengthMeters`、`DepartureLengthMeters`，不公开限速、总长或停站点；三段尺度和阶段转换见[节点与站台](../design/06-railway.md#节点与站台)。

`TrainPosition.Edge.DistanceMeters` 表示整列长度中心，`TrainView.OrderFacing` 表示车辆列表哪一端沿最近实际行驶方向在前。编组默认首端在前，线路上倒车及节点原路返回时切换标记；节点驶入其他线路时保持，等待中保留最近实际状态。两项均随存档恢复，场景不自行记一份持久朝向。标记不参与牵引、选路或战斗计算；中心间距、成员被毁后的收拢及整列在节点切换的规则见[铁路位置](../design/06-railway.md#位置)。

### 当前列车运行

列车能力来自有序成员的保存设计及当前耐久。长度为各车 `LengthCells × 0.5` 米之和，质量为车辆设计质量与已知库存货物质量之和；耐久大于零且定义未缺失的部件提供完整能力。部件定义是否缺失按当前内容判断，与库存容量采用同一规则；恢复不改写保存设计。牵引相加，最高速度取有效动力部件额定速度的最小值，加速度为牵引除以质量。定义型货物使用当前 `Goods`，组装体货物使用世界保存的衍生投影。库存定义缺失时，`MassTons`、`Acceleration` 只表示已知部分，`Blockers` 必含 `content_missing / Train.Inventory`，主体为列车；相关身份使用 `assembly:`、`composite:`、`voxel:` 前缀并按 Ordinal 排序，不能把这些数值当作完整质量和加速度。

每次从静止出发共用检查，依次返回 `control_missing / Train.Vehicles`、`traction_missing / Train.TractionKilonewtons`、`content_missing / Train.Inventory`、`overloaded / Train.Acceleration`。加速度等于规则下限允许出发。手动启动不满足时拒绝；停留后不满足时为 `Waiting`，每步重试。已经行驶的列车按已知质量继续到下一个停车点。铁路规则按 `WorldSummary.RailwayRule` 从当前投影读取，不保存另一个规则副本或添加缺失兜底；引用与普通恢复适配责任沿用世界生成路径。

每步为 1/60 秒，保持加速、巡航、制动三段精确积分。对距离 d、目标速度 u 的每个约束使用 `v² ≤ u² + 2bd`，取最严格者；非零终速峰值为 `v_peak² = (2abd + b·v0² + a·u²) / (a + b)`，受最高速度限制。节点内取车辆能力与站区限速的较小值，前瞻包含节点实际长度；入站前满足限速。Station、计划终点、停车和原路返回在 S 停稳，下一步才开始倒计时；途经越过 S 时消费目标并保持速度出站。正常制动及预测失败时的强制停车例外见[速度](../design/06-railway.md#速度)。

选路权重为线路长度除以最高速度，用时相同时按完整线路身份序列的字典序比较，与邻接枚举顺序无关。敌对聚居地及生物聚落可作当前目标，不能作中间节点。至少一条铁轨没有断点且不在修复才可走，选路不考虑拥堵；加轨施工不关闭原有铁轨。无路时停在节点，活动为 `NoRoute`，原因 `no_route / Train.Plan` 带目标节点身份，每步重新选路。原路返回先在 S 停稳，沿 `EnteredFrom` 反向起步时切换正反标记，断点引起的线路倒车也切换；战斗撤退仍归战斗阶段。

按 [#311 批准计划](https://github.com/odradekk/train/issues/311#issuecomment-6139384407)，入线及站台仲裁基于步开始时已提交的状态，同轮后授予的请求看到先授予的占用和入口空间；本步释放的铁轨、站台下一步才可用。等待按首次到达步、稳定 `TrainId` 排序，不由列车数组顺序决定。方向锁从在轨列车的位置派生，空轨解锁；选轨先取同向已锁且入口间距足够的铁轨，再取空轨，同类取最小轨号。有对向等待时，本方向不再进入本方向锁定轨；只在开放铁轨清空时记录交让，恢复校验拒绝断轨上的交让；未兑现交让优先给对向等待者，较早的本方向等待者不能抢先。

入口及同轨跟车的中心间距至少为两列长度之和的一半加内容最小间距，前车步开始时的位置作为后车制动障碍；不超车、不换轨。进入同向已锁定铁轨还要求入口到跟车界限的距离不小于当前速度的制动距离 `v²/(2b)`；不足时该轨不可选，可取空轨，否则在节点等待并写零速度，下一步从静止检查入线。需要站台时按步开始状态预测空闲站台与前方队列，以 S 或 W 为制动目标；下一线路不可进入时提前向 L 制动。预测不登记首次排队步，只有中心在 L 正式选轨失败才登记 `QueueEnteredStep`；预测失效且来不及制动时才强制停车。规则不变且没有断点的正常运行中，线路上每步减速不超过制动减速度乘步长。

`RailwayRuleProjection` 提供 `ApproachLengthMeters`、`DepartureLengthMeters`、`NodeSpeedLimitMetersPerSecond`，均须为正有限值，初值为 80 米、80 米、15 米/秒；铁路规则文件 `schema_version` 为 2。`SettlementTemplateProjection.PlatformLengthMeters` 为非负有限值，供节点站台段使用；模板有站点地块时必须大于 0，没有时为 0。

强制停车例外指来不及制动时，在该步写入零速度，并将列车停在应停点。到达线路端点或节点出口 L 的步骤截断于端点，舍去该步剩余时间。

节点尺度或限速变化不使 `PublishContent`、`PrepareRestore` 拒绝。静止在 W、S 的列车移到新对应点；`Departing` 阶段且活动为 `Waiting`、已有 `QueueEnteredStep` 的列车正在 L 等铁轨，移到新 L；其他 `Departing` 列车（包括失去动力停在中途或在 L 为 `Stopped` 的列车）及行驶中的列车保留距离并截到新 L 内，不依赖旧内容投影；必须停车却越过新 S 的进站列车置于 S、速度归零；不必停车且已越过新 S 的进站列车转为 `Departing`，并依序消费当前节点的途经点；超出新限速者按制动减速度减速。不新增保存字段。P 取聚居地保存模板副本的 `SettlementTemplateProjection.PlatformLengthMeters`，由 Content 按设计 16 的站点包络计算，Core 不写包络常数。

铁路规则变化后，已在线的同轨间距可暂时低于新规则值。`PublishContent` 和 `PrepareRestore` 照常接受并保留位置；后车速度归零、原地保持，直到前车拉开到规则值后恢复跟车，间距不再缩小。这是规则变化时的强制停车例外，不新增保存字段或拒绝代码。新车入线仍按当前规则检查入口间距；规则不变时，每个已提交步都保持规则要求的间距。

节点内每列车各走一条站内线，互不跟车、互不阻挡。预约站台号就是该列车的站内线；无站台号的列车所走的线由场景分配（#443）。

非敌对聚居地的 Station 停留在进入节点时正式预约，按申请步、稳定 `TrainId` 仲裁，从 1 起取最小空闲站台号；空节点、敌对聚居地、生物聚落及途经不预约。预约后的站台号保留至驶出 L 或删除、解编，停车、换计划和出站等待不释放，占用查询按预约派生。在 S 停稳且获所需预约的当步进入 `Servicing`，余时设为完整停留步数，下一步开始递减；索引仍指当前 Station，余时到零后消费；同节点连续 Station 沿用预约。站台数减少不重排已有预约，已有编号可以超过新容量，占用可暂时超过容量，新申请等待。越 W 但尚未越 S 的进站列车，以及静止在 S 的列车，因换计划或启动新增本站 Station 时立即按产生该需求的模拟步申请；没有可用站台时不拒绝命令，制动到 S，以 `Docked`、空站台号、`Waiting`、`platform_wait` 等待，获预约后开始停留。已越 S 的 `Departing` 列车（含停在 L 等铁轨的）保留已定驶出线路，先驶出，返回本站时再预约和停留。

`Stopped` 表示手动停车、无计划或单次完成；`Running` 表示在线路上或节点内行驶；`Servicing` 表示 Station 停留计时；`Waiting` 表示静止出发能力不足、等铁轨或等站台；`NoRoute` 表示当前目标无路：进站列车停在 S，阶段为 `Docked`；从 W 生成或编组后出发的列车停在 W，阶段为 `Waiting`。战斗活动尚不产生。等铁轨的原因是 `track_wait / Train.Position`，附线路身份；等站台是 `platform_wait / Train.Plan`，附节点身份，均通过 `Blockers` 与 `TrainActivityChanged.Reasons` 提供，持续等待不重复发活动事件。`Phase` 与活动分别表达位置阶段和业务状态；出站等待铁轨仍为 `Departing`，新列车在 W 为 `Waiting` 阶段和 `Stopped` 活动。`NextEdge` 表示已决定线路，出站等待时仍可保留，不因活动为 `Waiting` 清除。

有断点的整条铁轨禁止新车进入。已越过断点的列车继续驶出；断点前受阻组按跟车约束停车，等旧方向未受阻列车离开该铁轨后整组同时反向，切换 `OrderFacing` 并回出发节点重新选路，保持同轨同向且不超车。近距离断点来不及制动时，中心停在断点位置、速度归零；中心恰在断点上算受阻，已因该断点倒车的列车除外。`ReturningFromBreak` 记录实际掉头的断点，避免零牵引、中心等于断点时再次翻转；返程再遇断点则停车等待修复，不再反复掉头。

`TrainArrived` 在每次实际从线路到达节点时发出，包括中间节点；同节点重复目标和在当前节点启动不会重复发出。到达事件先于该到达引起的 `TrainActivityChanged`；活动变化才发活动事件，反复等待不重复通知。编组的 `TrainCompositionChanged` 携带新列车及完整有序成员；解编携带被删除列车及空成员数组。`CurrentRun` 返回当前计划的运行累计，`RecentReports` 从全局近期报告按列车筛选；结束累计时发 `RunReported`，具体边界见下文“自动交易与运行报告”。交战规则由完好武器派生：有武器为 `(ArmedFirst, ArmedOnly, Engage, 0)`，否则为 `(ArmedFirst, ArmedOnly, Retreat, 0)`；优先级和阈值是暂定值，由战斗阶段确定。

| 命令 | 当前条件及领域拒绝（代码 / 字段） |
| --- | --- |
| `FormTrain` | 有序成员非空且唯一：`composition_empty`、`vehicle_duplicate / OrderedVehicles`；车辆存在、玩家自有、未编组、在同一非敌对聚居地停放：`object_missing`、`not_owned`、`vehicle_in_train`、`vehicles_not_colocated`、`access_denied / OrderedVehicles`；新列车或库存编号耗尽为 `identity_exhausted / World.Next`。成功在 W 创建阶段 Waiting、活动 Stopped、无线路引用和站台号的列车及空库存，回执给出二者身份，保留车辆输入顺序 |
| `DisbandTrain` | 在节点且速度为零：`train_not_stationary_at_node / Train.Position`；节点不是聚居地：`settlement_unavailable / Train.Position`，聚居地敌对：`access_denied / Train.Position`；库存必须为空：`inventory_not_empty / Train.Inventory`。停留、等待、无路的静止列车也可解编；删除列车、计划和库存，成员原地停放，身份不复用 |
| `ReplaceRunPlan` | 非空：`plan_empty / Plan.Stops`；目标存在：`object_missing / Plan.Stops[i].Node`，主体为该节点；循环目标不能全部相同：`plan_degenerate / Plan.Stops`。Station 可设在任何现存节点，空节点、敌对聚居地及生物聚落均照常停留计时。同一 Station 内同一货物重复是业务拒绝 `plan_duplicate_good`，字段为 `Plan.Stops[i].GoodsRules[j].Good`；与目标存在检查一起按停靠点顺序进行，每站先检查目标存在，再检查重复货物，均在 `plan_degenerate` 之前。索引置 0，放弃剩余停留，保持启停意图；运行在线路上先到前方节点，保留节点内位置和预约，进站可重选下一线路、出站保持已定线路；出站阶段（含停在 L）依序消费新计划开头指向当前节点的途经点，本站 Station 仍先驶出再返回；其余途经目标越 S 才消费，停车列车保持停车 |
| `StartTrain` | 先对所有列车检查计划存在且未完成（`plan_missing / Train.Plan`、`plan_completed / Train.NextStopIndex`）；随后非 `Stopped` 时接受并清除尚未完成的停车请求，其余业务状态保持不变；仅在 `Stopped` 时检查上述静止出发条件。暂停停留沿用已有预约恢复余时，否则沿正常节点阶段继续；缺预约时申请，不足则等待 |
| `DebugAddRailBreak` | 调用前提：线路身份非零、轨号为正 `int`、距离为有限非负米数，否则在取得编号前抛 `ArgumentException`。线路不存在拒绝 `object_missing / Edge`；轨号超过现有铁轨数或距离超过线路长度拒绝 `invalid_state / Break`，两者主体均为线路；断点编号耗尽拒绝 `identity_exhausted / World.Next.Break`。成功增加断点、推进编号并发出 `RailBroken`，同轨未兑现交让清除 |
| `StopTrain` | 静止时原地停车并暂停停留；进站制动到 S，来不及则强制停车；已过 S 继续驶出并在下一节点停车；在线路上请求停在前方节点 S。重复请求保持同样业务状态 |

后四个命令不存在的列车统一为 `object_missing / Train`，主体为该列车；编组中缺失车辆的主体为该车辆实例，非自有用 `not_owned`，聚居地敌对用 `access_denied`。正常拒绝仍前进命令编号和边界序号并录制，无事件且业务状态不变。单次计划完成后保留计划、索引为空，须替换计划才可再次启动。通常活动为 `Stopped`；出站列车换计划并消费完开头当前节点的途经点后，若仍在行驶，保留已定驶出线路，设置 `StopRequested = true`，继续驶出并在下一节点 S 停稳后转为 `Stopped`。已静止在 L 的列车原地转为 `Stopped`，清除停车请求和排队步。这类仍在行驶的列车若到 L 正式选轨被拒绝，也立即原地转为 `Stopped`，清除停车请求和排队步，不转为 `Waiting`。这些停车均保留预约和下一线路；预约仍到实际驶出、删除或解编时才释放。相邻重复目标依序消费，零停留 Station 仍是停车点。

普通查询无副作用。报价不锁价格、库存、容量或资金，也不是提交凭证。只读派生计算若遇内部故障，返回 `CoreResult.Faulted` 及该视图的位置，不记录命令或修改任何活动会话；不能用旧视图的错误冻结新局。命令执行重新裁决时发生内部故障，仍按下节冻结。这一区分也适用于独立新局/读档准备与活动运行。

`Submit` 和 `Advance` 返回本次产生的 `EventEnvelope`，按 `Position`、该位置内的 `Ordinal` 排列。宿主在完整边界之后分发；核心不提供回调、消费者游标、补发窗口或另一个订阅系统。正常拒绝和故障回滚不返回未提交的业务事件；多步调用仍返回此前已经完成步骤的事件。

事件用于通知和瞬时表现。射击与命中事件保存武器部件、目标部件/生物及地点；实例表现引用保留当时设计，即使对象在同一步被删除也能解释。生物查询和表现引用保留种类身份 `Definition`，不保存设计；体素通过与核心视图或事件配对的固定内容视图调用 `FindCreature(Definition)` 取得当前种类外观。持续损伤、人员和库存以查询为准。后来打开场景只重建当前状态，不补演遗漏的射击。战报由宿主按[界面规则](../design/08-window-and-ui.md)保留，不等于核心录制或存档。

## 库存与租约

`FindInventory`、`FindLease` 的身份必须非零；对象不存在返回 `Lookup.Missing`。结果及其集合可以保留，后续命令、推进、发布或会话释放不改变旧结果。库存只有按 `GoodId` 计数的栈，三种身份不相互合并。缺定义货物保留身份和数量、照常占容量，`DefinitionMissing=true`，装卸拒绝 `content_missing / Good`。库存质量只包含已知定义的部分，不作为容量约束。

容量来自有效结构耐久及部件耐久均大于零、定义未缺失的存储部件。列车汇总成员车辆，聚居地汇总所有建筑，不按建筑类别限制。城内统计只含聚居地库存与当地租用库存，不含停靠列车。设物理容量为 P、有效租约额度合计为 A、到期租约货物合计为 E，聚居地库存容量为 `max(0, P-A-E)`。有效租约扣完整额度，不重复扣其中货物；到期只保留实际货物占位。

`InventoryView.Used` 为整件数量合计，`Reserved` 为预留容量合计，`Reservations` 保留 JobId、容量和预计返还。列车 Available 为 `max(0, Capacity-Used-Reserved)`；城内库存还取与 `max(0, P-全城Used-全城Reserved)` 的较小值。租用库存 Capacity 保留合同额度；非 ReadWrite 库存 Available 为零，到期显示的额度不构成继续占有空空间的承诺。能力下降不删货、不删预留，超容禁止新增入库，合法取出仍允许。

可租比例额度为 `floor(P×RentableCapacityRatio/10000)`，只计有效租约。实际承诺空间为聚居地库存的 Used+Reserved，加各有效租约的 `max(合同额度, Used+Reserved)`，再加各到期租约的 Used+Reserved；可租容量为比例剩余额度与实际未承诺空间的较小值，各项最低为零。到期预留继续占位。续租排除自身旧额度及占位，再检查原容量和自身 Used+Reserved，不重复计入；不自动缩减额度。

库存访问权独立于直接装卸授权。玩家列车、自有聚居地库存、有效租用库存及非敌对 NPC 聚居地库存显示 ReadWrite；到期租库为 WithdrawOnly；敌对聚居地库存和租库为 Frozen，优先于到期。冻结不释放额度、货物或预留。直接装卸只允许列车与同地自有聚居地库存或玩家租库之间移动，NPC 库存交易归 #384。

装卸要求非零身份、合法货物身份及正数量，否则取得编号前抛 ArgumentException。合法输入依序检查：对象存在（object_missing / Train、Settlement、LocalInventory）；列车所有权（not_owned / Train）；同地（train_not_at_settlement / Train.Position）；当地库存归属（invalid_state / LocalInventory）；冻结（access_denied / LocalInventory）；NPC 库存（not_owned / LocalInventory）；卸入到期租库（access_denied / LocalInventory）；在 Station 停留中（`Servicing`）、停稳在 S（`Docked` 且速度为 0）并持有站台预约（platform_required / Train.Position）；缺定义（content_missing / Good）；来源数量（insufficient_goods / Quantity）；目标及全城容量（insufficient_capacity / Train.Inventory.Capacity 或 LocalInventory.Capacity）。返回首个拒绝，不抢占站台，不新增站台状态。成功发一个 GoodsTransferred，Action 为 Load/Unload，双方库存和数量为实际完整转移，ActualAmount 为零；拒绝不改双方货物或资金，无业务事件。

Rent 要求非零聚居地身份和正容量，Renew 要求非零租约身份，两者期限必须为正；AcquireLease.MaximumPayment 必须非负，否则取得编号前抛 ArgumentException。命令解码拒绝负付款上限，代码为 invalid_state，字段为 /maximum_payment。只允许在非敌对 NPC 聚居地租用；敌对拒绝 access_denied / Request.Settlement，自有拒绝 invalid_state / Request.Settlement。其后依序检查市场规则（content_missing / MarketRule）、期限必须为当前 `LeaseDurationTicks` 的整数倍且倍数为 1 至 `MaximumLeaseDurationMultiple`，续租后的剩余租期也不得超过两者乘积（非整数倍、倍数超限、续租后剩余超限均为 invalid_state / Request.DurationSteps）、容量（insufficient_capacity / Request.Capacity）、付款上限（price_condition_failed / MaximumPayment）、钱包（insufficient_money / Wallet）及新租身份（identity_exhausted / World.Next.Lease 或 World.Next.Inventory）。对象不存在使用 object_missing / Request.Settlement 或 Request.Lease。

租金单位为最小货币整数单位/容量/模拟小时，总额为 `ceil(单价×容量×期限步数/216000)`，整份只向上取整一次，不逐期取整，无最低一元。期限上限和续租剩余期限的比较不会因合法规则值或请求期限过大而溢出。仅在规则存在、期限合法且容量检查通过后计价；到期步加法、checked Int128 计价中间计算和结果转换溢出沿用内部故障。有效续租从原到期步追加，到期续租从当前完成步起算，沿用容量并使用当前规则单价。LeaseView.Paid 是累计实际付款，LeaseAcquired.ActualPayment 为本次付款。QuoteLease 不锁资源；规则缺失、期限不合法或容量不足（含续租自身货物及预留占位放不下）时 Total 为空，其余关系、钱包等拒绝仍给总价和 Blockers。付款上限由 AcquireLease 重新检查，报价请求没有付款上限参数。报价计算故障返回 CoreResult.Faulted，不冻结活动会话。

从完成步 n 推进到 n+1 时，期限落在该边界的租约按 LeaseId 升序发 LeaseExpired，事件排在同一步铁路事件之前。到期状态由完成步派生，恢复不重复发事件，空租约及其库存不自动删除。市场规则缺失不暂停到期，也不禁止其它条件合法的手动装卸。规则更新不追改已付金额、合同额度或到期步。

预留随库存保存、恢复并参与检查点。解码检查正容量、合法正数返还及合计不超过容量、JobId 非零且全世界唯一；预计返还货物定义加入引用清单。预留校验不检查任务是否存在；当前车辆制造与修理不创建预留，创建、释放预留及相关任务交叉检查归 #332 等后续任务。本票不新增控制台命令，单命令手册和索引更新不适用。

## 市场报价与手动交易

#384 按[批准决定 D1–D9](https://github.com/odradekk/train/issues/384#issuecomment-6194386767)实现本节。玩法公式见[市场与交易](../design/19-markets-and-trading.md)，本节明确报价、执行及当前阶段边界。没有新增持久字段：钱包、货物、实例所有权、设计与耐久和唯一停车归属沿用现有编码；核心状态格式为 16，命令和重放格式仍为 2。目标、留货、价格、待售列表和名额不另存。#528 已接入三个经济初始化配置，生成初始库存和有限现车；初始化规则见上文“阶段一生成器”。现车也可由玩家卖给当地形成。自动规则和报告见下文“自动交易与运行报告”，委托采购见上文“NPC 车辆委托与施工报价”，真实经济内容及四模块交接归 #299。

### 备货与查询

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

### 货物报价与执行

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

### 现车

`VehiclesForSale` 是当地 NPC 拥有的停放车辆，损伤、缺定义或建造能力失效不使车辆消失。`VehiclePurchaseCapacities` 对非敌对 NPC 列出五个尺寸档位：`ForSale` 为真实待售数，`Limit` 为当前配置上限（未配置为 0），`Available` 为有该档有效建造能力时的 `max(0,Limit-ForSale)`，否则为 0。自有聚居地返回空待售和空名额数组；敌对聚居地保留待售数和配置上限，各档 Available 为 0；缺市场规则时名额数组为空，并由 Market.Rule 明确报告缺失。上限降低不删车，买走释放名额。

报价与执行依次检查聚居地／车辆存在（`object_missing / Seller`、`Buyer` 或 `Vehicle`），关系（自有 `invalid_state`，敌对 `access_denied`，字段为 Seller/Buyer），方向所有权（Buy `not_for_sale / Vehicle`，Sell `not_owned / Vehicle`），同地（`vehicles_not_colocated / Vehicle.Location`），已解编（`vehicle_in_train / Vehicle.Details.Train`），定义完整（`content_missing / Vehicle`），市场规则（`content_missing / MarketRule`）。Sell 再检查 #501 的有效施工部件车辆档位（`capability_zero / Buyer`）和名额（`insufficient_capacity / Buyer.VehiclePurchaseCapacity`）。最后检查总限价及 Buy 钱包，代码和字段沿用货物交易。

清空货物由既有 `DisbandTrain` 的非空货物／预留拒绝保证；未编组车辆没有单独库存。当前没有可触发的施工／战斗锁权威状态，不添加恒为假的锁分支；现车交易的锁定条件留待 #332/#343 接入。

估值 A 按 `StructuralBaseValue × 有效结构耐久/结构上限 + Σ PartProjection.BaseValue × 有效部件耐久/部件上限` 精确汇总，有效耐久取历史绝对值与当前上限的较小值，被毁部件贡献 0，不重复计算部件对结构耐久的贡献。各项整数分数先汇总、再乘整车万分比倍率，付款只 ceil 一次，收款只 floor 一次，无最低 1。报价条件不足返回 Total 空；限价或钱包不足仍提供已算出的总价。成交只改变钱包与原实例所有权，发 `VehicleTraded`，车辆仍在原地、原停车列表、未编组，设计与损伤不变。

四种新增命令仍用输入格式 2，规范输出字段顺序为：

| kind | 字段顺序 |
| --- | --- |
| `market.buy_goods` | `input_format_version, kind, train, settlement, local_inventory, good, quantity, maximum_payment` |
| `market.sell_goods` | `input_format_version, kind, train, settlement, local_inventory, good, quantity, minimum_receipt` |
| `market.buy_vehicle` | `input_format_version, kind, seller, vehicle, maximum_payment` |
| `market.sell_vehicle` | `input_format_version, kind, buyer, vehicle, minimum_receipt` |

身份为非零 ulong，数量为正 long，金额条件为非负 long，good 沿用装卸的三种规范货物编码。解码接受字段换序，拒绝缺失、多余、重复字段及非法数值，返回相应 JSON Pointer 的 `invalid_state`；不支持的版本或种类为 `unsupported_format`。成功和正常拒绝都进入录制，普通恢复与严格重放沿同份状态和命令实现执行。

## 命令与执行

命令输入是封闭的类型目录，不提供字符串路由加任意 JSON 的扩展口。全部命令在协调线程同步 `Submit`，包括 `FoundSettlement` 和携带完整固定投影的 `PublishContent`；核心可以在内部使用候选保证原子性，不公开命令准备对象或另一个完成入口。控制台先在宿主解析参数，再调用相同命令，不访问核心实现。

运行计划的 `PlanStop.Waypoint` 只有途经节点；`PlanStop.Station` 才携带停留步数及有序货物规则。`AutomaticGoodsRule` 共用货物、库存和目标数量，`AutomaticGoodsAction.Buy`／`Sell` 分别携带可选逐件付款上限／收款下限，空值表示无限价；`Load`／`Unload` 不携带价格条件。成交结果和报价方向继续使用 `TradeAction`。

| 领域 | 公开命令及限定 |
| --- | --- |
| 铁路 | 编组/解编、有序运行计划、启动/停车、现有线路加轨、断点修理、自动修理来源；没有新建任意图边或拆轨命令 |
| 贸易 | 足量货物买卖、免费装卸、实际车辆买卖、租赁/续租；手动总额付款上限/收款下限，自动规则逐件限价 |
| 自动交易 | 放在运行计划的有序停靠规则内；每站每货只一项动作，关系改变不会改写成另一种动作 |
| 生产 | 设施能力、带类型流程、启用状态和优先级；无批次取消退款、库存凭空补充或 NPC 生产规划 AI |
| 建设 | 自有任务或原子 NPC 委托，目标设计/地块/实例、显式来源顺序、执行设施；建造、改装、修理、拆解及任务优先级 |
| 编辑 | `ApplyInstanceDesign` 只换完整合法且构成未变的设计；构成变化走整份改装，无混合部分应用 |
| 聚居地 | 空节点建城、宣战、明确占领；无外交恢复、人口搬迁或配给开关 |
| 战斗 | 列车/聚居地交战规则、列车撤退和集中攻击；无直接指定伤害结果的玩家命令 |
| 内容与开发 | 固定候选的 `PublishContent`；开发构建允许真实断点调试命令，其他调试观察在宿主使用正常查询和驱动 |

`JobRequest` 不接受草稿或运行状态。建造的大型设备以`DesignKind.LargeEquipment.ManufacturedGood` 入库，不创建车辆。委托只允许车辆建造、改装、修理，设施必须有资格；受理顺序决定 NPC 队列，不能用请求的优先级插队。拆解可以主动暂停，其他三类没有主动暂停/取消命令。续租沿用原容量并重新校验，不自动部分缩减；相应容量、合同和时序遵循[租赁规则](../design/12-inventory-and-storage.md#租用库存与共有容量)。

提交结果分两类：

- `NotAccepted`：冻结、命令身份耗尽或无效的预备基准等使入口不再接收，不取得命令编号。
- `Completed`：回执包含编号、前后位置及 CommandDecision：Accepted 携带新建身份与模板副本清单，Rejected 携带业务问题，Faulted 携带稳定故障详情；不再另存一份可矛盾的故障状态与空值字段。正常拒绝不改业务状态，但前进命令编号和应有序号，留在录制中。

命令和每个模拟步各自原子。命令内部故障回到命令前业务边界并冻结，失败步骤回到该步前边界并冻结；先前完成步骤不回滚。诊断堆栈不进入确定性摘要。`Advance(steps)` 返回实际完成步数和事件，首个故障停止，不私自重试。暂停和倍速不影响同一命令在同一边界的结果。

`CoreProblem.Code` 是稳定、非本地化代码。共同代码为 `object_missing`、`content_missing`、`invalid_state`、`not_owned`、`access_denied`、`insufficient_goods`、`insufficient_capacity`、`insufficient_money`、`price_condition_failed`、`busy`、`frozen`、`identity_exhausted`、`content_changed`、`unsupported_format`、`incompatible_replay`、`generation_failed`；字段路径及相关身份补足定位。更多领域原因在本模块扩展，不允许调用方解析诊断文本。非法枚举、未初始化数组、不满足声明数值范围或使用已释放/已消费的对象属于编程错误，可抛 `ArgumentException`、`ObjectDisposedException`、`InvalidOperationException`。正常玩法拒绝不抛异常。

## 内容候选与同步提交

内容加载、资产检查和设计计算先在固定输入上完成；宿主核对操作及基准，并在协调线程调用 `Submit`。调用开始前可以取消；同步执行期间不再接受取消。核心在返回前落实接受、拒绝或内部故障及相应记录，不存在取消后须另交回的预备故障。耗时尚未测量；若实际阻塞不能接受，再有针对性调整，不预建后台命令协议。

- 建造、改装与立即应用随命令带入 `DesignInput`；对应完整设计先保留在固定内容候选中，接受后才激活。拒绝不改变本局内容，但录制仍须保留这次输入的依赖。
- `PublishContent` 携带候选 `CoreContentProjection`，核心先校验投影自身的数组、身份及当前读取字段，再比较输入身份，在现有命令事务内准备不可变世界、检查引用并一次提交世界与投影；接受后宿主立即 `Activate` 内容候选，再通知或继续推进。正常拒绝只改变应有计数和拒绝记录，旧业务和有效内容不变；内部故障保留旧业务边界并冻结。
- `FoundSettlement` 接受结果的 `SettlementCopies` 返回预留的最终所有者、源模板和地块对应。宿主从同一固定内容调用 `WithSettlementCopies`，纯内存关联已完成校验的模板副本；配对完成前不分发事件、不保存、不继续推进。来源或地块错配是编程错误，不在此阶段重新进行合法性裁决。若意外故障中断关联，宿主停止该活动组的使用并报告，不继续运行半配对状态。
- 新局仍为独立候选，先用其 `References.Templates` 调用相同复制操作，完整配对后激活。普通读档沿用内嵌副本，不用当前源模板补造。

内容的 `Activate` 和模板关联不做 I/O、解析或正常可失败业务计算。开发创作先逐文件写入，再按普通重载走上述顺序；磁盘与内存不再承诺整包事务，见[包写入与重载](editing.md#创作模式包写入与重载)。纯表现更新或有效内容无变化不制造核心命令。设计目录增删本身不改变外部输入身份，不能据此制造一条重载命令。提交后的通知／表现资源错误不回滚业务。

当前世界发布由内部 [WorldContentPublication](../../Core/World/WorldContentPublication.cs) 实现。检查集合为 `WorldReferences.Create` 的全部定义及 `Dependencies` 展开的依赖，包括保存的生成配置；源模板仅作副本来源说明，不检查源目录，也不重建聚居地。货物、流程、规则、聚居地类型、生物聚落类型和生物直接检查候选目录，生成配置选中的规则按对应类别检查，不能用另一类别的同名规则替代；地貌没有核心投影目录，按候选缺失标注检查。候选显式缺失、设计／返还单元的缺失清单及部件 `Missing` 沿用引用清单的 `WasMissing` 规则。保留的缺失须由候选显式标注，且发布前同一定义、同一字段也已 `WasMissing`；目录查不到且候选未标注时一律按 `content_missing` 拒绝，即使原来已缺失。借已有占位新增缺失字段仍拒绝，允许保留或部分补回原缺失。

候选必须提供世界保存的设计和衍生货物，新设计引用的组装体货物也须有投影；缺漏返回 `content_missing`，字段为 `World.Designs[<哈希>]`、`World.DesignGoods[<货物键>]` 或引用清单的具体字段。同哈希设计固定构成哈希、`Kind` 的类别／尺寸／建筑种类与设施标签／大型设备入库货物，以及部件编号与定义对应；数组按规范顺序逐项比较。改变这些关系或返还单元引用不存在的部件编号时返回定位到设计或部件的 `invalid_state`。耐久上限、能力、质量、价值、结构货物、BOM 和返还规格等可以更新；`CanConstruct=false` 不单独拒绝已有实例。全部问题按字段路径、定义键的 Ordinal 顺序返回，附相关身份，可定位到节点、线路、聚居地、生物聚落或库存时填写主体。

接受后规范保存当前设计与货物投影，重算定义依赖，并与解码器共用规则重算节点站台数。实例耐久绝对值、身份、库存、资金、图其余字段、模板业务副本和列车计划／停留进度保留；列车位置与速度仅按铁路规则变化的既定例外适配；查询及后续推进使用新规格。适配后的字段进入现有捕获与检查点，编码解码及同投影恢复保留它们；严格重放通过同一发布入口执行接受与拒绝。模块测试不代替 #424 的真实开发重载和 #298 的四模块联调。

## 捕获、编码与录制

`Capture(Save)` 只捕获完整边界并拒绝冻结会话；`Capture(Diagnostic)` 用于故障资料。捕获返回持久修订、位置、输入身份、引用清单、检查点和不透明载荷，后台只对它调用 `Encode()`。宿主组合内容与元数据，不遍历内部状态。持久修订覆盖核心可保存变化，不被仅有位置的比较替代；内容与存档元数据修订仍分别由其所有者管理。

### 当前载荷

#312 按[批准决定](https://github.com/odradekk/train/issues/312#issuecomment-6133949111)实现捕获边界，#309 扩展世界状态，#480 更新地块枚举，#325 增加租约、库存预留和下一租约编号，#380 增加每座聚居地的 `fuel_energy_remaining`，#381 增加 `production_facilities`，#382 增加 `population_state`，#481 更新节点权威位置，#383 将设施状态改为能力键并增加 `resource_regenerations`、`resource_recovery`，#486 增加设施上一步实际劳动占用。内部编码器 [CoreCapture](../../Core/Sessions/CoreCapture.cs) 当前使用独立的 `core_state_format_version = 16`，与宿主完整存档的 `SaveFormatVersion` 6 分别演进；不支持格式 1–15 或旧通用模拟框架载荷，旧版本返回 `unsupported_format`。#480 与 #325 各自的格式 3 布局不同，合并后使用格式 4 明确区分；燃料余量使用格式 5，生产状态使用格式 6，人口状态使用格式 7，#481 的节点权威位置布局使用格式 8，#383 的开采与资源周期状态使用格式 9，#501 扩展施工部件能力后使用格式 10，#486 的实际劳动占用使用格式 11，#385 的自动重评、运行累计和近期报告使用格式 12；#546 将历史报告的载运率改为可空，使用格式 13；#331 增加自有车辆施工任务与聚居地施工上一步汇总，使用格式 14；#387 增加委托资金来源与全局工钱累计，使用格式 15；#342 增加冻结建城任务，使用格式 16。

人口状态保存精确执行参数、有效进度、累计应需与满足量、按货物身份保留的消费剩余量、人口变化余量、短缺与可用性状态及最近一次结算；普通恢复和严格重放使用同一份生产推进。当前规则缺失或本期必需货物缺失时停用人口，保留这些状态；新局仍须有有效规则。燃料余量必须为有限非负数，新局为零，保存、恢复、检查点、严格重放及内容发布保留其电量，不反推已消费燃料。生产状态保存完整冻结批次、输出缓冲和上一步工作、供电与劳动分配结果；解码拒绝非有限或负的实际劳动占用，以及非法进度、系数、数量、排序、建筑／计划配对及同时存在批次与缓冲。诊断消息和堆栈不进入载荷。内部字段布局由编码器维护，宿主只通过公开契约取得所需资料。

格式 8 的聚居地模板副本保存 `platform_length_meters`；站台段 P 在建立聚居地时就已固定，不随源模板变化重算。

格式 14 的 `WorldState.ConstructionJobs`（`construction_jobs`）保存活动任务，`Next.Job`（`next.job`）保存下一任务编号；任务包括设施、聚居地、目标设计／修理车辆、优先级、规则身份与启动修订、冻结总工作量和电力／劳动系数、投入及实际来源消费、进度、主状态、上一步工作速率、限制及阻塞。每座聚居地保存 `LastConstructionPowerDemand`、`LastConstructionActualSupply`、`LastConstructionAllocatedLabor`（线格式为对应 snake_case 字段），即使本步最后一个任务完成并移除，也保留最后一步汇总，下一无请求步骤归零。这些状态参加捕获、校验、恢复、引用、检查点与严格重放。格式 15 在任务中增加 `Funding`（`funding`），世界增加 `UnassignedServicePayments`（`unassigned_service_payments`）；NPC 队列由活动任务的设施与编号派生，不另存队列，后继排队任务必须进度为零。

顶层载荷包含格式版本、位置、下一命令编号、修订、当前输入身份、故障、`world` 和 `references`。`world` 保存完整生成来源 `source`（含种子、生成器版本、生成内容语义修订、解析配置及开局输入）、图与资源、聚居地与生物聚落、聚居地模板业务副本和地块、实例耐久、列车、库存、资金、全部下一身份编号，以及实际设计投影和衍生货物。实例只保存 `DesignHash`，引用同一 `world.designs` 中的投影，不在每个实例重复设计。实例和库存位置由所有者派生，不进载荷。

铁路新增权威字段全部进入捕获、检查点和解码一致性检查：列车的唯一 `position`（节点或线路、轨号、方向、中心距离）、`activity`、`speed_meters_per_second`、`plan`、`next_stop_index`、`remaining_dwell_steps`、`stop_requested`，以及节点位置内的进入线路、下一线路、阶段、节点距离、预约站台号与正反标记；聚居地的 `parked_vehicles` 是未编组车辆的唯一停放归属。成员、库存所有者和下一身份编号沿用原世界状态。解码检查位置范围、节点/线路变体及节点阶段、距离和预约一致、活动与速度及计划进度匹配、暂停停留余时、停车请求与当前阶段相容、计划目标存在、Station 货物规则合法且同站货物唯一、循环不退化，以及车辆与列车或停放清单的双向唯一归属。有计划但已完成的列车，只允许活动为 `Stopped`，或活动为 `Running` 且 `stop_requested = true`；后一种情况覆盖出站、线路行驶和下一节点进站，不按位置变体枚举。能力、Blockers、预测距离、交战默认值均为查询派生，不增加持久副本。

#311 另外保存 `TrainState.QueueEnteredStep`（首次排队步）、`WorldState.TrackHandoffs`（线路、轨号、交让方向）、`WorldIdentities.Break`（下一断点编号）和 `TrainState.ReturningFromBreak`（实际掉头所对应的断点）；线格式分别为 `queue_entered_step`、`track_handoffs`、`next.break`、`returning_from_break`，可空或空集合也必须出现。已有 `EdgeMapItem.Breaks` 保存断点。四项及断点、编组方向全部参加捕获、检查点和严格重放。方向锁与站台占用按上述规则派生，不增加权威副本。

解码要求交让按线路、轨号排序唯一，方向有效且指向现有空轨；断点按编号排序，编号全世界唯一非零且小于 `next.break`，轨号存在、距离有限且在线路范围内。同轨列车须同向；不另作全量同轨间距校验。排队步非空只允许合法的节点等待站台或出口等待铁轨状态，不晚于捕获的已完成步号，可保留合法 Station 停留余时；返程标记非空只允许线路上的 `Running`，且对应断点属于同轨。铁路施工仍未实现，`Works` 必须为空。

`world.definition_dependencies` 保存当前世界使用的规则、类型及流程等外部依赖身份和字段路径，在生成时建立、接受内容发布时按候选重算，使无内容解码仍能重建引用；它不是冻结执行参数。顶层 `references` 是交接清单，生成候选、捕获及恢复候选都由同一 [WorldReferences](../../Core/World/WorldReferences.cs) 从状态计算。`Templates` 按最终聚居地所有者排序且唯一，包含源模板和本地地块号／尺寸；`Designs`、`Definitions` 覆盖实际依赖，`Inputs` 使用对应候选或捕获的当前输入身份。新局缺少所需投影即配置失败，成功候选没有缺失项。

检查点包含位置、下一命令编号、故障全部字段及全部 `world` 字段；修订和当前输入身份仍在检查点之外，生成内容语义修订作为世界来源的一部分保留。文本对 UTF-8 求 SHA-256，取大写十六进制。捕获、引用和检查点在会话推进及释放后仍有效。

`DecodeState` 不做内容适配，保留保存时的身份、位置、修订、引用和检查点；`Encode()` 按状态重新规范编码，编码器产生的规范载荷往返后逐字节相同。确认格式版本受支持之前的错误返回 `unsupported_format`：JSON 无法解析、根不是对象、缺少版本、版本不是无符号整数或版本不受支持，定位到根或版本字段。确认版本受支持后，字段缺失、多余、重复、类型、范围或身份格式错误返回带字段路径的 `invalid_state`；`world` 必须是完整世界对象，不能为 `null`。世界校验还检查有限连续量、规范集合顺序、唯一身份、图及所有者引用、模板地块和实例设计配对、下一身份编号大于现存最大编号，以及顶层引用清单与状态一致。不要求保存时的生成器版本仍可用于新局；解码及恢复不重新生成世界。后续领域一致性检查随领域实现补入。

`PrepareRestore` 使用调用方固定的当前投影。身份相同则适配报告为空；身份不同时候选改用当前输入身份，报告恰好一条 `CoreProblem("content_changed", "InputIdentity", null, [旧身份, 新身份])`。两种情况都保留修订、下一命令编号及世界业务状态，不重新生成世界或重算人口进度与余量；节点内列车的位置与速度按当前节点尺度和限速适配：静止在 W、S 的列车移到新对应点，`Departing` 且活动为 `Waiting`、已有 `QueueEnteredStep` 的列车移到新 L，其他 `Departing` 列车及行驶中的列车保留距离并截到新 L 内，不依赖旧内容投影；进站越过新 S 及限速变化的处理同上文铁路规则。#382 在独立候选中按当前人口规则更新该规则的货物依赖，冻结期和物理余量仍保留原引用；同投影且依赖未变时检查点不变，依赖发生变化时新清单随候选捕获保存。候选 `References.Inputs` 使用候选身份。`MissingReferences` 按传入投影中已知缺失标注及字段路径产生，也识别当前人口规则与其实际必需货物的缺失，不能等到激活后推进才发现未声明的人口依赖；不负责重新计算保存的初始世界。原捕获不变。普通保存或普通恢复遇到故障返回 `Failure(frozen, "Fault")`，诊断载荷仍可解码；外部自行实现的 `ICoreCapture` 传入恢复属于编程错误，抛 `ArgumentException`。

严格重放复用同一解码器，从捕获恢复完整世界和原输入身份，不走普通恢复的内容适配。测试覆盖公开新局创建、共享捕获情景、跨进程生成、保存恢复及生产重放路径；调度测试覆盖非默认排队、交让、断点编号及返程标记的捕获恢复、独立进程同后缀和断点命令严格重放，起点由生产捕获生成，没有另行提交起点载荷文件。#298 的真实四模块交接仍待验证，也未证明后续经济或战斗状态恢复。

### 自动交易与运行报告

本节按 [#385 批准计划](https://github.com/odradekk/train/issues/385#issuecomment-6197839137)记录实现约定。规则在执行时读取当前关系、库存归属、租约和访问权；关系不匹配的买卖不会改成免费装卸，而是跳过并报告未完成原因。租约到期后允许取货、不允许存货。真实占领、宣战和车辆全毁尚无可触发的生产路径，这些规则的模块证据不代表战斗路径或 #299 联调已通过。

| 决定 | 行为 |
| --- | --- |
| D1：持久化 | 核心状态格式为 16，命令输入格式和重放外壳格式均保持 2。计划规则保留数组顺序；规则、停靠重评剩余步数、运行累计和全局近期报告均参加捕获、恢复、检查点和严格重放。引用清单包含当前计划规则中的货物；历史报告只保留货物身份，不参与内容引用，名称由界面按身份读取并处理缺失。旧格式 2 命令中空 `goods_rules` 的解码含义不变。 |
| D2：运行边界 | 接受 `ReplaceRunPlan` 时，已有累计先以 `PlanReplaced` 结束并出报告，再为新计划建立累计，`StartedAt` 为该命令边界，无论列车是否停车。循环计划最后一站消费并回到索引 0 时，出 `LoopCompleted` 报告并立即建立下一圈累计；单次完成出 `PlanCompleted` 报告，`CurrentRun` 变为 null；解编出 `TrainDisbanded` 报告。`StopTrain`／`StartTrain` 不结束或新建累计，已完成计划的启动返回 `plan_completed`。`AllVehiclesDestroyed` 当前没有生产触发路径，不增加结束分支。 |
| D3：现金流 | 有 `CurrentRun` 时，该列车参与的手动与自动 Buy／Sell 金额记入本次累计，包括建立计划后、启动前的手动备货；Load／Unload 不记收支。现车买卖和租金没有列车身份，不归属任何运行。`ServicePayments` 当前恒为 0；#387 将委托工钱累计到 `Economy.UnassignedServicePayments`，材料补购款不计入服务支出。报告表达净现金流，不是利润。 |
| D4：运输件数 | `TransportedUnits` 累计运行期间该列车实际 Sell 和 Unload 的件数，包含手动与自动操作；装入不重复计数。 |
| D5：载运率 | `LoadedCapacityRatio = Σ(进入线路时已用载货量 × 线路长度) / Σ(进入线路时载货容量 × 线路长度)`。当前运行保存两个累计值；分母为 0 时返回 null（尚未进入线路，或进入时载货容量为 0），历史报告保留该空值；有值时不截断，容量下降导致超载时可以大于 1。 |
| D6：未完成规则 | 每次评估发一个包含该站全部规则结果的 `AutomaticTradeEvaluated`。累计的 `IncompleteRules` 按 `(StopIndex, RuleIndex)` 保留本次运行中各规则最近一次评估仍未完成的结果，之后同一规则完成即移除，查询按 StopIndex、RuleIndex 排序。完整评估历史通过事件提供，不另行保存。 |
| D7：近期报告 | 世界只有一份全局近期报告列表，按生成顺序追加，追加后按当时的 `RecentReportLimit` 丢弃最旧报告；缺市场规则时只追加、不裁剪。上限更新在下一次出报告时生效，不立即追改。`TrainView.RecentReports` 从此列表按列车筛选，解编后全局列表仍保留其报告。 |
| D8：停靠时序 | 没有货物规则的 Station 不评估、不保存评估计数，零停留时不进入 `Servicing`。有规则时计数只在 `Servicing` 中推进，进入的当步立即评估；模拟步 n→n+1 中的评估一律使用 n+1，命令触发的评估使用该命令边界；有规则的零停留站会暂时进入 `Servicing`，也评估一次；之后每满一个重评间隔评估一次。评估与离站同一步时先离站，不再评估。停车暂停停留及重评计数，启动后继续。缺市场规则时包括装卸在内的全部规则均不执行，计数保持；恢复且仍是同一次停留时接续，若首次评估尚未执行，在恢复后的第一步执行。离站、替换计划或解编清除计数；每次评估后按当时的间隔重置，间隔更新只影响下一轮。 |
| D9：输入校验 | 零身份、非法货物、负目标、负限价或未初始化数组在取得命令编号前抛 `ArgumentException`。同一 Station 内同一货物重复是业务拒绝 `plan_duplicate_good`，字段为 `Plan.Stops[i].GoodsRules[j].Good`。受理时不检查库存是否存在、归属或关系；这些在执行时检查并作为未完成原因报告。 |
| D10：执行顺序 | 同一站按规则数组顺序执行，后面的规则看到前面成交后的库存与钱包。同一步的列车沿 `Core/Railway/TrainOperations.Advance` 的原始列车遍历顺序：先按 `QueueEnteredStep ?? 当前推进开始步`，再按 `TrainId.Value` 排序。后处理列车看到前处理列车交易后的状态；成交后更新调度缓存中的载重能力。 |
| D11：部分成交 | Buy／Load 请求量为 `max(0, 目标 − 列车当前持有)`，Sell／Unload 为 `max(0, 持有 − 目标)`。资格通过后，在来源数量、留货或用途缺口、入库容量、逐件限价和钱包约束下取最长连续可成交前缀，一次提交库存与钱包并发一个 `GoodsTransferred`，数量为 0 不发成交事件。自有装载遵守当地补货目标，自有卸载只受真实入库条件限制，免费装卸不产生收支。未完成原因取截断处的第一个原因，沿用手动交易代码；逐件限价失败为 `price_condition_failed / MaximumUnitPrice` 或 `MinimumUnitPrice`，不能用平均价越过单件限制。 |
| D12：适配结束运行 | 普通恢复或内容发布的节点位置适配若消费了计划最后一站，按 D2 在该边界结束累计并生成报告。`PublishContent` 发出 `RunReported`；`PrepareRestore` 没有事件输出，报告只出现在查询中。这是适配完成本圈后的首次结算，不重复归集已有报告。 |

### 完整领域状态与录制

载荷读取和适配必须保留设计/损伤、对象及编号进度、等待顺序、批次与任务冻结规格、物资预留/输出、人口消费和统计进度及两类余量、本期规则、燃料电量、租约、现车、自动重评余时和经营报告。内部字段布局不属于公开数据模型；格式版本和完整权威字段定义仍由核心编码器在实现时维护，不能以本页查询字段代替存档字段。

数值规范编码使用十进制整数，不给 `Money`/`BasisPoints` 的线格式增加 `Value` 包装对象；连续量保持既有 round-trip `R` 表示，有限值及集合顺序依[确定性规则](../design/04-deterministic-simulation.md)。命令继续使用既定格式 2 的有序外壳；具体封闭命令负载在实现中由核心编解码，宿主只保留 `CanonicalPayload`。冻结规则必须保存原精确整数参数与原连续值，不从当前定义重新拼回。核心载荷和复现版本不等于契约基线版本；旧存档/命令迁移不在范围内。

录制的设计命令负载保留完整 DesignInput 与差额，起点保留实际采用的设计／衍生货物；设计哈希与 CoreInputId 不能代替这些业务资料。引用清单覆盖保留窗口内的全部命令，包括被拒绝和故障命令的目标设计；宿主持有对应未激活候选的固定内容依赖，普通存档不把它们误算作成功修改。

核心保管命令、检查点和故障片段；宿主按 `RequiredReferences` 保管对应内容并写文件。`ReadRecording` 给出截止位置，因而没有命令的运行区间也可以准确导出。`CaptureRecordingStart` 建立待写起点；宿主仅在起点和全部依赖已经发布后，以原起点标识调用 `ConfirmRecordingStart(PublishedWithDependencies)`。

当前会话创建时自动建立初始起点，出现在 `ReadRecording().Starts` 中，同样需要宿主确认。检查点只在录制起点、故障边界及读取窗口的截止边界生成；同一内容的截止检查点不重复列出，不逐命令生成，也不设置核心固定采样间隔。命令与对应记录在同一完整边界生效，查询、捕获、起点确认、暂停和倍速不生成模拟输入。当前 `content.publish` 的规范负载为 `{"input_format_version":2,"kind":"content.publish","input_identity":"<目标 CoreInputId>"}`；目标投影由宿主保留，核心不编码内容投影。窗口引用合并各起点和固定故障片段的完整清单，并包含全部 Completed 命令的前后及目标输入身份。设计和输入身份按 Ordinal 排序去重；定义依赖按类型身份及字段路径排序去重；模板按 `Owner` 排序且所有者唯一。起点世界的设计、模板和定义依赖在普通窗口及固定故障片段中保留。

施工命令同用输入格式 2：`construction.start_owned` 的 `request` 固定包含 `kind`、`facility:{building,capability}`、目标、`sources` 和 `priority`。`build_vehicle` 的目标为完整 `design:DesignInput`（含输入身份、规范设计投影和 `assembly_goods`）；`repair_vehicle` 的目标为非零 `vehicle`。来源保留显式顺序，设计内无业务顺序的集合使用既有规范排序。成功、拒绝和故障命令的设计身份、定义依赖及输入身份均进入录制引用，完整组装体货物投影留在规范负载；拒绝或故障携带的设计不会因此加入普通世界保存。

五个铁路命令同用输入格式 2：`train.form` 携带 `ordered_vehicles` 数字数组；`train.disband`、`train.start`、`train.stop` 携带 `train`；`train.replace_run_plan` 携带 `train` 与 `plan:{loop,stops}`。Waypoint 为 `{kind:"waypoint",node}`，Station 为 `{kind:"station",node,dwell_steps,goods_rules:[...]}`。每项规则为 `{action,good,local_inventory,target_quantity}`，`action` 是嵌套对象：买入为 `{kind:"buy",maximum_unit_price}`，卖出为 `{kind:"sell",minimum_unit_price}`，装卸仅有 `kind:"load"` 或 `kind:"unload"`；价格字段为 null 表示无限价。数组保留业务顺序，整数按完整范围编码，字段输出顺序固定；解码拒绝缺少、多余、重复或非法字段及非法货物规则。铁路命令的内容依赖来自前后输入身份，不产生发布目标。生产重放通过同一 `Submit` 和 `Advance` 路径执行，运行位置、停留进度及其余铁路权威状态均参加检查点比较。

开发断点命令也使用输入格式 2，规范字段顺序为 `{"input_format_version":2,"kind":"rail.debug_add_break","edge":1,"track":1,"distance_meters":0}`。`edge` 为非零 `ulong`，`track` 为正 `int`，`distance_meters` 为有限非负 `double`，按 invariant round-trip `R` 编码；解码允许字段重排，拒绝缺少、多余、重复和非法字段。命令同样经过 `Submit` 并录制、严格重放。当前仅交付 Core 命令，控制台接线不在本票范围，真实宿主切换已拆给 #424；尚无新增控制台入口，单命令手册和索引更新不适用。

`RecordedCommand` 的命令编号与前后位置统一由 `Result` 中的 `CommandReceipt` 提供，不另外保存可矛盾的编号或前置位置。此结构调整不改变既定命令线格式的编号、位置与结果语义。

`CoreReferenceManifest.Templates` 列出当前聚居地、冻结建城任务及保留录制所需的实例副本引用，按 `Owner` 排序且所有者唯一。内容捕获、普通读档和严格复现须保留这些所有者对应的固定副本；来源定义身份仅作来源说明，不能用同身份的当前模板替换已有副本。恢复时缺少所声明所有者的内容副本是载荷不一致，不用当前源模板兜底。副本引用及其固定场景模型设计由内容展开，宿主不自行遍历模板字段。

成功确认后，核心按保留策略裁掉较早的普通证据，返回已不被任何片段引用的输入身份；宿主还须考虑活动内容等其他使用者。写入失败调用 Failed，保留旧证据并记录缺口；新起点只恢复之后的连续范围。故障片段固定保留，不参与普通窗口裁剪。窗口数和起点间隔由测量确定，本契约不给出伪装成性能结论的默认值；宿主不能把一次普通保存成功当作录制起点成功。

每个已发出的起点只接受一次确认：从未发出的标识抛 `ArgumentException`，重复确认抛 `InvalidOperationException`。迟到的旧成功确认不倒退裁剪边界。失败缺口从该起点的位置开始，到之后某个起点确认成功的位置结束；已存在较新成功起点时，迟到失败形成闭合缺口，历史缺口仍保留。故障片段固定故障时窗口最早的起点至故障边界的全部命令、检查点和引用，之后的普通裁剪不改动它。冻结后不能捕获新的录制起点，返回 `frozen`、字段 `Fault`；故障资料由已固定片段和诊断捕获提供。

## 严格重放

宿主读取复现容器并还原包内内容投影，调用 `PrepareReplay`；核心比较原构建、模拟版本、兼容环境、配置和输入身份。依赖不完整、格式无法理解或身份不符明确失败，不跳过命令，也不读取安装目录补救。

当前 `CoreReplayInput.FormatVersion` 为 2（旧版 1 因检查点与命令回执包含旧地块档位而拒绝），与命令输入格式 2 分开。#385、#546 保持重放格式 2 和命令格式 2；重制起点为核心状态格式 16，检查点按新的世界结构重制。旧起点载荷由状态格式校验拒绝；检查点规范文本不带状态格式号，旧结构由世界载荷校验拒绝。不因这些内部变化再升重放外壳格式。`RuntimeIdentity` 的 `GameBuild`、`SimulationVersion`、`OperatingSystem`、`Architecture` 和 `DotNetRuntime` 全部按 Ordinal 匹配；不匹配返回 `incompatible_replay`，字段为 `OriginalRuntime.<成员>`，兼容粒度由宿主提供的字符串决定。准备阶段解码起点与全部命令，核验记录位置、检查点格式／SHA-256，并从 `contentHistory` 取得起点及所有命令前后、目标输入身份的投影；任一依赖缺失返回 `content_missing`。不支持的版本和种类返回 `unsupported_format`，非法字段和位置返回 `invalid_state`，附字段路径；带故障的起点不能作为可执行起点。准备阶段也对全部 `contentHistory` 使用与 `PublishContent`、普通恢复相同的内容投影校验，非法投影抛 `ArgumentException`，不能在执行一段前缀后才发现输入无法使用。检查点复用世界载荷读取与校验，世界内部差异以 JSON Pointer 报告。世界来源含生成配置，未来新增领域字段仍须同步接入。

重放只有读取、推进和边界捕获入口，没有 Submit。`Advance` 到达预算、指定位置、记录截止位置、首个差异或故障即返回明确停止原因；`stopAt` 可以指定同一步内某条命令后的边界，不能越过它。同一步内的命令按记录位置顺序执行，拒绝结果也核对。`CompareCheckpoints` 返回首个字段差异，不要求宿主解析检查点文本。`CaptureBoundary` 可作为普通读档候选建立可编辑副本；此后当前内容适配属于新会话，不改原重放或声称其仍是严格复现。

步骤预算计实际推进的模拟步数；当前边界上的命令不消耗预算，因此 `Advance(0)` 可以执行同一步命令，`stopAt` 仍先于下一条命令检查。指定已过去或记录范围内不存在的边界是编程错误，抛 `ArgumentOutOfRangeException`；指定记录末尾之后的位置仍在记录末尾停止。回执按完整决策和嵌套集合内容比较，不用 record 的引用相等判定。检查点按稳定字段顺序比较并报告 JSON Pointer、预期及实际值，伪造摘要或非法文本返回失败；位置相同的健康与故障边界分别核对。终止后重复推进保持终止位置，不重复返回事件；未重现记录中的故障时，不能暴露超出录制末尾的状态。

## 验证与性能责任

本票执行声明及消费样例编译、内容格式已有样例检查、文档链接与一致性审查。样例覆盖同一视图消费、当前交易重判、编辑候选、捕获编码、无界面推进、重放副本和录制反馈；它们没有假装执行尚未存在的业务实现。

后续[契约原型](https://github.com/odradekk/train/issues/292)按[共享用例规则](testing.md)补真实实现和有限情景替身：快照保留、拒绝原子性、多步首故障、候选取消/拒绝/故障、存档引用声明、人口和市场续接、记录持久化失败、严格重放差异、删除对象事件。独立复制契约和样例构建证明无实现引用；它不代替四模块独立测试或 Godot 实测。

读视图无需整世界复制，地点查询不应强迫调用方遍历全世界；没有承诺固定复杂度、零分配或固定时延。实现阶段测量地图摘要、地点/详情查询、保留视图的内存、批量推进及事件体积、候选准备、捕获/编码与录制窗口。调用方控制每次推进的步数；长操作可以由宿主用固定不可变输入调度，但不引入后台写世界。
