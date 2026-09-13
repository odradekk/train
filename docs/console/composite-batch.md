# `composite batch`：原子批量改装

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码)

```text
composite batch <id> <op> ...
composite batch 1 move 170 4 1 0 move 171 3 1 0
composite batch 1 rotate 170 px_py add 8 1 0 core:refit-marker px_py
```

参数：车辆运行时 ID，后接一组操作。整批先规划最终占用，再对最终布局做一次 Construction Validity，并按最终保留的新件数量预检 ID；通过才提交。占格交换合法。不能逐条生效再尝试回滚。模拟、查询、`Changed`、渲染和已支持碰撞只看到完整提交结果。

每个操作的词法与单项命令相同，但不重复车辆 ID：

| 操作 | 后续参数 |
| --- | --- |
| `add` | `<x> <y> <z> <namespace:id> <orientation>` |
| `remove` | `<partId>` |
| `move` | `<partId> <x> <y> <z>` |
| `rotate` | `<partId> <orientation>` |
| `replace` | `<x> <y> <z> <namespace:id> <orientation>` |

成功时与单项改装同一套字段行。占格交换不分配新身份：

```text
Composite Valid.
vehicle 1
allocated none
parts 170
construction Valid
Voxel count: 0.
```

同批含添加／替换时，`allocated` 按提交顺序列出新 ID。

票定失败例：同批把 P 旋回默认朝向再越界添加 `(8,1,0)`，整批 `EnvelopeExceeded`，P 仍为 `nz_py`，`NextEntityId` 与事务不变。随后仍可继续合法移动。其它常见整批拒绝：`UnknownVehicle`、`UnknownPart`、`UnknownVoxel`、`DuplicateOccupancy`、`Disconnected`、`DisallowedOrientation`、`Empty`、`TransactionExhausted`、`EntityIdExhausted`。失败行是 `Composite <Status> <id>.`，权威不变，也不会脱落到世界网格。

操作词无法解析或批次为空时只给出 Usage，不执行改装。单项入口见 [add](composite-add.md)、[remove](composite-remove.md)、[move](composite-move.md)、[rotate](composite-rotate.md)、[replace](composite-replace.md)。
