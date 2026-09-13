# `composite add`：向已有车辆添加零件

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码)

```text
composite add <id> <x> <y> <z> <namespace:id> <orientation>
composite add 1 2 1 0 core:refit-marker px_py
```

参数：车辆运行时 ID、目标 Local Cell、已加载体素定义、正交朝向。坐标是车辆局部格子，不是世界格子。添加只在整笔改装通过最终 Construction Validity 后才分配新的 `VoxelRuntimeId`，新件按当前定义上限初始化耐久，不沿用任何旧件状态。

成功按字段分行，分配的身份以返回值为准，不要硬编码恰好分到的整数：

```text
Composite Valid.
vehicle 1
allocated 170
parts 169
construction Valid
Voxel count: 0.
```

提交前按最终保留的新件数量预检 ID；单项添加需要一个。失败只返回 `Composite <Status> <id>.`，不分配身份、不推进 `NextEntityId` 或事务，也不改布局。常见状态：`UnknownVehicle`、`UnknownVoxel`、`DuplicateOccupancy`、`Disconnected`、`EnvelopeExceeded`、`DisallowedOrientation`、`TransactionExhausted`、`EntityIdExhausted`。非法施工不会转成脱落或写入世界网格。

用法不合法时只给出 Usage，不执行改装。批量添加见 [`composite batch`](composite-batch.md)。
