# `voxel load`：从存档恢复世界、车辆与正式铁路

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel load 11111111-2222-4333-8444-555555555555
```

参数：一个必填 UUID，必须能被 `Guid` 解析（推荐 `D` 格式）。非法 UUID 文本返回用法提示，不读取文件，也不改会话。

加载使用**当前**已发布目录，而不是存档里记住的整份定义。请求路径中的 UUID 必须与文件根字段 `id` 一致，否则在替换世界或会话身份之前返回 `MismatchedId`。先按当前 Seed / 生成器 / 边界完整构造并校验候选世界、车辆、铁路、编组和命令队列，再整体替换会话。写出格式为 `4`；读取接受 `2`、`3` 和 `4`。格式 `2` 缺铁路字段视为空铁路，以保留本构建前三切片存档；格式 `2` / `3` 没有世界修改命令队列，恢复为空队列。格式 `1` 或未知版本返回 `UnknownFormat`，没有开发期旧档迁移。不从 Blueprint 重造编组，不从 Mesh 或派生图反推轨段，也不在 Load 后改内存冒充恢复。

重建所需的 `external_definitions`（定义 ID 与保存时 `max_durability`）必须完整；缺记录整次拒绝，不会用当前上限猜测未写入 Delta 的绝对值。实例耐久保持存档绝对值；仅当当前上限低于存档值时截断。上限升高、Value/Mass/显示名、Semantic Revision 或无关内容变化不会单独拒绝加载。车辆按存档中的实际改装 / 断裂布局恢复，不按 Blueprint 名称重新制造；完整车、改装车和少接触碎块分别恢复，不统一强求制造合法。已写入且内部一致的存档内自定义定义可在外部 Composite 条目缺失时独立恢复；没有内嵌定义的外部引用，以及真正缺失的零件或 Archetype，仍返回 `MissingDefinition`。缺 Mod 或缺少必要 save-local 依赖同样返回 `MissingDefinition`。重复 ID / 占格、非法朝向、非有限 Transform、缩放或剪切等自身已不是单位正交旋转的轴、接触绑定、save-local 与车辆身份/Archetype/规格不一致、缺必需轨段或车辆引用、正向直轨/正体积/端点连接与占格/轨枕几何不一致、编组成员重复或车钩/格位/姿态不符、两车同组却没有合法车钩、单车却仍带连接、沿轨距离为负或超出轨段、停车标志与正速度并存且仍声称有活动车头、命令 `at_step` 早于当前模拟步、`scheduled_sequence` 大于 `transaction_count` 或队列序号不合法、运行中却没有当前动力和控制、无动力滑行却解析不到当前 `track_contact` 减速度、待执行命令指向不存在的编组、缺 `assembly_id` 却恢复出多于一个编组、`next_entity_id` 为 `0` 或不大于任何已用 Runtime ID（含轨段与编组）、或 `transaction_count` 已为 `ulong.MaxValue`，返回 `InvalidDelta`。合法的单车编组、多编组、以及 `running=false` 且 `active_locomotive_id=0` 的正速度滑行可以恢复，并与上述坏档可区分。结构事件后的合法队列与权威状态必须能原样往返。旧格式 3 文件若只有一个编组且命令未写 `assembly_id`，绑定到该编组。合法轨道损伤仍按存档几何与占格对照恢复，不把损伤本身当成坏档。当前目标速度低于存档速度时夹紧，不整份拒绝。滑行减速度始终读当前有效 `track_contact` 规格，不保留存档里的旧外部副本。`scheduled_sequence` 等于 `transaction_count` 是合法耗尽前边界，恢复后新命令继续按该游标递增，不会回绕反转同一步 FIFO。`next_entity_id=ulong.MaxValue` 是合法的耗尽游标，可正常加载，但不能再分配该 ID。合法已存旋转按原 double 位恢复，不重新正交归一化。`transaction_count` 为 `ulong.MaxValue - 1` 仍可加载，但无法再提交，以免写出自身不可读的存档。只有生成基底占用格的定义或朝向变化（会让恢复布局错位）才返回 `IncompatibleBase`。解析失败（未知根字段或嵌套未知字段、缺必填字段、截断、NaN/无穷）一律返回非成功状态，不会出现 `Loaded` / `Succeeded=true` 但会话未替换。

成功后正式 `query` / `add` / `remove` / `damage` / `collide`、`composite` 制造 / 改装 / 伤害、`rail` 起停与步进、引用校验、`reload`、显示和已支持碰撞都绑定恢复后的会话。图 / 轨道 / 车体显示重建不得改变正式姿态或定位；各编组显示读各自已提交姿态。会话 UUID 与显示名改为存档中的值，之后 [`voxel save`](voxel-save.md) 仍写入同一目录。开发停车、线路末端、前方缺口或支撑失效停车，以及全毁被引用轨段上的已停车车辆，恢复后按当前世界重查阻塞原因并保持零速，不自动解除停车。各接触仍有支撑、仅接触点之间存在缺口的运行/滑行状态可以恢复，并按各接触实际路径继续限位。恰好抵达缺口或末端的当步必须已是零速；缺必需定位几何引用整次 `InvalidDelta`，当前会话与上一份有效文件保留。有动力编组须再执行 [`rail start`](rail-start.md) `[assembly <id>]`。Host 显示累积余量是派生状态，恢复后从 0 重新累积。

格式 4 的 `world_edits` 恢复下一个命令编号、边界步内序号和待执行世界修改命令；它们在原目标步、按原顺序执行一次。保存前已被摧毁的目标不是坏档，到期时拒绝。引用从未分配的实体 ID、目标步不晚于存档步、编号越出 `[1, next_edit_id)`、编号或接受位置不递增、接受位置晚于存档边界，返回 `InvalidDelta`；未知命令类型、改装操作或输入格式版本返回 `UnknownFormat`；格式 4 缺该字段返回 `Truncated`，格式 2 / 3 出现该字段返回 `UnknownFormat`。读档后执行日志从空开始。详见 [`voxel edits`](voxel-edits.md)。

存档带 `world_source`（Playground 固定配置来源）时，会话恢复该来源信息，并在恢复后处于暂停状态；速度、运行开关、停车原因和待执行命令都按存档原值保留，用 [`rail step`](rail-step.md) 或 [`rail resume`](rail-resume.md) 继续。来源信息只作记录，加载仍按当前有效定义构造世界。没有 `world_source` 的存档加载后不暂停，与之前一致。来源类型不是 `fixed_configuration` 时返回 `UnknownSource`。

在 Playground 中加载成功后，观察的旧选择与聚焦/跟随目标一律清除（存档中同 ID 的车辆或同一格也视为新对象），镜头原位转为自由观察，并提示 `world-replaced load`（见 [observe](observe.md)）；加载失败时世界与观察状态都不变。

成功按字段分行输出 `Voxel Loaded.`、`id`、`name` 和当前世界体素总数。失败保留当前可编辑会话、当前会话 UUID/显示名和磁盘上的上一份有效存档，并报告：

| 状态 | 含义 |
| --- | --- |
| `NotFound` | 该 UUID 下没有 `save.json` |
| `ReadFailed` / `Truncated` | 文件无法读完、JSON 不完整或缺少必填字段 |
| `UnknownFormat` | `save_format_version` 不受支持，或出现未知根/嵌套字段 |
| `UnknownGenerator` | 生成器版本不受支持 |
| `IncompatibleBase` | 当前有效基底与存档基底布局不兼容，或身份（seed/尺寸）不匹配 |
| `MismatchedId` | 路径 UUID 与文件内 `id` 不一致 |
| `MissingDefinition` | 当前目录缺少存档用到的定义、Mod 或必要 save-local |
| `InvalidDelta` / `Occupied` / `OutOfBounds` | Delta 或车辆状态非法、重复占格或越界 |
| `Unavailable` | 当前会话没有可用的存档 store 或目录 |
| `UnknownSource` | `world_source` 的来源类型不受支持 |
| `UnsupportedVehicles` / `UnsupportedIdentities` | 保留给尚未纳入持久化契约的范围；当前静置车辆与脱落 RuntimeId 已支持，成功路径不再返回这两项 |

退出进程后再启动、进入同一切片，再用本命令恢复。不要用销毁会话或仅 `voxel reset` 代替进程重启验证。

正式菜单到保存 / 退出 / 恢复 / 继续的两种夹具步骤，以及把格式号改成 `1`、删 save-local、改非法朝向后的错误复现，见 [命令索引的切片 3 恢复节](README.md#切片-3-保存退出进程并恢复后继续)。
