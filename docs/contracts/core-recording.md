# 核心模拟层：运行报告与领域录制

本页记录运行报告与领域录制，与[契约目录](core.md)一起使用。

## 自动交易与运行报告

本节按 [#385 批准计划](https://github.com/odradekk/train/issues/385#issuecomment-6197839137)记录实现约定。规则在执行时读取当前关系、库存归属、租约和访问权；关系不匹配的买卖不会改成免费装卸，而是跳过并报告未完成原因。租约到期后允许取货、不允许存货。宣战和占领已由 #352 实现；#350 的战斗删除提供 `AllVehiclesDestroyed` 的触发路径；这些规则的模块证据不代表 #299／#303 的联调已通过。

| 决定 | 行为 |
| --- | --- |
| D1：持久化 | 核心状态格式为 23，命令输入格式和重放外壳格式均保持 2。计划规则保留数组顺序；规则、停靠重评剩余步数、运行累计和全局近期报告均参加捕获、恢复、检查点和严格重放。引用清单包含当前计划规则中的货物；历史报告只保留货物身份，不参与内容引用，名称由界面按身份读取并处理缺失。旧格式 2 命令中空 `goods_rules` 的解码含义不变。 |
| D2：运行边界 | 接受 `ReplaceRunPlan` 时，已有累计先以 `PlanReplaced` 结束并出报告，再为新计划建立累计，`StartedAt` 为该命令边界，无论列车是否停车。循环计划最后一站消费并回到索引 0 时，出 `LoopCompleted` 报告并立即建立下一圈累计；单次完成出 `PlanCompleted` 报告，`CurrentRun` 变为 null；解编出 `TrainDisbanded` 报告。`StopTrain`／`StartTrain` 不结束或新建累计，已完成计划的启动返回 `plan_completed`。`AllVehiclesDestroyed` 由 #350 的战斗删除触发，不增加其他结束分支。 |
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

## 完整领域状态与录制

载荷读取和适配必须保留设计/损伤、对象及编号进度、等待顺序、批次与任务冻结规格、物资预留/输出、人口消费和统计进度及两类余量、本期规则、燃料电量、租约、现车、自动重评余时和经营报告。内部字段布局不属于公开数据模型；格式版本和完整权威字段定义仍由核心编码器在实现时维护，不能以本页查询字段代替存档字段。

数值规范编码使用十进制整数，不给 `Money`/`BasisPoints` 的线格式增加 `Value` 包装对象；连续量保持既有 round-trip `R` 表示，有限值及集合顺序依[确定性规则](../design/04-deterministic-simulation.md)。命令继续使用既定格式 2 的有序外壳；具体封闭命令负载在实现中由核心编解码，宿主只保留 `CanonicalPayload`。冻结规则必须保存原精确整数参数与原连续值，不从当前定义重新拼回。核心载荷和复现版本不等于契约基线版本；旧存档/命令迁移不在范围内。

录制的设计命令负载保留完整 DesignInput 与差额，起点保留实际采用的设计／衍生货物；设计哈希与 CoreInputId 不能代替这些业务资料。引用清单覆盖保留窗口内的全部命令，包括被拒绝和故障命令的目标设计；宿主持有对应未激活候选的固定内容依赖，普通存档不把它们误算作成功修改。

核心保管命令、检查点和故障片段；宿主按 `RequiredReferences` 保管对应内容并写文件。`ReadRecording` 给出截止位置，因而没有命令的运行区间也可以准确导出。`CaptureRecordingStart` 建立待写起点；宿主仅在起点和全部依赖已经发布后，以原起点标识调用 `ConfirmRecordingStart(PublishedWithDependencies)`。

当前会话创建时自动建立初始起点，出现在 `ReadRecording().Starts` 中，同样需要宿主确认。检查点只在录制起点、故障边界及读取窗口的截止边界生成；同一内容的截止检查点不重复列出，不逐命令生成，也不设置核心固定采样间隔。命令与对应记录在同一完整边界生效，查询、捕获、起点确认、暂停和倍速不生成模拟输入。当前 `content.publish` 的规范负载为 `{"input_format_version":2,"kind":"content.publish","input_identity":"<目标 CoreInputId>"}`；目标投影由宿主保留，核心不编码内容投影。窗口引用合并各起点和固定故障片段的完整清单，并包含全部 Completed 命令的前后及目标输入身份。设计和输入身份按 Ordinal 排序去重；定义依赖按类型身份及字段路径排序去重；模板按 `Owner` 排序且所有者唯一。起点世界的设计、模板和定义依赖在普通窗口及固定故障片段中保留。

生产计划命令同用输入格式 2。规范负载为 `{"input_format_version":2,"kind":"production.set_plan","facility":{"building":2,"capability":"production"},"process":{"kind":"production","definition":"fixture:production"},"enabled":true,"priority":0}`。`capability` 只接受 `production` 或 `extraction`；`process.kind` 只接受 `production` 或 `recovery`，定义必须是合法身份；`priority` 是完整 `Int32`。解码允许字段重排，拒绝缺少、多余、重复或非法字段并返回 `invalid_state` 与 JSON 指针；未知种类或版本返回 `unsupported_format`。接受和拒绝的命令都进入录制，严格重放走同一 `Submit`。

施工命令同用输入格式 2。`construction.start_owned` 的 `request` 按 `kind`、`facility:{building,capability}`、目标、来源（如有）和 `priority` 输出：`build_vehicle` 与 `build_equipment` 携带 `design:DesignInput`；`build_building` 在 `facility` 后携带非零 `plot` 和 `design`；`repair_vehicle` 携带非零 `vehicle` 和 `sources`；`repair_building` 携带非零 `building` 和 `sources`；`refit_vehicle` 与 `refit_building` 分别携带 `vehicle` 或 `building`、完整 `change:DesignChangeProjection` 和 `sources`；`dismantle_vehicle` 与 `dismantle_building` 只有对应身份和 `priority`，不编码来源。`construction.commission_vehicle_work` 只接受 `build_vehicle`、`repair_vehicle`、`refit_vehicle`，并在 `request` 后携带 `maximum_payment`。`instance.apply_design` 携带 `change`，以及 `vehicle`、`building` 二者之一，不能同时出现，也不能都缺。`construction.set_deconstruction_paused` 携带 `job`、`paused`。变更投影包含当前设计与构成、目标 `DesignInput`、保留部件对应、新增货物、移除单元和改动计数。来源保留显式顺序，设计与变更中无业务顺序的集合使用既有规范排序。

成功、拒绝和故障命令的目标设计、定义依赖及输入身份均进入录制引用，完整组装体货物投影留在规范负载；拒绝或故障携带的设计不会因此加入普通世界保存。命令编码、解码与严格重放沿用同一生产提交和推进路径。

四个战斗命令同用输入格式 2，字段顺序固定。`engagement.set_train` 为 `train` 加 `rules:{priority,scope,encounter,retreat_structure_ratio}`；`priority` 取 `armed_first`、`lowest_durability` 或 `highest_durability`，`scope` 取 `armed_only` 或 `all`，`encounter` 取 `engage` 或 `retreat`，阈值为有限非负 `double`（以 round-trip `R` 编码）。`engagement.set_settlement` 为 `settlement` 加只有 `priority`、`scope` 的 `rules`。`combat.retreat` 只携带 `train`。`combat.focus_fire` 携带 `combat`、`controller` 和 `target`；控制方为 `{kind:"train",train}` 或 `{kind:"settlement",settlement}`，目标为世界对象 `{kind:"instance",instance:{kind:"vehicle"|"building",vehicle|building}}` 或 `{kind:"creature_group",creature_group}`。解码拒绝缺少、多余、重复或非法字段、零身份、非法枚举及越界阈值；这些错误在取得命令编号前抛 `ArgumentException` 或由解码结果定位字段。

五个铁路命令同用输入格式 2：`train.form` 携带 `ordered_vehicles` 数字数组；`train.disband`、`train.start`、`train.stop` 携带 `train`；`train.replace_run_plan` 携带 `train` 与 `plan:{loop,stops}`。Waypoint 为 `{kind:"waypoint",node}`，Station 为 `{kind:"station",node,dwell_steps,goods_rules:[...]}`。每项规则为 `{action,good,local_inventory,target_quantity}`，`action` 是嵌套对象：买入为 `{kind:"buy",maximum_unit_price}`，卖出为 `{kind:"sell",minimum_unit_price}`，装卸仅有 `kind:"load"` 或 `kind:"unload"`；价格字段为 null 表示无限价。数组保留业务顺序，整数按完整范围编码，字段输出顺序固定；解码拒绝缺少、多余、重复或非法字段及非法货物规则。铁路命令的内容依赖来自前后输入身份，不产生发布目标。生产重放通过同一 `Submit` 和 `Advance` 路径执行，运行位置、停留进度及其余铁路权威状态均参加检查点比较。

三个铁路施工命令同用输入格式 2：`rail.expand` 携带 `edge`、`material_source`；`rail.repair` 在二者之间还携带 `break`；`rail.set_automatic_repair` 的 `material_source` 可为数字或 null。身份为非零 `ulong`。编码字段顺序固定，解码允许重排；解码拒绝缺少、多余、重复、零身份和非法字段。不新增控制台命令，单命令手册和索引更新不适用。

开发断点命令也使用输入格式 2，规范字段顺序为 `{"input_format_version":2,"kind":"rail.debug_add_break","edge":1,"track":1,"distance_meters":0}`。`edge` 为非零 `ulong`，`track` 为正 `int`，`distance_meters` 为有限非负 `double`，按 invariant round-trip `R` 编码；解码允许字段重排，拒绝缺少、多余、重复和非法字段。命令同样经过 `Submit` 并录制、严格重放。当前仅交付 Core 命令，控制台接线不在本票范围，真实宿主切换已拆给 #424；尚无新增控制台入口，单命令手册和索引更新不适用。

开发生物群命令同样使用输入格式 2，规范字段顺序为 `{"input_format_version":2,"kind":"creature.debug_spawn_group","node":1,"creature":"core:small-creature","count":1}`。`node` 为非零 `ulong`，`creature` 为合法定义身份字符串，`count` 为正 `int`；解码允许字段重排，拒绝缺少、多余、重复和非法字段。命令经过 `Submit` 并录制、严格重放，供 CLI 配方制作繁忙战斗样例；不新增控制台入口，单命令手册和索引更新不适用。

`RecordedCommand` 的命令编号与前后位置统一由 `Result` 中的 `CommandReceipt` 提供，不另外保存可矛盾的编号或前置位置。此结构调整不改变既定命令线格式的编号、位置与结果语义。

`CoreReferenceManifest.Templates` 列出当前聚居地、冻结建城任务及保留录制所需的实例副本引用，按 `Owner` 排序且所有者唯一。内容捕获、普通读档和严格复现须保留这些所有者对应的固定副本；来源定义身份仅作来源说明，不能用同身份的当前模板替换已有副本。恢复时缺少所声明所有者的内容副本是载荷不一致，不用当前源模板兜底。副本引用及其固定场景模型设计由内容展开，宿主不自行遍历模板字段。

成功确认后，核心按保留策略裁掉较早的普通证据，返回已不被任何片段引用的输入身份；宿主还须考虑活动内容等其他使用者。写入失败调用 Failed，保留旧证据并记录缺口；新起点只恢复之后的连续范围。故障片段固定保留，不参与普通窗口裁剪。窗口数和起点间隔由测量确定，本契约不给出伪装成性能结论的默认值；宿主不能把一次普通保存成功当作录制起点成功。

每个已发出的起点只接受一次确认：从未发出的标识抛 `ArgumentException`，重复确认抛 `InvalidOperationException`。迟到的旧成功确认不倒退裁剪边界。失败缺口从该起点的位置开始，到之后某个起点确认成功的位置结束；已存在较新成功起点时，迟到失败形成闭合缺口，历史缺口仍保留。故障片段固定故障时窗口最早的起点至故障边界的全部命令、检查点和引用，之后的普通裁剪不改动它。冻结后不能捕获新的录制起点，返回 `frozen`、字段 `Fault`；故障资料由已固定片段和诊断捕获提供。
