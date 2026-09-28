# `voxel save`：保存当前世界、车辆与正式铁路

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel save
voxel save Slice 3 Persist
```

参数：可选显示名。省略时沿用会话当前显示名；尚未命名时默认为 `Development World`。多个 token 用空格拼接成一个名称。名称经去首尾空白后须为 1–64 字符，不能是 `.` 或 `..`，也不能含 `/ \ : * ? " < > |`。显示名只写在存档文件内，不参与路径。

成功时把当前切片的固定生成基底、Runtime Delta、实际车辆，以及正式铁路 / 编组 / 运动 / 待执行命令写入 `user://saves/<UUID>/save.json`（物理路径为用户数据根下的 `saves/<UUID>/save.json`）。写出格式号为 `5`。首次保存分配稳定 UUID；之后重复保存复用同一 UUID。改名不改身份。`voxel reset` 只重建世界，不清空该 UUID。开发期不迁移更旧格式；本构建仍可读格式 `2`–`4` 文件（规则见 [`voxel load`](voxel-load.md)）。新保存一律写成格式 `5`（无铁路的切片也写出空的 `railway` / `assemblies` 与空的 `world_edits`）。格式 5 不再写旧的 `scheduled_commands` / `scheduled_sequence`：列车起停与其他修改同在 `world_edits` 队列中。

存档记录：

- 外部体素 `DefinitionId`、上次 Semantic Revision，以及保存时各定义的耐久上限（作为未写入 Delta 的实例绝对值基线）
- 实际车辆与零件 Runtime ID、定义与 Archetype 引用、Local Grid、耐久、朝向、接触绑定、连续 Transform
- 必要的存档内（save-local）定义及其依赖；外部只存 ID 与 LastUsedRevision，不嵌入旧 Mod
- 带 RuntimeId 的脱落世界体素
- 正式铁路：轨段身份、几何、明确连接、占格与依赖映射；损伤由世界占用与占格对照恢复，不从 Mesh 或派生图反推
- 实际编组：一个或多个独立编组的车辆引用、存活车钩、各自接触姿态与参考游标、活动动力或无动力滑行、速度与运行/停车开关。无活动车头写 `active_locomotive_id=0`
- 模拟步
- 世界修改命令队列 `world_edits`：下一个命令编号、当前边界步内序号，以及已接受但未执行的体素 / 车辆 / 铁路施工与修复 / 编组 / 列车起停修改（编号、接受位置、目标步、规范负载）。起停命令的目标编组即使执行前已消失也照常保存，到期在原位置拒绝。保存不执行也不清空队列；执行日志与 `content.publish` 记录不写入。见 [`voxel edits`](voxel-edits.md)
- `WorldSeed`、`NextEntityId`、下一事务计数等影响后续提交 / 断裂 / 命令入队的权威状态；数组按稳定顺序写出
- 文化无关的 JSON 数值，double 按 `G17` 往返
- 仅 Playground 建立的世界：可选根字段 `world_source`，记录来源类型 `fixed_configuration`、具名配置 ID、配置版本、解析后的有效参数和内容依赖（种类、ID、Semantic Revision）。它只描述初始世界来源，不含验证步骤。本构建还会在来源内写入 `base_fingerprint`（64 位大写十六进制），概括建立边界上的配置、基底参数、轨道、车辆身份与布局、`NextEntityId` 和事务计数；它不是体素 `base_layout`，也不包含耐久、速度、运行开关、停车原因、模拟步、待执行命令、镜头或 Catalog Generation。格式号仍是 `5`。带 `world_source` 但没有 `base_fingerprint` 的旧档读取失败。普通切片保存不写 `world_source`。直接调用会话 `Reset()` 会去掉来源；Playground 受保护的 [`voxel reset`](voxel-reset.md) 会重新建立同一配置并继续写出来源。本字段加入前的旧构建读到 `world_source` 或 `base_fingerprint` 会按未知字段返回 `UnknownFormat`

不把构建标识或 Catalog Generation 写进普通存档。Catalog Generation 只是运行时发布计数。严格隔离回放不在本命令的范围内。

不保存网格、Collider、场景树、高亮或 Host 显示累积余量，也不新增有状态 RNG。不从渲染 float、Mesh 或派生图反推 Transform / 轨段。目标速度来自当前动力定义，不写入存档。

写入先完成候选文件 `save.json.candidate` 并 flush，再整体替换目标文件。保存复用加载的身份校验：`next_entity_id` 为 `0` 或不大于任何已用 Runtime ID 时返回 `InvalidDelta`，不覆盖上一份可读存档。`ulong.MaxValue` 是合法的耗尽游标，仍可保存；它本身不表示可分配的新 ID。失败返回 `WriteFailed` 或 `InvalidName` 等状态，当前可编辑会话和上一份有效存档都保持不变。`transaction_count` 已为 `ulong.MaxValue` 时捕获失败并返回 `InvalidDelta`，不覆盖上一份可读存档。提交入口在计数到达 `MaxValue - 1` 时即拒绝再分配，因此正常提交不会写出随后无法加载的计数。会话因世界修改命令内部故障而锁定时返回 `SimulationFaulted`，不写文件。

成功按字段分行输出：

```text
Voxel Saved.
id 11111111-2222-4333-8444-555555555555
name Slice 3 Persist
Voxel count: 26.
```

失败同样以 `info` 写出 `Voxel <Status>.` 和当前总数，例如非法名称得到 `InvalidName`。没有独立的存档列表或选档 UI；记下输出中的 UUID，之后用 [`voxel load`](voxel-load.md) 恢复。有车、有 RuntimeId、有正式铁路、编组或运动状态都不再返回 `UnsupportedVehicles` / `UnsupportedIdentities` / `UnsupportedRailway`。缺必需引用、未知格式/生成器、不兼容基底或真实 I/O 失败时保留旧会话和上一份有效文件。前方缺口停车、支撑失效停车和全毁被引用轨段（保留登记身份与几何，供排队修复定位）随格式 5 一并保存；恢复后按当前世界重查停车原因，不自动解除停车。平滑插值/跟随相机和正式设计器仍不在本命令范围内。

正式菜单下两种夹具（五类改装壳体、桥接断裂 / 脱落）的保存 → 退出进程 → 新进程加载 → 继续，以及读档错误复现，见 [命令索引](README.md#切片-3-保存退出进程并恢复后继续)。
