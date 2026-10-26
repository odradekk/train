# 核心模拟层：施工能力与任务

本页记录施工能力与任务，与[契约目录](core.md)一起使用。

## 施工能力输入与执行交接（#501）

`PartCapability.Construction(double WorkPerSecond, PlotSize MaximumBuildingPlotSize, SizeClass? MaximumVehicleSize)` 的三个参数均无默认值；调用方显式传 `null` 表示没有车辆施工能力。`WorkPerSecond` 为正有限值，建筑上限只接受 `Small`、`Medium`、`Large`、`Giant`，Core 输入校验拒绝站点档位与非法枚举；非空车辆上限必须是有效的车辆档位。能力来自完好部件，工作速率相加，两个上限分别取完好施工部件的最大值；不新增无调用方的公开汇总接口。

- #343：建筑建造、改装、修理已在命令边界检查目标设计的地块档位不超过设施建筑上限；小站点按大、大站点按巨型归档。资格看定义未缺失的施工部件，不看损伤；工作速率只计完好部件。站点归档在阶段验收时仍待用户确认。
- #331：已在自有车辆建造、修理的命令边界检查设施车辆上限；车辆改装已由 #332 按目标设计档位检查。`null` 一律拒绝，非空时目标车辆档位不得超过上限。不能把存在施工工作速率解释为能够造车。
- 拆解和制造大型设备只要求施工能力，不检查上述档位。#501 交付表达、输入校验、持久化和交接；#343 已接入大型设备制造与建筑拆解，车辆拆解已由 #332 接入。

## 自有车辆施工

[#331 的批准计划](https://github.com/odradekk/train/issues/331#issuecomment-6233893871)只实现 `StartOwnedJob(JobRequest.BuildVehicle)` 和 `StartOwnedJob(JobRequest.Repair)` 的车辆目标。设施必须位于玩家自有聚居地，且建筑归玩家所有、未被摧毁。车辆上限按当前设计中定义未缺失的施工部件判断资格，不看部件损伤；上限为 `null` 或低于目标档位拒绝。实际工作速率和档位能力只计完好部件。每个施工能力最多一个任务，已占用返回 `busy`，不排队。

制造请求的 `DesignInput.InputIdentity` 必须等于当前内容身份；目标须为车辆、`CanConstruct`，且设计及 BOM 货物不缺定义。接受后完整目标设计与 `AssemblyGoods` 才进入世界设计目录。修理目标可以是当地停放车辆，也可以是位于设施节点、`Activity == Stopped` 的玩家列车中的车辆，不要求解编。修理只处理有效定义：被毁部件各消耗一件 `RepairGood`；结构货物按数量乘结构耐久损失比例逐种向上取整；受损未毁部件只计 `RepairWorkPerDamagedPart`。损失用保存耐久与当前上限的较小值计算。缺定义部件和结构货物不消耗、不计工作量，部件耐久记录原样保留；没有可修工作量时拒绝。

`Sources` 是显式消费顺序，空数组表示没有来源，只能用于无需货物的请求。允许设施所在聚居地库存和当地可访问的玩家列车库存；列车来源复用 `LoadGoods` 的归属、同节点、修理锁、`Servicing`、`Docked`、零速及站台预约检查。重复、不存在或不可访问来源定位到 `Sources[i]`。受理先完成全部校验和材料分配，再原子扣料、分配任务编号并占用设施；任何拒绝都不扣料、不占能力。

任务冻结目标设计身份、总工作量、电力／劳动系数、规则身份和启动修订，保存实际材料来源及投入。已有实例与任务的规格来自世界保存的设计，普通读档沿用内嵌设计，规格适配通过 `PublishContent` 完成。`PublishContent` 不允许删除活动任务必需定义；规则更新不重算冻结的总工作量与系数。任务通过 `SettlementResources.Resolve` 与生产、开采及人口同次仲裁；资源不足或完好部件能力不足为 `ResourceBlocked`，保留进度，能力不足的 `Limits` 含 `Capability`。设施被毁、失去归属或设计资格，以及目标实例消失或被毁，使任务失败并释放设施。制造完成后创建当地停放车辆；修理完成后有效部件和结构耐久恢复到当前上限。接受发 `ConstructionStarted`，状态变化发 `ConstructionStateChanged`，完成再发 `ConstructionCompleted`。完成或失败立即移除任务，之后 `FindJob` 返回 `Missing`，结果从事件读取。

`FindInstance` 的 `UnderRepair`、`UnderRefit`、`UnderDeconstruction` 均从活动任务派生，不另存锁。任一施工锁都拒绝同一车辆的第二个施工任务、`ApplyInstanceDesign`、所在列车的 `StartTrain`、货物买卖与装卸，以及 `SellVehicle`。修理、改装允许编组和解编；`FormTrain` 含拆解中的车辆时返回 `instance_locked`。稳定拒绝码如下，字段与相关身份补充定位：

| 代码 | 原因 |
| --- | --- |
| `object_missing`、`not_owned`、`instance_destroyed` | 设施或目标不存在、非玩家所有、已被摧毁 |
| `invalid_design`、`content_changed`、`content_missing` | 目标不能制造、输入身份过期、必需定义缺失 |
| `conditions_not_met`、`capability_zero`、`busy` | 设施车辆档位资格不符、完好施工能力不足、能力已占用 |
| `instance_locked` | 车辆持有施工锁，或列车包含持有施工锁的车辆 |
| `train_not_stationary_at_node`、`vehicles_not_colocated` | 修理目标列车未在当地停止，或停放车辆不在当地 |
| `duplicate_source`、`access_denied`、`insufficient_goods` | 来源重复、库存不允许访问、总材料不足 |
| `train_not_at_settlement`、`platform_required` | 列车来源不在当地，或不满足停站与站台访问条件 |
| `invalid_state`、`identity_exhausted` | 无可修工作量，或任务编号耗尽 |

运行中的阻塞还使用 `power_unavailable`、`labor_unavailable`。`FindFacility(Construction)` 与 `SetJobPriority` 仍未实现。建筑施工与大型设备制造见下文；不增加取消和退款。

## 车辆立即应用、改装与拆解（#332）

按 [#332 批准计划 D1–D12](https://github.com/odradekk/train/issues/332#issuecomment-6245198930)实现车辆范围，并增加 `SettlementView.OwnedJobs` 查询；不新增控制台命令。建筑目标及建筑 `Locked` 生产状态由 #343 接入，规则见下节。

`ApplyInstanceDesign` 检查当前输入身份、实例设计哈希与构成、完整合法且无缺失引用的目标，以及部件对应。仅构成未变时立即换设计，保留损伤、列车归属与运行状态，不消费货物或建立任务；构成变化整份拒绝。设计哈希变化发 `InstanceDesignChanged`。

自有 `Refit` 消费 `AddedGoods`，冻结目标设计、保留部件对应和 `RemovedItems`。目标位置沿用修理，设施车辆上限按目标档位检查，`null` 拒绝。工作量为改动单位数乘改装系数，加拆下受损部件的修理工作量。只改朝向等零工作量改装仍建正式任务，遵守队列，首个可执行步完成，不请求资源；`Progress` 为 0，工钱为 0。完工时原子换设计并发 `InstanceDesignChanged`：结构耐久为 `ceil(M新 × min(S, M旧) / M旧)`，采用 checked `Int128`；保留部件沿用完工时最新绝对耐久，新部件满耐久。

`Dismantle` 仅接受设施所在聚居地的玩家停放车辆；编组车辆返回 `vehicle_in_train / Instance.Details.Train`。先 `DisbandTrain`，其库存非空或有预留时拒绝解编。拆解只要求施工能力，不检查车辆档位。工作量为保存设计的 BOM 件数乘拆解系数，加可返还单元中受损部件的修理工作量。占位不返还，其他有效部分照常返还。完成时原子移除实例并入库。`SetDeconstructionPaused` 只接受拆解任务，其他种类返回 `invalid_state / Job.Kind`；暂停保留进度、预留和锁，目标或设施失效仍使任务失败。

返还按整件单元计算：任一内部部件缺失或被毁，整个单元不返；其余整件返还，每个受损部件只计一次修理工作量。根层同种结构货物先合并，再按最新结构耐久比例向下取整。自有任务在设施所在自有聚居地库存预留，容量不足则原子拒绝启动；初始无返还时不建空预留。完成时每种货物实际返还为最新计算值与开工预留 `ExpectedReturns` 同种数量的较小值，未列入预留的货物返还 0。`PublishContent` 不重算返还和预留，也不因库存容量拒绝发布；损伤可减少返还。完成或失败均释放整份预留。

## 建筑施工与大型设备（#343）

按 [#343 批准计划](https://github.com/odradekk/train/issues/343#issuecomment-6249973069)实现自有聚居地的 `BuildBuilding`、`BuildEquipment`、建筑 `Repair`／`Refit`／`Dismantle`、建筑 `ApplyInstanceDesign`，以及这些请求的 `GetJobQuote`。`OwnedJobs` 不新增字段，建筑任务与车辆任务一起按 `JobId` 列出。不新增控制台命令。委托的建筑目标、`BuildEquipment` 和任何 `Dismantle` 仍抛 `ArgumentException`。`SetDeconstructionPaused` 只接受拆解；建造、改装、修理返回 `invalid_state / Job.Kind`。

建造校验地块：不存在或不在设施聚居地 `object_missing / Plot`；已有建筑 `plot_occupied / Plot`；已是工地 `busy / Plot.ConstructionJob`；目标不是可制造建筑 `invalid_design / Design`；`FitsPlot` 失败 `conditions_not_met / Plot`；设施建筑上限不足 `conditions_not_met / Facility`。受理后地块占用为 `PlotOccupant.Construction`，BOM 只扣一次。完工分配建筑身份，实例插在车辆之前并按身份升序；`InstanceDetails.Building` 的尺寸是地块尺寸。地块模板已有加工或开采计划时补空的设施行，没有计划则不创建，查询为停用。

修理、改装、拆解的建筑必须在设施聚居地的地块上、玩家所有且未摧毁。不在当地 `conditions_not_met / Instance`；缺失、非玩家、已摧毁、已锁定沿用车辆的 `object_missing`、`not_owned`、`instance_destroyed`、`instance_locked`，字段为 `Instance`。目标建筑的设施行按顺序检查：该行有批次时 `invalid_state / Instance.Batch`，输出缓冲非空时 `invalid_state / Instance.OutputBuffer`。目标正在作为执行设施施工时 `invalid_state / Instance.ConstructionJob`。执行设施就是目标时 `invalid_state / Instance`。锁定中的建筑不能作为执行设施，`Qualification` 返回 `conditions_not_met / Facility`。改装目标不适合该地块 `conditions_not_met / Instance.Details.Plot`。

改装复用车辆差额、整件返还、开工预留和返还上限。结构耐久同样为 `ceil(M新 × min(S, M旧) / M旧)`。零工作量改装仍是正式任务。立即应用只接受构成未变的目标。修理跳过缺定义部件，完工只恢复其余部件和结构耐久。拆解不查档位；占位格不返还；暂停不累计工作，恢复后继续。完工移除实例和生产设施行，不为已移除设施补发 `ProductionStateChanged`，`FindFacility` 返回 `Missing`。

大型设备只要求施工能力。开工在设施所在自有库存为一件 `ManufacturedGood` 预留容量，不足则 `insufficient_capacity / ReturnInventory.Capacity`。目标为 `NewAssembly`，不建实例、不占地块。完工把这一件放入该库存，`ConstructionCompleted.ReturnedGoods` 列出它；失败只释放预留，不退料。

建筑改装、修理、拆解期间，已有计划的加工和开采进入 `ProductionState.Locked`，问题为 `instance_locked`。状态顺序是流程缺失、没有计划则为停用、锁定、计划未启用则为停用、能力为零、不满足条件、出料阻塞、等待原料、运行中。锁定期间生产、开采、施工和发电能力为 0，仓储容量和站台保留。地块模板已有对应计划时，进入和离开锁定各发一次 `ProductionStateChanged`；没有计划的建筑查询保持停用，不发该事件。站点的建造、改装、修理、拆解完工后用完好站台部件重算 `Node.Platforms`，并按新节点尺寸适配列车。缩小仓储不删除已有货物。

## NPC 车辆委托与施工报价

[#387 的批准计划](https://github.com/odradekk/train/issues/387#issuecomment-6239791107)接入 `CommissionVehicleWork` 的车辆制造和修理，以及 `GetJobQuote` 的 `OwnInventory`、`NpcCommission` 两种资金来源。两条命令和报价共用需求、来源分配及拒绝计算；报价不预留货物、资金或队列位置，提交重新裁决。报价内部故障返回该读视图位置的 `CoreResult.Faulted`，不冻结活动会话。

两种资金来源均实现 `BuildVehicle` 和车辆 `Repair`、`Refit`；委托改装沿用同一需求、来源、补购、原子收费和 FIFO 流程。按[用户决定 D12](https://github.com/odradekk/train/issues/332#issuecomment-6246041891)，委托改装返还进入设施所在聚居地的玩家租用库存。委托的 `BuildBuilding`、`BuildEquipment`、`Dismantle` 和所有建筑目标不合法，抛 `ArgumentException`。非法资金来源、零身份、未初始化来源数组和负 `MaximumPayment` 也在取得命令编号前抛 `ArgumentException`。

业务检查按以下顺序执行，报价和命令返回相同代码与字段：

1. 设施所在聚居地存在（`object_missing / Facility`）；委托位于自有聚居地时 `invalid_state / Facility`，敌对时 `access_denied / Facility`。
2. 制造的输入身份、设计种类、可制造性及必需定义；修理的目标存在、玩家归属、修理锁、当地停车位置和结构未毁，沿用上节的代码和字段。
3. 设施存在、归属、结构未毁、档位资格和完好施工能力；自有任务检查设施空闲，委托允许排队。设施及目标其余拒绝沿用上节。
4. 施工规则存在（`content_missing / ConstructionRule`），委托还须有市场规则（`content_missing / MarketRule`）；计算工作量及材料，无可修工作量时 `invalid_state / Instance.Durability`。
5. 按数组顺序检查每个来源的重复、存在和访问权限。自有任务沿用当地自有库存／当地列车来源；委托仅允许当地玩家列车库存及该聚居地的玩家租用库存，到期租约仍可取出。列车来源复用装载的归属、同节点、停站、站台、零速及修理锁检查。NPC 库存不能作为玩家来源，返回 `access_denied / Sources[i]`；其他来源拒绝也定位到 `Sources[i]`。
6. 先分配玩家来源，再按货物稳定身份顺序从当地真实可售库存补购缺额，复用市场留货及逐件边际价格。两种资金来源均检查全部货物后才报告缺料；`insufficient_goods / Sources` 的 `RelatedIdentities` 按货物键的 Ordinal 顺序列出所有缺货身份，编码与 `restock_shortage` 相同。补购直接投入施工，不经列车货舱，不受玩家货舱容量限制；不会先买半份材料，也不建立等待供货的任务。
7. 有预计返还时，在玩家来源扣料后的候选状态检查容量。自有任务使用设施所在自有聚居地库存；委托按库存编号升序，选择该聚居地第一份 `ReadWrite` 且可用容量足以容纳全部预计返还的玩家租库。没有合适库存时，报价与命令均拒绝 `insufficient_capacity / ReturnInventory.Capacity`，不扣料、不收费、不建任务。无预计返还时不需要租库或预留。
8. 任务编号可用；计算材料款、工钱和总额后依次检查总限价（`price_condition_failed / MaximumPayment`）和钱包（`insufficient_money / Wallet`）。

受理时在选中的租库建立完整返还预留，任务的 `ReturnInventory` 指向该库存。施工期间租约到期或库存冻结，预留仍参与全城 `TotalReserved` 和容量统计；完成时照常存入该租库，完成或失败均释放整份预留。

`RequiredGoods` 是整份需求，`SourceConsumption` 仅为玩家来源，`NpcPurchases` 是缺额补购。拒绝时，`SourceConsumption` 和 `NpcPurchases` 只包含到失败检查为止算出的部分分配，仅供诊断参考，不能据此消费材料或执行补购。`OwnInventory` 的补购为空，`MaterialPayment` 与 `ServicePayment` 为零。启动条件不足时 `TotalPayment` 为空；仅限价或钱包不足时仍保留已算出的总额。工钱为冻结总工作量乘市场规则的 `CommissionMoneyPerWork`，无最低费用。接受时一次提交全部玩家材料消费、NPC 补购、总付款、工钱累计及任务建立，发 `ConstructionStarted`，携带资金来源和两项实际付款。材料款只影响钱包及事件；只有工钱计入 `Economy.UnassignedServicePayments`，不计入任何列车运行的 `ServicePayments`。

每个 `FacilityKey` 以 `JobId` 派生严格 FIFO，不保存第二套队列。队首未结束时，后续任务为 `Queued`，进度和速率为零，不请求资源，即使队首为 `ResourceBlocked` 也不跳队。不同设施可并行；请求 `Priority` 仅参与当地资源仲裁，不影响队列顺序。队首完成或失败后，下一步才处理后继任务，开工通过既有 `ConstructionStateChanged` 可见。排队任务同样检查设施被毁、失去归属或资格、目标消失或被毁的失败条件，不退款。修理和改装从受理起取得锁，排队期间同样适用全部施工锁限制。

已受理委托沿用保存的投入、工作量和电力／劳动系数。合法内容更新不重扣材料、不重收工钱、不回算历史付款；后续报价使用更新后的规则。保存恢复、检查点和严格重放包括资金来源、队列状态与全局工钱累计。普通世界仅保留接受的设计，录制仍保留所有 Completed 命令（包括拒绝）携带的完整设计输入及依赖。
