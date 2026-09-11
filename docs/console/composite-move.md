# `composite move`：移动已有零件并保留身份

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

```text
composite move <id> <partId> <x> <y> <z>
composite move 1 170 3 1 0
```

参数：车辆运行时 ID、被移动零件的真实 `VoxelRuntimeId`、目标 Local Cell。移动按实际身份跟踪同一零件，保留耐久、朝向和已有状态。单项移到已占格返回 `DuplicateOccupancy`；要把两件互换，必须走 [`composite batch`](composite-batch.md) 的同批两个 `move`，不能先生效再回滚。

成功示例：

```text
Composite Valid.
vehicle 1
allocated none
parts 169
construction Valid
Voxel count: 0.
```

失败（占用、不连通、越包络、未知零件、事务计数耗尽）整笔拒绝，布局、RuntimeId、耐久、引用、`NextEntityId` 和事务保持。非法移动不会变成脱落。用法不合法时只给出 Usage。
