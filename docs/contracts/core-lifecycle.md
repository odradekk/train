# 核心模拟层：会话生命周期

本页记录会话生命周期，与[契约目录](core.md)一起使用。

## 创建、恢复与销毁

## 阶段一生成器

[#309 的批准计划](https://github.com/odradekk/train/issues/309#issuecomment-6134702996)落实为内部 [WorldGenerator](../../Core/World/WorldGenerator.cs)，只接受生成器版本 `stage1-world-v1`。七阶段依次生成地貌、节点、非交叉连通图、占据者、资源、初始铁路及有效起点。无模板的聚居地类型不参选；优先从偏好当前节点地貌或没有地貌偏好的类型中按权重选择，若没有这样的类型，则从全部有模板的类型中选择。类型和模板按规范身份排序后抽取，人口取类型闭区间。模板业务字段复制到聚居地内，特殊库存和生产计划进入初始状态；生物聚落仅保存类型、节点和身份，不生成生物群。地貌特征身份固定为 `lake`、`river`。

初始铁路只用铁轨数范围有效且不经过生物聚落的线路。先检查包含全部聚居地的连通分量能否满足环数与可达空节点数，再按格子 `(y,x)` 顺序尝试删除空节点；仅在诱导子图仍连通、环数及空节点数仍足够时删除，重复整轮直到没有删除。每轮检查计入预算，结果为“包含意义上最少”：去掉任一空节点都会破坏条件，不保证全局最少。精确枚举所有空节点子集的搜索呈指数增长，已在目标规模耗尽预算，因此采用批准决定 12 的删点规则。剩余集合上按长度、身份做 Kruskal，再按随机分数增加成环边；初始铁轨数取配置、线路铁轨上限与 `[1,2]` 的交集。

起点验证非敌对、站点、已建铁路可达的非敌对聚居地和空节点数量，以及是否排除相邻生物聚落。`FullyLoadedPurchaseRoundsPerTrain`、`RequiredAffordableCommissionDesign` 只检查取值、车辆设计种类和引用，不评估经济条件；生成成功不证明经济或战斗开局可玩。初始车队按输入顺序在起点创建，车辆归玩家、满耐久，列车停着且不占站台，`EnteredFrom` 为空。实例和库存不另存位置，查询从所属列车或聚居地派生。

NPC 初始化按 [#528 的批准决定](https://github.com/odradekk/train/issues/528#issuecomment-6198284367)作用于全部非玩家聚居地，包括敌对聚居地。先保留模板特殊库存；`FillInputsAndLivingTargets=true` 时，按当时库存计算一次运行时市场补货目标并补缺口，包含既有食品替代、生产两轮备货与共享容量预算。补货缺口超过真实剩余空间时，按整数缺口同比分配，先向下取整，再按最大余数分配余量，同余数按稳定货物身份顺序；不删除已有库存，不因空间不足重试。

随后按 `OutputReserveHours` 预存产出：设施筛选和潜在速率与市场补货相同，采用 `ProductionQueries.Potential` 中 `Running`、`WaitingForInputs`、`OutputBlocked` 的设施，按货物汇总潜在产量后向上取整，扣除该货物已有库存，只补不足。产出缺口共用补货之后的真实剩余空间，容量不足时沿用上述整数分配。很大的预存时长使用大整数保留比例，避免中间乘积溢出。预存是开局直接放入库存，不推进模拟、不执行生产批次、不消耗原料或扣除节点资源。

`MaximumForSaleVehicles>0` 时，有有效车辆车间的 NPC 聚居地生成满耐久待售实例，所有权等于当地关系，车辆停放当地且未编组。候选先按车间支持档位和市场规则 `VehiclePurchaseLimits.MaximumForSale` 的剩余名额筛选，再按 DesignHash 排序，有放回等概率抽取；每辆占用对应档位名额。可生成名额是车间支持且有候选的各档位上限之和，数量在 `[MinimumForSaleVehicles, min(MaximumForSaleVehicles, 可生成名额)]` 内均匀抽取。没有车间时为零辆；有车间但名额低于最低数量时，立即返回 `config_invalid`，字段为 `Configuration.NpcInitialization.MinimumForSaleVehicles`，不做约束重试。敌对关系的交易访问限制不影响初始生成，也不使用运行时的可收购数作为生成名额。

初始化不是搜索，不增加候选或搜索工作量计数。三项关闭时保留原生成路径、随机键和预算结果；生成器版本仍为 `stage1-world-v1`，核心状态格式为 23，命令格式仍为 2。新增库存和车辆使用既有权威状态、引用、捕获、恢复和重放协议，不新增持久字段。

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
