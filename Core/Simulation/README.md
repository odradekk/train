# 模拟时间与通用随机算法

本模块提供定步长时间换算和无状态的 64 位整数混合，只依赖 .NET 库，不引用 Godot、局部场景或世界实体。

| 类型 | 职责 |
| --- | --- |
| `SimulationClock` | 定义构建固定的模拟频率和步长。 |
| `SimulationScheduler` | 累计宿主帧间隔，返回完整模拟步数并保存显示插值余量；本身不执行模拟。 |
| `SplitMix64` | 对显式输入执行固定的无状态整数变换；不保存随机状态，也不定义用途键。 |

时间换算实现[时间、命令与确定性：时钟与倍速](../../docs/design/04-deterministic-simulation.md#时钟与倍速)中的定步长部分；倍速和世界推进入口由后续切片实现。通用整数变换遵循[随机性](../../docs/design/04-deterministic-simulation.md#随机性)和 [ADR 0003](../../docs/adr/0003-use-scalar-doubles-and-stateless-random-for-determinism.md)，具体用途的字段顺序、编码和版本由相应用途定义。项目依赖遵循 [ADR 0009](../../docs/adr/0009-split-the-code-into-godot-free-core-and-scene-libraries-and-rewrite-the-core.md)。

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

在仓库根目录运行 `pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Scope simulation -BuildGame` 执行本模块的单元测试和游戏构建。
