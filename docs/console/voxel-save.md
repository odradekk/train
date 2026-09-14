# `voxel save`：保存当前世界与静置车辆

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel save
voxel save Slice 3 Persist
```

参数：可选显示名。省略时沿用会话当前显示名；尚未命名时默认为 `Development World`。多个 token 用空格拼接成一个名称。名称经去首尾空白后须为 1–64 字符，不能是 `.` 或 `..`，也不能含 `/ \ : * ? " < > |`。显示名只写在存档文件内，不参与路径。

成功时把当前切片的固定生成基底、Runtime Delta，以及实际静置车辆写入 `user://saves/<UUID>/save.json`（物理路径为用户数据根下的 `saves/<UUID>/save.json`）。格式号为 `2`。首次保存分配稳定 UUID；之后重复保存复用同一 UUID。改名不改身份。`voxel reset` 只重建世界，不清空该 UUID。

存档记录：

- 外部体素 `DefinitionId`、上次 Semantic Revision，以及保存时各定义的耐久上限（作为未写入 Delta 的实例绝对值基线）
- 实际车辆与零件 Runtime ID、定义与 Archetype 引用、Local Grid、耐久、朝向、接触绑定、连续 Transform
- 必要的存档内（save-local）定义及其依赖；外部只存 ID 与 LastUsedRevision，不嵌入旧 Mod
- 带 RuntimeId 的脱落世界体素
- `WorldSeed`、`NextEntityId`、下一事务计数等影响后续提交 / 断裂的权威状态；数组按稳定顺序写出
- 文化无关的 JSON 数值，double 按 `G17` 往返

不保存网格、Collider、场景树或高亮，也不新增有状态 RNG。不从渲染 float 反推 Transform。

写入先完成候选文件 `save.json.candidate` 并 flush，再整体替换目标文件。保存复用加载的身份校验：`next_entity_id` 为 `0` 或不大于任何已用 Runtime ID 时返回 `InvalidDelta`，不覆盖上一份可读存档。`ulong.MaxValue` 是合法的耗尽游标，仍可保存；它本身不表示可分配的新 ID。失败返回 `WriteFailed` 或 `InvalidName` 等状态，当前可编辑会话和上一份有效存档都保持不变。`transaction_count` 已为 `ulong.MaxValue` 时捕获失败并返回 `InvalidDelta`，不覆盖上一份可读存档。提交入口在计数到达 `MaxValue - 1` 时即拒绝再分配，因此正常提交不会写出随后无法加载的计数。

成功按字段分行输出：

```text
Voxel Saved.
id 11111111-2222-4333-8444-555555555555
name Slice 3 Persist
Voxel count: 26.
```

失败同样以 `info` 写出 `Voxel <Status>.` 和当前总数，例如非法名称得到 `InvalidName`。没有独立的存档列表或选档 UI；记下输出中的 UUID，之后用 [`voxel load`](voxel-load.md) 恢复。当前切片已实现完整车辆与脱落身份持久化后，有车或有 RuntimeId 不再返回 `UnsupportedVehicles` / `UnsupportedIdentities`。已有正式铁路、编组或沿轨运动状态时返回 `UnsupportedRailway`：不写候选文件、不报告 `Saved`，上一份有效存档保持不变。无铁路的切片 4 空世界仍可保存。铁路运行存档、编组持久化和正式设计器仍不在本命令范围内。

正式菜单下两种夹具（五类改装壳体、桥接断裂 / 脱落）的保存 → 退出进程 → 新进程加载 → 继续，以及读档错误复现，见 [命令索引](README.md#切片-3-保存退出进程并恢复后继续)。
