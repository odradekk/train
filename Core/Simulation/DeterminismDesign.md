# 确定性契约的数据与方法设计

数据定义、方法签名和格式约定已在结对讨论中确认，并按 design-recipe 完成例子测试、结构模板和计算实现。`SimulationSession.Submit` 已接入编码与日志，状态和日志候选计算完成后一次发布；独立固定向量与真实跨进程测试验证确定性契约。

规范依据为 [04](../../docs/design/04-deterministic-simulation.md#规范编码)、[09](../../docs/design/09-persistence.md#保存边界)，范围依据为 [结对结论](https://github.com/odradekk/train/issues/221#issuecomment-6034533033)。以下区分既有规范与本轮已确认的具体约定。

## 数据定义

| 数据 | 种类与解释 | 命名例子 |
| --- | --- | --- |
| `EncodedCommand` | 结构：规范负载文本及其 UTF-8 SHA-256；不含执行身份。 | `NoChangePayload` |
| `CommandExecution` | 复用已有结构：来源、命令号、位置、事务号、执行/拒绝/故障及原因。 | `Executed`、`Rejected`、`Faulted` |
| `CommandLogEntry` | 结构：负载、执行记录、记录规范文本、截至此记录的全历史摘要。 | `ExecutedEntry` |
| `CommandLogSnapshot` | 结构：窗口前摘要和有序不可变记录；记录数 0–256。 | `EmptyLog`、`OneEntryLog` |
| `RandomKey` | 结构：世界种子、非空用途名、用途声明的有序 UInt64 分量。 | `EmptyComponents`、`ResourceChoice`、`MaxComponents` |
| `CoreStateSnapshot` | 结构：领域格式名、正版本、完整不可变权威状态；Fault 是 null/故障记录的可选分支。 | `BoundarySnapshot`、`ResourceSnapshot`、`FrozenSnapshot` |
| `CheckpointObjectId`、`CheckpointObject` | 结构：种类与数值身份、只读字段树；字段树复用 .NET `JsonElement` 的 null/布尔/数字/字符串/数组/对象结构。 | `ResourceObject`、`EntityObject`，含数组和嵌套对象 |
| `AuthoritativeCheckpoint` | 结构：完整边界位置、规范对象文档、摘要。 | `BoundaryCheckpoint` |
| `DeterminismEnvironment`、`DeterminismIdentity` | 结构：来源、实际构建、语义内容摘要及 Windows x64/.NET 运行时身份；位于权威文档外。 | `Identity`；这里只是格式例子，不是已验证身份 |
| `CheckpointTrace` | 结构：身份与按模拟位置严格递增的检查点序列。 | `EmptyTrace`、`OnePointTrace` |
| `CheckpointDifference` | 结构：最早位置、可选对象、字段路径和两侧值；缺失用 null，JSON null 用文本 `"null"`。 | `ChangedHolding`、`MissingObject`、`MissingPoint` |
| `DeterminismErrorKind`、`DeterminismError` | 枚举与结构：未知格式/非法数据/上下文不兼容、稳定代码和 JSON Pointer。 | `UnknownFormat`、`InvalidData`、`IncompatibleContext` |
| `DeterminismResult<T>` | 分支：完整成功值，或单一失败；无部分成功值。 | `DecodedCommand`、`FailedCommand`、`DecodedState`、`FailedState`、`RestoredSession`、`FailedRestore` |
| `CheckpointComparison` | 分支：已验证一致、首差异、不能进行同输入比较。 | `Equal`、`Different`、`NotComparable` |

数据例子位于 [DeterminismExamples.cs](../../Train.Tests/Core/Simulation/DeterminismExamples.cs)，函数例子位于同目录的编码、日志、随机键、状态及检查点测试。SHA-256 向量使用标准算法对手写文本计算；随机键的十组字节和 UInt64 向量用独立 Python 标准库参考程序计算，不调用被测模块。

## 方法清单：输入、输出与计算职责

| 方法 | 输入 → 输出 | 主要计算与效果 |
| --- | --- | --- |
| `SimulationCommandCodec.Encode` | 已解析命令 → `EncodedCommand` | 规范负载和单条摘要；不分配身份、不修改会话。 |
| `SimulationCommandCodec.Decode` | 外部 JSON → `DeterminismResult<CoreCommand>` | 校验 v2 包络和具体字段，构造命令；失败不接受命令。 |
| `SimulationCommandLog.Append` | 旧窗口、执行结果、负载 → 新窗口 | 记录规范文本、新链摘要、裁剪与摘要锚点；不修改旧窗口。 |
| `SimulationSession.GetCommandLog` | 会话 → 日志快照 | 读取已发布历史；计算期间仍可读旧窗口。 |
| `KeyedRandom.Encode` | 完整键 → 不可变字节 | 固定位宽、字节序、字符串和字段边界。 |
| `KeyedRandom.Sample` | 完整键 → UInt64 | 固定 SplitMix64 组合；不消费随机状态。 |
| `SimulationStateCodec.Capture` | 完整状态 → `CoreStateSnapshot` | 捕获全部权威字段并附领域格式；不额外推进或解除冻结。 |
| `SimulationStateCodec.Encode` | 捕获值 → JSON | 可跨进程恢复的核心载荷；不写玩家存档或文件。 |
| `SimulationStateCodec.Decode` | JSON → 完整候选或错误 | 读包络、基础边界与领域字段；不发布状态。 |
| `SimulationCalculations.CheckInitialState` | 完整起点 → 首个不变量错误或 null | 检查起点合法性；与现有 before→candidate 后继检查分别使用。 |
| `SimulationStateCodec.Restore` | 候选、计算对象、命令格式 → 新会话或错误 | 校验全部起点后建立新会话；宿主成功后整体替换，失败不改变当前会话。 |
| `SimulationStateCodec.CreateCheckpoint` | 完整状态 → 检查点 | 业务对象投影、基础对象、规范文档和摘要；排除日志及主机设置。 |
| `SimulationCheckpoints.Decode` | 另一进程的文档 → 检查点或错误 | 检查规范对象结构，得到位置和摘要。 |
| `SimulationCheckpoints.Compare` | 两条轨迹 → 三种对照结果 | 先核对上下文，随后定位最早采样位置、对象及字段。 |

领域格式只提供实际数据计算：命令的种类、字段写入和字段读取；状态的业务字段写入、完整领域状态读取和权威对象投影。公共层不反射 CLR 类型、不建立种类注册系统、不定义生产世界命令。

会话的效果约定：接受前完成命令编码；执行、拒绝和故障都生成日志候选。提交前计算完整状态与日志候选，随后一次发布二者；入口拒收不写执行日志。内部故障仍回滚权威字段并附 Fault，但保留这次尝试的故障日志。日志候选计算不能置于权威状态已经发布之后。所有调用方已迁移到接收命令格式的三参构造器，不保留绕过日志的运行分支。

## 已确认的具体格式

### 命令与日志

04 已规定输入版本 2、紧凑 UTF-8 JSON、顶层版本/种类/固定种类字段顺序、严格字段与数值检查、256 条窗口，以及链摘要公式。本轮已确认如下约定：

- 输出无 BOM、无尾换行，沿用当前内容模块的 `Utf8JsonWriter` 和 `UnsafeRelaxedJsonEscaping` 约定；所有数值按明确位宽与有限 double 的 InvariantCulture `R` 文本写入。
- 执行记录采用独立版本 1，字段顺序为 `record_format_version`、`source`、`command_id`、`position`、`transaction_id`、`payload`、`outcome`、`reason`。position 内先 `completed_steps` 后 `sequence`；payload 内嵌规范命令对象，reason 显式写 null 或稳定代码。
- 来源文本固定为 `ui`/`console`/`automation`；结果固定为 `executed`/`rejected`/`faulted`。此映射不使用 enum.ToString、注册次序或运行时哈希。
- 链输入严格是 **64 字符前一摘要文本 + 一个 LF + 记录规范文本** 的 UTF-8 字节，不用 32 字节二进制摘要代替。初值为 64 个零。
- 第 257 条起裁剪最旧条目，保留被裁记录的最后摘要作为 `PrefixSha256`；后续摘要继续覆盖整个本次会话历史。读取窗口即可从锚点核对其中各条记录。
- 日志不进入恢复载荷或检查点。恢复新会话从空链开始，权威状态比较不受历史窗口影响。

第一组具体命令仅放在资源测试夹具：`fixture.no_change`、`fixture.create_entity`、`fixture.delete_entity`、`fixture.create_then_reject`、`fixture.invalid_candidate`。仅删除命令带非零 UInt64 `entity_id`。额外的 `fixture.encoding_probe` 用于验证标量、转义、有限 double 的 R 文本、无序集合和有序序列；生产领域以后提供自己的格式。

### 随机键

04 已规定无状态 SplitMix64、显式版本/用途/稳定身份，以及每个用途独立布局和固定向量；本轮已确认无符号身份的基础格式：

1. 四个 ASCII 字节 `TRNG`。
2. UInt32 小端算法版本 1。
3. UInt64 小端 WorldSeed。
4. UInt32 小端用途 UTF-8 字节数，再写完整用途字节。
5. UInt32 小端分量个数，再依次写每个 UInt64 小端分量。

用途非空，不折叠大小写、不做 Unicode 归一化；分量序列允许为空、零和 UInt64.MaxValue，各用途再约束身份意义。本轮固定算法 1，不提供版本选择或隐式兼容路径。当前不增加 signed、范围采样、概率映射或任何生产用途。

设编码字节为 b[0..n)，组合为 `a=0`，逐字节执行 `a=SplitMix64.Mix(a XOR b[i])`，最后返回 `SplitMix64.Mix(a XOR (UInt64)n)`。全部运算按 unchecked UInt64；使用现有 Mix 的含增量语义。测试用途 `fixture.resource-choice` 的分量顺序固定为事务号、实体 ID。算法、字节和结果分别提供独立预期向量。

### 核心恢复载荷

已确认保存全部权威边界、跨进程恢复、失败不改变当前会话，完整玩家存档组织留给存档工单。核心包络与玩家存档版本分别解释：

- 顶层固定 `core_state_format_version`（1）、`state_kind`、`state_version`、`simulation`、`domain`。
- simulation 固定为 `world_seed`、`generator_version`、`completed_steps`、`boundary_sequence`、`next_command_id`、`next_entity_id`、`transaction_count`、`fault`。
- fault 为 null，或 `code`、`phase`、`context`、`entity_id`。context 为 null，或 `transaction_id`、`operation`、`position`、`command_id`；position 与日志一致。phase 固定小写枚举映射，operation 为 `command`/`step`，可选编号显式为 null；不编码异常消息、栈或墙钟。
- domain 的固定字段、集合与序列含义由对应 StateKind/StateVersion 的格式定义，未知领域格式明确拒绝；不使用通用反射多态序列化。
- 起点检查独立于后继检查；检查基本 ID/边界约束后检查实际领域关系。版本、字段、计数或领域关系不合法都返回完整错误，无半恢复会话。
- 起点计数按已完成事务的含义检查：`TransactionCount = CompletedSteps + (NextCommandId - 1)`，求和不能溢出；`BoundarySequence <= NextCommandId - 1`。下一命令号与下一实体 ID 非零，耗尽哨兵沿用第一步语义。故障中的尝试上下文不计入已完成事务。
- 故障尝试上下文如存在，须使用冻结边界下一次事务的身份：事务号加一，命令使用下一命令号和当前步内下一序号，模拟步使用下一步、序号零且命令号为 null。准备身份前的故障允许空上下文；不根据故障阶段推断操作种类。
- 实体 ID 的唯一性、非零、相对分配器上界以及业务引用由领域起点检查负责；基础检查不猜测尚未存在的世界实体模型。
- 允许恢复含 Fault 的完整状态，并保留冻结。恢复无故障起点或重新创建世界才恢复运行，不在读取时静默清除 Fault。

资源测试格式固定为 `fixture.resources` 版本 1；domain 按 `stock`、`holdings`、`entity_ids`、`demands` 顺序写入。库存与持有量是 UInt64，实体集合为非零身份，holdings 为唯一 `entity_id` 与 `amount` 对，demands 为唯一 `(entity_id, request_id)`、Int32 `priority`、UInt64 `amount` 和 bool `allow_partial`。这些集合写入时按稳定身份排序，读取时允许改变枚举顺序并检查重复身份；业务引用和分配器上界由起点检查验证。

### 检查点与首差异

04 已规定独立规范文档、完整权威字段、UTF-8 SHA-256、稳定集合顺序、上下文身份和最早对象/字段。本轮已确认：

- 顶层固定 `checkpoint_format_version`（1）、`objects`；每个对象固定 `kind`、`id`、`fields`。simulation/0 为必需基础对象，业务投影不能占用该身份。
- 对象先按 Kind 的 Ordinal，再按 UInt64 ID 排序；Fields 和嵌套对象按字段名 Ordinal 排序，数组按索引保持顺序。领域无序集合须在投影时按稳定身份排序，不能由公共层猜测一个数组是不是集合。
- 基础对象纳入全部已有权威字段，故障独立于合法业务状态。领域投影必须包括所有影响后续模拟的业务字段和关系；不只投影当前画面显示的数据。
- 比较轨迹先核对原始起点、实际构建、语义内容摘要及环境，缺失/非法/不兼容返回 NotComparable；运行时使用实际 System.Environment.Version 的数值版本文本，包含主版本、次版本和构建版本。内容不同明确归为内容适配。两个空轨迹也要先核对身份。
- 首差异全序：采样位置（已完成步号、步内序号）→ 对象（Kind、数值 ID）→ 字段 Ordinal → 子对象字段/数组下标。缺失采样、对象或字段都能报告；null 值与缺失分别表示。
- 位置和摘要从实际文档读取/计算。相同调用方摘要不能取代实际权威字段比较。

## 函数例子与验证顺序

1. 命令公共包络及夹具 kind：独立文本/摘要向量，乱序字段重编码一致，旧格式、重复/未知/缺失字段及非法数值明确拒绝。
2. 日志：三种结果、入口拒收、旧快照不变；第 256/257/258 条验证裁剪、锚点和持续全历史链。
3. 随机键：零/最大种子和分量、空分量、不同用途、Unicode 字节长度与字段边界、交换身份顺序；独立字节和 UInt64 结果向量。
4. 状态捕获/恢复：全部基础及资源字段、拒绝后的顺序/分配器、冻结故障、非法版本/字段/关系；失败保留当前会话。
5. 检查点：独立文本/摘要、对象枚举置换、有序关系、非有限值；多处不同只报告最早对象/字段，身份不兼容不能报告已验证一致。
6. 跨进程对照：使用普通 C# 测试驱动而非 Godot 或生产 Train.Headless，分别运行连续基线、独立重复、保存后退出并在新进程恢复继续。通过真实测试目录读写载荷，在相同模拟位置比较检查点，并变化每帧步数、暂停与倍速安排；命令保持相同模拟位置与步内顺序。

以上例子已写成测试。真实跨进程驱动复用 `Train.Tests` 的 C# 可执行入口，保持普通 MSTest 测试发现；没有新增生产进程。两套宿主帧安排都实际经过暂停与 1/2/4/8 倍速：steady 的实际推进调用为 `[1,1,1,1]`，bursty 为 `[1,1,2]`；在相同位置采样，step 3 不另采样。恢复进程从 `(1,3)` 的完整核心载荷继续；先在恢复边界立即采样，再执行剩余步骤，使用同一原始起点身份，并从空日志链开始。每个位置还以已完成事务号和实体 ID 采样随机键，期末结果有独立固定向量；这些派生证据不写入权威文档。构建身份由实际 `Train.Core.dll` 与 `Train.Tests.dll` 的文件摘要清单计算，父进程独立核对。

运行 `pwsh -NoProfile -File scripts/run-tests.ps1 -Family unit -Family headless -Scope simulation -BuildGame` 验证函数例子和真实进程入口。测试分别断言独立预期值和完整轨迹比较，子进程的载荷、结果和 stdout/stderr 留在本次 TRX 结果目录内。去掉 `-Scope simulation` 可执行所有 unit/headless 检查。

## 计算实现的职责分解

| 入口 | 分解后的任务 |
| --- | --- |
| 命令编码/解码 | 种类负载、UTF-8 摘要；公共包络读取成功/失败后分别读取领域或传播错误。 |
| 日志追加 | 执行记录、链摘要、窗口裁剪；旧窗口与追加条目均保持不可变。 |
| 随机键 | 完整键的字节布局；逐字节累积混合及末尾长度混合。 |
| 状态编码/解码 | 基础边界、领域、包络；读取成功后构造完整领域状态，失败保留错误路径。 |
| 状态恢复 | 格式检查、完整起点检查、错误定位、新会话建立；当前会话不作为输入。 |
| 检查点 | 基础及领域投影、对象文档、位置和摘要；字段树按对象/数组/标量自然递归。 |
| 轨迹比较 | 先核对身份，再按位置比较完整文档；缺点、缺对象、缺字段与 JSON null 分开。 |

公共实现按此清单分解计算，并共用严格字段读取和基础状态文档，避免恢复载荷与检查点采用不同字段契约。该步骤不涉及暂停/倍速控件；真实世界界面由 [#223](https://github.com/odradekk/train/issues/223#issuecomment-6034593378) 完成。
