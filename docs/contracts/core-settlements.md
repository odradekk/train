# 核心模拟层：聚居地与外交

本页记录聚居地与外交，与[契约目录](core.md)一起使用。

## 聚居地与经济查询

`FindSettlement` 对零编号抛 `ArgumentException`，不存在的非零编号返回 `Lookup.Missing`。地块保留运行身份及模板本地编号；普通地块允许除站点外的建筑种类，站点地块仅允许站点。`Market.Storage` 复用库存与租约的统一容量划分，租约列表包含尚未删除的到期租约。查询只读取当前读视图，不推进消费、生产、租约或统计。

`Power.GenerationCapacity` 是当前有效发电部件的能力之和，`FuelEnergyRemaining` 是已保存的燃料能量余量。`Demand`、`ActualSupply` 是各设施上一步保存结果之和，不因当前内容发布而重算。`Population.AllocatedLabor` 同样汇总各设施上一步保存的 `LastAllocatedLabor`，完成批次也保留最后一步分配；下一无工作请求步骤归零。`AvailableLabor` 按当前人数乘当前有效人口规则劳动比例计算，人口停用时为零；`IdleLabor = max(0, AvailableLabor - AllocatedLabor)`。当前劳动容量下降后，上一边界实际分配可以暂时高于当前容量。

人口周期尚未开始时返回 `NotStarted`；已有周期但尚无消费统计时，满足率为空、趋势为 `Stable`。消费后满足率来自同一周期的累计应需与满足量，零应需项按已满足处理；趋势按冻结规则的变化率判断，负变化率且人数为一时为 `AtMinimum`。缺当前规则或本期必需引用时返回 `Disabled` 并保留周期。当前规则修订与本期执行修订分开提供，不伪造尚未发生的结算。

`Economy.Wallet` 读取真实钱包。市场、人口、施工的 `RuleStatusView` 返回本局所选身份及当前发布修订；当前定义不存在或已标缺失时修订为空，并给出 `content_missing`。以下字段按 #486 批准的阶段范围处理，后续工单须接入其新增状态：

| 字段 | 当前取值与后续责任 |
| --- | --- |
| `VehiclesForSale` | 当地 NPC 拥有的实际停放车辆，按身份排序；来源包括世界生成的初始现车和玩家卖给当地的原实例 |
| `Surrendered` | 保存的投降状态。只有敌对 NPC 聚居地在战斗中士气严格低于 `SurrenderThreshold` 时为 `true`；恢复至不低于阈值时解除 |
| `CommissionJobs`、`Economy.UnassignedServicePayments` | #387 返回当地活动委托的任务编号（按受理顺序），并累计全局实际已付委托工钱；材料补购款、租金及各列车运行账目不计入该累计 |
| `OwnedJobs` | #332 返回设施在该聚居地、资金来源为 `OwnInventory` 的活动制造、修理、改装、拆解任务编号，按 `JobId` 升序；种类、状态和进度经 `FindJob` 读取。暂停的拆解仍列出，完成或失败后与 `FindJob` 返回 `Missing` 同步移除；NPC 聚居地返回空数组 |
| `Market.Goods`、`VehiclePurchaseCapacities`、设施 `PotentialConsumption` | #384 从当前真实库存、需求、能力和市场规则派生；字段定义见下文[市场部分](core-market.md#市场报价与手动交易) |
| `Economy.RecentRunReports` | #385 接入全局近期运行报告，按生成顺序返回；与列车查询共用同一份历史，包含已解编列车的报告 |
| `Facilities` 中的施工设施 | 当前返回已实现的加工、回收和开采设施；施工详情查询仍待后票，未列出不表示不存在施工能力或活动施工任务；活动任务由 `FindJob` 查询 |
| `ParkingCapacity` | `int.MaxValue` 表示 Demo 不限停放数量，不表示已实现有限停车容量 |
| `MoraleRatio` | 保存的士气，初始为 `1`，范围 `[0,1]` |
| `Engagement` | #350 保存并返回列车和聚居地的交战规则。聚居地默认 `(ArmedFirst, All)`；列车在生成或编组时按当时有无完好武器写入默认值，之后只由 `SetTrainEngagement`／`SetSettlementEngagement` 修改。目标、撤退与阈值规则见[战斗](core-combat.md#战斗) |

上述停车和士气值只由查询给出；自格式 23 起士气与投降状态加入持久状态，交战规则自 #350 起是持久状态。

## 建城与玩家聚居地

#342 实现 `FoundSettlement`、`PreviewSettlement`、`FindJob` 的建城分支及 `SettlementFounded`。世界生成与建城共用类型和模板选择规则；建城从当前固定投影选择，以世界种子、节点身份和独立用途键生成随机数，不消耗新局生成的随机序列。

- `PreviewSettlement(NodeId)` 与受理共用节点、内容及身份可用性校验，返回起始人口、生活货物每秒预计用量、精确 BOM、冻结工期及阻止原因。预览不指定材料列车；提交时另查该列车的所有权、施工锁、位置、速度与完整材料。生活物资预计量按基本食物优先使用主食、改善饮食使用加工食品计算。
- 人口不可用时复用人口模块的停用判断，只有预览返回 `CoreResult.Failure`，问题为 `content_missing` 并指向缺失规则或货物；不伪装成零需求，也不增加建城拒绝条件。
- 材料列车归玩家所有、位置是目标 `TrainPosition.Node` 且速度为零即可，不限制节点内阶段，不要求站台。占据节点返回 `node_occupied / Node`，已有建城预留返回 `busy / Node.FoundingJob`。材料不足返回 `insufficient_goods / MaterialSource.Inventory`，列出缺货身份；正常拒绝不改变库存、节点或任何实体编号来源。
- 接受时一次扣除完整起始建筑 BOM，从全世界共用来源取得 `JobId` 并预留最终 `SettlementId`；编号耗尽返回 `identity_exhausted`，仍按 Completed 拒绝进入录制。`Created` 只列已经创建的任务，`SettlementCopies` 立即列出预留所有者、源模板及地块号/尺寸。宿主按既有协议完成内容配对后才通知、保存或推进。
- 任务冻结玩家业务模板、类型身份、受理时人口下限、工期和已投入材料，不锁定或继续依赖材料列车，不消耗施工能力、电力或劳动力。只保留 `founding` 建筑，不授予 NPC 预建建筑、库存或生产计划。源模板更新不改任务；普通恢复时即使类型已缺失，完工仍使用冻结人口。
- 恰好推进满冻结工期时创建预留身份的玩家聚居地及其起始建筑，库存为空，人口从成立后开始统计。站台数复用建筑能力计算，节点尺度变化复用列车适配；清除节点任务标记并发出一次 `SettlementFounded`。进行中的副本、设计、类型与任务规则从受理起进入引用清单、保存、检查点和严格重放。建筑后续建设归 #343，界面归 #346，真实四模块交接归 #301。

建城完成事件和节点适配事件在资源事件之后、生产状态变化事件之前发出。

建城命令沿用输入格式 2，规范字段顺序为 `{"input_format_version":2,"kind":"settlement.found","node":1,"material_source":1}`；两个身份均为非零 `ulong`，解码允许重排，拒绝缺失、未知、重复及非法字段。不新增控制台命令。

## 宣战与占领

#352 实现 `DeclareWar`、`OccupySettlement`、士气与投降。两条命令沿用输入格式 2，规范字段顺序为 `{"input_format_version":2,"kind":"settlement.declare_war","settlement":1}` 和 `{"input_format_version":2,"kind":"settlement.occupy","settlement":1,"train":1}`；身份均为非零 `ulong`，解码允许重排，拒绝缺失、未知、重复及非法字段。不新增控制台命令。

- `DeclareWar` 只接受非敌对聚居地。对象不存在为 `object_missing / Settlement`；关系不是 `NonHostile` 为 `invalid_state / Settlement.Relation`；当地存在资金来源为 `NpcCommission` 的活动委托为 `invalid_state / Settlement.CommissionJobs`；目标节点上有玩家列车，或停有玩家自有的车辆为 `invalid_state / Settlement.Node`。成功后关系改为 `Hostile`，同时更新节点占据者、当地建筑和 NPC 停放车辆的 `Ownership`，清空投降并发出一次 `SettlementRelationChanged(NonHostile → Hostile)`。玩家自有停放车辆不动。租用库存的冻结由保存的关系与现有访问权规则派生，不新增冻结字段。
- `OccupySettlement` 只接受敌对聚居地。对象不存在为 `object_missing / Settlement` 或 `object_missing / Train`；关系不是 `Hostile` 为 `invalid_state / Settlement.Relation`；指定列车不在该节点为 `invalid_state / Train.Position`；该节点仍有战斗为 `invalid_state / Settlement.Node`；未投降且当地仍有能开火的武器为 `conditions_not_met / Settlement`。这里的“能开火”复用战斗开战判断：建筑和 NPC 停放车辆中至少一件武器部件耐久大于零且定义未缺失。成功后关系改为 `PlayerOwned`，同步节点占据者、建筑和 NPC 停放车辆所有权；人口、统计进度、燃料、地块、受损建筑和士气原样保留；清空投降；当地全部租约（含已到期）的货物并入聚居地库存，允许超容，随后删除租约和租用库存，不退租金、不发 `LeaseExpired`；交易规则或命令之后引用这些已删除的租用库存时只返回 `object_missing / LocalInventory`，不冻结会话。玩家自有停放车辆不动。事件为 `SettlementRelationChanged(Hostile → PlayerOwned)` 后接节点适配产生的列车事件。
- 关系变化后复用 `TrainOperations.AdaptNodes`：占领时列车已在当地，关系变为自有会影响站台需求；宣战要求节点上没有玩家列车，因此不额外适配。
- 查询返回保存的 `MoraleRatio` 与 `Surrendered`。世界生成和建城的初始士气为 `1`，未投降。

## 士气、投降与恢复

- 战斗规则缺失（按世界所选身份找不到 `CombatRuleProjection`）时，士气、投降和恢复都不变，已有状态原样保留。
- 每步伤害结算后，对每个本步受到实际伤害的聚居地（含玩家自有聚居地）更新一次士气。伤害项 = 本步实际扣除的耐久（部件与结构，按剩余有效耐久截断，建筑和停放车辆都算）÷ 步初该聚居地全部现存建筑的有效结构耐久上限之和 × `MoraleDamageCoefficient`；分母为零时不计伤害项。武器项 = 步初完好且定义存在的武器部件中本步被毁的数量（部件耐久归零，或所在单位被摧毁，每件只计一次）× `WeaponDestroyedMoraleLoss`。结果截断到 `[0,1]`。由于使用步初建筑上限，战斗中被摧毁的建筑仍计入分母。
- 只有敌对 NPC 聚居地会投降：更新后士气严格低于 `SurrenderThreshold` 且尚未投降时置 `Surrendered`，并发一次 `SettlementSurrendered(combat, settlement)`。投降后该聚居地的建筑和停放车辆在战斗敌对判断中不算敌对：不开火、不被选为目标、不加入，也不会新开战斗；控制方不从未结束的战斗记录中移除。现有结束判断在同一步结束战斗。
- 每步战斗阶段开头、加入和开战之前，节点上没有战斗的聚居地恢复 `MoraleRecoveryPerSecond × SimulationTime.StepSeconds`，最多到 `1`；已投降且士气达到或超过 `SurrenderThreshold` 时解除投降。解除不发事件。玩家列车仍在该节点且仍能攻击时，同一步重新开战。
- 事件顺序：`CombatStarted`／`CombatJoined` → 活动变化与 `CombatRetreatStarted` → `CombatHits` → `CombatDestruction` → `TrainCompositionChanged` → `RunReported` → 伤害触发的 `CombatRetreatStarted` → `SettlementSurrendered` → `CombatEnded` → `LairCleared` → 恢复活动的 `TrainActivityChanged`。
