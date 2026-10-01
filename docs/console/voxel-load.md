# `voxel load`：从存档恢复世界、车辆与正式铁路

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel load 11111111-2222-4333-8444-555555555555
voxel load 11111111-2222-4333-8444-555555555555 discard
voxel load 11111111-2222-4333-8444-555555555555 save
voxel load 11111111-2222-4333-8444-555555555555 cancel
```

参数：一个必填 UUID，必须能被 `Guid` 解析（推荐 `D` 格式）。非法 UUID 文本返回用法提示，不读取文件，也不改会话。可选 `discard` / `save` / `cancel` 与 [`voxel reset`](voxel-reset.md) 相同：有未保存变化时必须做出选择，`cancel` 和失败的保存都不读取。

加载使用**当前**已发布目录，而不是存档里记住的整份定义。请求路径中的 UUID 必须与文件根字段 `id` 一致，否则在替换世界或会话身份之前返回 `MismatchedId`。先按存档自身的来源、Seed / 生成器 / 边界完整构造并校验候选世界、车辆、铁路、编组和命令队列，再整体替换会话及其世界身份；世界编辑器中允许范围、种子或来源与当前世界不同。旧切片体素开发场景仍要求身份与当前会话相同，否则返回 `IncompatibleBase` 和 `stage base subject identity relationship seed_bounds`，保留当前世界。旧切片存档的种子、范围和 Chunk 尺寸还必须匹配对应的标准切片身份，否则同样拒绝。写出格式为 `5`；读取接受 `2`–`5`。格式 `2` 缺铁路字段视为空铁路，以保留本构建前三切片存档；格式 `2` / `3` 没有世界修改命令队列，恢复为空队列。格式 `3` / `4` 必须带旧列车队列字段 `scheduled_commands` / `scheduled_sequence`（缺失为 `Truncated`），但只接受空的旧队列：旧命令没有修改编号与边界位置，无法放进统一的输入全序，非空时整次返回 `UnsupportedRailway`，不猜测顺序也不部分加载；旧 `scheduled_sequence` 大于 `transaction_count` 仍是 `InvalidDelta`。格式 `5` 出现这两个旧字段返回 `UnknownFormat`；格式 `2`–`4` 的 `world_edits` 中出现铁路、编组或起停修改同样返回 `UnknownFormat`。格式 `1` 或未知版本返回 `UnknownFormat`，没有开发期旧档迁移。不从 Blueprint 重造编组，不从 Mesh 或派生图反推轨段，也不在 Load 后改内存冒充恢复。

重建所需的 `external_definitions`（定义 ID 与保存时 `max_durability`）必须完整；缺记录整次拒绝，不会用当前上限猜测未写入 Delta 的绝对值。实例耐久保持存档绝对值；仅当当前上限低于存档值时截断。上限升高、Value/Mass/显示名、Semantic Revision 或无关内容变化不会单独拒绝加载。车辆按存档中的实际改装 / 断裂布局恢复，不按 Blueprint 名称重新制造；完整车、改装车和少接触碎块分别恢复，不统一强求制造合法。已写入且内部一致的存档内自定义定义可在外部 Composite 条目缺失时独立恢复；没有内嵌定义的外部引用，以及真正缺失的零件或 Archetype，仍返回 `MissingDefinition`。缺 Mod 或缺少必要 save-local 依赖同样返回 `MissingDefinition`。重复 ID / 占格、非法朝向、非有限 Transform、缩放或剪切等自身已不是单位正交旋转的轴、接触绑定、save-local 与车辆身份/Archetype/规格不一致、缺必需轨段或车辆引用、正向直轨/正体积/端点连接与占格/轨枕几何不一致、编组成员重复或车钩/格位/姿态不符、两车同组却没有合法车钩、单车却仍带连接、沿轨距离为负或超出轨段、停车标志与正速度并存且仍声称有活动车头、运行中却没有当前动力和控制、无动力滑行却解析不到当前 `track_contact` 减速度、`next_entity_id` 为 `0` 或不大于任何已用 Runtime ID（含轨段与编组）、或 `transaction_count` 已为 `ulong.MaxValue`，返回 `InvalidDelta`。合法的单车编组、多编组、以及 `running=false` 且 `active_locomotive_id=0` 的正速度滑行可以恢复，并与上述坏档可区分。结构事件后的合法队列与权威状态必须能原样往返。合法轨道损伤仍按存档几何与占格对照恢复，不把损伤本身当成坏档。当前目标速度低于存档速度时夹紧，不整份拒绝。滑行减速度始终读当前有效 `track_contact` 规格，不保留存档里的旧外部副本。`next_entity_id=ulong.MaxValue` 是合法的耗尽游标，可正常加载，但不能再分配该 ID。合法已存旋转按原 double 位恢复，不重新正交归一化。`transaction_count` 为 `ulong.MaxValue - 1` 仍可加载，但无法再提交，以免写出自身不可读的存档。只有生成基底占用格的定义或朝向变化（会让恢复布局错位）才返回 `IncompatibleBase`。解析失败（未知根字段或嵌套未知字段、缺必填字段、截断、NaN/无穷）一律返回非成功状态，不会出现 `Loaded` / `Succeeded=true` 但会话未替换。

成功后正式 `query` / `add` / `remove` / `damage` / `collide`、`composite` 制造 / 改装 / 伤害、`rail` 起停与步进、引用校验、`reload`、显示和已支持碰撞都绑定恢复后的会话。图 / 轨道 / 车体显示重建不得改变正式姿态或定位；各编组显示读各自已提交姿态。会话 UUID 与显示名改为存档中的值，之后 [`voxel save`](voxel-save.md) 仍写入同一目录。开发停车、线路末端、前方缺口或支撑失效停车，以及全毁被引用轨段上的已停车车辆，恢复后按当前世界重查阻塞原因并保持零速，不自动解除停车。各接触仍有支撑、仅接触点之间存在缺口的运行/滑行状态可以恢复，并按各接触实际路径继续限位。恰好抵达缺口或末端的当步必须已是零速；缺必需定位几何引用整次 `InvalidDelta`，当前会话与上一份有效文件保留。有动力编组须再执行 [`rail start`](rail-start.md) `[assembly <id>]`。Host 显示累积余量是派生状态，恢复后从 0 重新累积。

格式 4 / 5 的 `world_edits` 恢复下一个命令编号、边界步内序号和待执行世界修改命令（格式 5 含铁路施工 / 修复、编组与列车起停）；它们在原目标步、按原顺序执行一次，新进程得到与原进程相同的结果。保存前已被摧毁的目标（含已消失的编组、全毁轨段）不是坏档，到期时按业务规则执行或拒绝。引用从未分配的实体 ID（含编组身份）、队列中出现 `content.publish`、目标步不晚于存档步、编号越出 `[1, next_edit_id)`、编号或接受位置不递增、接受位置晚于存档边界，返回 `InvalidDelta`；未知命令类型、改装操作或输入格式版本返回 `UnknownFormat`；格式 4 / 5 缺该字段返回 `Truncated`，格式 2 / 3 出现该字段返回 `UnknownFormat`。读档后执行日志从空开始。详见 [`voxel edits`](voxel-edits.md)。

存档带 `world_source`（空白或固定配置来源）时，会话恢复该来源信息，并在恢复后处于暂停状态；速度、运行开关、停车原因和待执行命令都按存档原值保留，用 [`rail step`](rail-step.md) 或 [`rail resume`](rail-resume.md) 继续。来源信息只作记录，加载仍按当前有效定义构造世界。带 `world_source` 时必须有 `base_fingerprint`。先按当前有效内容重新建立该固定配置并比对指纹：没有指纹、配置不存在、版本或有效参数对不上、文档 Seed / 生成器版本与来源参数不一致、依赖没有列全，或重建结果与指纹不一致，都返回失败并给出阶段、对象和关系（例如 `base` / 配置 ID / `base_fingerprint`，或 `dependency` / 依赖 ID / `content_dependency`），当前会话保持不变。必要依赖缺失返回 `MissingDefinition`。依赖的 Semantic Revision 变了但重建指纹仍一致时，加载成功并报告适配，不把外部 Blueprint 的新布局套到已有实例上。带固定来源却没有 `base_fingerprint` 的旧档明确不支持，不会退回只校验体素 `base_layout`。没有 `world_source` 的切片会话读档仍不因此暂停。是否暂停由会话是不是世界编辑器宿主决定，不由当前来源是否为空推断。同一世界编辑器会话里，每次成功的本命令都暂停，包括第一次读入没有来源的存档之后再读一次；速度、运行开关、停车原因和待执行命令按存档保留。直接调用会话 `Load()` 的切片入口不套用这道暂停。空白来源 `blank` 使用 `source_version=1`，输入中的范围、种子、Chunk 尺寸必须与文档一致，内容依赖必须为空；重建基底指纹后再应用 Delta，不要求资源定义。未知空白来源版本、非法范围、输入矛盾或指纹不匹配均明确拒绝。缺少来源却声称使用空白基底的文件也拒绝。来源类型既不是 `fixed_configuration` 也不是 `blank` 时返回 `UnknownSource`。

失败结果在 `Voxel <状态>.` 之后可以带一行 `stage` / `subject` / `relationship`。耐久或速度被当前内容夹紧时，成功结果另起一行 `adapted`，并且该会话视为未保存，因为内存中的持久值已经和文件不同。只报告修订或布局保留时不因此变成未保存。合法停车不是 `SimulationFaulted`，也不会被自动修复或吸附。引用从未分配的实体仍是 `InvalidDelta`，关系为 `entity_id`，当前会话不变。

在世界编辑器中加载成功后，观察的旧选择与聚焦/跟随目标一律清除（存档中同 ID 的车辆或同一格也视为新对象），镜头原位转为自由观察，并提示 `world-replaced load`（见 [observe](observe.md)）；加载失败时世界与观察状态都不变。

成功按字段分行输出 `Voxel Loaded.`、`id`、`name` 和当前世界体素总数。失败保留当前可编辑会话、当前会话 UUID/显示名和磁盘上的上一份有效存档，并报告：

| 状态 | 含义 |
| --- | --- |
| `NotFound` | 该 UUID 下没有 `save.json` |
| `ReadFailed` / `Truncated` | 文件无法读完、JSON 不完整或缺少必填字段 |
| `UnknownFormat` | `save_format_version` 不受支持，或出现未知根/嵌套字段 |
| `UnknownGenerator` | 生成器版本不受支持 |
| `IncompatibleBase` | 当前有效基底与存档基底布局不兼容，或来源输入与文档身份不一致 |
| `MismatchedId` | 路径 UUID 与文件内 `id` 不一致 |
| `MissingDefinition` | 当前目录缺少存档用到的定义、Mod 或必要 save-local |
| `InvalidDelta` / `Occupied` / `OutOfBounds` | Delta 或车辆状态非法、重复占格或越界 |
| `Unavailable` | 当前会话没有可用的存档 store 或目录 |
| `UnknownSource` | `world_source` 的来源类型不受支持 |
| `UnsupportedRailway` | 格式 3 / 4 存档带非空的旧列车起停队列，无法放进统一输入全序 |
| `UnsupportedVehicles` / `UnsupportedIdentities` | 保留给尚未纳入持久化契约的范围；当前静置车辆与脱落 RuntimeId 已支持，成功路径不再返回这两项 |

退出进程后再启动、进入任一世界编辑器或体素开发场景，再用本命令恢复。空白、固定配置与旧切片存档可以在同一世界编辑器会话中往返打开；旧切片体素开发场景仅恢复同一身份的存档。原文件保持不变。不要用销毁会话或仅 `voxel reset` 代替进程重启验证。

正式菜单到保存 / 退出 / 恢复 / 继续的两种夹具步骤，以及把格式号改成 `1`、删 save-local、改非法朝向后的错误复现，见 [命令索引的切片 3 恢复节](README.md#切片-3-保存退出进程并恢复后继续)。
