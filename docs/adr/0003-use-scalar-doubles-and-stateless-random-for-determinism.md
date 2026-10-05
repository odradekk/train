# Use scalar doubles and stateless random within a fixed runtime boundary

Accepted through [#34](https://github.com/odradekk/train/issues/34). Simulation uses scalar `double` for continuous quantities and integers for voxel coordinates, counts and runtime IDs. Determinism is required for the same game build, effective semantic content, initial state and tick-assigned inputs on Windows x64 with the same .NET runtime version; a CPU, runtime or OS change requires revalidation rather than an assumed bitwise guarantee. This meets the current reproducibility goal without introducing a fixed-point framework. #35 clarifies that installing changed mod values intentionally changes semantic inputs.

The domain uses ordinary C# types, including three scalar doubles for continuous 3D coordinates. Godot math types belong to adapters and presentation, and hardware-width-dependent `System.Numerics.Vector<T>` is excluded from the initial domain implementation. Container iteration, registration and thread completion order cannot determine simulation results; runtime hash codes cannot define random keys or persistent identities.

The first version uses stateless keyed random for world generation, decoration and fracture tie-breaking, based on the SplitMix64 integer mixing function. It does not keep or advance generator state. Purpose-specific key layouts, encoding, field order and algorithm version must be fixed and covered by golden vectors. Stateful streams are deferred until a concrete feature needs sequence advancement, at which point their complete state must be persisted.

Semantic revision hashes use SHA-256, retaining the algorithm already used by the catalog while replacing its combined definition/image fingerprint contract. Semantic fields and canonical content encoding are defined in [the content contract](../design/02-definitions-and-content.md). See [the simulation contract](../design/04-deterministic-simulation.md) for floating-point round trips and the required repeat-run, save/resume and rendering-independent validation.

## 2026-10-05 修订

经[核心模拟层的时间、命令与复现](https://github.com/odradekk/train/issues/202)确认：确定性契约只覆盖核心模拟层，场景不在其中（[ADR-0008](0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md)）。核心模拟层没有体素坐标，体素坐标只在体素引擎层使用整数。断裂平局这个随机用途随断裂规则一起删除；以后的用途以节点、线路、车辆、战斗、事务等稳定身份为键，各自提供固定向量。双精度标量、兼容环境边界、无状态带键随机和 SHA-256 Revision 的结论不变。
