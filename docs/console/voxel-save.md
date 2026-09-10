# `voxel save`：保存当前世界

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel save
voxel save Slice 2 Resume
```

参数：可选显示名。省略时沿用会话当前显示名；尚未命名时默认为 `Development World`。多个 token 用空格拼接成一个名称。名称经去首尾空白后须为 1–64 字符，不能是 `.` 或 `..`，也不能含 `/ \ : * ? " < > |`。显示名只写在存档文件内，不参与路径。

成功时把当前切片的固定生成基底与 Runtime Delta 原子写入 `user://saves/<UUID>/save.json`（物理路径为用户数据根下的 `saves/<UUID>/save.json`）。首次保存分配稳定 UUID；之后重复保存复用同一 UUID。改名不改身份。`voxel reset` 只重建世界，不清空该 UUID。存档记录外部 `DefinitionId`、上次 Semantic Revision，以及保存时各定义的耐久上限（作为未写入 Delta 的实例绝对值基线）；实例状态用绝对值。不保存场景树、网格或 Collider。

写入先完成候选文件 `save.json.candidate` 并 flush，再整体替换目标文件。失败返回 `WriteFailed` 或 `InvalidName` 等状态，当前可编辑世界和上一份有效存档都保持不变。

成功按字段分行输出：

```text
Voxel Saved.
id 11111111-2222-4333-8444-555555555555
name Slice 2 Resume
Voxel count: 1089.
```

失败同样以 `info` 写出 `Voxel <Status>.` 和当前总数，例如非法名称得到 `InvalidName`。没有独立的存档列表或选档 UI；记下输出中的 UUID，之后用 [`voxel load`](voxel-load.md) 恢复。

当前范围只保存体素世界。不保存车辆、Blueprint、铁路、贸易或未出现的运行时 ID。当前会话已有车辆时返回 `UnsupportedVehicles`，不写文件，也不把丢车的世界存档报告为成功。
