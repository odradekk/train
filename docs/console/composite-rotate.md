# `composite rotate`：旋转已有零件并保留身份

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码)

```text
composite rotate <id> <partId> <orientation>
composite rotate 1 170 nz_py
```

参数：车辆运行时 ID、零件真实 `VoxelRuntimeId`、目标正交朝向。旋转按身份跟踪同一零件，保留格子、耐久和已有状态。目标朝向必须属于该零件定义的 `allowed_orientations`；`core:fixed` 这类仅默认朝向的定义对 `nz_py` 返回 `DisallowedOrientation`。

票定成功序列把标记件从默认 `px_py` 转到局部 +X→−Z、+Y→+Y、+Z→+X，编码为 `nz_py`。标记件顶面箭头指向局部 +X，因此 `nz_py` 后面向世界 −Z，不能只靠查询文本代替看图。可用 [`composite query`](composite-query.md) 核对该件 ID、耐久和朝向，不要硬编码分配编号。

成功示例：

```text
Composite Valid.
vehicle 1
allocated none
parts 169
construction Valid
Voxel count: 0.
```

失败只返回 `Composite <Status> <id>.`，不改朝向、耐久或事务。常见状态：`UnknownVehicle`、`UnknownPart`、`DisallowedOrientation`、`TransactionExhausted`。`core:fixed` 对 `nz_py` 为 `DisallowedOrientation`。用法不合法时只给出 Usage。同批旋转再添加见 [`composite batch`](composite-batch.md)。

定时执行：末尾追加 `at <step>`，在该步边界执行。输出与规则见 [`voxel edits`](voxel-edits.md)。
