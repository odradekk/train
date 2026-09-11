# `composite remove`：从已有车辆移除零件

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

```text
composite remove <id> <partId>
composite remove 1 171
```

参数：车辆运行时 ID 与要拆除的零件 `VoxelRuntimeId`。不能用 `voxel remove` 或世界坐标代替本命令。移除按最终 Construction Validity 整笔校验：拆掉接触绑定所需零件、拆连通、越包络都会整批拒绝并保留原布局。

成功时 `allocated none`，零件数减少，车辆 Composite ID 保持：

```text
Composite Valid.
vehicle 1
allocated none
parts 168
construction Valid
Voxel count: 0.
```

未知车辆或未知零件返回 `UnknownVehicle` / `UnknownPart`；事务计数无法再分配返回 `TransactionExhausted`。权威不变。用法不合法时只给出 Usage。批量拆除见 [`composite batch`](composite-batch.md)。
