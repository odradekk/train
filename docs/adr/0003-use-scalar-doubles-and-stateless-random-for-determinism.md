# Use scalar doubles and stateless random within a fixed runtime boundary

Accepted through [#34](https://github.com/odradekk/train/issues/34). Simulation uses scalar `double` for continuous quantities and integers for voxel coordinates, counts and runtime IDs. Determinism is required for the same game build, effective semantic content, initial state and tick-assigned inputs on Windows x64 with the same .NET runtime version; a CPU, runtime or OS change requires revalidation rather than an assumed bitwise guarantee. This meets the current reproducibility goal without introducing a fixed-point framework. #35 clarifies that installing changed mod values intentionally changes semantic inputs.

The domain uses ordinary C# types, including three scalar doubles for continuous 3D coordinates. Godot math types belong to adapters and presentation, and hardware-width-dependent `System.Numerics.Vector<T>` is excluded from the initial domain implementation. Container iteration, registration and thread completion order cannot determine simulation results; runtime hash codes cannot define random keys or persistent identities.

The first version uses stateless keyed random for world generation, decoration and fracture tie-breaking, based on the SplitMix64 integer mixing function. It does not keep or advance generator state. Purpose-specific key layouts, encoding, field order and algorithm version must be fixed and covered by golden vectors. Stateful streams are deferred until a concrete feature needs sequence advancement, at which point their complete state must be persisted.

Semantic revision hashes use SHA-256, retaining the algorithm already used by the catalog while replacing its combined definition/image fingerprint contract. Semantic fields and canonical content encoding are defined in [the content contract](../design/02-definitions-and-content.md). See [the simulation contract](../design/04-deterministic-simulation.md) for floating-point round trips and the required repeat-run, save/resume and rendering-independent validation.

## 2026-10-05 修订

经[核心模拟层的时间、命令与复现](https://github.com/odradekk/train/issues/202)确认：确定性契约只覆盖核心模拟层，场景不在其中（[ADR-0008](0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md)）。核心模拟层没有体素坐标，体素坐标只在体素引擎层使用整数。断裂平局这个随机用途随断裂规则一起删除；以后的用途以节点、线路、车辆、战斗、事务等稳定身份为键，各自提供固定向量。双精度标量、兼容环境边界、无状态带键随机和 SHA-256 Revision 的结论不变。

## 货币定价的局部精确计算例外

经[市场与交易：库存、定价与复合体买卖](https://github.com/odradekk/train/issues/281) Q27 确认：货币定价用整数分子、分母保留中间结果，到结算单位才按规则取整；余额和实际金额仍用最小货币单位的有符号 64 位整数。其他连续模拟量保持标量 `double`，不引入全局定点框架，也不改变确定性运行环境边界。

取舍依据是整数边界的实际差异：100 × 0.5 × (1 + 0.1) 的精确结果为 55，而本机 `double` 得到 55.000000000000007，向上取整成为 56。决定不接受这种由价格参数近似造成的额外收款，也不以任意 epsilon 或换成仍需中间舍入的有限精度小数掩盖问题；代价是定价参数及核心运算须保存精确值。

业务公式见 [19](../design/19-markets-and-trading.md#货物的边际价格)。经[核心模拟层公开契约](https://github.com/odradekk/train/issues/287)确认，基本价值和实际金额用 `long`；倍率、敏感度、价差公开为万分比整数（10000 表示 1），不公开 `Fraction`。内部用标准整数类型保留精确中间结果并作必要溢出检查；意外溢出沿用核心内部故障，不增金额越界业务错误或自动跳过分支。耐久和伤害用整数（内容上限与伤害原本为整数），结构累计用 `long`；流程、施工总工作量和工作量系数用整数，进度、速率、电量与劳动力仍用 `double`。这些是规划边界，尚非实现迁移证据；其他模块读取报价，不复制运算。该决定不是数值实现、契约原型或性能已经通过的证据。
