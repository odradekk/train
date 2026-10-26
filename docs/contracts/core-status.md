# 核心模拟层：交付与验证

本页记录交付与验证，与[契约目录](core.md)一起使用。

## 交付状态与入口

已提供独立的 [Train.Core.Contracts](../../Contracts/Core/Train.Core.Contracts.csproj) 声明和[可编译消费样例](../../prototypes/core-contract/ConsumerExamples.cs)。当前实现了核心工厂、候选与会话生命周期、阶段一小世界生成、同步内容发布、列车编组及计划运行、方向锁与跟车、对向公平、站台排队、断点掉头及 `DebugAddRailBreak`、节点战斗、伤害与撤退、不可变世界查询，以及当前世界状态的捕获、编码、解码、普通恢复、默认录制、起点确认与裁剪、固定故障片段、严格重放和检查点比较。其他领域行为仍待交付；模块证据不代表 #298 的真实四模块联调或完整 Demo 已通过，调用样例和旧原型也不构成生产行为证据。

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
- 会话的 `Read`、`Submit`、`Advance` 和 `Dispose`。实现 `PublishContent`、列车命令（`FormTrain`、`DisbandTrain`、`ReplaceRunPlan`、`StartTrain`、`StopTrain`）、铁路施工命令（`ExpandRail`、`RepairRail`、`SetAutomaticRailRepair`）、四个战斗命令、`DebugAddRailBreak` 和 `DebugSpawnCreatureGroup`，推进包含列车运行及线路、站台调度、节点开战与伤害结算；`PublishContent` 在比较身份前使用与新局相同的内容投影校验，未初始化数组等输入错误在事务前抛 `ArgumentException`，不取得命令编号。身份相同时在事务前抛 `ArgumentException`；合法投影身份不同时，在同步事务内检查世界引用并适配保存的设计、衍生货物、当前定义依赖和节点站台数，具体拒绝与兼容规则见下文“[内容候选与同步提交](core-content-sync.md#内容候选与同步提交)”。铁路命令由 [CoreSession.Submit](../../Core/Sessions/CoreSession.cs) 经 [TrainCommands.Validate 和 Execute](../../Core/Railway/TrainCommands.cs) 处理。铁路货物查询和出发检查从当前货物目录识别缺失，恢复候选的缺失标注见下文“[当前载荷](core-capture.md#当前载荷)”。
- 读视图的 `Position`、`Revision`、`InputIdentity` 和 `Fault`。修订在每个 Completed 命令、完成步骤及新步骤故障时各增加一次；NotAccepted、查询和零步推进不增加。修订已达 `ulong.MaxValue` 时，故障仍写入并冻结，修订保持最大值；正常路径的递增仍使用 checked 算术。
- `World`、`FindNode`、`FindEdge`、`FindLocation`、`FindInstance`、`FindTrain`、`FindCombat`，覆盖生成的图、占据者、资源、实例、运行列车和进行中的战斗；车辆位置随所属列车派生，轨道查询列出实际在轨列车。
- `FindInventory`、`FindLease`、`QuoteLease`、`LoadGoods`、`UnloadGoods`、`AcquireLease` 及租约到期，按 [#325 的批准计划](https://github.com/odradekk/train/issues/325#issuecomment-6172428536)实现。库存预留参与容量和持久化；车辆与建筑的制造、修理不产生返还预留；车辆改装与拆解由 #332 产生和释放返还预留，建筑改装、拆解与大型设备制造由 #343 产生和释放，具体规则见下文“[库存与租约](core-inventory.md#库存与租约)”。
- #384 接入 `QuoteTrade` 的四种动作、`BuyGoods`／`SellGoods`、`QuoteVehicle` 和 `BuyVehicle`／`SellVehicle`，以及补货目标、两轮备货、潜在消耗和现车名额；具体语义见下文“[市场报价与手动交易](core-market.md#市场报价与手动交易)”。#528 接入按配置计算的 NPC 初始库存和有限现车，见下文“[阶段一生成器](core-lifecycle.md#阶段一生成器)”。
- #385 接入运行计划的自动货物规则、停靠重评、运行累计、近期报告及 `AutomaticTradeEvaluated`、`RunReported`；规则顺序、部分成交、时序与持久化见下文“[自动交易与运行报告](core-recording.md#自动交易与运行报告)”。
- `FindSettlement` 与 `Economy` 按 [#486 的批准计划](https://github.com/odradekk/train/issues/486#issuecomment-6190044442)接入已有状态：聚居地库存身份、地块、停放车辆、站台与占用、租约、仓储划分、加工、回收和开采设施、电力、人口、钱包和当前规则状态。尚未实现及暂定字段逐项见下文“[聚居地与经济查询](core-settlements.md#聚居地与经济查询)”。
- `FindFacility` 的 `Production` 键、模板中的加工／回收计划、批次推进、输出缓冲及生产事件，按 [#381 的批准计划](https://github.com/odradekk/train/issues/381#issuecomment-6183793182)实现，具体范围见下文“[加工与回收](core-production.md#加工与回收)”。玩家用 #622 的 `SetProductionPlan` 编辑已保存的计划副本。
- `Extraction` 设施、开采批次、节点资源原地恢复与全局再生，按 [#383 的批准决定](https://github.com/odradekk/train/issues/383#issuecomment-6188092446)实现，使用同一批次运行时与资源仲裁；参数、时序及事件见下文“[节点资源](core-production.md#节点资源)”。
- #331 接入 `StartOwnedJob` 的 `BuildVehicle` 和车辆 `Repair`、`FindJob`、施工事件与修理锁；施工与生产、开采、人口共用单步资源仲裁，具体范围见下文“[自有车辆施工](core-construction.md#自有车辆施工)”。
- #387 接入两种资金来源的车辆制造／修理 `GetJobQuote`、`CommissionVehicleWork`、原子补购收费及按设施 FIFO 委托队列；全局工钱、查询、事件和保存重放见下文“[NPC 车辆委托与施工报价](core-construction.md#npc-车辆委托与施工报价)”。
- #332 接入车辆 `ApplyInstanceDesign`、自有 `Refit`／`Dismantle`、`SetDeconstructionPaused`、两种资金来源的车辆改装报价及委托改装；委托返还进入当地玩家租库，查询、事件与保存重放沿用同一任务路径。
- 按 [#332 批准计划 D11](https://github.com/odradekk/train/issues/332#issuecomment-6245198930)增加 `SettlementView.OwnedJobs`，让宿主从聚居地查询活动自有施工任务，再经 `FindJob` 读取详情；不新增持久字段。
- `Capture`、`DecodeState` 和 `PrepareRestore`，覆盖当前真实世界状态；格式与恢复规则见下文“[当前载荷](core-capture.md#当前载荷)”。公开 `PrepareNew` 路径有共享 `CaptureScenario` 及跨进程生成、保存恢复测试。
- `ReadRecording`、`CaptureRecordingStart`、`ConfirmRecordingStart`、`PrepareReplay` 和 `CompareCheckpoints`，覆盖当前完整世界状态、`PublishContent`、列车命令（`FormTrain`、`DisbandTrain`、`ReplaceRunPlan`、`StartTrain`、`StopTrain`）、铁路施工命令（`ExpandRail`、`RepairRail`、`SetAutomaticRailRepair`）、三个战斗命令（`SetTrainEngagement`、`FocusFire`、`Retreat`）和 `DebugAddRailBreak`，包含正常业务拒绝。运行在线路上、停留、调度和战斗中的录制通过生产重放路径逐检查点比较。铁路施工命令的录制与严格重放用例见 [RailwayWorkTests.Railway_jobs_round_trip_and_strict_replay_keeps_the_receipts](../../Tests/Core/Railway/RailwayWorkTests.cs)。
- #382 将人口消费与结算接入 `Advance`：六类生活需求、主食优先与加工食品共享满足、整数取用后的物理余量、分类加权、人口变化余量和最低一人。每次消费全部完成后判断各项短缺，仅在状态变化时发 `PopulationShortageChanged`；结算发 `PopulationSettled`，从多于一人降至一人时发 `PopulationMinimumReached`。停用与恢复使用 `PopulationAvailabilityChanged`，停用期间不消费、不累计周期、不判断短缺，劳动力为零。

下表列出尚未实现的行为。尚未实现的独立成员直接抛 `NotImplementedException`，不以业务失败、故障结果或缺失查询代替。未实现的命令在取得编号和进入事务捕获之前抛出；异常消息只注明成员或命令种类。

| 未实现成员 | 交付责任 |
| --- | --- |
| `FindFacility` 的 `Construction` 键、`SetJobPriority` | 后续施工查询与调度 |
| 其余命令、其他 `Find*`、报价和预览 | 后续经济运输、制造与编辑、自有建设、生物与外交等领域任务 |

旧 `Train.Core.Simulation` 及宿主入口已删除。`RandomKey`、`KeyedRandom`、`SplitMix64` 原算法迁到 `Core/World/`，作为内部实现保留；固定向量测试及独立 Python 参考工具位于 `Tests/Core/World/`。会话始终持有世界状态；内部会话测试也使用真实生成状态，拒绝与事件由内部测试体供给。列车运行、调度及后续领域任务须将各自新增权威字段补入捕获、恢复和检查点，不能把初始世界往返当作完整玩法恢复证据。

#380 已实现内部建筑能力计算、聚居地共用燃料能量余量和单步资源仲裁。存储、发电、生产、开采、施工共用有效部件判断；设施请求在基本消费与改善消费之间，按 Priority、BuildingId、CapabilityKind 单次排序处理完整批次输入或联合用电／用工请求。只从聚居地自有库存取用货物和燃料，闲置发电不耗燃料，已取出的能量余量不随燃料规格更新而重算。#381 与 #382 已将真实设施请求、人口消费与劳动力接入 `Advance` 的同一次资源仲裁；模块测试不表示完整经济流程已运行。交接逐项如下：

- #381 已接入真实批次输入、最大有用工作速率和批次推进，不在步内重启。
- #382 的 `PopulationOperations.Begin` 在消费到期时生成基本消费请求并提供人数乘当前有效劳动比例的容量；`PopulationConsumption.PrepareImprovement` 使用基本消费实际授予量生成改善请求，`PopulationOperations.Complete` 在三段消费后回写状态与事件。基本消费预估与仲裁读取同一份库存。同一步每座聚居地最多调用一次 `SettlementResources.Resolve`，无到期消费及设施请求时跳过仲裁。人口请求和真实设施请求进入这同一次调用，使用人口提供的真实劳动容量；生产结果与人口结果保留在同一个提交世界中。本步产出下一步才可消费，期末人口变化下一步才影响请求和劳动力分配。
- #486 已将已有权威状态接入 `FindSettlement` 及其 `PowerPoolView`、人口和仓储查询；加工、回收和开采设施详情与 `FindFacility` 使用同一查询实现。
- #384 已接入潜在消耗、两轮备货及最低组合的共用燃料抵扣。潜在计算复用 `SettlementResources.AllocateWork` 的电力／劳动分配，实际运行仍由 `Resolve` 扣料和提交；查询不消费库存或燃料。
- #299 验证上述真实模块路径及此前阶段路径；本票的内部仲裁、保存恢复与隔离证据不能替代阶段联调。

#382 的居民备货计算为内部 `PopulationStockTargets.Calculate`。调用方传入实际本期规则（尚未开始时为当前规则，停用时传空）、人数、市场基本／改善备货时长、主食与加工食品各自的库存数量、物理消费余量、需求类别与该类容量预算，返回按货物身份排序的整数目标。只有这两种替代食品的现货影响目标，其余现货只参与报价和可成交数量。目标包含应保留的现货，不是净采购缺口；加工食品共享基础与改善覆盖，物理余量抵扣后再逐项向上取整，预算不足时以标准库 `BigInteger` 保存整数目标及比例中间量，按整数目标比例和最大余数分配，最终数量转换为容量可表达的整数；平局按稳定货物身份。#384 的 `MarketStockTargets` 按基本、生产、改善顺序提供预算并合并目标；基本目标已经压缩时，后续预算为零。该内部入口及市场的模块证据不代替 #299 真实联调。

#486 的人口查询接入使用已保存的 `SettlementState.Population` 与 `PopulationState`：本期规则及修订、有效步数、累计应需与满足量、六项短缺状态、上次可用性以及最近结算。周期进度保存 `ElapsedSteps` 与 `LastAdvancedStep` 时间锚点；可用时在当前边界加上距锚点的步数，停用时保持不动，恢复后重新锚定。非消费步不重建人口数组；应需与满足量仅在消费步同时累加同一区间的实际计算值，不从总步数反推累计值。消费物理余量和人口变化余量只用于模拟及持久化，不进入查询。当前有效劳动比例立即生效，本期消费、权重和增长参数保持冻结；可用性须结合当前投影及本期必需引用判断，上次可用性也用于冻结进度。`LastSettlement` 只保留最近一条；查询接入后 `RecentSettlements` 返回同一条，尚无结算时为空数组。结算原因按变化率的符号判断，整数变化为零也可以是增长或短缺流失；负变化率且结算后为一人时为 `MinimumReached`。

## 验证与性能责任

本票执行声明及消费样例编译、内容格式已有样例检查、文档链接与一致性审查。样例覆盖同一视图消费、当前交易重判、编辑候选、捕获编码、无界面推进、重放副本和录制反馈；它们没有假装执行尚未存在的业务实现。

后续[契约原型](https://github.com/odradekk/train/issues/292)按[共享用例规则](testing.md)补真实实现和有限情景替身：快照保留、拒绝原子性、多步首故障、候选取消/拒绝/故障、存档引用声明、人口和市场续接、记录持久化失败、严格重放差异、删除对象事件。独立复制契约和样例构建证明无实现引用；它不代替四模块独立测试或 Godot 实测。

读视图无需整世界复制，地点查询不应强迫调用方遍历全世界；没有承诺固定复杂度、零分配或固定时延。实现阶段测量地图摘要、地点/详情查询、保留视图的内存、批量推进及事件体积、候选准备、捕获/编码与录制窗口。调用方控制每次推进的步数；长操作可以由宿主用固定不可变输入调度，但不引入后台写世界。
