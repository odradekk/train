# 核心模拟层：生产与资源

本页记录生产与资源，与[契约目录](core.md)一起使用。

## 加工与回收

计划直接读取每座聚居地已保存的 `Template.ProductionPlans`，按地块的 `TemplateNumber` 定位建筑；没有第二份运行计划。源模板更新不改变该副本。不新增控制台命令，也不因计划编辑增加权威状态；核心存档格式和命令输入格式保持当前版本。

`SetProductionPlan` 只改该地块、该能力的一条计划。`Facility` 或 `Process` 为 null、建筑编号为 0、能力种类未定义或为 `Construction`、流程种类无法映射，或流程定义不是合法身份时，在取得命令编号前抛出：空引用为 `ArgumentNullException`，其余为 `ArgumentException`。编码不再单独校验，只在能力或流程种类无法映射时抛 `ArgumentException`。流程种类沿用世界 JSON 的 `production` 与 `recovery`。

业务拒绝按第一次命中返回，主体是该建筑实例，命令编号已消耗，世界与事件不变：没有聚居地地块持有该建筑或实例不存在时为 `object_missing` / `Facility`；聚居地关系或建筑归属不是 `PlayerOwned` 时为 `not_owned` / `Facility`；`min(实例结构耐久, 设计上限) <= 0` 时为 `instance_destroyed` / `Facility`；设计部件没有声明该能力时为 `conditions_not_met` / `Facility.Capability`，有效能力为零仍可接受；流程不在投影中或已列入缺失时为 `content_missing` / `Process`；开采能力只接受开采流程，加工能力接受加工或回收、不接受开采时为 `conditions_not_met` / `Process`（开采流程使用 `ProcessRef.Production`）；建筑设施标签不含流程要求的标签时为 `conditions_not_met` / `Facility.Tags`。改装、修理或拆解锁定不是拒绝。没有计划时锁定仍为停用；加上计划后进入锁定。提交时不检查地貌，接受后地貌不符由既有求值表现为“不满足条件”。优先级是任意 `Int32`，升序，没有范围拒绝。

尚无该设施键的设施行时，按占用建筑的方式追加空行（无批次、空缓冲、上一步读数为零），再按建筑身份、能力种类排序。已有行保持批次、进度、缓冲和上一步读数。计划按地块号、能力种类排序。没有删除命令；停用是 `Enabled=false`。接受后重算定义依赖。更换或停用计划不取消批次、不退料、不改冻结产出和进度。`FacilityView.Process` 立即是新计划；`ActiveBatchView.Process` 仍是批次冻结的流程，二者可以不同。新流程的原料只在下一批启动时投入一次。

`FindFacility` 支持 `CapabilityKind.Production`（加工与回收）及 `Extraction`（开采）。建筑不在聚居地地块上，或既无该能力的计划、设计也没有声明该能力时返回 `Missing`。设备损坏或缺定义造成有效能力为零，不会使仍有计划或声明能力的设施消失。`Construction` 键仍待后票，抛出注明 `FindFacility` 和能力种类的 `NotImplementedException`。`PotentialConsumption` 由全城同一次潜在电力／劳动分配换算为每秒原料与燃料消耗，使用计划流程；与 `FindSettlement.Facilities` 使用同一计算。

#346 要给设施写入第一条计划时，遍历 `SettlementView` 地块上的建筑，对 `Production` 和 `Extraction` 分别调用 `FindFacility`；结果不是 `Missing` 就可以设置计划。`FindSettlement.Facilities` 是同一组 `Find` 结果：包含已有计划的设施，也包含设计声明了该能力但还没有计划的设施（停用、流程为空），不包含既无计划也未声明能力的建筑。是否已有计划看返回的 `Process` 是否为空。已有计划的 `Process`、`Enabled`、`Priority`、`State`，以及在制批次的 `Batch.Process`，都从 `FindFacility` 读取。已接受命令由 `ExecuteCommand` 既有的 `ProductionQueries.StateChanges` 在 `ProductionState` 变化时发出 `ProductionStateChanged`；只改优先级或流程而不改变状态时不发事件，界面重新查询。

状态按流程缺失、没有计划则为停用、锁定、计划未启用则为停用、能力为零、不满足条件、出料阻塞、等待原料、运行中的顺序判断。流程缺失指计划流程缺失，或已有批次的流程缺失；二者任一缺失则暂停并保留进度，批次只在两个流程都存在时推进。锁定来自该建筑的改装、修理或拆解任务，且只作用于已有计划的设施；流程缺失仍优先于锁定。没有计划的建筑保持停用。停用只阻止启动新批次；当前流程的 Tag／地貌要求不再满足时，状态为“不满足条件”，已有批次仍按冻结规格完成。能力为零或流程缺失时保留批次进度。流程补回后继续，不重新扣原料。`CurrentSpecificationDiffers` 比较批次自身流程的当前投影与冻结规格，不比较计划流程。

启动步只原子投入完整原料并建立批次，下一步才工作。工作请求上限取当前有效能力与剩余工作量所需速率的较小值，使用冻结的工作量和电力／劳动系数。生产推进接收 #382 人口换算提供的劳动容量，与基本／改善消费在同一次 `SettlementResources.Resolve` 中仲裁；人口规则或本期必需货物缺失时，人口停用并提供零劳动力。

完成产出和已有缓冲在批次请求仲裁之后按计划优先级、建筑身份入库，同一批多输出按货物身份的 Ordinal 顺序。使用扣除租约和预留后的真实可用容量，不扩大超容；本步完成或步初缓冲非空的设施不在本步启动下一批。生产能力失效不阻止已完成货物的内部转入。仅冻结产出或缓冲货物定义缺失时，仍按件数转入库存并保留身份；已有库存规则将其标为缺失且排除原料和燃料使用。计划流程或已有批次的流程缺失则暂停执行，不用冻结参数替代缺失定义。批次只在两者都存在时推进。

`SettlementState.ProductionFacilities` 按 `FacilityKey`（建筑身份、能力种类）保存 `Batch`、`OutputBuffer` 和上一步的 `LastActualWorkPerSecond`、`LastLimits`、`LastPowerDemand`、`LastActualSupply`、`LastAllocatedLabor`。同一建筑的加工和开采计划独立运行，共同竞争电力、劳动力与输出容量；相同优先级、建筑身份的请求及入库按能力种类排序。批次保存流程、启动版本、冻结产出、总工作量、两项系数和进度。请求功率为本步最大有用速率乘冻结电力系数，实际供电及实际劳动占用为获分配速率乘对应冻结系数；无工作请求时结果归零／空。`ActualWorkPerSecond` 和 `Limits` 直接返回上一步结果，能力、主状态和规格差异按当前内容派生。内容发布不重算上一步结果；读档、旧读视图和检查点保留它们。电力与实际劳动占用查询汇总每座聚居地全部设施的上一步结果，包括开采；施工另保存聚居地上一步汇总，以保留任务完工移除后的读数。

## 节点资源

开采使用本节点的储量作为批次投入，不读取库存中的同种货物。启动时经同一次 `SettlementResources.Resolve` 按优先级、建筑身份、能力种类原子扣除完整 `Extracts`，下一步才工作；冻结产出为同种同量货物。设施标签、地貌、有效开采能力、缺失流程、停用、供电、劳动力与 OutputBuffer 沿用普通批次规则。资源不足时等待完整一批，不部分开采。不可再生项扣到零时删除，可再生项保留零储量；设施和计划保留。

可再生项使用自己已保存的 `ResourceRenewal.Renewable(Maximum, UnitsPerSecond)`。每步按 `UnitsPerSecond / 60` 加入小数余量，累计出的整量恢复储量；恢复量最多为步初距离上限的差额，到达上限时丢弃余量。恢复在本步开采扣量之后提交，因此本步恢复量下一步才可开采；步初已满的项不会因本步开采而取得恢复量。非零余量按节点、货物身份保存于 `resource_recovery`，参与检查点及严格重放，其货物依赖由已有节点资源引用覆盖。普通恢复和内容发布不重算上限、速率或余量。

不可再生项的参数来自 `world.source` 中已解析的资源生成配置。`resource_regenerations` 按货物保存生成时全世界该货物的初始储量合计及下一次抽取序号；初始总量使用 `Int128`，不以当前剩余量重算，其货物依赖由已有生成配置引用覆盖。内容发布和普通恢复不改变这些参数。周期步数为 `max(1, ceil(RegenerationIntervalSeconds × 60))`，乘积按一次 `double` 运算后向上取整；周期超过 `ulong` 可表达步数时，在可表达的会话时间内不会到期。各货物的整数检查进度从已保存的完成步数与自身周期派生，不另存进度。步初完成步数为 `n` 时，恰在 `(n + 1) % 周期步数 == 0` 的步骤检查，读取步初的全世界剩余量与资源存在性；恢复后继续同一周期。

剩余量严格低于初始总量乘触发比例时，每个检查周期最多新增一项。阈值比较使用输入比例的精确二进制值和整数储量，不把大整数合计转成 `double`；初始量为零或比例为零时不触发。候选是生成表允许地貌、步初没有该资源的节点，占据者不限，按 `NodeId` 升序排列。新增量取配置的闭区间，不再次判定初始生成的出现概率。无候选时不抽取、不推进序号，下个周期继续检查。抽到零时不新增、不发事件，但消耗序号；下个周期继续检查。新储量下一步才可开采，世界图结构和地貌不变。没有扣量、没有未满可再生项、没有到期检查的空步直接保留已提交的世界记录和节点数组；只有储量实际变化的节点才重建。

随机沿用 `KeyedRandom` 算法版本 1。选节点的用途是 `resource.regeneration.node.v1`，抽储量的用途是 `resource.regeneration.reserve.v1`；两者的 `WorldSeed` 为世界种子，分量依次为货物标识 UTF-8 字节数、每个字节各自扩为 `ulong`、该货物抽取序号。序号从零开始，有候选且实际抽取时递增。编码及混合遵循已有 `KeyedRandom` 小端编码；节点索引和储量均用已有整数区间映射。固定向量见 `ResourceStateTests`。

资源事件在生产／人口提交后、生产状态变化事件前发出。同一步先发全部 `ResourceExhausted`，再发全部 `ResourceRecovered`，各组按节点身份、货物身份的 Ordinal 顺序；最后发 `ResourceRegenerated`，按货物身份的 Ordinal 顺序。不可再生项扣到零并删除时发 `ResourceExhausted(Renewable=false)`；可再生项扣到零时发 `ResourceExhausted(Renewable=true)`。`ResourceRecovered` 只在恢复提交将零储量变为正数时发出，`Quantity` 是本步恢复后的储量；其他恢复步及单纯到达上限不发事件。同一步允许先耗尽再恢复，两事件按上述顺序发出。`ResourceRegenerated.Quantity` 是实际新增量。查询不会推进周期、改变储量或发事件，旧读视图保持不变。

本票只提供核心内部情景的模块与独立进程证据，不新增 builtin 开采内容；真实内容装配及四模块交接仍由阶段二 #299 验证。本票未新增命令，控制台手册不适用。
