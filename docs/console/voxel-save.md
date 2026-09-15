# `voxel save`：保存当前世界、车辆与正式铁路

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel save
voxel save Slice 3 Persist
```

参数：可选显示名。省略时沿用会话当前显示名；尚未命名时默认为 `Development World`。多个 token 用空格拼接成一个名称。名称经去首尾空白后须为 1–64 字符，不能是 `.` 或 `..`，也不能含 `/ \ : * ? " < > |`。显示名只写在存档文件内，不参与路径。

成功时把当前切片的固定生成基底、Runtime Delta、实际车辆，以及正式铁路 / 编组 / 运动 / 待执行命令写入 `user://saves/<UUID>/save.json`（物理路径为用户数据根下的 `saves/<UUID>/save.json`）。写出格式号为 `3`。首次保存分配稳定 UUID；之后重复保存复用同一 UUID。改名不改身份。`voxel reset` 只重建世界，不清空该 UUID。开发期不迁移更旧格式；本构建仍可读前三切片留下的格式 `2` 文件。新保存一律写成格式 `3`（无铁路的切片也写出空的 `railway` / `assemblies` / 命令队列）。

存档记录：

- 外部体素 `DefinitionId`、上次 Semantic Revision，以及保存时各定义的耐久上限（作为未写入 Delta 的实例绝对值基线）
- 实际车辆与零件 Runtime ID、定义与 Archetype 引用、Local Grid、耐久、朝向、接触绑定、连续 Transform
- 必要的存档内（save-local）定义及其依赖；外部只存 ID 与 LastUsedRevision，不嵌入旧 Mod
- 带 RuntimeId 的脱落世界体素
- 正式铁路：轨段身份、几何、明确连接、占格与依赖映射；损伤由世界占用与占格对照恢复，不从 Mesh 或派生图反推
- 实际编组：一个或多个独立编组的车辆引用、存活车钩、各自接触姿态与参考游标、活动动力或无动力滑行、速度与运行/停车开关。无活动车头写 `active_locomotive_id=0`
- 模拟步、同一步 FIFO 待执行命令及其目标编组与顺序计数。目标必须是当前仍存在的编组；结构事件已取消消失目标，保存与随后加载认可同一合法队列
- `WorldSeed`、`NextEntityId`、下一事务计数等影响后续提交 / 断裂 / 命令入队的权威状态；数组按稳定顺序写出
- 文化无关的 JSON 数值，double 按 `G17` 往返

不保存网格、Collider、场景树、高亮或 Host 显示累积余量，也不新增有状态 RNG。不从渲染 float、Mesh 或派生图反推 Transform / 轨段。目标速度来自当前动力定义，不写入存档。

写入先完成候选文件 `save.json.candidate` 并 flush，再整体替换目标文件。保存复用加载的身份校验：`next_entity_id` 为 `0` 或不大于任何已用 Runtime ID 时返回 `InvalidDelta`，不覆盖上一份可读存档。`ulong.MaxValue` 是合法的耗尽游标，仍可保存；它本身不表示可分配的新 ID。失败返回 `WriteFailed` 或 `InvalidName` 等状态，当前可编辑会话和上一份有效存档都保持不变。`transaction_count` 已为 `ulong.MaxValue` 时捕获失败并返回 `InvalidDelta`，不覆盖上一份可读存档。提交入口在计数到达 `MaxValue - 1` 时即拒绝再分配，因此正常提交不会写出随后无法加载的计数。

成功按字段分行输出：

```text
Voxel Saved.
id 11111111-2222-4333-8444-555555555555
name Slice 3 Persist
Voxel count: 26.
```

失败同样以 `info` 写出 `Voxel <Status>.` 和当前总数，例如非法名称得到 `InvalidName`。没有独立的存档列表或选档 UI；记下输出中的 UUID，之后用 [`voxel load`](voxel-load.md) 恢复。有车、有 RuntimeId、有正式铁路、编组或运动状态都不再返回 `UnsupportedVehicles` / `UnsupportedIdentities` / `UnsupportedRailway`。缺必需引用、未知格式/生成器、不兼容基底或真实 I/O 失败时保留旧会话和上一份有效文件。前方缺口停车、支撑失效停车和全毁被引用轨段随格式 3 一并保存；恢复后按当前世界重查停车原因，不自动解除停车。平滑插值/跟随相机和正式设计器仍不在本命令范围内。

正式菜单下两种夹具（五类改装壳体、桥接断裂 / 脱落）的保存 → 退出进程 → 新进程加载 → 继续，以及读档错误复现，见 [命令索引](README.md#切片-3-保存退出进程并恢复后继续)。
