# 核心运行边界、模拟时间与随机算法

本模块实现命令 v2、日志链、随机键、状态恢复及检查点比较的契约；测试使用独立固定向量和真实进程对照验证。

本模块只依赖 .NET，不引用 Godot 或局部场景，提供定步长时间换算、无状态整数混合，以及不可变运行边界上的命令和完整模拟步事务。

| 类型 | 职责 |
| --- | --- |
| `SimulationClock` | 定义构建固定的模拟频率和步长。 |
| `SimulationScheduler` | 累计宿主帧间隔，返回完整模拟步数并保存显示插值余量；本身不执行模拟。 |
| `SimulationDriver`、`SimulationStatus`、`SimulationSpeed` | 在单一宿主线程驱动已验证会话，管理暂停、1/2/4/8 倍速、当前帧整步额度、时间余量和实际速度；查询返回完整状态。 |
| `SplitMix64` | 对显式输入执行固定的无状态整数变换；不保存随机状态，也不定义用途键。 |
| `SimulationState`、`SimulationPosition` | 保存完整边界上的不可变权威数据，以及按步号和步内序号排列的位置。 |
| `SimulationSession` | 查询最后完整状态，逐步推进或提交命令，检查候选后发布；内部故障回滚并冻结。 |
| `SimulationCalculations` | 实现共享完整步、实体 ID 分配、不变量检查和故障状态；四个业务计算由领域实现。 |
| `TransactionContext`、`Transition` | 将事务身份作为输入，将完整候选状态和待发布事件作为输出。 |
| `StepRequests`、`ResolvedStep`、`CoreCommand` | 声明领域数据的用途；具体字段由相应领域定义。 |
| `CommandDecision`、`SubmissionResult`、`AdvanceResult` | 区分命令的计算结果、入口拒收与执行记录，以及批量推进实际完成的结果。 |
| `SimulationFaultDiagnostic` | 将本次新故障及原始异常交给宿主记录，不参与权威编码。 |
| `SimulationCommandCodec`、`SimulationCommandLog` | 定义命令 v2 负载、最近 256 条执行记录和全历史链摘要。 |
| `RandomKey`、`KeyedRandom` | 定义带用途和有序身份分量的完整随机键及无状态结果。 |
| `SimulationStateCodec`、`CoreStateSnapshot` | 定义完整核心状态的捕获、载荷读取、起点校验与恢复；不组织玩家存档。 |
| `SimulationCheckpoints`、`CheckpointTrace` | 定义规范权威文档及兼容输入下的首差异报告。 |
| `DeterminismResult`、`DeterminismError` | 区分完整成功、未知格式、非法数据及不兼容上下文。 |

时间换算实现[时间、命令与确定性：时钟与倍速](../../docs/design/04-deterministic-simulation.md#时钟与倍速)中的定步长部分。运行边界对应同文档的 [Tick 流水线](../../docs/design/04-deterministic-simulation.md#tick-流水线)、[命令](../../docs/design/04-deterministic-simulation.md#命令)、[Transaction](../../docs/design/04-deterministic-simulation.md#transaction) 和[运行时 ID](../../docs/design/04-deterministic-simulation.md#运行时-id)。通用整数变换遵循[随机性](../../docs/design/04-deterministic-simulation.md#随机性)和 [ADR 0003](../../docs/adr/0003-use-scalar-doubles-and-stateless-random-for-determinism.md)，具体用途的字段顺序、编码和版本由相应用途定义。项目依赖与不可变查询遵循 [ADR 0009](../../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)。

## 运行边界的计算约定

`SimulationState` 保存种子、生成器版本、已完成步号、当前边界序号、下一个命令号、下一个实体 ID、事务计数和故障。`TransactionContext` 表示尝试使用的身份；内部故障回滚后，这些身份不代表已经提交。准备身份前发生故障时，故障上下文可以为空。

查询返回最后完整状态。一次命令或一个完整模拟步先构造不可变候选状态，通过不变量检查后才由会话原子发布；失败候选与它的事件一起丢弃。每个完整模拟步是一个事务，批量推进中的早先成功步不会因为后一步失败而回滚。

`Evaluate` 计算请求，`Resolve` 计算获准结果和稳定顺序，`Commit` 根据计划计算业务候选状态。它们和 `ComputeCommand` 是领域必须实现的抽象方法。共享 `ComputeStep` 用同一完整起点串联三阶段，再推进边界计数；异常按实际阶段转换为稳定故障，保留已有错误码和实体定位，诊断消息不进入权威状态。命令计算与验证阶段由会话入口确定。默认完整步没有事件；领域加入其他阶段或事件时须覆盖此方法，保持同一完整步事务，并负责报告准确的内部失败阶段。输入数据及集合必须不可变；计算期间允许查询旧边界，重入修改入口会抛出内部异常。

已接受命令的成功和预期拒绝都消耗命令号、步内序号和事务计数。预期拒绝从命令前记录复制，只更新这些身份字段，沿用原业务字段和不可变集合；会话检查候选值与事件，夹带业务修改或事件属于内部故障。内部故障恢复执行前全部状态，再附故障并冻结。命令号、序号或事务计数无法递增时入口拒收，不分配身份；推进时步号或事务计数达到上限则停止，保留此前成功步，不附故障。实体 ID 分配发生在候选 Commit 顺序中；下一 ID 为 `ulong.MaxValue` 时不能递增，返回耗尽，不绕回。

不变量检查要求状态类型、世界种子和生成器版本保持一致，实体分配器非零且不后退；命令和模拟步分别满足自己的完整后继计数与上下文。领域扩展检查时须先执行基础检查，再检查业务字段。事件必须使用已初始化的不可变数组，无事件使用 `Empty`。

新发生的内部故障随 `AdvanceResult.Diagnostic` 或 `SubmissionResult.Completed.Diagnostic` 返回，包含稳定故障和原始异常。冻结后的调用不重复返回诊断，恢复冻结状态也不产生历史异常详情。宿主在收到结果时记录一次错误日志；异常消息、调用栈及宿主真实时间不进入状态捕获、检查点或命令摘要。

构造器接收完整起点、领域计算与命令格式，初始数据的有效性由创建或恢复起点的调用方保证。`CheckInitialState` 检查完整起点，`Restore` 只在验证成功后创建新会话，失败不改变已有会话；恢复保留故障冻结，日志从空链开始。

`Submit` 在接受前编码命令，随后计算状态和执行日志候选，一次发布二者。查询在计算期间仍读取旧边界和旧日志。执行、预期拒绝和内部故障各产生一条记录，入口拒收不追加记录；故障日志保留尝试身份，权威计数仍回滚。内存保留最近 256 条，裁剪时推进窗口前摘要，链继续覆盖本次会话的全部命令历史。日志不进入恢复载荷或权威检查点。

`SimulationDriver` 由 App 主线程调用，接收创建或恢复后已验证的会话。

驱动将真实帧间隔乘以目标倍速后交给调度器，所得整步只作为本帧的推进额度。工作预算在完整步之间检查：有额度时至少尝试一步；预算耗尽后放弃未执行的整步额度，跨帧只保留不足一步的时间余量。已完成步及其事件全部保留，模拟步号连续，事务不截断；过载时游戏时间变慢，后续帧不追赶旧额度。`PendingSteps` 只表示当前调用尚未完成的额度，调用结束后为零。非正或非有限帧间隔不新增时间、不推进。暂停不累计时间，暂停与切换倍速保留不足一步的余量；冻结或计数耗尽时停止累计。手动推进要求暂停，不消费自动推进余量；核心命令仍经同一个会话接受、执行和记录。

实际速度按自动完成的模拟秒除以单调真实耗时计算，采样满一秒后在帧结束时重新开始。切换暂停或档位重置采样；暂停、冻结和计数耗尽时显示零。目标倍速、工作预算、余量和测量值均为宿主数据，不进入权威状态或检查点。驱动的各入口只能由创建它的宿主线程调用；计算期间允许查询完整边界，禁止重入修改。取得的不可变快照可供其他线程读取。

`SimulationScheduler` 应由单一宿主线程使用。暂停或无效帧间隔不会累积时间，也不会清除之前的余量；`Reset()` 才会丢弃余量。调用方应提供适合帧驱动的时间间隔，调度器不限制一次收集的步数。`DisplayAlpha` 只供显示插值使用，不进入核心状态。

## 数值约定

| 数值 | 单位、来源和当前状态 |
| --- | --- |
| 每秒 60 步、每步 1/60 秒 | 由[时钟设计](../../docs/design/04-deterministic-simulation.md#时钟与倍速)确定的构建常量，当前固定；改变它会改变模拟结果。 |
| 取整补偿 1e-9 | 单位为步，避免浮点误差使整步边界少算一步；沿用已有实现，尚无独立测量或设计依据，暂保留此保护值。补偿产生的负余量归零。 |
| SplitMix64 增量、乘法因子和移位数 | 按 [SplitMix64 参考实现](https://prng.di.unimi.it/splitmix64.c)固定；加法与乘法按模 2^64 执行，不是可调整的玩法数值。 |

`Mix(value)` 包含参考算法的固定增量加法，之后才执行混合，不能当作省略增量的终结变换。它只返回结果，不保存参考生成器的内部状态。

## 示例与验证

[调度器测试](../../Tests/Core/Simulation/SimulationSchedulerTests.cs)覆盖 0.4 步与 0.7 步的累计、暂停和无效间隔、重置，以及 30／60／144 帧每秒下相同的步数。[混合算法测试](../../Tests/Core/Simulation/SplitMix64Tests.cs)使用独立按参考算法计算的六组固定向量，覆盖普通输入和无符号溢出。

[驱动测试](../../Tests/Core/Simulation/SimulationDriverTests.cs)使用可控单调时间，验证 1/2/4/8 档位、完整步预算、整步额度放弃、实际速度、暂停命令与手动步进、余量保留、故障停止和线程限制。例如四步加半步需求、每步 3 ms、预算 5 ms 时完成两步、放弃两步额度并保留半步余量；一个步骤耗时 6 ms 时仍完整完成该步。测试覆盖过载后 8× 切到 1×、长帧后不追赶旧额度。

[驱动确定性对照](../../Tests/Core/Simulation/SimulationDriverDeterminismTests.cs)将同一起点和同一命令脚本交给实际 `SimulationDriver`，改变目标倍速、帧间隔、预算和计算耗时，覆盖暂停期间的执行、拒绝和后续命令；在 (0, 2)、(4, 3)、(8, 1)、(12, 0) 比较权威检查点及命令链。

运行边界与计算例子直接断言实际用途的预期输出：

| 例子 | 输入与预期输出 |
| --- | --- |
| [运行边界测试](../../Tests/Core/Simulation/SimulationSessionTests.cs) | 零步不变；(60, 2) 推进到 (61, 0)；单步和批量同结果；第 4 步故障保留前三步及事件；候选发布前查询仍得到旧状态。 |
| 同文件的命令序列 | 暂停边界上成功、目标消失拒绝、成功；三条占连续位置，拒绝不撤销此前成功、不换目标、不重试，后续命令继续。 |
| 同文件的故障与上限 | 非法候选或内部异常回滚并冻结；冻结和身份耗尽不接受新命令；新故障只返回一次诊断，异常详情不改变检查点或命令链。 |
| [计算例子](../../Tests/Core/Simulation/SimulationCalculationTests.cs) | 库存 5，请求 3、4，同优先级按身份获准 3、2；反向枚举仍同结果；业务优先级优先；完整批次不可部分授予；Commit 保留未使用库存。 |
| 同文件的基础计算 | ID 7 分配为 7、候选下一值 8；耗尽不绕回；非法 NextEntityId 被检查拒绝；故障只附加到完整起点。 |

资源状态、请求、计划和领域计算实现只存在于[测试夹具](../../Tests/Core/Simulation/SimulationExamples.cs)，不代表生产世界玩法已实现。多数运行边界测试用预设结果或异常隔离领域算法；另有真实资源计算经会话连续推进及命令执行的例子。测试同时覆盖普通异常的阶段定位、拒绝夹带修改、计数上限和重入回滚。

[命令编码](../../Tests/Core/Simulation/SimulationCommandEncodingTests.cs)、[日志](../../Tests/Core/Simulation/SimulationCommandLogTests.cs)、[随机键](../../Tests/Core/Simulation/KeyedRandomTests.cs)、[状态](../../Tests/Core/Simulation/SimulationStateCodecTests.cs)和[检查点](../../Tests/Core/Simulation/SimulationCheckpointTests.cs)例子分别断言具体输入输出、版本拒绝、窗口锚点、不变量和首差异。规范文档与摘要有手写预期值；随机键使用[独立 Python 参考程序](../../Tests/Core/Simulation/ReferenceVectors.py)计算字节与 UInt64 向量。比较器从实际文档重算位置与摘要，并比较完整字段，调用方提供的相同摘要不能代替证据。

[进程对照](../../Tests/Core/Simulation/SimulationProcessTests.cs)启动真实 C# 子进程，分别重复运行、保存退出及在新进程恢复，变化帧预算、暂停与 1/2/4/8 倍速。它还独立断言恢复边界、期末业务状态、固定文档、身份计数和随机值；恢复后的新日志链单独验证。结果、载荷与 stdout/stderr 保留在本次测试目录。

在仓库根目录运行 `pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Family headless -Scope simulation -BuildGame` 执行本模块的单元、会话集成、跨进程测试、游戏构建及真实 Godot 的 `simulation-host` 检查；结果保存在统一入口生成的 `TestResults/` 目录。`-Family unit -BuildGame` 执行所有单元测试和游戏构建，不运行 Godot 检查。
