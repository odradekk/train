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

`FindInstance` 返回的 `PartDamageView.DefinitionMissing` 与列车能力、库存容量共用部件缺定义规则，每次查询都结合保存设计的缺失标记与当前读视图的内容投影判断。普通恢复不将查询标记写回保存设计，也不改写部件耐久记录。

`Lookup.Missing` 表示对象已不存在，不自动换选另一个对象。UI 名称与设计展示资料按内容身份向内容读取。人口查询的当前劳动力已由核心计算；本期执行版本不等于当前发布版本，调用方不能从历史规则重算劳动力。消费发生在统计结算之前，满足率和周期摘要来自同一已提交边界，见[人口规则](../design/18-population-and-labor.md)。

查询只公开调用所需结果，不公开消费／人口余量、调度倒计时、完整冻结规则或批次功耗与劳动系数。批次版本、冻结产出、总量与进度、`CurrentSpecificationDiffers` 及仓储容量划分有明确业务用途，继续公开。内部规则用模块测试或推进后的可观察结果验证，不为测试增加普通查询字段；存档仍完整保留内部权威状态。

场景从 `WorldSummary.WorldSeed` 取得当前世界的原始种子，普通读档与严格重放均保留它，不要求宿主解析核心载荷。`WorldSummary.RailwayRule` 是本局所选铁路规则身份，体素据此查询内容的曲线半径与坡度资料，不从规则目录猜选项。`SimulationTime` 公开既定的每秒 60 步及每步秒数，这是构建常量，不是可配置的另一套时钟。以上查询和下述列车显示状态由[体素公开契约](https://github.com/odradekk/train/issues/289)补齐。

`TrainView.PlatformQueueEnteredStep` 为当前有效站台申请首次入队的模拟步，可空。仅当列车位于节点、尚未进入 Departing、当前计划需要友方站台、尚未持有站台预约且已有申请队龄时非空；此条件与查询中的 `platform_wait` 阻塞一致，包含尚在 Approaching 的申请者。站台分配依次按该值、`TrainId.Value` 升序决定；不同入口共享同一节点的站台申请顺序。出线等待、已获站台及未申请列车返回空。该字段投影已有存档队龄，不新增权威状态或改变存档格式。

`TrainPosition.Node(NodeId Id, EdgeId? EnteredFrom, int? EnteredTrack, EdgeId? NextEdge, int? NextTrack, NodeTravelPhase Phase, double DistanceMeters, int? PlatformNumber)` 是节点内唯一权威位置；`NodeTravelPhase` 为 `Approaching / Waiting / Docked / Departing`。删除 `NodeTransitView` 和 `TrainView.NodeTransit`，内部也不另存转场。`EnteredFrom`、`EnteredTrack` 为实际进入线路及铁轨，两者同时为空或同时非空；轨号须为正，允许超过线路当前铁轨数。`NextEdge` 为已决定的下一线路；`NextTrack` 在进入 `Departing` 时确定，不提前占用铁轨。非 `Departing` 阶段 `NextTrack` 为空；非空时须有 `NextEdge`，轨号在当前铁轨数内。出站无开放铁轨且没有仍存在的原选择时允许为空；原选择仍存在但关闭、又无替代时保留。体素消费实际进出线路及铁轨，不重算选轨。选择快照、失效重选与 L 点检查时序见[铁轨与通行](../design/06-railway.md#铁轨与通行)。`DistanceMeters` 为中心沿节点路径的米数，`PlatformNumber` 为可空预约站台号。`NodeView` 仅增加 `ApproachLengthMeters`、`PlatformLengthMeters`、`DepartureLengthMeters`，不公开限速、总长或停站点；三段尺度和阶段转换见[节点与站台](../design/06-railway.md#节点与站台)。

`TrainPosition.Edge.DistanceMeters` 表示整列长度中心，`TrainView.OrderFacing` 表示车辆列表哪一端沿最近实际行驶方向在前。编组默认首端在前，线路上倒车及节点原路返回时切换标记；节点驶入其他线路时保持，等待中保留最近实际状态。两项均随存档恢复，场景不自行记一份持久朝向。标记不参与牵引、选路或战斗计算；中心间距、成员被毁后的收拢及整列在节点切换的规则见[铁路位置](../design/06-railway.md#位置)。
