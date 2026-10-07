# 核心运行边界、模拟时间与随机算法

本模块只依赖 .NET，不引用 Godot 或局部场景。已有可执行代码提供定步长时间换算和无状态整数混合；新增的运行边界目前处于**模板与输入输出例子阶段，计算逻辑尚未实现**。

| 类型 | 职责 |
| --- | --- |
| `SimulationClock` | 定义构建固定的模拟频率和步长。 |
| `SimulationScheduler` | 累计宿主帧间隔，返回完整模拟步数并保存显示插值余量；本身不执行模拟。 |
| `SplitMix64` | 对显式输入执行固定的无状态整数变换；不保存随机状态，也不定义用途键。 |
| `SimulationState`、`SimulationPosition` | 保存完整边界上的不可变权威数据，以及按步号和步内序号排列的位置。 |
| `SimulationSession` | 声明查询、推进 N 步和当前边界提交命令的统一入口。 |
| `SimulationCalculations` | 声明完整模拟步、命令、实体 ID 分配、不变量检查和故障状态的计算模板。 |
| `TransactionContext`、`Transition` | 将事务身份作为输入，将完整候选状态和待发布事件作为输出。 |
| `StepRequests`、`ResolvedStep`、`CoreCommand` | 声明领域数据的用途；具体字段由相应领域定义。 |
| `CommandDecision`、`SubmissionResult`、`AdvanceResult` | 区分命令的计算结果、入口拒收与执行记录，以及批量推进实际完成的结果。 |

时间换算实现[时间、命令与确定性：时钟与倍速](../../docs/design/04-deterministic-simulation.md#时钟与倍速)中的定步长部分。运行边界模板对应同文档的 [Tick 流水线](../../docs/design/04-deterministic-simulation.md#tick-流水线)、[命令](../../docs/design/04-deterministic-simulation.md#命令)、[Transaction](../../docs/design/04-deterministic-simulation.md#transaction) 和[运行时 ID](../../docs/design/04-deterministic-simulation.md#运行时-id)。通用整数变换遵循[随机性](../../docs/design/04-deterministic-simulation.md#随机性)和 [ADR 0003](../../docs/adr/0003-use-scalar-doubles-and-stateless-random-for-determinism.md)，具体用途的字段顺序、编码和版本由相应用途定义。项目依赖与不可变查询遵循 [ADR 0009](../../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)。

## 模板的计算约定

`SimulationState` 保存种子、生成器版本、已完成步号、当前边界序号、下一个命令号、下一个实体 ID、事务计数和故障。`TransactionContext` 表示尝试使用的身份；内部故障回滚后，这些身份不代表已经提交。准备身份前发生故障时，故障上下文可以为空。

查询返回最后完整状态。一次命令或一个完整模拟步先构造不可变候选状态，通过不变量检查后才由会话原子发布；失败候选与它的事件一起丢弃。每个完整模拟步是一个事务，批量推进中的早先成功步不会因为后一步失败而回滚。

`Evaluate` 计算请求，`Resolve` 计算获准结果和稳定顺序，`Commit` 根据计划计算业务候选状态。完整步的边界计数在 `ComputeStep` 中完成；后续损伤、派生状态和事件计算也属于完整步事务。领域实现必须保持输入数据及集合不可变，不能在模拟阶段发布事件、修改会话或重入。

已接受命令的成功和预期拒绝都消耗命令号、步内序号和事务计数。预期拒绝保持业务状态和实体分配器；内部故障恢复执行前全部状态，再附故障并冻结。命令号或序号无法递增时入口拒收，不分配身份。实体 ID 分配发生在候选 Commit 顺序中；下一 ID 为 `ulong.MaxValue` 时不能递增，返回耗尽，不绕回。

目前只有模板构造器和 `GetState()` 保存、返回数据。`Advance`、`Submit` 和所有计算模板显式抛出 `NotImplementedException`；没有空实现、默认成功或跳过测试来表示行为已完成。模板没有接入 App。命令 v2 编码、命令日志、随机键、检查点、恢复及暂停倍速界面由后续步骤补齐。

`SimulationScheduler` 应由单一宿主线程使用。暂停或无效帧间隔不会累积时间，也不会清除之前的余量；`Reset()` 才会丢弃余量。调用方应提供适合帧驱动的时间间隔，调度器不限制一次收集的步数。`DisplayAlpha` 只供显示插值使用，不进入核心状态。

## 数值约定

| 数值 | 单位、来源和当前状态 |
| --- | --- |
| 每秒 60 步、每步 1/60 秒 | 由[时钟设计](../../docs/design/04-deterministic-simulation.md#时钟与倍速)确定的构建常量，当前固定；改变它会改变模拟结果。 |
| 取整补偿 1e-9 | 单位为步，避免浮点误差使整步边界少算一步；沿用已有实现，尚无独立测量或设计依据，暂保留此保护值。补偿产生的负余量归零。 |
| SplitMix64 增量、乘法因子和移位数 | 按 [SplitMix64 参考实现](https://prng.di.unimi.it/splitmix64.c)固定；加法与乘法按模 2^64 执行，不是可调整的玩法数值。 |

`Mix(value)` 包含参考算法的固定增量加法，之后才执行混合，不能当作省略增量的终结变换。它只返回结果，不保存参考生成器的内部状态。

## 示例与验证

[调度器测试](../../Train.Tests/Core/Simulation/SimulationSchedulerTests.cs)覆盖 0.4 步与 0.7 步的累计、暂停和无效间隔、重置，以及 30／60／144 帧每秒下相同的步数。[混合算法测试](../../Train.Tests/Core/Simulation/SplitMix64Tests.cs)使用独立按参考算法计算的六组固定向量，覆盖普通输入和无符号溢出。

新增例子分为两种，均直接断言实际用途的预期输出，不把未实现异常当成通过：

| 例子 | 输入与预期输出 |
| --- | --- |
| [运行边界测试](../../Train.Tests/Core/Simulation/SimulationSessionTests.cs) | 零步不变；(60, 2) 推进到 (61, 0)；单步和批量同结果；第 4 步故障保留前三步及事件；候选发布前查询仍得到旧状态。 |
| 同文件的命令序列 | 暂停边界上成功、目标消失拒绝、成功；三条占连续位置，拒绝不撤销此前成功、不换目标、不重试，后续命令继续。 |
| 同文件的故障与上限 | 非法候选或内部异常回滚并冻结；冻结和身份耗尽不接受新命令；实体销毁后已提交 ID 不复用。 |
| [计算例子](../../Train.Tests/Core/Simulation/SimulationCalculationTests.cs) | 库存 5，请求 3、4，同优先级按身份获准 3、2；反向枚举仍同结果；业务优先级优先；完整批次不可部分授予；Commit 保留未使用库存。 |
| 同文件的基础计算 | ID 7 分配为 7、候选下一值 8；耗尽不绕回；非法 NextEntityId 被检查拒绝；故障只附加到完整起点。 |

资源状态、请求、计划和领域计算模板只存在于[测试夹具](../../Train.Tests/Core/Simulation/SimulationExamples.cs)。资源计算例子描述待实现领域计算的用途，不证明生产世界已实现。运行边界测试用预设结果或异常隔离领域算法；它们验证会话的身份分配、检查及发布规则。当前计算测试预期处于失败阶段，构建成功不代表这些行为已完成。

在仓库根目录运行 `pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Scope simulation -BuildGame` 执行本模块的单元测试和游戏构建；结果保存在统一入口生成的 `TestResults/` 目录。计算模板尚未填充时该命令应返回非零，原有调度器与混合向量测试仍应通过。
