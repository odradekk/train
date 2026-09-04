# 04 确定性模拟

> 决策出处：转录 Q49、Q63–Q88、Q152；研究 [`docs/research/dotnet-godot-determinism.md`](../research/dotnet-godot-determinism.md)（票 #26）。

## 边界

本文定义 Simulation 时钟、确定性目标、Tick 流水线、资源仲裁、Transaction、随机性、运行时 ID。Damage Transaction 的结构结算见 [03](03-composite-and-behavior.md)；存档内容见 [09](09-persistence.md)；窗口状态与 Pause 的关系见 [08](08-desktop-window-and-camera.md)。

## 不变量

- Simulation 与 FPS、Camera、Window Focus 解耦。
- 注册顺序、容器枚举顺序、线程完成顺序不得决定 Gameplay。
- 所有 Gameplay Random 最终源于 WorldSeed。
- 能够使用 Stateless Random 的场景默认不维护 RNG State；Stateful RNG State 必须持久化。
- 结构修改按 Transaction 原子结算。

## 规则

### Simulation Clock

- Simulation 拥有独立于 Rendering FPS 的逻辑时钟。Godot 仅提供 Host：Godot SimulationHost → SimulationClock → WorldSimulation。
- Simulation 不依赖 SceneTree 中是否存在某对象、当前 FPS、Camera 是否看到某区域、窗口是否获得焦点。
- 首版：Pause 与 1×。架构预留 2×、4× 等倍速，但在系统稳定前不开放倍速 Gameplay。

### 确定性目标

- 同一游戏版本 + 同一存档 + 同一输入 + 兼容运行环境 = 相同 Simulation Result。当前不要求不同 CPU 或操作系统之间逐 bit 完全一致。
- 所有能够改变 World State 的遍历顺序必须显式稳定。不能让以下因素决定 Gameplay：Dictionary 枚举顺序、HashSet 枚举顺序、Behavior 注册顺序、Thread completion order、Node order、非确定性 Async completion。
- 研究 #26 补充的事实：x64 同二进制同机上标量 double 确定，RyuJIT 不收缩 FMA；必须隔离在模拟之外的是 string 哈希与字典枚举序（桶碰撞超过阈值后会换成随机比较器）、ICU 文化排序、`Vector<T>` 宽度、C 运行时 Math 函数、随引擎构建在 float 与 double 之间切换的 Godot `real_t`。具体策略由开放问题中的票决定。

### Tick 流水线

```text
Simulation Tick
1. Apply Commands
2. Behavior Evaluation：读取 committed state，产生 Request / Intent
3. Resolve：共享资源竞争、Gameplay priority、deterministic fallback
4. Commit
5. Structural Resolution：destroyed voxels、connectivity、fragmentation、ownership transfer
6. Derived State：capabilities、operational condition
7. Domain Events
```

### Resolver

多个行为竞争共享资源时：Request → Resolver → Commit。例如 Engine A 与 Engine B 同时 RequestFuel，Resolver 采用 Explicit Gameplay Priority → Deterministic Fallback，而不是"谁先执行谁得到"。

### Transaction

- 所有修改世界状态的顶层行为拥有 World 内单调递增的 TransactionId，用于 Debug、日志、Deterministic Random Key、事件索引、未来 Replay 辅助。
- Transaction 不采用完整 Event Sourcing；当前 World State 才是 authoritative truth。

### 随机性

- 所有随机性来自 WorldSeed，但不存在一个全局唯一、不断消费的 Random 实例。
- **Stateless Keyed Random** 是默认方案：HashRandom(WorldSeed, SystemKey, EventId, EntityId)，相同 Key 永远得到相同结果。适合一次性事件随机、Damage tie-break、AI 决策点、World generation cell、Loot、程序装饰。
- **Stateful RNG Stream** 只有当"随机序列推进本身具有领域意义"时使用，例如持续的 stochastic process。Stream 尽可能局部化：AIStream(EntityId)、MarketStream(SettlementId)。Stateful RNG State 必须进入 Save。

### Runtime Instance ID

- 运行时对象使用存档内唯一的确定性整数 ID：NextEntityId : ulong；不使用随机 UUID。
- ID 分配必须发生在确定性的 Commit 顺序中。跨存档的 Definition 身份继续使用 Namespaced ID。

## 首版范围

SimulationClock、Command、Transaction、Evaluate、Resolve、Commit、Structural Resolution、Domain Event，以及 Determinism 测试（roadmap Phase 4）。

## 明确延后

倍速 Gameplay、Offline Progress、Simulation LOD、跨平台 bit-perfect determinism、完整 Event Sourcing。

## 开放问题

- [定义确定性与数值策略](https://github.com/odradekk/train/issues/34)：域内连续量用 double、float 还是定点（转录 Q152）；域层禁止 Godot 数学类型与 `Vector<T>`；keyed random 与语义哈希采用哪种具名稳定哈希；哪些系统用有状态 RNG 流、哪些用无状态随机（转录 Q65、Q71）；浮点在存档中的往返格式；确定性测试的形态。
- Simulation LOD 列为未来功能，而转录 Q5 选择了全图全精度模拟；转录 Q5 已选全图全精度模拟，采用时需统一方向。
- Simulation Tick Rate：未冻结参数（[roadmap](roadmap.md)）。
