# `voxel cell`：正式单格体素编辑

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

即时提交可撤销；排期会结束撤销历史，确认词规则见 [`confirm-end-history`](README.md#confirm-end-history)。

```text
voxel cell add <x> <y> <z> <namespace:id> <orientation> [at <step>]
voxel cell remove <x> <y> <z> [at <step>]
voxel cell replace <x> <y> <z> <namespace:id> <orientation> [at <step>]

voxel cell add 60 3 20 core:ferrous-ore px_py
voxel cell replace 60 3 20 core:ledge pz_py
voxel cell remove 60 3 20 at 303
```

`add` 要求目标格为空，`remove` 和 `replace` 要求目标格已有普通世界体素。`add` 与 `replace` 还要求定义存在且朝向属于该定义允许的正交朝向。三种操作都提交为 `voxel.cell-set` 正式编辑；这里虽然只有一个目标格，仍使用与后续多格工具相同的整组校验和规范编码。旧的 [`voxel add`](voxel-add.md) 与 [`voxel remove`](voxel-remove.md) 保持原有入口和编码。

`replace` 的定义和朝向都与原体素相同时，以 `NoChange` 拒绝，不会恢复耐久。定义或朝向改变时按新体素处理：耐久恢复到新定义的最大值，原有脱落零件的运行时身份不保留。

不带 `at` 时在当前边界立即执行；带 `at <step>` 时按正式输入顺序排队，并在目标步重新校验。成功返回 `Edit <id> voxel.cell-set executed Applied`，排队返回 `Edit <id> voxel.cell-set pending`。越界、空格、占用、未知定义或不允许的朝向会拒绝整个编辑，世界不变。

普通体素工具不能修改铁路或车辆占用格。有轨段登记的铁路冲突会报告占用该格的轨段；未登记但仍存活的铁路定义体素同样拒绝，不伪造轨段身份。车辆冲突会报告车辆及零件身份，便于选择正确对象工具处理，不会隐式拆轨、拆车或把车辆零件写成世界体素。具体队列、日志、保存恢复和拒绝规则见 [`voxel edits`](voxel-edits.md)。

有登记对象的拒绝信息在正式编辑结果后追加相应的归属行：

```text
conflict rail-segment <segment_id> at (<x>, <y>, <z>)
conflict vehicle <vehicle_id> part <part_id> at (<x>, <y>, <z>)
```

未登记铁路体素返回 `RailOccupied`，不输出伪造的轨段 ID。
