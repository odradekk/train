# 核心模拟层：列车运行

本页记录列车运行，与[契约目录](core.md)一起使用。

## 当前列车运行

列车能力来自有序成员的保存设计及当前耐久。长度为各车 `LengthCells × 0.5` 米之和，质量为车辆设计质量与已知库存货物质量之和；耐久大于零且定义未缺失的部件提供完整能力。部件定义是否缺失按当前内容判断，与库存容量采用同一规则；恢复不改写保存设计。牵引相加，最高速度取有效动力部件额定速度的最小值，加速度为牵引除以质量。定义型货物使用当前 `Goods`，组装体货物使用世界保存的衍生投影。库存定义缺失时，`MassTons`、`Acceleration` 只表示已知部分，`Blockers` 必含 `content_missing / Train.Inventory`，主体为列车；相关身份使用 `assembly:`、`composite:`、`voxel:` 前缀并按 Ordinal 排序，不能把这些数值当作完整质量和加速度。

每次从静止出发共用检查，依次返回 `control_missing / Train.Vehicles`、`traction_missing / Train.TractionKilonewtons`、`content_missing / Train.Inventory`、`overloaded / Train.Acceleration`。加速度等于规则下限允许出发。手动启动不满足时拒绝；停留后不满足时为 `Waiting`，每步重试。已经行驶的列车按已知质量继续到下一个停车点。铁路规则按 `WorldSummary.RailwayRule` 从当前投影读取，不保存另一个规则副本或添加缺失兜底；引用与普通恢复适配责任沿用世界生成路径。

每步为 1/60 秒，保持加速、巡航、制动三段精确积分。对距离 d、目标速度 u 的每个约束使用 `v² ≤ u² + 2bd`，取最严格者；非零终速峰值为 `v_peak² = (2abd + b·v0² + a·u²) / (a + b)`，受最高速度限制。节点内取车辆能力与站区限速的较小值，前瞻包含节点实际长度；入站前满足限速。Station、计划终点、停车和原路返回在 S 停稳，下一步才开始倒计时；途经越过 S 时消费目标并保持速度出站。正常制动及预测失败时的强制停车例外见[速度](../design/06-railway.md#速度)。

选路权重为线路长度除以最高速度，用时相同时按完整线路身份序列的字典序比较，与邻接枚举顺序无关。敌对聚居地及生物聚落可作当前目标，不能作中间节点。至少一条铁轨没有断点且不在修复才可走，选路不考虑拥堵；加轨施工不关闭原有铁轨。无路时停在节点，活动为 `NoRoute`，原因 `no_route / Train.Plan` 带目标节点身份，每步重新选路。原路返回先在 S 停稳，沿 `EnteredFrom` 反向起步时切换正反标记，断点引起的线路倒车也切换；撤退中的列车按[战斗](core-combat.md#战斗)的目的地选路，未设置目的地时停在原地重试。

按 [#311 批准计划](https://github.com/odradekk/train/issues/311#issuecomment-6139384407)，入线及站台仲裁基于步开始时已提交的状态，同轮后授予的请求看到先授予的占用和入口空间；本步释放的铁轨、站台下一步才可用。等待按首次到达步、稳定 `TrainId` 排序，不由列车数组顺序决定。方向锁从在轨列车的位置派生，空轨解锁；出站预选与 L 点固定铁轨入线检查见[铁轨与通行](../design/06-railway.md#铁轨与通行)。有对向等待时，本方向不再进入本方向锁定轨；只在开放铁轨清空时记录交让，恢复校验拒绝断轨上的交让；未兑现交让优先给对向等待者，较早的本方向等待者不能抢先。

入口及同轨跟车的中心间距至少为两列长度之和的一半加内容最小间距，前车步开始时的位置作为后车制动障碍；不超车、不换轨。进入同向已锁定铁轨还要求入口到跟车界限的距离不小于当前速度的制动距离 `v²/(2b)`；不足时固定铁轨不可进入，在节点等待并写零速度，下一步从静止检查入线。需要站台时按步开始状态预测空闲站台与前方队列，以 S 或 W 为制动目标；已定驶出铁轨不可进入（或尚无可定铁轨）时提前向 L 制动。预测不登记首次排队步，只有中心在 L 正式入线检查失败才登记 `QueueEnteredStep`；预测失效且来不及制动时才强制停车。规则不变且没有断点的正常运行中，线路上每步减速不超过制动减速度乘步长。

`RailwayRuleProjection` 提供 `ApproachLengthMeters`、`DepartureLengthMeters`、`NodeSpeedLimitMetersPerSecond`，均须为正有限值，初值为 80 米、80 米、15 米/秒；铁路规则文件 `schema_version` 为 2。`SettlementTemplateProjection.PlatformLengthMeters` 为非负有限值，供节点站台段使用；模板有站点地块时必须大于 0，没有时为 0。

强制停车例外指来不及制动时，在该步写入零速度，并将列车停在应停点。到达线路端点或节点出口 L 的步骤截断于端点，舍去该步剩余时间。

节点尺度或限速变化不使 `PublishContent`、`PrepareRestore` 拒绝。静止在 W、S 的列车移到新对应点；`Departing` 阶段且活动为 `Waiting`、已有 `QueueEnteredStep` 的列车正在 L 等铁轨，移到新 L；其他 `Departing` 列车（包括失去动力停在中途或在 L 为 `Stopped` 的列车）及行驶中的列车保留距离并截到新 L 内，不依赖旧内容投影；必须停车却越过新 S 的进站列车置于 S、速度归零；不必停车且已越过新 S 的进站列车转为 `Departing`，并依序消费当前节点的途经点；超出新限速者按制动减速度减速。不新增保存字段。P 取聚居地保存模板副本的 `SettlementTemplateProjection.PlatformLengthMeters`，由 Content 按设计 16 的站点包络计算，Core 不写包络常数。

铁路规则变化后，已在线的同轨间距可暂时低于新规则值。`PublishContent` 和 `PrepareRestore` 照常接受并保留位置；后车速度归零、原地保持，直到前车拉开到规则值后恢复跟车，间距不再缩小。这是规则变化时的强制停车例外，不新增保存字段或拒绝代码。新车入线仍按当前规则检查入口间距；规则不变时，每个已提交步都保持规则要求的间距。

节点内每列车各走一条站内线，互不跟车、互不阻挡。预约站台号就是该列车的站内线；无站台号的列车所走的线由场景分配（#443）。

非敌对聚居地的 Station 停留在进入节点时正式预约，按申请步、稳定 `TrainId` 仲裁，从 1 起取最小空闲站台号；空节点、敌对聚居地、生物聚落及途经不预约。预约后的站台号保留至驶出 L 或删除、解编，停车、换计划和出站等待不释放，占用查询按预约派生。在 S 停稳且获所需预约的当步进入 `Servicing`，余时设为完整停留步数，下一步开始递减；索引仍指当前 Station，余时到零后消费；同节点连续 Station 沿用预约。站台数减少不重排已有预约，已有编号可以超过新容量，占用可暂时超过容量，新申请等待。越 W 但尚未越 S 的进站列车，以及静止在 S 的列车，因换计划或启动新增本站 Station 时立即按产生该需求的模拟步申请；没有可用站台时不拒绝命令，制动到 S，以 `Docked`、空站台号、`Waiting`、`platform_wait` 等待，获预约后开始停留。已越 S 的 `Departing` 列车（含停在 L 等铁轨的）保留已定驶出线路，先驶出，返回本站时再预约和停留。

`Stopped` 表示手动停车、无计划或单次完成；`Running` 表示在线路上或节点内行驶；`Servicing` 表示 Station 停留计时；`Waiting` 表示静止出发能力不足、等铁轨或等站台；`NoRoute` 表示当前目标无路：进站列车停在 S，阶段为 `Docked`；从 W 生成或编组后出发的列车停在 W，阶段为 `Waiting`；`Combat` 表示列车因参战而停在节点（原本为 `Stopped` 的列车保持 `Stopped` 但仍参战），`Retreating` 表示正在脱离或驶向撤退目的地，二者规则见[战斗](core-combat.md#战斗)。等铁轨的原因是 `track_wait / Train.Position`，附线路身份；等站台是 `platform_wait / Train.Plan`，附节点身份，均通过 `Blockers` 与 `TrainActivityChanged.Reasons` 提供，持续等待不重复发活动事件。`Phase` 与活动分别表达位置阶段和业务状态；出站等待铁轨仍为 `Departing`，新列车在 W 为 `Waiting` 阶段和 `Stopped` 活动。`NextEdge` 表示已决定线路，出站等待时仍可保留，不因活动为 `Waiting` 清除。

有断点的整条铁轨禁止新车进入。已越过断点的列车继续驶出；断点前受阻组按跟车约束停车，等旧方向未受阻列车离开该铁轨后整组同时反向，切换 `OrderFacing` 并回出发节点重新选路，保持同轨同向且不超车。近距离断点来不及制动时，中心停在断点位置、速度归零；中心恰在断点上算受阻，已因该断点倒车的列车除外。`ReturningFromBreak` 记录实际掉头的断点，避免零牵引、中心等于断点时再次翻转；返程再遇断点则停车等待修复，不再反复掉头。

`TrainArrived` 在每次实际从线路到达节点时发出，包括中间节点；同节点重复目标和在当前节点启动不会重复发出。到达事件先于该到达引起的 `TrainActivityChanged`；活动变化才发活动事件，反复等待不重复通知。编组的 `TrainCompositionChanged` 携带新列车及完整有序成员；解编携带被删除列车及空成员数组。`CurrentRun` 返回当前计划的运行累计，`RecentReports` 从全局近期报告按列车筛选；结束累计时发 `RunReported`，具体边界见下文“[自动交易与运行报告](core-recording.md#自动交易与运行报告)”。交战规则在生成或编组时按当时有无完好武器写入默认值（有武器为 `(ArmedFirst, ArmedOnly, Engage, 0)`，否则为 `(ArmedFirst, ArmedOnly, Retreat, 0)`），之后只由 `SetTrainEngagement`／`SetSettlementEngagement` 修改，不随武器损坏变化。

| 命令 | 当前条件及领域拒绝（代码 / 字段） |
| --- | --- |
| `FormTrain` | 有序成员非空且唯一：`composition_empty`、`vehicle_duplicate / OrderedVehicles`；车辆存在、玩家自有、未编组、在同一非敌对聚居地停放：`object_missing`、`not_owned`、`vehicle_in_train`、`vehicles_not_colocated`、`access_denied / OrderedVehicles`；新列车或库存编号耗尽为 `identity_exhausted / World.Next`。成功在 W 创建阶段 Waiting、活动 Stopped、无线路引用和站台号的列车及空库存，回执给出二者身份，保留车辆输入顺序 |
| `DisbandTrain` | 在节点且速度为零：`train_not_stationary_at_node / Train.Position`；节点不是聚居地：`settlement_unavailable / Train.Position`，聚居地敌对：`access_denied / Train.Position`；库存必须为空：`inventory_not_empty / Train.Inventory`。停留、等待、无路的静止列车也可解编；删除列车、计划和库存，成员原地停放，身份不复用 |
| `ReplaceRunPlan` | 非空：`plan_empty / Plan.Stops`；目标存在：`object_missing / Plan.Stops[i].Node`，主体为该节点；循环目标不能全部相同：`plan_degenerate / Plan.Stops`。Station 可设在任何现存节点，空节点、敌对聚居地及生物聚落均照常停留计时。同一 Station 内同一货物重复是业务拒绝 `plan_duplicate_good`，字段为 `Plan.Stops[i].GoodsRules[j].Good`；与目标存在检查一起按停靠点顺序进行，每站先检查目标存在，再检查重复货物，均在 `plan_degenerate` 之前。索引置 0，放弃剩余停留，保持启停意图；运行在线路上先到前方节点，保留节点内位置和预约，进站可重选下一线路、出站保持已定线路；出站阶段（含停在 L）依序消费新计划开头指向当前节点的途经点，本站 Station 仍先驶出再返回；其余途经目标越 S 才消费，停车列车保持停车 |
| `StartTrain` | 先对所有列车检查计划存在且未完成（`plan_missing / Train.Plan`、`plan_completed / Train.NextStopIndex`）；随后非 `Stopped` 时接受并清除尚未完成的停车请求，其余业务状态保持不变；仅在 `Stopped` 时检查上述静止出发条件。暂停停留沿用已有预约恢复余时，否则沿正常节点阶段继续；缺预约时申请，不足则等待 |
| `DebugAddRailBreak` | 调用前提：线路身份非零、轨号为正 `int`、距离为有限非负米数，否则在取得编号前抛 `ArgumentException`。线路不存在拒绝 `object_missing / Edge`；轨号超过现有铁轨数或距离超过线路长度拒绝 `invalid_state / Break`，两者主体均为线路；断点编号耗尽拒绝 `identity_exhausted / World.Next.Break`。成功增加断点、推进编号并发出 `RailBroken`，同轨未兑现交让清除。该线路已开启自动修复时，同一命令按新断点建立修复任务，事件在 `RailBroken` 之后；任务编号耗尽则整条命令拒绝 `identity_exhausted / World.Next.Job`，断点也不写入 |
| `DebugSpawnCreatureGroup` | 开发命令。调用前提：节点身份非零、生物定义为合法身份、数量为正 `int`，否则在取得编号前抛 `ArgumentException`。节点不存在拒绝 `object_missing / Node`；活动投影没有该生物或其生命值不为正拒绝 `content_missing / Creature`，相关身份为生物定义；生物群或生物编号放不下拒绝 `identity_exhausted / World.Next.Creature`，三者主体均为节点。成功在节点建立一个无家（`Home` 为空）、活动为游荡的新生物群，成员是指定数量的同种满血生物，回执 `Created` 为生物群身份，不发事件。新群与清剿后失去聚落的生物群一样游荡；节点相邻全是 NPC 聚居地时留在原地，参加该节点的战斗 |
| `StopTrain` | 静止时原地停车并暂停停留；进站制动到 S，来不及则强制停车；已过 S 继续驶出并在下一节点停车；在线路上请求停在前方节点 S。重复请求保持同样业务状态 |
| `ExpandRail` | 调用前提：线路和材料来源编号非零，否则在取得编号前抛 `ArgumentException`。依序只返回首个问题：线路不存在 `object_missing / Edge`，主体为线路；活动投影没有世界铁路规则 `content_missing / RailwayRule`，主体为线路，相关身份为规则身份；Rail 货物缺失 `content_missing / RailwayRule.RailGood`，主体为线路，相关身份为货物键；现有轨数加进行中加轨任务数达到上限 `invalid_state / Edge.Tracks`，主体为线路；来源不存在 `object_missing / MaterialSource`，主体为聚居地；来源不是玩家 `not_owned / MaterialSource`，主体为聚居地；库存不足 `insufficient_goods / MaterialSource.Inventory`，主体为聚居地，相关身份为货物键；任务编号耗尽 `identity_exhausted / World.Next.Job`。件数 `ceil(长度 × RailUnitsPerMeter)`，工期步数 `ceil(长度 × BuildSecondsPerMeter × 60)`，各取整一次，转换用 checked。成功扣一次 Rail、建立已开工任务，回执 `Created` 为任务编号，事件 `RailwayWorkStarted`。加轨不关闭原有铁轨。到期当步 `Tracks` 加 1 并发 `RailExpanded`，新轨下一步起参与调度 |
| `RepairRail` | 调用前提：线路、断点和材料来源编号非零，否则抛 `ArgumentException`。依序只返回首个问题：线路不存在 `object_missing / Edge`，主体为线路；断点不在该线路 `object_missing / Break`，主体为线路；该断点已有任务（含未开工）`busy / Break`，主体为线路；活动投影没有世界铁路规则 `content_missing / RailwayRule`，主体为线路，相关身份为规则身份；Rail 货物缺失 `content_missing / RailwayRule.RailGood`，主体为线路，相关身份为货物键；来源不存在 `object_missing / MaterialSource`，主体为聚居地；来源不是玩家 `not_owned / MaterialSource`，主体为聚居地；库存不足 `insufficient_goods / MaterialSource.Inventory`，主体为聚居地，相关身份为货物键；任务编号耗尽 `identity_exhausted / World.Next.Job`。修复没有轨数上限检查。修复件数用规则整数 `RepairRailUnitsPerBreak`，工期步数 `ceil(RepairSecondsPerBreak × 60)`。成功同样扣料、开工并发 `RailwayWorkStarted`。完成当步删除断点和任务，清除指向该断点的 `ReturningFromBreak`，发 `RailRepaired`；正在返程的列车保持当前方向开回出发节点，出站列车保留已定 `NextEdge`。材料不足直接拒绝，不建任务 |
| `SetAutomaticRailRepair` | 调用前提：线路编号非零，材料来源为空（关闭）或非零，否则抛 `ArgumentException`。线路不存在 `object_missing / Edge`。重复设置同一来源（含重复关闭）接受且不发事件。关闭删除该边未开工修复任务，已开工任务继续，不发事件，状态以查询为准。开启或更换来源时，来源不存在 `object_missing / MaterialSource`，非玩家 `not_owned / MaterialSource`；不检查库存和铁路规则。更换来源时，该边未开工任务改用新来源并当即重新判断。该边尚无修复任务的断点按断点号依次占用任务号：够料则扣料、冻结工期、清空阻塞并发 `RailwayWorkStarted`；材料不足、铁路规则投影缺失（`content_missing / RailwayRule`）、Rail 货物缺失或来源在重判时不再可用，则建立未开工任务（进度 0、投入为空、阻塞为原因），发一次 `RailwayWorkChanged`，不发 Started。阻塞变化才再发 Changed。新任务编号放不下时整条命令拒绝 `identity_exhausted / World.Next.Job`，设置不改。回执 `Created` 只含本次新建的任务。手动 `RepairRail` 遇到该断点已有任务时拒绝 `busy / Break` |

后四个命令不存在的列车统一为 `object_missing / Train`，主体为该列车；编组中缺失车辆的主体为该车辆实例，非自有用 `not_owned`，聚居地敌对用 `access_denied`。正常拒绝仍前进命令编号和边界序号并录制，无事件且业务状态不变。单次计划完成后保留计划、索引为空，须替换计划才可再次启动。通常活动为 `Stopped`；出站列车换计划并消费完开头当前节点的途经点后，若仍在行驶，保留已定驶出线路，设置 `StopRequested = true`，继续驶出并在下一节点 S 停稳后转为 `Stopped`。已静止在 L 的列车原地转为 `Stopped`，清除停车请求和排队步。这类仍在行驶的列车若到 L 正式入线检查被拒绝，也立即原地转为 `Stopped`，清除停车请求和排队步，不转为 `Waiting`。这些停车均保留预约和下一线路；预约仍到实际驶出、删除或解编时才释放。相邻重复目标依序消费，零停留 Station 仍是停车点。

铁路施工在列车移动和聚居地生产之后、紧接建城推进：先按任务编号推进已开工任务，已用步数加 1，等于冻结工期则当步完成；再按任务编号尝试让未开工任务开工。当步开工的任务从下一步开始计时。修好的铁轨和新铁轨从下一步起参与调度，与本步释放的铁轨下一步才可用一致。到期的加轨任务不等编号更小的任务；显示轨号为 `Tracks + 1 + 同一条边上编号更小的进行中加轨任务数`，修复任务取断点轨号。查询的 `Works`、`FindJob` 的线路分支和 `EdgeView.AutoRepairMaterialSource` 由任务与自动修复设置算出；保存的线路 `Works` 保持为空。未开工进度为 0，已开工进度为已用步数除以冻结工期。

普通查询无副作用。报价不锁价格、库存、容量或资金，也不是提交凭证。只读派生计算若遇内部故障，返回 `CoreResult.Faulted` 及该视图的位置，不记录命令或修改任何活动会话；不能用旧视图的错误冻结新局。命令执行重新裁决时发生内部故障，仍按下节冻结。这一区分也适用于独立新局/读档准备与活动运行。

`Submit` 和 `Advance` 返回本次产生的 `EventEnvelope`，按 `Position`、该位置内的 `Ordinal` 排列。宿主在完整边界之后分发；核心不提供回调、消费者游标、补发窗口或另一个订阅系统。正常拒绝和故障回滚不返回未提交的业务事件；多步调用仍返回此前已经完成步骤的事件。

事件用于通知和瞬时表现。射击与命中事件保存武器部件、目标部件/生物及地点；实例表现引用保留当时设计，即使对象在同一步被删除也能解释。生物查询和表现引用保留种类身份 `Definition`，不保存设计；体素通过与核心视图或事件配对的固定内容视图调用 `FindCreature(Definition)` 取得当前种类外观。持续损伤、人员和库存以查询为准。后来打开场景只重建当前状态，不补演遗漏的射击。战报由宿主按[界面规则](../design/08-window-and-ui.md)保留，不等于核心录制或存档。
