# 核心模拟层：读取与事件约定

本页记录读取与事件约定，与[契约目录](core.md)一起使用。

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

`EconomyView.CashFlows` 返回本局所有实际钱包变动，按提交顺序保留，不设条数或天数上限。`CashFlowEntry.Amount` 是带符号的实际钱包增减：收入为正、支出为负，零金额不记录。每次委托按总付款记一笔，材料款和工钱不拆成两笔。只有货物买卖记录 `Good` 和正数 `Quantity`；其他项目的 `Good` 为空、`Quantity` 为 0。`Settlement` 和 `Train` 按实际交易对象填写，无对应对象时为空；记录不依赖对象继续存在。`Position` 是该笔变动实际提交的完整模拟边界，包含命令序号，不能用读取时的位置替代。运行报告及界面可从明细汇总净现金流、收入、支出和按天金额；净现金流不扣除货物成本。

`SettlementView.TodayProduction` 返回自有聚居地当天按货物汇总的完成产量，每天游戏时间 00:00 分日。批次完成即计入，包括尚在设施输出缓冲中的货物；稍后从缓冲入库不重复计数，交易、运输等仅入库的货物不计入。非自有聚居地返回空数组。该统计与现金明细均属于权威状态，普通保存恢复和严格重放保留它们。

`FindInstance` 返回的 `PartDamageView.DefinitionMissing` 与列车能力、库存容量共用部件缺定义规则，每次查询都结合保存设计的缺失标记与当前读视图的内容投影判断。普通恢复不将查询标记写回保存设计，也不改写部件耐久记录。

`Lookup.Missing` 表示对象已不存在，不自动换选另一个对象。UI 名称与设计展示资料按内容身份向内容读取。人口查询的当前劳动力已由核心计算；本期执行版本不等于当前发布版本，调用方不能从历史规则重算劳动力。消费发生在统计结算之前，满足率和周期摘要来自同一已提交边界，见[人口规则](../design/18-population-and-labor.md)。

查询只公开调用所需结果，不公开消费／人口余量、调度倒计时、完整冻结规则或批次功耗与劳动系数。批次版本、冻结产出、总量与进度、`CurrentSpecificationDiffers` 及仓储容量划分有明确业务用途，继续公开。内部规则用模块测试或推进后的可观察结果验证，不为测试增加普通查询字段；存档仍完整保留内部权威状态。

场景从 `WorldSummary.WorldSeed` 取得当前世界的原始种子，普通读档与严格重放均保留它，不要求宿主解析核心载荷。`WorldSummary.RailwayRule` 是本局所选铁路规则身份，体素据此查询内容的曲线半径与坡度资料，不从规则目录猜选项。`SimulationTime` 公开既定的每秒 60 步及每步秒数，这是构建常量，不是可配置的另一套时钟。以上查询和下述列车显示状态由[体素公开契约](https://github.com/odradekk/train/issues/289)补齐。

`TrainView.PlatformQueueEnteredStep` 为当前有效站台申请首次入队的模拟步，可空。仅当列车位于节点、尚未进入 Departing、当前计划需要友方站台、尚未持有站台预约且已有申请队龄时非空；此条件与查询中的 `platform_wait` 阻塞一致，包含尚在 Approaching 的申请者。站台分配依次按该值、`TrainId.Value` 升序决定；不同入口共享同一节点的站台申请顺序。出线等待、已获站台及未申请列车返回空。该字段投影已有存档队龄，不新增权威状态或改变存档格式。

`TrainPosition.Node(NodeId Id, EdgeId? EnteredFrom, int? EnteredTrack, EdgeId? NextEdge, int? NextTrack, NodeTravelPhase Phase, double DistanceMeters, int? PlatformNumber)` 是节点内唯一权威位置；`NodeTravelPhase` 为 `Approaching / Waiting / Docked / Departing`。删除 `NodeTransitView` 和 `TrainView.NodeTransit`，内部也不另存转场。`EnteredFrom`、`EnteredTrack` 为实际进入线路及铁轨，两者同时为空或同时非空；轨号须为正，允许超过线路当前铁轨数。`NextEdge` 为已决定的下一线路；`NextTrack` 在进入 `Departing` 时确定，不提前占用铁轨。非 `Departing` 阶段 `NextTrack` 为空；非空时须有 `NextEdge`，轨号在当前铁轨数内。出站无开放铁轨且没有仍存在的原选择时允许为空；原选择仍存在但关闭、又无替代时保留。体素消费实际进出线路及铁轨，不重算选轨。选择快照、失效重选与 L 点检查时序见[铁轨与通行](../design/06-railway.md#铁轨与通行)。`DistanceMeters` 为中心沿节点路径的米数，`PlatformNumber` 为可空预约站台号。`NodeView` 仅增加 `ApproachLengthMeters`、`PlatformLengthMeters`、`DepartureLengthMeters`，不公开限速、总长或停站点；三段尺度和阶段转换见[节点与站台](../design/06-railway.md#节点与站台)。

`TrainPosition.Edge.DistanceMeters` 表示整列长度中心，`TrainView.OrderFacing` 表示车辆列表哪一端沿最近实际行驶方向在前。编组默认首端在前，线路上倒车及节点原路返回时切换标记；节点驶入其他线路时保持，等待中保留最近实际状态。两项均随存档恢复，场景不自行记一份持久朝向。标记不参与牵引、选路或战斗计算；中心间距、成员被毁后的收拢及整列在节点切换的规则见[铁路位置](../design/06-railway.md#位置)。

`PreviewTrainFormation(orderedVehicles, train)` 在当前读视图预览有序编组。`train` 为空时预览新编组，非空时预览该列车的原地重编，允许其现有成员。未初始化数组、零车辆身份或零列车身份抛 `ArgumentException`；成员、归属和位置不满足编组条件时只返回首个领域问题。成功返回 `TrainFormationPreview`：`LengthMeters`、`MassTons`、`TractionKilonewtons`、`Capacity`、`Blockers`、`DepartureProblem`。计算沿用列车实际能力规则；新编组按空库存计算，重编包含原库存货物。

容量不足和身份耗尽等提交阻塞放在 `Blockers`，仍可读取成功预览的指标。`DepartureProblem` 只返回首个出发问题，优先顺序为缺控制、缺牵引、库存内容缺失、超载，与实际出发相同；新编组的 `DepartureProblem.Subject` 为 `null`，已有列车则为该列车主体。加速度恰等于下限允许出发。查询不修改世界、不占用身份，也不生成命令或录制。宿主用缺控制和缺牵引提示不可出发，超载显示警告；提交时仍由 Core 重新检查当前状态。

`PreviewRailwayWork(request, materialSource)` 在当前读视图预览扩建一轨或修复指定断点，返回 `RailwayWorkPreview`。`RequiredMaterials` 是完整材料需求，`DurationTicks` 是按当前铁路规则计算的总模拟步数，`Blockers` 是当前阻塞。结果不含金额；铁路施工不花钱。查询不扣材料、不占用任务编号、不生成命令，也不改变旧读视图。提交仍由核心按当时状态重新检查。

`request` 为 `RailwayWorkRequest.AddTrack(Edge)` 或 `RepairBreak(Edge, Break)`。空请求抛 `ArgumentNullException`，零线路、断点或非空的零来源身份抛 `ArgumentException`。`materialSource = null` 是合法预览，返回 `invalid_state / MaterialSource` 阻塞，同时保留可确定的材料和工期。来源不存在、非玩家所有或缺材料时分别返回 `object_missing`、`not_owned`、`insufficient_goods`，也保留估算。线路不存在或铁路规则、钢轨定义缺失时返回相应阻塞，材料为空、工期为零。

预览同时报告适用的阻塞：现有轨数与在建加轨任务数之和达到上限、断点不存在或已有修复任务、规则或来源问题、任务编号耗尽。缺规则时不继续检查来源；来源不可用时不检查其库存。预览阻塞不承诺与命令首个拒绝的顺序相同。内部计算故障返回 `CoreResult.Faulted`，不冻结会话。返回值为可保留的不可变值，线程与生命周期沿用读视图约定。
