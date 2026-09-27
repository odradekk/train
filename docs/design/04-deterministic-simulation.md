# 04 确定性模拟

> 决策出处：转录 Q49、Q63–Q88、Q152；研究 [`docs/research/dotnet-godot-determinism.md`](../research/dotnet-godot-determinism.md)（票 #26）；2026-09-05 [#34](https://github.com/odradekk/train/issues/34) 的决策记录于 [ADR-0003](../adr/0003-use-scalar-doubles-and-stateless-random-for-determinism.md)。

## 边界

本文定义 Simulation 时钟、确定性目标、Tick 流水线、资源仲裁、Transaction、随机性、运行时 ID。Damage Transaction 的结构结算见 [03](03-composite-and-behavior.md)；存档内容见 [09](09-persistence.md)；窗口状态与 Pause 的关系见 [08](08-desktop-window-and-camera.md)。

## 不变量

- Simulation 与 FPS、Camera、Window Focus 解耦。
- 注册顺序、容器枚举顺序、线程完成顺序不得决定 Gameplay。
- 所有 Gameplay Random 最终源于 WorldSeed。
- 首版只使用 Stateless Random；后续有具体需求时才引入 Stateful RNG，其完整状态必须持久化。
- 结构修改按 Transaction 原子结算。

## 规则

### Simulation Clock

- Simulation 拥有独立于 Rendering FPS 的逻辑时钟。Godot 仅提供 Host：Godot SimulationHost → SimulationClock → WorldSimulation。
- Simulation 不依赖 SceneTree 中是否存在某对象、当前 FPS、Camera 是否看到某区域、窗口是否获得焦点。
- 遵守 [01](01-voxel-world.md) 的当前已定规则：全图全精度实时模拟（转录 Q5），不因距离或区域不可见而降低模拟精度。Visual Streaming 与表现层 LOD 不改变这一规则。
- 首版：Pause 与 1×。架构预留 2×、4× 等倍速，但在系统稳定前不开放倍速 Gameplay。

### 确定性目标

- 同一游戏构建 + 同一有效语义内容 + 同一初始状态或存档 + 同一模拟步输入 + 兼容运行环境 = 相同 Simulation Result。首版兼容边界为 Windows x64、同一 .NET 运行时版本；更换运行时、CPU 或操作系统后需要重新验证，不默认承诺逐位一致。#35 规定更新后的 Mod 数值作用于旧实例，因此变更语义内容不属于“相同输入”。
- 所有能够改变 World State 的遍历顺序必须显式稳定。不能让以下因素决定 Gameplay：Dictionary 枚举顺序、HashSet 枚举顺序、Behavior 注册顺序、Thread completion order、Node order、非确定性 Async completion。
- `GetHashCode()`、`HashCode.Combine()` 等运行时哈希不能生成随机键或持久化身份。字符串身份的比较与排序使用 Ordinal 语义，结果相关的集合遍历使用显式稳定顺序。
- #26 的源码与实验结论是选型依据，不是跨环境保证；库函数、运行时或数值优化变化均需在上述边界内通过确定性验证。

### 数值类型与计算边界

- 领域内连续量统一使用 `double`，包括沿轨距离与连续位置；体素坐标、计数、运行时 ID 继续使用整数。首版不引入定点数体系。
- 领域层使用普通 C# 类型与标量计算，连续三维坐标由三个 `double` 表达。Godot 数学类型仅用于适配与表现层，转换后的渲染值不能写回权威模拟状态。
- 首版领域层不使用硬件宽度可变的 `System.Numerics.Vector<T>`。后续仅在性能测试证明需要时考虑向量化，并单独验证其对计算结果的影响。
- 有效世界状态中的连续量必须有限，拒绝 `NaN` 与正负无穷值。

### 浮点存档往返

- `double` 以可无损往返的 JSON 数值保存，不按显示精度截断或固定小数位舍入。写入采用与文化无关的 round-trip 表示，读取必须还原相同的 `double` 值；不写入非有限值。
- 格式化与展示不改变世界状态。存档往返测试覆盖实际出现的连续量及边界值，并验证恢复后的模拟与未中断运行一致；存档整体结构见 [09](09-persistence.md)。

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

### 本阶段输入边界与顺序

[确定性和存档恢复决策](https://github.com/odradekk/train/issues/97)已确认世界修改、伤害、修复及列车控制统一记录“已完成模拟步号 + 该步边界内操作序号”，使同一步内的操作具有明确全序。运行中输入分配到确定的命令边界，并记录实际执行位置与次序；序号不能由线程完成顺序或重放时的墙钟时间重新猜测。

暂停操作经过完整正式事务及必要结构结算，但不推进列车运动、停站倒计时或模拟时钟。例如在完成第 100 步后依次拆轨、修轨和启动，三项操作具有不同序号，仍处于相同的模拟步边界。保存与重放保留该次序和待执行输入；具体捕获条件见 [09](09-persistence.md#保存边界与待执行输入)。

相机、面板查询及渲染原点操作另行记录，不参与领域仲裁。本规则细化输入的归属，不改变已有通行请求按等待先后及同一步稳定编组 ID 的仲裁规则。

#### 命令失效与内部故障

- 已接受的命令在执行时重新检查当前前提。目标已拆除、空间被占或启动条件失效时，按原执行位置记录明确拒绝结果并出队，不自动重试、不改指向其他对象，也不撤销之前已成功的独立命令；后续命令和模拟可以继续。复合原子命令内部仍须整体回滚。
- 有效待执行命令引用曾经存在但已销毁的目标，不等于存档损坏；允许随存档恢复，并在原时机检查和拒绝。未知格式、非法编号或损坏的必要世界引用仍按加载失败处理，不能借此宽松解释坏档。
- 不可恢复的内部异常或不变量破坏另作模拟故障，停在最后完整提交边界并导出诊断，不伪装成普通操作拒绝。

### Resolver

多个行为竞争共享资源时：Request → Resolver → Commit。例如 Engine A 与 Engine B 同时 RequestFuel，Resolver 采用 Explicit Gameplay Priority → Deterministic Fallback，而不是"谁先执行谁得到"。Facility 对库存 Input、Power、Labor 与 Storage Capacity 的竞争同样经 Resolver，Input 按完整 Batch 授予，Power / Labor 允许部分分配，见 [13](13-production-runtime-and-facilities.md)。生产、开采与施工 Job 严格遵守 Tick Barrier：本 Tick 提交的状态变化下一 Tick 才参与评估。

铁路多车通行使用同一确定性仲裁边界：完整路径授权不能由编组遍历先后抢占；按请求等待先后、同一步稳定编组 ID 仲裁，同距候选路径也使用稳定顺序。等待顺序在重算候选路径与读档后保持，具体运行语义见 [06 的网络运行契约](06-railway.md#本阶段网络运行契约)。拆分时占用与锁的转交必须与结构结算一致，不能暴露先清空保护再重建的中间状态。

### Transaction

- 所有修改世界状态的顶层行为拥有 World 内单调递增的 TransactionId，用于 Debug、日志、Deterministic Random Key、事件索引、未来 Replay 辅助。
- Transaction 不采用完整 Event Sourcing；当前 World State 才是 authoritative truth。

### 随机性

- 所有随机性来自 WorldSeed，但不存在一个全局唯一、不断消费的 Random 实例。
- **首版全部采用 Stateless Keyed Random**：由 WorldSeed、用途和坐标或事件身份决定结果，与调用先后顺序无关。世界生成使用生成阶段、坐标或候选身份及重试序号；装饰使用用途与空间身份；断裂平局裁决使用事件与候选身份。不得用候选枚举顺序充当身份。
- **算法**：基于 [SplitMix64 参考实现](https://prng.di.unimi.it/splitmix64.c) 的 64 位整数混合函数组合显式输入。运算按 `unchecked ulong` 语义执行；每次调用只依赖传入的键，不保存或推进参考生成器中的可变状态。
- **随机键契约**：固定算法版本、各用途的字段顺序与编码。数字位宽、坐标符号、字符串编码与字段边界必须明确，不能通过运行时哈希、文化格式或注册顺序隐式产生。首次实现的切片 spec 必须写出完整键布局和组合步骤，并提供固定输入到预期输出的黄金向量；后续实现调整不得悄悄改变结果。
- 本阶段生成的阶段、候选与重试使用稳定用途及空间或候选身份构造键，候选身份与正式 EntityId 分离，不使用候选遍历序号或线程完成顺序。键规范随相应生成/运行规则版本解释，不能静默改变旧输入含义；失败候选不能通过消耗正式 ID 改变后续随机身份。
- **Stateful RNG Stream 延后**：当前不实现有状态随机流，也不预置 AI、市场等流。后续玩法确实需要序列推进时再添加局部流，并保存完整状态。

### 内容哈希

- Semantic Revision 使用 SHA-256。它沿用现有 Catalog 的算法，不沿用将定义属性与图片像素混在一起的 Fingerprint 规则。
- 参与语义哈希的字段、规范化编码以及 Asset Revision 与 Catalog Generation 的分工见 [02](02-definitions-and-content.md)。随机取值与 Revision 内容身份是两个独立契约。

### 确定性验收

- 同一初始状态和命令序列重复运行，在相同 Simulation Tick 及相同边界内操作位置比较权威状态哈希；哈希输入按稳定顺序覆盖影响后续模拟的状态，排除渲染、窗口和缓存状态。
- 在相同有效语义内容下比较连续运行与中途 Save → Load → Continue，恢复到相同模拟步时结果一致；更新内容后的读档另验收新规格生效与状态适配。
- 改变渲染帧率、暂停恢复和命令入队时机，只要命令仍在相同模拟步执行，模拟结果就必须一致。
- 暂停期间有多次操作时，还须保持相同的边界内次序；同一步号本身不足以确定输入相同。内容发布尝试按 [09](09-persistence.md#复现录制范围与内容切换)记录原输入及结果，跨内容版本的录制不能只保留末尾版本。
- 以固定输入与预期输出验证随机算法及其键编码，避免“同一错误实现跑两次”仍通过。具体场景、步数及检查点随首批切片 spec 落定。

本阶段还须完成以下证据矩阵；它定义验收要求，不代表当前实现已通过：

| 对照或场景 | 必需证据 |
| --- | --- |
| 同输入重复运行、保存退出恢复继续 | 在已定构建、语义内容与兼容环境边界内，跨进程比较同一步及同一边界内操作位置的规范化权威状态；除哈希外，失败报告能定位首次差异对象与字段。 |
| 暂停修改、待执行命令 | 保留操作次序，覆盖命令成功及拒绝；执行前目标消失等预期拒绝不重试、不换目标、不撤销先前独立操作，后续命令继续；保存恢复前后结果一致。 |
| 铁路及车辆状态转换 | 跨道岔、授权/排队、停站、拆分保护、换端、断轨与故障冻结恢复均与不中断运行对照。 |
| 建筑状态转换 | 受损及多分离部分、原位残块、完整修复前后保持身份、修复目标、归属、占用与站点引用一致。 |
| 生成与外部执行条件 | 覆盖生成成功、失败及重试；改变集合枚举顺序、渲染帧率、相机/驻留和原点条件，不改变同输入的领域结果。 |
| 损坏或不兼容输入 | 明确拒绝并保持原会话；合法内容更新另验收适配，不混入同输入一致性对照。 |
| 复现窗口与文件 | 新起点可用后才裁剪，覆盖容量不足/起点失败及完整性标记；重放内容发布成功与拒绝、纯表现切换及失败世界切换。导出失败保留已有文件，缺依赖/不适用版本/损坏包明确拒绝。 |

关键结构事务前后设置检查点，随机算法和编码另以黄金向量验证。标准生成地图须纳入已定的 10 节泰坦、4 节及 8 节跨档混编三列联合运行；具体时长、负载和检查点安排由[性能验收](https://github.com/odradekk/train/issues/98)及[阶段规格](https://github.com/odradekk/train/issues/99)承接。

### Runtime Instance ID

- 运行时对象使用存档内唯一的确定性整数 ID：NextEntityId : ulong；不使用随机 UUID。
- ID 分配必须发生在确定性的 Commit 顺序中。跨存档的 Definition 身份继续使用 Namespaced ID。
- 生成候选使用独立稳定身份，正式实体 ID 只按成功提交的稳定顺序分配；失败候选或失败事务回滚临时实体 ID 分配，输入序号与拒绝记录仍保留。已成功提交的 ID 即使对象销毁也不回收或重新分配给另一对象。
- 保存并核验 NextEntityId 及必要操作序号，计数溢出明确拒绝，不绕回或重排现有 ID。失效命令仍保留输入顺序和拒绝记录，按上文规则出队并继续后续独立操作。

## 首版范围

SimulationClock、Command、Transaction、Evaluate、Resolve、Commit、Structural Resolution、Domain Event、标量 double 数值边界、基于 SplitMix64 混合的无状态随机、SHA-256 Revision 算法选择，以及上述确定性验收（roadmap Phase 4）。

## 明确延后

有状态随机流、定点数体系、未经独立验证的领域向量化；倍速 Gameplay、Offline Progress、跨平台 bit-perfect determinism、完整 Event Sourcing。

Simulation LOD 仅作为延后评估的候选方向，不是已承诺的后续功能；未来引入须先重审全图全精度模拟规则，见下文开放问题。

## 开放问题

- 生成网络、多列车竞争、输入顺序与恢复契约已由[确定性与存档决策](https://github.com/odradekk/train/issues/97)确认，沿用既定模拟不变量；具体编码、黄金向量、检查点及切片验收由[阶段规格](https://github.com/odradekk/train/issues/99)落实，负载和录制容量由[性能验收](https://github.com/odradekk/train/issues/98)承接。
- 地图迷雾（不立票）：Simulation LOD；若后续性能问题促使考虑分层模拟，须先重审转录 Q5 的全图全精度模拟规则，并同步 [01](01-voxel-world.md)、本文与 [07](07-rendering.md)；新决策前继续遵守当前规则。
- 地图迷雾（不立票）：Simulation Tick Rate 调优（[roadmap](roadmap.md)）；出现需要改变当前固定步长的近期验证问题时再评估。
