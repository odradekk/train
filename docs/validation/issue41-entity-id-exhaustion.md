# #41 实体 ID 耗尽修复验证

日期：2026-09-13。基线：`d1cf36b0baec05118d2a65dd34f5fe50724db956`；验证对象为该基线上叠加 .NET 10 / Fedora 迁移及本次修复的本地工作区。本文不代表修复已发布或 #41 已关闭。

## 问题与修复

验收发现的[阻断问题](https://github.com/odradekk/train/issues/41#issuecomment-5652186994)：正式存档的 `next_entity_id=ulong.MaxValue` 可以加载，但随后制造会抛出异常，添加零件会把计数绕回 0；添加后保存报告 `Saved`，再加载却返回 `InvalidDelta`。

制造、改装和车辆伤害现在都在写入权威状态前预检整次操作实际需要的 ID。伤害先规划所有受影响车辆，再检查所有次继承者需要的 ID 总数；不足时整次返回 `EntityIdExhausted`，不修改布局、耐久、身份、所有权或两项计数器，也不发布 `Changed`。

`ulong.MaxValue` 保留为合法耗尽游标，本身不分配。无需新 ID 的操作仍可执行，耗尽状态仍可保存、恢复。保存和加载复用同一身份校验，拒绝为 0 或不大于已用 ID 的游标，避免覆盖上一份可读存档。

## 验证

- 原始制造与单项添加复现：均返回 `EntityIdExhausted`，权威快照不变，随后 `Saved` → `Loaded`。
- `CompositeVehiclePersistenceTests` 增加 5 项回归测试：从真实文件恢复最大值与临界游标，覆盖多 ID 制造、添加/替换/批量改装、多车辆断裂整体预留、无需新 ID 的操作，以及耗尽状态保存恢复和非法游标保存拒绝。
- `dotnet test Train.Tests/Train.Tests.csproj --no-restore --nologo --logger 'trx;LogFileName=tests.trx' --results-directory .godot/id-exhaustion-validation`：312/312 通过，0 失败、0 跳过；其中车辆存档测试 27/27 通过。结果：`.godot/id-exhaustion-validation/tests.trx`。
- `dotnet build Train.csproj --no-restore --nologo`：通过，0 警告、0 错误。
- `pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 3`：通过。使用 PowerShell 7 和 `godot-dotnet`；制造、改装、断裂、热重载及跨进程存档恢复标记均通过。日志：`.godot/id-exhaustion-validation/slice3.log`。缺少必要定义的热重载拒绝是脚本预期负例。
- 独立 Standards 与 Spec 审查：均无发现。

本次未调整存档格式或项目版本。
