# .NET 与 Godot 4.7 的确定性事实

研究票：[调研 .NET 与 Godot 4.7 的确定性事实](https://github.com/odradekk/train/issues/26)

研究日期：2026-09-04

本票只记录有官方依据或本机可复现依据的事实，并给出建议。数值策略与确定性承诺的最终决定属于「定义确定性与数值策略」票（#34），本文不替它做决定。

## 结论

按 §25 的目标（同一游戏版本 + 同一存档 + 同一输入 + 兼容运行环境 = 相同 Simulation Result），下列事实已确认：

- **必然破坏确定性、必须禁止进入 Simulation**：`string.GetHashCode()`、`HashCode.Combine`、`object.GetHashCode()` 的默认实现（每进程随机种子）；`Dictionary`/`HashSet` 的枚举顺序（文档明确为 undefined）；`string` 的默认（culture-sensitive）比较与排序（依赖 ICU 版本与操作系统）。
- **算法可用但没有跨版本承诺**：带种子的 `System.Random`。它在 .NET 6 及之后仍走 Knuth 减法生成器的兼容实现（直到当前 main 分支未变），是纯整数运算，本机重复运行完全一致；但官方文档明确说明不保证跨 .NET 大版本稳定。
- **x64 上的 `double`/`float` 基本四则运算与 `Math.Sqrt` 是确定的**：RyuJIT 在 x64 上把浮点分配到 XMM 寄存器并发射 SSE2 标量指令（`addsd`/`mulsd`/`divsd`/`sqrtsd`），按 IEEE-754 精确舍入到目标类型，不存在 x87 80 位超精度问题。本机实测确认 RyuJIT **不会**把 `a * b + c` 自动收缩成 FMA，且分层编译（tier0 到 tier1）与 Debug/Release 都不改变结果位。
- **不确定的浮点来源是三处，不是"浮点本身"**：`Math`/`MathF` 的 22 个超越函数（官方文档写明调用底层 C 运行时，结果随操作系统与架构而异）；`Vector<T>` 的宽度随 CPU 与运行时开关变化（本机把 `DOTNET_EnableAVX2=0` 就把 `Vector<float>.Count` 从 8 变成 4）；任何随宽度变化的归约顺序。
- **Godot 的数学类型不能进入领域层**：`Godot.Vector3` 等类型的分量类型是 `real_t`，在 C# 侧由 `global using real_t = System.Single` 或 `System.Double` 决定，取决于引擎是否以 `precision=double` 编译。同一份 C# 源码在两种引擎构建下语义不同，且切换需要重编译引擎与导出模板。Godot 物理引擎被官方明确声明为非确定性。
- **稳定哈希已经有现成方案**：`System.IO.Hashing` 是 NuGet 包（非 in-box），9.0.0 提供 `net8.0` 目标框架且无额外依赖，可直接用于本项目。本机实测其 XXH32/XXH64/XXH3-64 输出与 xxHash 官方 sanity 向量逐位一致，xxHash 规范承诺同一变体在任何 CPU/OS 上输出相同。
- **定点数没有内建类型，但成本可控**：C# 无 fixed-point 类型。`long` 上的 Q32.32 用 `Math.BigMul(ulong, ulong, out ulong)` 实现，本机实测与 `double` 同一数量级；用 `Int128` 实现则慢约 12 倍；`decimal` 语义完全确定（C# 标准规定精确计算后按 banker's rounding 舍入）但比 `double` 慢约 7 倍。

## 事实、结论和建议

### 本次实测环境

本文中标注"实测"的数据均在本机取得，可复现：Windows 11 x64；`Fma.IsSupported=True`、`Avx2.IsSupported=True`、`Avx512F.IsSupported=False`；.NET SDK `9.0.316`，`TargetFramework=net8.0`，运行时 `8.0.30`；Release 构建（另标注处为 Debug）。探针工程建在仓库外的临时目录，未写入本仓库。

### 哈希码的每进程随机化

**观察**：`String.GetHashCode` 的文档写明哈希码"不保证稳定"，在不同 .NET 实现、不同 .NET 版本、同一版本的 32/64 位平台之间都可能不同，甚至同一程序的两次连续运行也可能返回不同哈希码；因此"绝不应被持久化"。[String.GetHashCode](https://learn.microsoft.com/en-us/dotnet/api/system.string.gethashcode)

**观察**：这不是理论可能性。运行时用 Marvin 哈希实现 `string.GetHashCode`，其 `Marvin.DefaultSeed` 由 `Interop.GetRandomBytes` 在静态初始化时生成。[Marvin.cs](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/Marvin.cs)

**观察**：`System.HashCode` 的官方文档写明它"使用一个静态初始化的随机种子来强制这一最佳实践，意味着哈希码只在一个操作系统进程范围内是确定的"。[HashCode 文档源](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System/HashCode.xml)

**观察**：源码确认 `HashCode` 的 `s_seed` 同样来自 `Interop.GetRandomBytes`，并参与 `Initialize` 的四个初始状态。[HashCode.cs](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/HashCode.cs)

**观察**：实测。同一二进制连续两次运行，`"rail.straight".GetHashCode()` 分别为 `0x5FCEC050` 和 `0xBA3E22CD`；`HashCode.Combine(1, 2)` 分别为 `0x04979BEB` 和 `0xE50B8AC5`。

**结论**：任何进入 Simulation、存档、事件 ID、随机 key 或排序键的哈希，都不能来自 `GetHashCode()`/`HashCode`。这直接约束 §32 的 `HashRandom(WorldSeed, SystemKey, EventId, EntityId)`：它必须用自己实现的稳定哈希，不能用 `HashCode.Combine`。

**建议**：把默认 `GetHashCode` 的用途限定在"进程内哈希表桶定位"。领域层需要哈希语义时使用独立命名的稳定函数（见"稳定哈希"一节）。可考虑用分析器规则或代码评审约定禁止 `Domain/` 下出现 `HashCode.Combine`。

### 带种子的 System.Random

**观察**：`Random` 的官方文档写明"`Random` 类中随机数生成器的实现不保证在 .NET 大版本之间保持相同。因此不应假设同一种子在不同 .NET 版本中产生相同的伪随机序列"。[Random 类](https://learn.microsoft.com/en-us/dotnet/api/system.random)

**观察**：源码显示 .NET 6 起 `Random` 按构造方式分流：无参构造且类型恰为 `Random` 时使用 `XoshiroImpl`（xoshiro256\*\*）；**带种子**构造时使用 `Net5CompatSeedImpl`，注释写明"为了兼容曾经依赖该序列的场景"。[Random.cs (v8.0.0)](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/Random.cs)

**观察**：兼容实现是 Knuth 减法生成器：56 元素 `int` 种子数组、幻数 `161803398`、`_inext=0`/`_inextp=21`，全部为整数运算；`Sample()` 为 `InternalSample() * (1.0 / int.MaxValue)`。[Random.Net5CompatImpl.cs](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/Random.Net5CompatImpl.cs)

**观察**：当前 main 分支（.NET 10 开发线）中带种子路径仍走兼容实现（类名改为 `CompatSeedImpl`）。[Random.cs (main)](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Random.cs)

**观察**：实测。`new Random(12345).Next()` 的前四个值在两次运行中均为 `143337951, 150666398, 1663795458, 1097663221`；`NextDouble()` 前三个值的位模式均为 `3FB11653BE222CA7 3FB1F5F93C23EBF2 3FE8CAE040B195C1`。

**结论**：带种子 `System.Random` 在 .NET 6/8/9/10 上事实稳定，且底层是整数算法，不受浮点差异影响。但这是**实现事实，不是契约**。若把它写进存档格式（例如 §32.2 的 Stateful RNG Stream 状态），运行时升级就可能悄悄改变 Gameplay。

**建议**：Simulation 内部不直接使用 `System.Random`。§32.2 需要的 Stream 用自实现、可序列化的生成器（如 SplitMix64 或 xoshiro256\*\*），状态就是一到四个 `ulong`，可以直接进存档，也不依赖 BCL 的版本行为。`System.Random` 只留给工具、测试夹具、纯表现层。

### 浮点：语言标准允许的自由度

**观察**：C# 标准（ECMA-334）§8.3.7 写明"浮点运算可以以高于运算结果类型的精度执行"，并举例说明硬件的扩展精度类型可以被用于所有浮点运算；要强制回到目标类型的精确精度，需要显式 cast。[C# standard, types.md](https://github.com/dotnet/csharpstandard/blob/draft-v8/standard/types.md)

**结论**：语言层面并不保证 `double` 表达式逐位可复现。这条自由度是 x87 时代留下的；它意味着确定性不能建立在"C# 规范保证"上，只能建立在"我们锁定的目标平台上 JIT 的实际行为"上，这与 §25"当前不要求不同 CPU/操作系统之间逐 bit 完全一致"的边界一致。

**建议**：把确定性承诺写成"Windows x64 + 指定 .NET 主版本 + 指定 Godot 构建"，而不是"C# 保证"。这一点应在 §25 里显式补上运行环境的定义。

### 浮点：RyuJIT 在 x64 上的实际行为

**观察**：RyuJIT 的 x64 目标定义把所有浮点寄存器映射到 `XMM0`–`XMM15`，`REG_FLOATRET`/`REG_DOUBLERET` 均为 `REG_XMM0`。[targetamd64.h](https://github.com/dotnet/runtime/blob/v8.0.0/src/coreclr/jit/targetamd64.h)

**观察**：指令表中标量 `double` 运算使用 SSE2 指令 `addsd`/`mulsd`/`divsd`/`sqrtsd`。[instrsxarch.h](https://github.com/dotnet/runtime/blob/v8.0.0/src/coreclr/jit/instrsxarch.h)

**结论**（推断，基于上述两条与 IEEE-754）：x64 上 `double`/`float` 的 `+ - * /` 与 `Math.Sqrt` 在寄存器中就按目标类型宽度舍入，不会出现 x87 的 80 位中间结果，因此 C# 标准允许的"更高精度"在这个平台上不会发生。

**观察**：实测 FMA 收缩。取 `a = 1 + 2^-52`、`b = 1 - 2^-52`，在 `Fma.IsSupported=True` 的机器上：`a * b + (-1.0)` 得到 `+0.0`（位 `0000000000000000`），`Math.FusedMultiplyAdd(a, b, -1.0)` 得到 `-4.930380657631324E-32`（位 `B970000000000000`）。两者不同，说明 JIT 没有把乘加收缩成 FMA。`float` 版本同样不收缩。

**观察**：.NET 9 新增的 `Vector64/128/256/512.MultiplyAddEstimate` 文档写明"在原生支持 `FusedMultiplyAdd` 的硬件上，本方法**可能**返回按一次三元运算舍入的结果；在没有专门支持的硬件上，可能就返回 `(left * right) + addend`"。[Vector64.cs](https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/System.Private.CoreLib/src/System/Runtime/Intrinsics/Vector64.cs)

**结论**：普通 `a * b + c` 是两次独立舍入的确定运算；只有显式调用 `Math.FusedMultiplyAdd`（确定，单次舍入）或 `*.MultiplyAddEstimate`（**不确定**，随硬件变化）才涉及 FMA。后者按名字就是 estimate，不得进入 Simulation。

**观察**：实测分层编译。同一个 `[MethodImpl(NoInlining)]` 浮点表达式调用 200 万次（跨越 tier0 到 tier1 重编译），结果位模式与首次调用差异次数为 0。

**观察**：实测构建配置。同一表达式的编译期常量折叠结果与运行期计算结果逐位相同，且 Debug 与 Release 输出相同（`double` 位 `3F7B4E81B4E81B50`，`float` 位 `3BDA740F`）。

**结论**："同一二进制 + 同一机器"对标量浮点四则运算与 `Math.Sqrt` 是可靠的：分层编译、Debug/Release、常量折叠都不改变结果。

**建议**：允许 Simulation 使用 `double` 做标量四则运算与 `Math.Sqrt`；禁止 `MultiplyAddEstimate` 与任何 `*Estimate`/`*Reciprocal*` 近似 API。

### Math / MathF 与平台 C 运行时

**观察**：`System.Math` 的以下 22 个成员在官方文档中带有同一句备注——"本方法调用底层 C 运行时，确切结果或有效输入范围可能随不同操作系统或架构而异"：`Acos`、`Acosh`、`Asin`、`Asinh`、`Atan`、`Atan2`、`Atanh`、`Cbrt`、`Cos`、`Cosh`、`Exp`、`ILogB`、`Log`、`Log10`、`Log2`、`Pow`、`ScaleB`、`Sin`、`SinCos`、`Sinh`、`Tan`、`Tanh`。`Math.Sqrt` 没有这条备注。`System.MathF` 有 23 处同样的备注。[Math 文档源](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System/Math.xml)

**观察**：CoreCLR 的原生实现确实直接转调 C 运行时的 `pow`、`exp`、`sin`、`sincos` 等。[floatdouble.cpp](https://github.com/dotnet/runtime/blob/v8.0.0/src/coreclr/classlibnative/float/floatdouble.cpp)

**结论**：超越函数是 §25 边界内唯一"同机器同二进制仍可能随运行环境变化"的浮点入口——一旦操作系统的 CRT/libm 更新，`Math.Sin` 的最后一位就可能变。`Math.Sqrt` 因为是 IEEE-754 要求正确舍入的运算且映射到 `sqrtsd`，不在此列。

**建议**：Simulation 内禁止直接调用上述 22 个函数。铁路几何、曲线采样等确实需要三角函数的地方，改用以下之一：预计算查表（整数索引）；自实现的多项式近似（纯四则运算，代码进版本控制）；或把结果算进静态 Definition 数据而不是每 tick 计算。表现层（渲染、相机、插值）不受此限制。

### SIMD 与 Vector&lt;T&gt; 宽度

**观察**：`System.Numerics.Vector<T>` 的官方文档写明"`Vector<T>` 实例的元素数量是固定的，但其上限取决于 CPU 寄存器"。[Vector&lt;T&gt; 文档源](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System.Numerics/Vector%601.xml)

**观察**：实测。本机默认 `Vector<float>.Count=8`、`Vector<double>.Count=4`（AVX2，256 位）。仅设置环境变量 `DOTNET_EnableAVX2=0` 后变为 4 和 2；`DOTNET_EnableHWIntrinsic=0` 后变为 4 和 2 且 `Vector.IsHardwareAccelerated=False`。二进制与机器都没变。

**观察**：实测宽度依赖的归约。对同一组 64 个 `double`（`1/i`）做分道累加再合并，1 道、4 道、8 道分别得到 `4012F9BE897CD527`、`4012F9BE897CD529`、`4012F9BE897CD528` 三个不同的位模式。

**结论**："同一机器"并不保证同样的 SIMD 宽度——环境变量、运行时配置、以后换 CPU 都会改。而浮点加法不满足结合律，所以任何按向量宽度分道的浮点归约都会随宽度给出不同结果。这是比 FMA 更现实的风险。

**建议**：Simulation 内禁止使用 `Vector<T>`、`Vector128/256/512<T>` 做浮点归约（求和、点积、平均等聚合）。整数 SIMD 的归约是精确的，可以使用。需要向量化的热点先证明是热点，再单独评审。

### Godot 单精度与双精度构建

**观察**：Godot 的 C# 绑定中 `Vector3` 的 `X`/`Y`/`Z` 字段类型是 `real_t`。[Vector3.cs](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/glue/GodotSharp/GodotSharp/Core/Vector3.cs)

**观察**：`real_t` 在 C# 侧是一个全局 using 别名：`#if REAL_T_IS_DOUBLE` 时为 `System.Double`，否则为 `System.Single`。[GlobalUsings.cs](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/glue/GodotSharp/GodotSharp/GlobalUsings.cs)

**观察**：`REAL_T_IS_DOUBLE` 只在 `GodotFloat64=true` 时定义。[GodotSharp.csproj](https://github.com/godotengine/godot/blob/4.7.2-stable/modules/mono/glue/GodotSharp/GodotSharp/GodotSharp.csproj)

**观察**：C++ 侧对应 `#ifdef REAL_T_IS_DOUBLE typedef double real_t; #else typedef float real_t;`。[math_defs.h](https://github.com/godotengine/godot/blob/4.7.2-stable/core/math/math_defs.h)

**观察**：启用大世界坐标（双精度）"需要重新编译编辑器和你打算使用的所有导出模板二进制"，并加 `precision=double` 的 SCons 选项；GDExtension 的 API 在双精度构建下以不兼容的方式改变，扩展必须重新编译。官方还写明单精度下推荐的活动范围：第一人称 3D 约 ±4096 米，第三人称约 ±8192 米，俯视角约 ±32768 米。[Large world coordinates](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/physics/large_world_coordinates.rst)

**观察**：Godot 的导出 feature tag 中，`double` 表示"运行在双精度构建上"，`single` 表示"运行在单精度构建上"，可用于运行时判别。[Feature tags](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/export/feature_tags.rst)

**结论**：`Godot.Vector3`/`Transform3D`/`Mathf` 的**数值类型本身**是引擎构建选项的函数。同一份领域代码在单精度与双精度引擎下算出不同结果，而且这个切换在 C# 源码里完全不可见（别名加条件编译）。这是 §74「Domain Model 使用普通 C# 类型」最硬的技术依据，比"不依赖 SceneTree 生命周期"更严重：它会静默改变数值语义。

**结论**：§25 里"兼容运行环境"必须包含"引擎精度构建"这一维度。官方 Godot 4.7 发布产物是单精度；双精度需要自编译整条工具链，与既有基线（见 [godot-windows-mvp-baseline.md](./godot-windows-mvp-baseline.md)）"不自编译引擎"直接冲突。

**建议**：

- 领域层的位置、长度、质量、价格一律使用自有类型（整数 voxel 坐标、`long` 定点或 `double`），绝不使用 `Godot.Vector3`/`Vector2`/`Transform3D`/`Basis`/`Quaternion`/`Mathf`。
- 仅在 Presentation Adapter 的边界上做一次转换：领域坐标转 `Godot.Vector3`。这与 §75 的 `AssetId` 边界是同一条线。
- 与 §152 的结论一致：列车权威位置用 `RailPosition`（边 ID 加沿路径距离），World Transform 只是派生结果。
- 保持单精度官方构建。如果将来世界尺度真的超出上表范围，优先考虑分块加 Render Origin 平移（§65 已有此设计），而不是切换到双精度构建。

### Godot 时钟与物理

**观察**：Godot 文档写明 Godot 的物理引擎不是确定性的，因此物理 bug 修复不算破坏兼容性，可以进补丁版本。[Release policy](https://github.com/godotengine/godot-docs/blob/4.7/about/release_policy.rst)

**观察**：`_Process` 按帧率调用，`_PhysicsProcess` 按固定频率调用（默认每秒 60 次），C# 签名均为 `double delta`。[Idle and physics processing](https://github.com/godotengine/godot-docs/blob/4.7/tutorials/scripting/idle_and_physics_processing.rst)

**观察**：`physics/common/max_physics_steps_per_frame` 默认为 `8`，用于限制每个渲染帧最多模拟多少物理步，以避免"物理死亡螺旋"；代价是渲染帧率过低时游戏会变慢。[ProjectSettings](https://github.com/godotengine/godot-docs/blob/4.7/classes/class_projectsettings.rst)

**观察**：Godot 的 `RandomNumberGenerator` 底层是 PCG32（`pcg32_random_t`）。[random_pcg.h](https://github.com/godotengine/godot/blob/4.7.2-stable/core/math/random_pcg.h)

**结论**：`_PhysicsProcess` 的调用次数与墙钟时间之间没有固定关系（负载高时被 clamp），所以它可以作为 §24 SimulationClock 的**驱动源**，但 tick 数必须由 SimulationClock 自己累积并作为存档状态，不能从 `delta` 反推。Godot 的 RNG 与物理都不进 Simulation。

**建议**：`SimulationHost` 在 `_PhysicsProcess` 里把 `delta` 折算成整数 tick 预算并调用 `SimulationClock.Advance(n)`；`WorldSimulation` 只接受整数 tick 数，不接受 `delta`。Pause 与倍速只改变 `n`，不改变每个 tick 的语义。

### Dictionary / HashSet 枚举顺序

**观察**：`Dictionary<TKey,TValue>` 的官方文档写明"返回项的顺序是未定义的（undefined）"；`Keys` 与 `Values` 集合的顺序"未指定，但保证彼此对应"。[Dictionary 文档源](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System.Collections.Generic/Dictionary%602.xml)

**观察**：`HashSet<T>` 的官方文档写明它是"元素没有特定顺序"的集合，且"未排序"。[HashSet 文档源](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System.Collections.Generic/HashSet%601.xml)

**观察**：实测。`Dictionary<int,int>` 依次插入 0 到 5 后，枚举得到 `0 1 2 3 4 5`；执行 `Remove(2)`、`Remove(4)` 再插入 100、200 后，枚举得到 `0 1 200 3 100 5`——空闲槽被复用，顺序不再是插入顺序。

**观察**：更危险的一条：当 `Dictionary` 的键是引用类型且使用默认比较器时，一旦某次查找的碰撞次数超过 `HashHelpers.HashCollisionThreshold`（常量 `100`），字典会把 `NonRandomizedStringEqualityComparer` 换成随机化比较器并 `Resize(forceNewHashCodes: true)` 重新哈希全部键。[Dictionary.cs](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs) [HashHelpers.cs](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/Collections/HashHelpers.cs)

**结论**：`Dictionary<string, T>` 的枚举顺序在最坏情况下会直接依赖那个每进程随机的字符串哈希种子。这不是"通常是插入顺序、偶尔会变"，而是"可以在同一台机器上两次运行给出不同顺序"。§25 把 Dictionary/HashSet 枚举顺序列为禁止依赖项是正确的，且理由比"顺序不保证"更强。

**建议**：

- Simulation 的遍历一律走显式有序结构：`List<T>`、数组、按 `ulong` EntityId 排序的索引，或 `SortedDictionary` 加显式 `IComparer`。
- `Dictionary`/`HashSet` 只用于 O(1) 查找，不用于枚举。如果必须枚举，先取出再按显式键排序。
- 可以考虑在 Simulation 层封一个 `DeterministicSet`/`DeterministicMap`（内部 `Dictionary` 加有序 `List`），让"枚举顺序稳定"成为类型保证而不是纪律要求。

### 排序稳定性与字符串比较

**观察**：`List<T>.Sort` 与 `Array.Sort` 的官方文档写明"本实现执行的是不稳定排序：两个相等元素的相对顺序可能不被保留"。LINQ 的 `Enumerable.OrderBy` 文档写明"本方法执行稳定排序"。[List 文档源](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System.Collections.Generic/List%601.xml) [Array 文档源](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System/Array.xml) [Enumerable 文档源](https://github.com/dotnet/dotnet-api-docs/blob/main/xml/System.Linq/Enumerable.xml)

**观察**：从 .NET 5 起，Windows 上的全球化 API 默认走 ICU（`icu.dll`），ICU 不可用时回落到 NLS；受影响的行为包括字符串排序与搜索、排序键、大小写转换。[Globalization and ICU](https://learn.microsoft.com/en-us/dotnet/core/extensions/globalization-icu)

**观察**：官方最佳实践文档写明"语言学和文化感知的比较器会不时发生行为调整。ICU 和较老的 Windows NLS 都会随世界语言的变化而更新"，而"Ordinal 比较器的行为永不改变，因为它执行的是精确的按位搜索和比较"。[Best practices for comparing strings](https://learn.microsoft.com/en-us/dotnet/standard/base-types/string-comparison-net-5-plus)

**观察**：实测。对 `rail.straight`、`rail-straight`、`railStraight`、`Rail.Straight`、`rail_straight`、`rail straight` 六个字符串，默认 `List<string>.Sort()` 得到 `rail straight | rail_straight | rail-straight | rail.straight | Rail.Straight | railStraight`，而 `StringComparer.Ordinal` 得到 `Rail.Straight | rail straight | rail-straight | rail.straight | railStraight | rail_straight`。两者顺序不同。

**结论**：`string` 的默认比较是 culture-sensitive 的。§11 的 Namespaced ID（形如 `mod:category/name`）如果参与任何影响 Gameplay 的排序或 tie-break，默认比较会把 Gameplay 绑到操作系统的 ICU 版本上。这是一个比 Dictionary 顺序更隐蔽的漏洞，因为它在本机测试中永远不会暴露。

**建议**：

- Simulation 内所有字符串比较、排序、字典键比较显式指定 `StringComparer.Ordinal`。
- 需要稳定顺序时使用 `OrderBy`（稳定）或给 `Sort` 提供全序比较器（保证没有相等元素）；不要依赖 `List<T>.Sort` 在相等元素上的顺序。
- 更强的做法：Definition 加载时把 Namespaced ID 一次性映射成稠密的整数 `DefinitionId`，Simulation 内部只比较整数，字符串只在加载、存档和日志里出现。这同时解决排序、哈希和性能三个问题。

### 稳定哈希：keyed random 与语义内容哈希

**观察**：`System.IO.Hashing` 命名空间提供 `Crc32`、`Crc64`、`XxHash32`、`XxHash64`、`XxHash3`、`XxHash128`、`Adler32`。[System.IO.Hashing 命名空间](https://learn.microsoft.com/en-us/dotnet/api/system.io.hashing)

**观察**：它是 NuGet 包（不在共享框架内）。`System.IO.Hashing` 9.0.0 的 nuspec 中包含 `<group targetFramework="net8.0" />` 且该组无任何依赖项，包许可为 MIT。本机在 `net8.0` 工程上加 `PackageReference` 后直接编译运行成功。[nuspec](https://api.nuget.org/v3-flatcontainer/system.io.hashing/9.0.0/system.io.hashing.nuspec)

**观察**：xxHash 规范写明"给定变体应当产生完全相同的输出，与所用 CPU/OS 无关；特别地，结果与 CPU 的字节序和位宽无关"。规范版本 0.2.0（2023-06-29），涵盖 XXH32、XXH64、XXH3-64、XXH3-128。[xxhash_spec.md](https://github.com/Cyan4973/xxHash/blob/dev/doc/xxhash_spec.md)

**观察**：实测一致性。`System.IO.Hashing` 9.0.0 在 net8.0 上对空输入给出 `XXH32=0x02CC5D05`、`XXH64=0xEF46DB3751D8E999`、`XXH3-64=0x2D06800538D394C2`，与 xxHash 官方 sanity 向量中的对应值逐位相同。[xsum_sanity_check.c](https://github.com/Cyan4973/xxHash/blob/dev/cli/xsum_sanity_check.c)

**观察**：SplitMix64 的参考实现（Sebastiano Vigna，公有领域）为：`z = (s += 0x9E3779B97F4A7C15)`；`z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9`；`z = (z ^ (z >> 27)) * 0x94D049BB133111EB`；`return z ^ (z >> 31)`。全部是 64 位整数运算，可在 C# 中用 `unchecked` 逐行复刻。[splitmix64.c](https://prng.di.unimi.it/splitmix64.c)

**观察**：FNV-1a 的定义是 `hash = offset_basis`，然后对每个字节 `hash = (hash xor octet) * FNV_prime`，同样是纯整数运算、规范公开。[FNV Hash](http://www.isthe.com/chongo/tech/comp/fnv/)

**观察**：实测成本（2000 万次，见"本次实测环境"）：`XxHash3.HashToUInt64` 对 24 字节 key 用时 307 ms（约 15 ns 每次）；手写 SplitMix64 用时 131 ms（约 6.5 ns 每次）。

**结论**：两类需求应该用两个不同的函数，不要合并：

| 用途 | 需求 | 合适的函数 |
|---|---|---|
| §32.1 Stateless Keyed Random | 若干个 `ulong`/`int` key 混合成一个均匀 `ulong`，每 tick 调用量大 | SplitMix64 finalizer 逐字段混合，或 xxHash3 over 固定布局的栈上 span |
| §32.2 Stateful RNG Stream | 可推进、状态可进存档 | SplitMix64 或 xoshiro256\*\*，状态 1 到 4 个 `ulong` |
| 语义内容哈希（Definition/Revision 指纹、存档校验、热加载变更检测） | 变长字节流、雪崩性好、跨平台稳定、有官方测试向量 | `XxHash3`（64 位）或 `XxHash128`（128 位） |

**结论**：FNV-1a 可以自己实现且无依赖，但雪崩性弱于 xxHash，且没有 .NET 内建实现；在已经可以用 `System.IO.Hashing` 的前提下没有必要。CRC32/CRC64 只适合检错，不适合做随机源或内容指纹。

**建议**：

- 语义内容哈希用 `System.IO.Hashing` 的 `XxHash3`/`XxHash128`，并在写入存档时同时记录算法名与版本，便于将来换算法。
- keyed random 用项目自有的一小段代码（SplitMix64），不引入依赖，因为它要在 Simulation 热路径上被调用，且实现只有四行、可以进单元测试锁死向量。
- 无论选哪个，都为它写一组固定的黄金向量测试（输入到期望 `ulong`），使得任何实现改动都会被测试挡住。这是把"实现事实"升级成"项目契约"的唯一办法。

### 定点数在 C# 中的做法与代价

**观察**：C# 没有内建定点类型。可用的三条路径都基于已有类型：`long` 上的 Q 格式（需要 128 位中间积）、`Int128`（.NET 7 起）、`decimal`。

**观察**：`Math.BigMul(ulong, ulong, out ulong)` 与 `Int128` 在 `net8.0` 上均可用（本机编译通过）。

**观察**：C# 标准写明 `decimal` 的运算"先计算精确结果（按每个运算符定义保留 scale），再舍入到可表示的值；舍入到最近的可表示值，等距时舍入到最低有效位为偶数的值（banker's rounding）"，溢出抛 `System.OverflowException`。[C# standard, types.md](https://github.com/dotnet/csharpstandard/blob/draft-v8/standard/types.md)

**观察**：实测成本。2000 万次循环，每次一乘一加，累加形成依赖链：

| 方案 | 用时 | 相对 double |
|---|---:|---:|
| `double` 乘加 | 79 ms | 1.0 倍 |
| Q32.32 定点，`Math.BigMul` 取高低 64 位拼接 | 43 ms | 0.5 倍 |
| Q32.32 定点，`(Int128)a * b >> 32` | 951 ms | 12 倍 |
| `decimal` 乘加（实测 200 万次后乘 10） | 约 585 ms | 7.4 倍 |

这些是**延迟受限**的微基准（累加链串行），只能说明数量级，不能当作吞吐量比例。

**结论**：

- `long` 加 `Math.BigMul` 的 Q32.32 与 `double` 属于同一数量级，定点的成本主要不在指令，而在工程成本：需要自己实现除法、开方、三角函数、溢出检查与格式化，且每处 Q 格式（小数位数）都要人工把关。
- `Int128` 写法可读但慢一个数量级，因为 128 位乘法在 x64 上是软件例程。若走定点路线，必须用 `Math.BigMul`。
- `decimal` 语义完全确定且跨平台一致，但它是十进制、有 `OverflowException`、慢约 7 倍，适合价格与金额这类需要精确十进制语义的低频量，不适合每 tick 的几何计算。
- 从本文其余部分看，x64 上标量 `double` 四则运算加 `Math.Sqrt` 已经是确定的。定点数的真正价值是**跨平台逐位一致**（§25 明确不要求），以及**避免误差累积**（这是数值问题，不是确定性问题）。

**建议**：把"是否引入定点"与"是否要跨平台逐位一致"绑定成同一个决定，交给 #34。若维持 §25 的 A 级承诺（同一兼容环境内可复现），本文没有发现必须引入定点的技术理由。若确实要引入，只在明确的量上引入（例如 §152 的 `RailPosition.DistanceAlongPath` 用 `long` 定点毫米或微米），而不是全局替换 `double`。

## 给「定义确定性与数值策略」票的建议

以下是建议，不是决定。

1. **把 §25 的"兼容运行环境"写成一个具体清单**，至少包含：CPU 架构（x64）、操作系统（Windows）、.NET 主版本、Godot 版本与**精度构建**（single）。这四项任一变化都不承诺逐位一致。
2. **禁止清单（应能被代码评审或分析器检查）**：`GetHashCode()`/`HashCode.Combine` 参与 Gameplay；`Dictionary`/`HashSet` 枚举驱动 Gameplay；默认 `string` 比较与排序；`Math`/`MathF` 的 22 个超越函数；`Vector<T>` 浮点归约；`*Estimate` 系列 API；`Godot.*` 数学类型进入 `Domain/`；`System.Random` 进入 Simulation；Godot 物理与 `RandomNumberGenerator` 影响 Simulation。
3. **允许清单**：x64 上 `double`/`float` 的 `+ - * /`、比较、`Math.Sqrt`、`Math.Abs`、`Math.Min`/`Math.Max`、显式取整；整数与 `long` 运算；`decimal`；自实现的 SplitMix64 与 xxHash。
4. **§32 的 `HashRandom` 落地**：`HashRandom(WorldSeed, SystemKey, EventId, EntityId)` 用 SplitMix64 逐字段混合实现，`SystemKey` 用编译期常量整数而不是字符串，`EventId` 复用 §28 的 `TransactionId`。
5. **§33 的 `NextEntityId : ulong`** 与本文无冲突：整数自增天然确定，只要分配点在 Commit 顺序内。
6. **加一个确定性回归测试**：固定 seed、固定输入命令序列、跑 N 个 tick，对 World State 做 `XxHash128` 指纹并与黄金值比对。这个测试能同时抓住上面所有类别的回归，成本远低于逐条纪律检查。
7. **不建议现在引入定点数**，除非 #34 决定把承诺提升到跨平台逐位一致。

## 不采用

- 跨平台逐 bit 一致作为首版目标（§25 与 Q151 已选 A 级承诺，成本远高于收益）。
- Godot 双精度（`precision=double`）构建：需要自编译编辑器与全部导出模板，与既有"不自编译引擎"基线冲突，且当前世界尺度未超出单精度推荐范围。
- 用 `System.Random`（含带种子）作为 Simulation 的 RNG。
- 用 `HashCode`/`GetHashCode` 作为 keyed random 或内容指纹。
- 全局把 `double` 替换成定点数。
- 用 CRC32/CRC64 作为随机源或内容指纹。

## 剩余不确定性

- **未验证**：ARM64、Linux、macOS 上的对应行为。本文所有实测均在 Windows x64 单机完成，`Math.Sqrt` 与四则运算的确定性推断在 ARM64 上应当同样成立（NEON 标量指令同样精确舍入），但未实测。
- **未验证**：Godot 双精度构建的实际行为。所有关于 `real_t` 的结论来自源码与文档，未编译过 `precision=double` 的引擎。
- **未验证**：Godot 导出产物内嵌的 .NET 运行时补丁版本是否会随导出模板变化。若会，"同一游戏版本"的定义需要把运行时补丁号也算进去。
- **无官方契约**：带种子 `System.Random` 的算法稳定性、RyuJIT 不做 FMA 收缩，这两条都是**当前实现事实**（源码加实测），官方均未承诺。前者有明确的反向文档声明，后者没有任何书面承诺。若项目要依赖它们，必须用黄金向量测试自行锁死。
- **未量化**：查表或多项式近似替代三角函数的精度与性能代价，取决于铁路曲线的具体需求，应在 Railway 相关票中评估。
- **微基准局限**：本文的成本表是延迟受限的依赖链循环，`Math.BigMul` 定点快于 `double` 是整数加法链延迟低于浮点加法链所致，不代表真实工作负载下定点更快。
