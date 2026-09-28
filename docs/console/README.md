# 控制台命令手册

本手册面向游戏内开发控制台，不是 PowerShell、Godot 命令行或归档 MVP 的命令说明。

适用范围：当前主分支的游戏内开发控制台，包括 `help`、`clear`、`quit`、`reload`、`loglevel`，以及 `voxel` 子命令（增删查询见 PR #47，伤害见 PR #48，已有世界热重载见切片 1，多 Chunk 边界见切片 2 / #50，基础碰撞查询见 #51，局部旋转碰撞盒见 #52，世界存档与进程重启恢复见 #53，静置车辆存档与跨进程恢复见 #62）、`composite` 子命令（切片 3 静置车辆制造 / 查询 / 重置 / 伤害与脱落、五类原子改装、实体 ID 耗尽处理，以及已有车辆的当前内容热重载）和 `rail` 子命令（切片 4 正式直轨施工、占格查询、原轨段修复、一车头一车厢刚性编组、固定步长起停与跨段运行、运行中车钩破坏后的独立滑行、断轨安全停车与显式恢复、沿轨显示插值与选定存活车辆跟随相机，以及正式铁路 / 多编组运行停车与滑行状态的版本化文件存档），以及 Playground 的 `observe` 子命令（#124 独立选择、检查、自由观察与显式聚焦 / 跟随）。

## 打开控制台与输入规则

- 在游戏窗口中按反引号键（通常与 `~` 共用）打开或关闭控制台；打开后按 `Esc` 只关闭控制台。
- 输入一条命令后按 `Enter` 执行。下文代码块中的每一行都需分别提交。
- 命令不带 `/` 前缀。`<参数>` 表示必填，`[参数]` 表示可选；实际输入时去掉尖括号和方括号。
- 参数按空白分隔；连续空白和首尾空白会被忽略。没有引号转义、多命令分号串联或行内注释语法。
- 命令名、`voxel` / `composite` / `rail` 子命令、日志等级和朝向编码不区分大小写；定义 ID 必须使用小写。
- 空行无操作；未知命令报告 `Unknown command: ...`。
- 命令回显及多数结果使用 `info` 日志。如果看不到结果，先执行 `loglevel info`，再重试查询。

## 命令速查

| 命令 | 用途 | 可用条件 |
| --- | --- | --- |
| [help](help.md) | 列出命令名称 | 所有构建 |
| [clear](clear.md) | 清空控制台日志缓冲区 | 所有构建 |
| [quit](quit.md) | 退出游戏进程 | 所有构建 |
| [reload](reload.md) | 重新加载内容目录，并原子适配已有世界实例、车辆、碎块、接触绑定和已提交编组运行条件 | 仅 Debug 构建 |
| [loglevel](loglevel.md) `[debug\|info\|warning\|error]` | 查询或设置日志过滤等级 | 所有构建 |
| [voxel reset](voxel-reset.md) | 重建初始开发世界 | 包含 PR #47，且已进入体素开发场景 |
| [voxel query](voxel-query.md) `<x> <y> <z>` | 查询一个格子的实例状态 | 同上 |
| [voxel collide](voxel-collide.md) `<x> <y> <z>` | 查询一格完整盒的领域碰撞（`local_box` 格内盒外须用六参数） | 同上 |
| [voxel collide](voxel-collide.md) `<minx> <miny> <minz> <maxx> <maxy> <maxz>` | 查询探测盒与当前碰撞形状的重叠 | 同上 |
| [voxel remove](voxel-remove.md) `<x> <y> <z> [at <step>]` | 移除一个体素 | 同上 |
| [voxel add](voxel-add.md) `<x> <y> <z> <namespace:id> <orientation> [at <step>]` | 在空格中添加指定体素 | 同上 |
| [voxel damage](voxel-damage.md) `<x> <y> <z> <amount> [<x> <y> <z> <amount> ...] [at <step>]` | 对一个或多个体素施加同一事件的指定伤害 | 同上 |
| [voxel edits](voxel-edits.md) `[pending \| log [<count>] \| fault]` | 查询世界修改命令的边界位置、待执行队列、执行日志与内部故障 | 同上 |
| [voxel save](voxel-save.md) `[<display-name>]` | 把当前世界、车辆、正式铁路、编组、模拟步、待执行列车命令与世界修改命令原子写入 `user://saves/<UUID>/save.json` | 同上 |
| [voxel load](voxel-load.md) `<uuid>` | 按当前有效定义恢复存档并整体替换会话；格式 4 写出，仍可读格式 2、3 | 同上 |
| [composite reset](composite-reset.md) `[hollow\|bridge\|bridge-right]` | 清空已制造车辆并重建当前切片世界，可选立即制造空心壳或桥接夹具 | 包含切片 3，且已进入体素开发场景 |
| [composite manufacture](composite-manufacture.md) `[<namespace:id>] [pose <x> <y> <z>] [at <step>]` | 从已发布 Blueprint 制造一辆静置车辆 | 同上 |
| [composite damage](composite-damage.md) `<id> <x> <y> <z> <amount> [...] [at <step>]` | 对一辆车的一个或多个局部零件施加同一事件伤害并结算断裂/脱落 | 同上 |
| [composite query](composite-query.md) `[page [<page>] \| damaged [<page>] \| <id> [parts [<page>]]]` | 分页列出车辆摘要、受损零件，或查询单车接触与零件布局 | 同上 |
| [composite add](composite-add.md) `<id> <x> <y> <z> <namespace:id> <orientation> [at <step>]` | 向已有车辆添加零件，成功才分配新身份 | 同上 |
| [composite remove](composite-remove.md) `<id> <partId> [at <step>]` | 按真实 RuntimeId 拆除零件 | 同上 |
| [composite move](composite-move.md) `<id> <partId> <x> <y> <z> [at <step>]` | 移动已有零件并保留身份与状态 | 同上 |
| [composite rotate](composite-rotate.md) `<id> <partId> <orientation> [at <step>]` | 旋转已有零件并保留身份与状态 | 同上 |
| [composite replace](composite-replace.md) `<id> <x> <y> <z> <namespace:id> <orientation> [at <step>]` | 拆除旧件并初始化新件 | 同上 |
| [composite batch](composite-batch.md) `<id> <op> ... [at <step>]` | 原子批量改装；占格交换合法，失败整批拒绝 | 同上 |
| [rail construct](rail-construct.md) | 提交切片 4 固定直轨施工计划 | 包含切片 4，且已进入体素开发场景 |
| [rail assemble](rail-assemble.md) | 制造并定位一车头一车厢的刚性编组 | 同上 |
| [rail start](rail-start.md) `[assembly <id>] [reverse] [step]` | 按当前或指定模拟步启动指定编组；无参且多编组时拒绝广播，`reverse` 明确拒绝 | 同上 |
| [rail stop](rail-stop.md) `[assembly <id>] [step]` | 在当前或指定模拟步停指定编组并清零速度 | 同上 |
| [rail pause](rail-pause.md) | 暂停 Host 自动推进 | 同上 |
| [rail resume](rail-resume.md) | 恢复 1× Host 推进 | 同上 |
| [rail step](rail-step.md) `[n]` | 正式推进 1 步或 `n` 步 | 同上 |
| [rail query](rail-query.md) `[occupancy\|intervals\|connections\|graph\|rebuild\|assembly\|motion\|display \| <x> <y> <z>]` | 查询轨段、连接、实际占格、通行区间、编组、运动或显示插值 | 同上 |
| [rail follow](rail-follow.md) `[vehicle <id>]` | 查询或选择跟随相机的存活车辆 | 同上 |
| [rail repair](rail-repair.md) `<x> <y> <z> [...]` | 修复原轨段损坏占格并恢复依赖区间 | 同上 |
| [observe](observe.md) | 查询 Playground 镜头模式、选择、失效提示与选中对象检查 | 已进入 Playground |
| [observe list](observe-list.md) | 列出可观察的车辆与轨段 | 同上 |
| [observe select](observe-select.md) `vehicle <id> \| segment <id> \| cell <x> <y> <z> \| none` | 选择或清除检查对象；不移动镜头 | 同上 |
| [observe focus](observe-focus.md) | 镜头平滑聚焦选中对象 | 同上 |
| [observe follow](observe-follow.md) | 镜头跟随选中车辆的显示车体 | 同上 |
| [observe free](observe-free.md) | 结束聚焦/跟随，镜头原位转为自由观察 | 同上 |

兼容性说明：目前 `help`、`clear`、`quit`、`reload` 会忽略额外参数，额外参数不会形成新功能。例如 `quit help` 仍会退出游戏。请按上述无参数语法使用。

## 使用前提与通用参数

运行当前构建，从主菜单选择“进入切片 1 体素场景”、“进入切片 2 体素场景”、“进入切片 3 静置车辆场景”或“进入切片 4 正式直轨场景”。未进入或已经离开体素场景时，所有 `voxel` 命令都会提示 `Enter the voxel development scene before using voxel commands.`，所有 `composite` 命令都会提示 `Enter the voxel development scene before using composite commands.`，所有 `rail` 命令都会提示 `Enter the voxel development scene before using rail commands.`。场景、控制台和测试共用同一世界会话与正式命令，没有单独的演示写入入口。主菜单的 “Playground · …” 按钮按具名固定配置建立世界（见下文 [Playground](#playground具名固定配置)）；进入后同样可用全部 `voxel` / `composite` / `rail` 命令。

四个切片都是有限测试配置，不是产品尺寸限制。Chunk 只作空间分区，不改变占用、耐久或朝向规则。切片 3 车辆体素不属于世界占用。切片 4 轨道占格是世界体素 `core:rail`；显示由 `TrackRenderer` 生成，不走普通立方体面。

| 切片 | 入口 | Chunk / 世界 | 初始占用 | 有效坐标 |
| --- | --- | --- | --- | --- |
| 1 | 进入切片 1 体素场景 | 一个 16³ Chunk | 各轴 `4..11`，共 512 格 | `0..15` |
| 2 | 进入切片 2 体素场景 | 边长 16，世界 32³，共 2×2×2 个 Chunk | 地面 `x,z=0..31`、`y=0`，加 `14..17` 三轴交汇 4³ 实心块，共 1,088 格 | `0..31` |
| 3 | 进入切片 3 静置车辆场景 | 边长 16，世界 32³，Seed=42 | 空世界；可制造 168 格空心壳或 55 格桥接夹具，车辆体素不进入世界网格；失去身份的碎块才转入世界 | `0..31` |
| 4 | 进入切片 4 正式直轨场景 | 边长 16，世界 320×32×32，Seed=4 | 菜单进入或 `R` 后施工固定直轨并制造一车头一车厢刚性编组，257 根轨枕、1032 个正体积占格；车辆体素不属于世界占用；`voxel reset` 清空轨道与编组 | `0..319` / `0..31` / `0..31` |

切片 2 基底带有明确的 Seed、`GeneratorVersion=slice-2-integer-fixture-v1` 和尺寸参数；切片 3 使用 Seed=42 与 `GeneratorVersion=slice-3-parked-vehicle-v1`；切片 4 使用 Seed=4 与 `GeneratorVersion=slice-4-straight-rail-v1`。相同输入与有效内容重复生成同一基底。世界生成不消费随机流；切片 3 断裂平局使用 Seed 与事务 ID 的无状态 SplitMix64 键。`voxel reset` / `composite reset` 走同一正式生成入口。切片 4 的 `rail construct` 走 `RailEditTransaction`，不在重置时偷偷写入轨道。

| 参数 | 格式与范围 | 示例 |
| --- | --- | --- |
| `x`、`y`、`z` | 三个单独的十进制整数，按世界 X/Y/Z 顺序填写；有效范围取决于当前切片（上表）。Y 是竖直轴，不接受小数、逗号三元组或括号 | 切片 1：`4 4 4`；切片 2 边界：`15 16 15` |
| `namespace:id` | 已加载体素定义的完整 ID，必须包含一个冒号。两部分各 1–64 字符，以小写 ASCII 字母或数字开头，后续可含小写字母、数字、`_`、`-` | `core:ferrous-ore` |
| `orientation` | 必填的正交朝向编码，不是角度；详细取值见下节 | `px_py` |

当前内置测试定义 `core:ferrous-ore` 允许全部 24 种朝向；`core:fixed` 只允许默认朝向。两者原始配置均为 Value=10、Mass=1、MaxDurability=100，碰撞为 `full_box`。`core:hollow` 同样占一整格，但碰撞为 `none`：不能重复放置，`voxel collide` 不命中。`core:ledge` 是偏心且不对称的单个 `local_box`：仍占一整格，只有盒内区域命中，并随实例正交朝向旋转。切片 3 改装夹具 `core:refit-marker` 与壳体同 Value/Mass/上限 100，允许全部 24 向，顶/侧/底纹理不同，顶面与侧面均有指向局部 +X 的箭头；`core:refit-replacement` 上限 150，替换后按新上限初始化。正式 `reload` 成功后，已有世界体素和已有车辆零件按当前目录采用 Mass、Value、耐久上限和碰撞规则；已提交编组按当前 propulsion / `track_contact` 校验运行与滑行条件，失败整次拒绝。`voxel query` / `composite query` 显示当前规格，`voxel collide` 使用当前碰撞。外部蓝图布局更新不覆盖已有实际车辆；新制造使用新模板。Mod 或后续定义变更可能改变可用 ID 和参数，以实际加载内容为准。

## 朝向编码

格式为 `<Right>_<Up>`：前一部分表示体素局部 +X 轴指向的世界轴，后一部分表示局部 +Y 轴指向的世界轴；局部 +Z 轴由两者叉积确定。这里是体素坐标轴定义，不是相机朝向。

`px`/`nx` 分别表示世界 +X/−X，`py`/`ny` 表示 +Y/−Y，`pz`/`nz` 表示 +Z/−Z。两个轴必须垂直；不能使用 `px_px` 或 `px_nx`。

全部 24 种有效编码如下：

| Right | 可用编码 |
| --- | --- |
| `px` | `px_py`、`px_ny`、`px_pz`、`px_nz` |
| `nx` | `nx_py`、`nx_ny`、`nx_pz`、`nx_nz` |
| `py` | `py_px`、`py_nx`、`py_pz`、`py_nz` |
| `ny` | `ny_px`、`ny_nx`、`ny_pz`、`ny_nz` |
| `pz` | `pz_px`、`pz_nx`、`pz_py`、`pz_ny` |
| `nz` | `nz_px`、`nz_nx`、`nz_py`、`nz_ny` |

`px_py` 是默认朝向；`pz_py` 保持局部上方向为世界 +Y、局部右方向改为世界 +Z；`px_pz` 将局部上方向改为世界 +Z，可用于观察倾转。

有效编码还必须满足定义的 `allowed_orientations`：

| 定义限制 | 允许的朝向 |
| --- | --- |
| `none` | 仅 `px_py`，表示不允许旋转，不是禁止放置 |
| `four_way` | `px_py`、`pz_py`、`nx_py`、`nz_py` |
| `all_24` | 上表全部 24 种 |

## 结果与错误处理

| 返回内容 | 含义与处理 |
| --- | --- |
| `Added` / `Removed` / `Damaged` | 修改成功，后附修改后的正式查询和总数；`Damaged` 表示未归零，当前耐久已更新。伤害结果按字段分行，避免合法长 `namespace:id` 把耐久或总数截成错误数字。车辆伤害成功后还会列出仍存在的车辆；总状态只看本次受影响车辆及其后继，这些对象都不再保留身份时为 `Removed`，不因场上无关车辆改为 `Damaged` |
| `UnknownVehicle` | 车辆伤害或查询的运行时 ID 不存在；会话不变 |
| `OutOfBounds` | 增删或伤害坐标超出当前切片有效范围（切片 1 为 `0..15`，切片 2 为 `0..31`），改用有效坐标 |
| `Occupied` | 添加目标已占用；改选空格，或明确移除原体素后再添加 |
| `Empty` | 移除或伤害目标本来就是空格，无体素可操作，也不会创建实例 |
| `InvalidDamage` | 伤害值不是正整数，例如 `0` 或负数；世界不变 |
| `UnknownDefinition` | ID 格式合法，但当前内容目录没有该定义；检查拼写和内容加载诊断 |
| `DisallowedOrientation` | 编码是合法正交朝向，但该定义不允许；改用其允许的方向 |
| `Collision Hit` / `Miss` / `OutOfBounds` / `InvalidQuery` | 领域碰撞查询结果；命中按字段分行写出格子、定义和 `kind`。已解析但非有限或零/负体积为 `InvalidQuery`，合法越界单格（含整数极值）为 `OutOfBounds`。语义见 [voxel collide](voxel-collide.md) |
| `Usage: voxel ...` | 子命令、参数个数、无法解析的坐标文本、ID 格式或朝向编码不合法；按本手册的完整语法重新输入。`NaN` / `Infinity` 能解析为数字，因此走 `InvalidQuery` 而不是 Usage。非法 UUID 文本也是用法提示，不会读文件 |
| `Saved` / `Loaded` | 保存或恢复成功，后附 `id`、显示名和当前世界体素总数 |
| `Unavailable` | 当前会话没有可用的存档 store 或目录；不写文件、不读档 |
| `UnsupportedVehicles` / `UnsupportedIdentities` | 保留给尚未纳入持久化契约的范围；当前静置车辆与脱落 RuntimeId 已支持 |
| `UnsupportedRailway` | 保留状态，成功路径不再返回；正式铁路、编组与运动状态已写入格式 4 |
| `Edit <id> <kind> pending` / `Edit PastStep.` / `Edit UnallocatedReference.` / `Edit InputExhausted.` / `Edit SimulationFaulted.` | 带 `at <step>` 的世界修改命令回报：已排队，或因过去步、引用从未分配的车辆 / 零件 ID、命令编号 / 步内序号耗尽、会话故障而拒绝且不消耗编号。不带 `at` 的立即修改在编号或序号耗尽时返回 `InputExhausted`，会话故障时返回 `SimulationFaulted`；见 [voxel edits](voxel-edits.md) |
| `UnknownSource` | `voxel load` 的存档带有不受支持的 `world_source` 来源类型；当前会话不变 |
| `Committed` / `Repaired` / `Intact` / `UnknownCell` / `Conflict` / `IndependentLineConflict` / `ClearanceConflict` / `WorldObstacle` | `rail` 施工或修复结果。成功才分配轨段身份或恢复占格；失败整份计划拒绝 |
| `Formed` / `NoRailway` / `AlreadyAssembled` / `UnknownVehicle` / `MissingPropulsion` / `MissingControl` / `MissingContact` / `MissingConnection` / `InsufficientSupport` / `DisconnectedPath` / `InvalidConnection` / `MultipleActiveLocomotives` / `VehicleConstraint` | `rail assemble` 结果。成功才制造车辆并形成编组；失败不改世界、车辆与计数 |
| `Started` / `Stopped` / `Advanced` / `Paused` / `Resumed` / `ReverseUnsupported` / `StepOverflow` / `PastStep` / `NoAssembly` / `UnknownAssembly` / `NoRailway` / `MissingPropulsion` / `MissingControl` / `InsufficientSupport` / `DisconnectedPath` / `VehicleConstraint` / `TransactionExhausted` | `rail start` / `stop` / `step` / `pause` / `resume` 结果。反向启动、过去目标步、未知或未指定的多编组目标、或步号溢出整次拒绝且原状态不变。可执行目标步是 `[当前步, ulong.MaxValue]`；成功起停一律入队并消费事务，目标编组写入队列。当前步与未来步按同一入队顺序执行，到步新命令不能越过同目标步旧队列。到达 `MaxValue` 后仍执行该步命令，再推进为 `StepOverflow` |
| `InvalidName` / `NotFound` / `WriteFailed` / `ReadFailed` / `Truncated` | 显示名非法、没有该 UUID 文件、写入/读取失败或 JSON 不完整；世界与上一份有效文件保持不变 |
| `UnknownFormat` / `UnknownGenerator` / `IncompatibleBase` / `MismatchedId` / `MissingDefinition` / `InvalidDelta` | 版本、未知字段、路径与文档 UUID 不一致、缺少必要定义记录、基底或 Delta 不能安全恢复；不部分加载，也不报告成功。`transaction_count` 为 `ulong.MaxValue`、`scheduled_sequence` 大于事务计数、非法铁路几何/占格/轨枕、重复车辆、两车无车钩、停车却带速度且仍声称有活动车头、运行中却缺少当前 control、缩放/剪切轴、悬空命令目标、以及 save-local 与车辆身份不一致，属于 `InvalidDelta`。合法单车、多编组与无动力滑行可以恢复 |
| `TransactionExhausted` | 制造 / 改装 / 车辆伤害 / 编组时再提交会写出不可加载的计数（当前值已为 `MaxValue - 1` 或 `MaxValue`）；不绕回、不部分提交、不覆盖可读存档 |
| `EntityIdExhausted` | 制造、改装、车辆伤害或编组预检整次提交所需的新实体 ID 后发现不足。拒绝时权威状态、`NextEntityId` 与事务计数都不变。`ulong.MaxValue` 是合法的耗尽游标但绝不分配；已耗尽时仍可执行不需要新 ID 的改装或伤害，并可保存、加载 |

格式校验失败时不执行世界命令。例如缺少朝向、ID 使用大写、朝向写成 `90` 或 `px_px`，得到的是用法提示，而不是 `UnknownDefinition` / `DisallowedOrientation`。

格式全部合法后，添加按边界、占用、定义存在性、定义允许朝向的顺序检查；伤害按伤害值、边界、占用的顺序检查。一次只返回首先遇到的问题。例如在已占用格测试未知定义会先得到 `Occupied`，应选择空格验证目标错误。多目标伤害先校验全部目标，任一项失败则整件事失败。所有失败增删和伤害均保留原世界状态。

`voxel` 的返回文本（包括失败状态）统一以 `info` 级别写入日志，不能仅根据日志颜色或严重等级判断操作是否成功。`voxel save` / `voxel load` 失败同样写 `info`，并保留当前可编辑世界和上一份有效存档。

## 常用操作示例

### 验证移除、重加和重复占格

先进入体素开发场景，逐行执行：

```text
loglevel info
voxel reset
voxel query 4 4 4
voxel collide 4 4 4
voxel remove 4 4 4
voxel collide 4 4 4
voxel add 4 4 4 core:ferrous-ore px_py
voxel add 4 4 4 core:ferrous-ore px_py
voxel add 0 0 0 core:hollow px_py
voxel query 0 0 0
voxel collide 0 0 0
voxel add 0 0 0 core:ferrous-ore px_py
voxel add 1 0 0 core:ledge px_py
voxel collide 1.18 0.08 0.48 1.22 0.12 0.52
voxel collide 1.68 0.38 0.48 1.72 0.42 0.52
voxel remove 1 0 0
voxel add 1 0 0 core:ledge pz_py
voxel collide 1.18 0.08 0.48 1.22 0.12 0.52
voxel collide 1.48 0.38 0.18 1.52 0.42 0.22
```

原始定义下应依次观察到初始 512 格、`(4,4,4)` 碰撞 `Hit`/`full_box`、移除后 511 格且该格 `Miss`、重加后 512 格/耐久 100，最后一次 `core:ferrous-ore` 为 `Occupied`。空心占格后总数 513，`query` 占用而 `collide` 为 `Miss`，再放 `core:ferrous-ore` 得到 `Occupied`。偏心台沿占 `(1,0,0)` 后，身份朝向下盒内探测 `Hit`/`local_box`、格内盒外 `Miss`；改成 `pz_py` 后原盒内探测变为 `Miss`，旋转后的新区域 `Hit`。

### 观察部分伤害、归零和内部空腔

```text
voxel reset
voxel damage 4 4 4 25
voxel damage 4 4 4 75
voxel add 4 4 4 core:ferrous-ore px_py
voxel reset
voxel damage 5 5 5 100
voxel query 5 5 5
voxel damage 4 5 5 100
```

角落格先扣到耐久 75，再归零为空、总数 511；重加后耐久回到 100。内部格伤害 100 后为空、总数 511，空腔仍被外层遮挡；再对 `(4,5,5)` 伤害 100 后形成通向外部的开口，总数 510，可从固定视角观察内部暴露面。不要用 `voxel remove` 代替上述伤害步骤。

### 同一事件伤害多个目标

```text
voxel reset
voxel damage 4 4 4 100 5 4 4 100 6 4 4 25
voxel query 6 4 4
```

前两格删除、第三格耐久 75，总数 510。控制台按字段分行输出状态、该格查询和总数；短 `namespace:id` 与合法最长体素定义标识都须完整显示耐久和 `Voxel count`，不能被 200 字符上限截成 `7` 或 `5`。把三组坐标和伤害值换成其他枚举顺序，最终占用、耐久和可见面相同。

### 验证失败不改变世界

```text
voxel reset
voxel add 16 4 4 core:ferrous-ore px_py
voxel add 0 0 0 core:missing px_py
voxel add 0 0 0 core:fixed pz_py
voxel damage 4 4 4 0
voxel damage 0 0 0 25
voxel query 0 0 0
```

在未额外添加 `core:missing` 定义的原始内容下，依次得到 `OutOfBounds`、`UnknownDefinition`、`DisallowedOrientation`、`InvalidDamage`、`Empty`；最后查询仍为空，总数仍为 512，`(4,4,4)` 耐久仍为 100。

### 热重载已有受损体素

```text
voxel reset
voxel damage 4 4 4 20
voxel query 4 4 4
reload
voxel query 4 4 4
```

先把 `(4,4,4)` 打到耐久 80。把同一定义的上限改为 150 后再 `reload`，查询仍为耐久 80，但 `max` 为 150；再改为 60 后重载，耐久与 `max` 均为 60。`voxel query` 按字段分行，长合法 Mod ID 仍须完整显示耐久、当前规格和 `Voxel count`。候选缺少已放置定义或无法保留当前朝向时，整次 `reload` 拒绝，查询和画面保持旧发布结果。切片 3 已有车辆走同一入口：改装设备 P 耐久 75 时上限 100→150 仍为 75，再到 60 后截断为 60；`composite query damaged` 显示当前上限。核心标签或必要定义缺失时整次拒绝，车辆、目录 Generation 和画面不变，之后仍可查询、改装和伤害。读档后的车辆沿用同一适配规则。

## 相关快捷键与当前边界

体素场景中关闭控制台后，可用 `Delete` 移除当前 HUD 目标格（切片 1 为 `(4,4,4)`，切片 2 为 `(15,16,15)`，切片 4 为 `(80,1,15)`），`Insert` 在该格添加默认朝向的 `core:ferrous-ore`，切片 3 可用 `M` 制造正式空心壳，切片 4 可用 `T` 启动、`Y` 停车、`P` 暂停/恢复、`F` 在存活车辆间循环跟随相机，`R` 重置当前切片基底（切片 3 同时清空车辆；切片 4 重置后立即再施工固定直轨并编组），`Esc` 返回主菜单。切片 4 HUD 按正式运动状态写「运行 / 滑行 / 停止」，并保留支撑失效、前方缺口、线路末端与会话暂停。它们是场景快捷键，不是控制台命令；`quit` 则退出整个游戏。`R` / `voxel reset` / `composite reset` 不清空已分配的存档 UUID。退出场景后再次进入会创建新的开发世界，但磁盘上的 `user://saves/<UUID>/save.json` 仍在；要用 [`voxel load`](voxel-load.md) 才能恢复。切片 3 的实际车辆、save-local 布局和脱落 RuntimeId，以及切片 4 的正式铁路、编组、运动和待执行命令，都随格式 4 一并保存；本构建仍可读格式 2、3 文件。失败时保留旧会话，不会报告成功却丢车、丢轨或丢队列。

### 切片 4 正式直轨

从主菜单进入切片 4 后，菜单进入已经施工固定直轨，并制造一车头一车厢刚性编组。若先 `voxel reset`，再逐行执行：

```text
loglevel info
voxel reset
voxel save Slice 4 Empty
rail construct
rail assemble
rail start
rail step 300
rail query
rail query assembly
rail query motion
rail query display
rail follow
composite query
composite damage <loco-id> 7 1 1 100
rail query assembly
rail step 30
rail query assembly
rail start
rail start assembly <coast-id>
rail query 80 1 14
rail query 80 1 15
rail query 80 1 16
rail query connections
composite query
voxel damage 110 1 15 100
rail query intervals
rail query graph
rail query assembly
rail step 2000
rail query assembly
rail repair 110 1 15
rail start
voxel save After Railway
```

空世界可以保存。施工成功后应看到 2 个轨段、1 条明确连接、257 根轨枕，以及边缘格与左右钢轨占格。`rail assemble` 后 `rail query assembly` 应看到活动车头、前接触点 80 与 Operational；`composite query` 列出 `core:dev-locomotive` 与 `core:dev-carriage`。`rail start` 后再 `rail step 300`，前接触应为 96 / 87、速度 4。用命令返回的车头身份对后车钩 `(7,1,1)` 施加 100 点伤害后，应看到两个编组，瞬间前接触仍为 96 / 87、速度各 4；再 `rail step 30` 后前车 98 / 4，后车 88.875 / 3.5。无参 `rail start` 此时为 `UnknownAssembly`；对后车 `rail start assembly <id>` 为 `MissingPropulsion`。`rail start 0`、`rail stop 101`、`rail start 101` 再一次性 `rail step 102`，与先 `rail step 101`、到步再 `rail start 101`、然后 `rail step 1` 得到同一状态 102：运行中、前接触 82.833888888888893、速度约 0.0333，事务计数相同。仅 `rail start 0` 与 `rail stop 101` 再 `rail step 102` 时，无论一帧推进 1 步还是 2 步，前接触均为 82.833611111111111、速度 0；`rail query motion` 在到期前列出 `pending stop 101 assembly <id>`，以及各编组的 `assembly <id> speed … running … heading … halt …`。第 300 步后再伤害 `(110,1,15)` 100，第一段缺失 `x=110..111`；继续步进后车头前接触停在 `x=110` 入侧边界、速度 0、`halt forward-gap`，任何必要接触不得进入缺口，派生图在 `rebuild` 前为 stale 且不授权跨越。未修轨时 `rail start` 为 `DisconnectedPath`，零速与 `halt forward-gap` 保持。第 300 步伤害接触之间的 `(93,1,15)` 不立即冻结；由最先到达 `x=93` 的后方接触限位，抵达当步零速。拆分后再 `rail step 30` 伤害 `(88,1,15)` 100，仅后车立即零速并 `halt support-lost`，前车独立继续。修复只恢复可启动条件；有动力须再 `rail start` 从零加速，无动力后车仍是 `MissingPropulsion`。全毁第一段现存占格后，原轨段身份与定位几何保留、通行区间为空、`halt support-lost`；另建平行线不会接替旧引用，修原身份后才可启动。`voxel save After Railway` 应返回 `Saved` 并写出格式 4；退出进程后再 `voxel load` 同一 UUID，缺口停车、支撑失效与全毁引用都保持原权威状态且不自动解除停车。删除被引用轨段几何的坏档整次 `InvalidDelta`，当前会话与旧有效文件保留。开发停车、线路末端停车或滑行归零恢复后为零速；有动力编组须再执行 `rail start`。Host 显示累积余量不写入存档。

### Playground（具名固定配置）

主菜单每个 “Playground · …” 按钮对应一份具名固定配置（`Domain/Playground/PlaygroundConfiguration.cs`）：`straight-rail-train`（切片 4 基底 + 正式直轨 + 一车头一车厢）和 `straight-rail-empty`（同一直轨，无车辆）。配置只声明初始世界，由正式施工与编组命令建立；建立后全局时钟默认暂停，速度、运行开关与待执行命令不被清零。HUD 显示配置 ID / 版本 / 来源类型、当前步、暂停状态与编组运动；按钮 “运行 / 暂停 / 单步 / 推进 N 步 / 启动列车 / 停车 / 保存” 与下列命令走同一正式会话操作。Playground 没有 `R` 重置或 `Esc` 返回菜单这类会丢失会话的快捷键；安全切换配置不在当前入口范围内。

```text
loglevel info
rail query motion
rail step
rail step 299
rail query assembly
rail resume
rail pause
voxel save Playground Train
```

建立后 `rail query motion` 报告 `paused true`、第 0 步；打开控制台、查询或空等帧都不推进。先点 “启动列车” 或执行 `rail start`，`rail step` 与 `rail step 299` 后恰为第 300 步，前接触 96、速度 4，仍处于暂停；`rail resume` 后才由 Host 按 60 步/秒推进。无车配置同样可建立并推进全局步。`voxel save` 在格式 4 中写出 `world_source`，新进程加载后恢复来源并保持暂停，见 [voxel load](voxel-load.md)。注意：`voxel reset` 仍是现有命令，会清空 Playground 世界并去掉来源信息。

HUD 下方的修改面板把 “世界格 / 量 / 延迟步” 与 “最新车辆局部格” 输入转成世界修改命令：放置矿石、移除体素、损伤体素、在此制造空壳，以及对最新制造车辆的损伤零件、加装、拆除、移到 +X、旋转、替换与批量加装两格。延迟步为 0 时立即执行，否则排到当前步加延迟的边界。面板按局部格找到零件后提交带真实 ID 的命令，与下列控制台写法产生同一负载和位置；最下一行显示边界位置、下一个编号、待执行数与最近一条记录。

```text
voxel add 60 3 20 core:ferrous-ore px_py
voxel damage 60 3 20 25
composite manufacture core:dev-hollow-shell pose 60 6 24
composite add <shell> 2 1 0 core:refit-marker px_py at 303
voxel remove 60 3 20 at 303
voxel edits
voxel save Playground Edits
```

暂停在第 300 步时，前四条依次得到 `300:0`…`300:3`，时钟与列车不动；后两条排到第 303 步，`voxel edits` 报告 `boundary 300:6`。保存后在新进程加载，队列原样恢复，推进到第 303 步时两条命令按原顺序执行一次。

屏幕右侧观察面板与 `observe` 命令共用同一观察状态：对象列表、检查信息（身份、所属、耐久、运行资格、失败原因）、镜头模式与失效提示。选择（列表、画面左键点击或 `observe select`）从不移动镜头；只有“聚焦选中”/`F`/`observe focus` 与“跟随选中”/`V`/`observe follow` 改变镜头，“自由观察”/`C`/`observe free` 让镜头原位回到自由观察。Playground 镜头不再读 `rail follow` 的会话跟随状态。观察状态不写入存档、不占命令编号，打开面板、查询和切换镜头都不暂停或推进。

```text
observe
observe list
observe select vehicle <loco>
observe follow
rail resume
observe
composite damage <loco> 7 1 1 100
observe
observe select vehicle <carriage>
observe follow
composite damage <carriage> 1 0 1 100
observe
observe free
```

建立后镜头为 `mode free selection none`，不需要车辆。选中车头时镜头不动；跟随后运行、暂停都读车体显示姿态。断开后钩仍跟随同一车头。车厢核心被毁后 `observe` 报告 `mode free selection none` 与 `notice target-lost vehicle <carriage> mode follow camera free-in-place`，镜头停在原位，不跳到车头。

验证步骤与配置分开登记（`PlaygroundSequences`），自动入口 `scripts/run-godot-playground-smoke.ps1` 在独立会话上运行合法、拒绝与 `straight-rail-ordered-edits` 序列，并驱动真实场景按钮、修改面板与控制台输入完成暂停多操作、待执行结构命令、保存与跨进程继续对照；输出写入新的 `.godot/validation/issue-122/<时间戳>/`。观察链路另有 `scripts/run-godot-playground-observation-smoke.ps1`，在真实场景中驱动自由观察、列表与点击选择、聚焦/跟随、暂停与运行中观察、拆分与销毁目标及碰撞查询，并跨进程确认观察状态不进入存档；输出写入 `.godot/validation/issue-124/<时间戳>/`。

### 切片 2 边界复现

从主菜单进入切片 2 后逐行执行：

```text
loglevel info
voxel reset
voxel query 15 16 15
voxel collide 15 16 15
voxel collide 15.25 16 15 16.75 17 16
voxel remove 16 16 15
voxel collide 16 16 15
voxel query 15 16 15
voxel add 16 16 15 core:ferrous-ore px_py
voxel remove 15 15 15
voxel add 15 15 15 core:ferrous-ore px_py
voxel remove 15 16 16
voxel add 15 16 16 core:ferrous-ore px_py
voxel remove 16 16 16
voxel add 16 16 16 core:ferrous-ore px_py
voxel damage 15 16 15 100 16 16 16 100 15 16 16 25
voxel query 15 16 16
```

重置后应为 1,088 格。初始 `(15,16,15)` 碰撞 `Hit`；跨 `15/16` 的盒应同时命中两侧。去掉 `x=16` / `y=15` / `z=16` 一侧后，另一侧 `15/16` 邻接面应露出，被删格 `collide` 立即 `Miss`、邻格仍 `Hit`；交汇格 `(16,16,16)` 删除后邻接暴露面更新，无重叠面或残影。同一事件跨区伤害后总数 1,086，`(15,16,16)` 耐久 75 且仍 `Hit`，被删两格 `Miss`。把最后一组伤害改成含空格或越界目标应整件拒绝，占用和碰撞保持不变。

### 保存、退出进程并恢复后继续修改

从主菜单进入切片 2 后逐行执行。这是正式入口，不要用销毁会话代替退出进程：

```text
loglevel info
voxel reset
voxel remove 4 0 4
voxel add 4 1 4 core:hollow px_py
voxel damage 14 14 14 25
voxel remove 17 17 17
voxel add 17 17 17 core:ferrous-ore nz_py
voxel add 15 1 8 core:ledge pz_py
voxel query 14 14 14
voxel collide 4 1 4
voxel collide 14 14 14
voxel collide 15.48 1.38 8.18 15.52 1.42 8.22
voxel save Slice 2 Resume
```

记下 `id` 行的 UUID。关闭控制台后 `Esc` 返回主菜单，或 `quit` 退出游戏进程，再启动同一构建，从主菜单重新进入切片 2，然后：

```text
loglevel info
voxel load <刚才的 UUID>
voxel query 4 0 4
voxel query 4 1 4
voxel query 14 14 14
voxel query 17 17 17
voxel collide 4 1 4
voxel collide 14 14 14
voxel collide 15.48 1.38 8.18 15.52 1.42 8.22
voxel damage 14 14 14 75
voxel query 14 14 14
```

恢复后 `(4,0,4)` 为空，`(4,1,4)` 为 `core:hollow` 且整格 `collide` 为 `Miss`，`(14,14,14)` 耐久 75 且 `full_box` 命中，`(17,17,17)` 朝向 `nz_py`，`(15,1,8)` 的偏心盒在 `pz_py` 下命中上述探测盒。继续伤害 75 后该格删除，总数比恢复时少 1。重复 `voxel save` 仍写入同一 UUID；改显示名不改路径。验证时使用独立用户数据，不要覆盖实际存档。

### 切片 3 制造、查询和重置

从主菜单进入切片 3 后逐行执行：

```text
loglevel info
composite manufacture
composite query
composite query 1
composite query 1 parts 2
voxel save Slice 3 Persist
composite reset
```

原始夹具下应制造 1 辆 168 零件的空心壳。不带 ID 的查询只给出第 1 页紧凑摘要（含车辆 ID 和零件数）。`composite query 1` 给出连续 Transform `8,8,8 1,0,0 0,1,0 px_py`、包络 `[0,0,-1]->[8,6,4]`，以及接触点 `(1,-0.75,1.5)` / `(7,-0.75,1.5)`。世界体素数保持 0。`voxel save Slice 3 Persist` 应返回 `Saved` 并写出 UUID。重置后车辆数为 0，`NextEntityId` 回到 1；已分配的存档 UUID 仍在，可用 `voxel load` 把车读回来。

继续验证桥接伤害：

```text
composite reset bridge
composite query
composite damage 1 3 1 1 100
composite query
composite query damaged
composite reset bridge
composite damage 1 3 1 1 100 1 1 1 100
voxel query 8 8 8
```

等值桥断裂后应有 2 辆仍保留身份但 `Disabled` 的车，世界体素仍为 0。桥加左核心同事件伤害后只剩右侧车辆，左侧碎块出现在世界网格，且不会同时属于车辆。`voxel query 8 8 8` 对脱落零件额外给出 `runtime <id>`，与原零件 ID 相同；普通世界体素没有该行。

### 切片 3 保存、退出进程并恢复后继续

从主菜单进入切片 3 后逐行执行。这是正式入口，不要用销毁会话代替退出进程，也不要用未恢复的旧截图当证据：

```text
loglevel info
composite manufacture
composite add 1 2 1 0 core:refit-marker px_py
composite damage 1 2 1 0 25
composite move 1 <P> 3 1 0
composite rotate 1 <P> nz_py
composite add 1 4 1 0 core:refit-marker px_py
composite move 1 <P> 4 1 0 <Q> 3 1 0
composite replace 1 <Q> core:refit-replacement px_py
composite remove 1 <R>
voxel save Slice 3 Persist
```

记下 `id` 行的 UUID。关闭控制台，`Esc` 返回主菜单，再 `quit` 退出进程。重新启动同一构建，从主菜单进入切片 3，然后：

```text
loglevel info
voxel load <刚才的 UUID>
composite query
composite query damaged
voxel collide 8 8 8
composite move 1 <P> 3 1 0
composite move 1 <P> 4 1 0
composite damage 1 0 1 0 10
voxel save Slice 3 Persist
```

恢复后应仍是一辆 169 零件的改装壳：P 保留原 ID、耐久 75、朝向 `nz_py`，Q/R 不在车上。显示与已支持碰撞从权威状态重建，不能出现重复网格、残影或拉伸。继续改装、伤害、`reload` 和再次 `voxel save` 都绑定恢复后的会话，并复用同一 UUID。验证时使用独立用户数据，不要覆盖实际存档或 worktree 内置内容。

第二类夹具（桥接伤害后的主 / 次继承者与脱落世界体素）从主菜单进入切片 3 后逐行执行，同样不要用销毁会话代替退出进程：

```text
loglevel info
composite reset bridge
composite damage 1 3 1 1 100
composite query
voxel save Slice 3 Bridge
```

记下 UUID。`Esc` 回主菜单后 `quit`，再启动同一构建进入切片 3：

```text
loglevel info
voxel load <刚才的 UUID>
composite query
```

恢复后仍是两辆 `Disabled` / `MissingContact` 的后继，世界体素 0，原 / 新 Composite ID 与零件 ID 保持。若要核对脱落，把伤害换成 `composite damage 1 3 1 1 100 1 1 1 100`，保存前 `voxel query 8 8 8` 应有 `runtime <id>`；新进程加载后同一格仍占用且 RuntimeId 不变，`voxel collide 8 8 8` 为 `Hit` / `full_box`。

读档错误复现（独立用户数据）：对一个已保存的 UUID 执行 `voxel load` 后，把 `save.json` 的 `save_format_version` 改成 `1` 或 `99` 再加载，应得 `UnknownFormat` 且当前会话车辆数不变；删掉 `save_local_definitions` 或把零件朝向改成 `none`，分别得 `MissingDefinition` / `InvalidDelta`；缺文件得 `NotFound`。失败后仍可 `composite manufacture` 或加载上一份有效文件。

目前没有独立的撤销命令，也没有通过 `voxel query` 更改 HUD 选中坐标的功能。切片 4 已有 [`rail construct`](rail-construct.md) / [`rail assemble`](rail-assemble.md) / [`rail start`](rail-start.md) / [`rail stop`](rail-stop.md) / [`rail pause`](rail-pause.md) / [`rail resume`](rail-resume.md) / [`rail step`](rail-step.md) / [`rail query`](rail-query.md) / [`rail follow`](rail-follow.md) / [`rail repair`](rail-repair.md)，以及正式铁路 / 多编组运行停车、滑行与断轨安全停车状态的 [`voxel save`](voxel-save.md) / [`voxel load`](voxel-load.md)。沿轨显示插值与选定存活车辆跟随相机已接入切片 4；正式设计器仍不在当前切片。

### 切片 3 五类原子改装

从主菜单进入切片 3 后逐行执行。逐步用 `composite query` 核对返回的真实 ID、耐久和朝向，不要写死恰好分到的整数：

```text
loglevel info
composite manufacture
composite add 1 2 1 0 core:refit-marker px_py
composite damage 1 2 1 0 25
composite query damaged
composite move 1 <P> 3 1 0
composite rotate 1 <P> nz_py
composite add 1 4 1 0 core:refit-marker px_py
composite batch 1 move <P> 4 1 0 move <Q> 3 1 0
composite replace 1 3 1 0 core:refit-replacement px_py
composite remove 1 <R>
```

`<P>` / `<Q>` / `<R>` 分别取自两次 `allocated` 行和替换返回的新 ID。成功末态：P 在 `(4,1,0)`，耐久 75，朝向 `nz_py`；Q/R 不存在；零件 169；世界体素 0。再从该固定结果验证失败：

```text
composite batch 1 rotate <P> px_py add 8 1 0 core:refit-marker px_py
composite move 1 <P> 0 1 0
composite move 1 <P> 3 2 1
composite add 1 5 1 0 core:fixed px_py
composite rotate 1 <locked> nz_py
```

前三例应整批拒绝且 P 仍为 `nz_py`、耐久 75；非法旋转返回 `DisallowedOrientation`。权威不变后仍可 `composite move 1 <P> 3 1 0`。单项移到对方占用格应 `DuplicateOccupancy`；同批两个 `move` 才能交换。

改装后的断裂必须读实际布局。在桥上再加第二桥后，同一事件切断两桥，应得到两辆各 27 零件的后继，各自的存档内定义/Revision 与真实零件和接触点一致，且不再沿用断裂前的 56 零件定义。未改装过的车断裂后仍不发明存档内定义。这些实际布局可随格式 4 保存，本构建仍可读格式 2、3；恢复时不得按外部 Blueprint 名称重造。

```text
composite reset bridge
composite add 1 3 0 1 core:refit-marker px_py
composite damage 1 3 1 1 100 3 0 1 100
composite query
```

### 切片 3 已有车辆热重载

进入切片 3 后先完成固定改装序列，使 P 耐久为 75，再改隔离内容后执行正式 `reload`。不要改玩家文件或仓库内置内容：

```text
composite query damaged
reload
composite query damaged
```

把隔离副本里的 `core:refit-marker` 上限改为 150 后 `reload`，P 仍为 75，`damaged` 行的 `max` 为 150；再改为 60 后重载，P 截断为 60 并从受损列表消失。外部蓝图增加内部格后，已有车零件数不变，再 `composite manufacture` 才使用新模板。先 `composite reset bridge-right`（或把 `core:identity-core-heavy` 的 Value 从 10 改到 20 后 `reload` 已有右侧重核桥），再 `composite damage 1 3 1 1 100`，右侧成为主继承者；不要只改默认等值 `core:dev-bridge` 的普通核心。删除必要核心定义后 `reload` 整次拒绝，`composite query`、改装、伤害与画面保持拒绝前状态。读档后的车辆走同一适配入口。

## 维护与实现依据

每个可执行命令各用一个文件，`voxel` 子命令也分别维护。新增、修改或移除命令时，同步更新对应文件和本页索引；共享参数、朝向编码与组合示例统一保留在本页。每份命令文档应覆盖语法、参数、使用方法、结果和限制，尚未交付的命令明确标注。

基础命令与输入行为见 [ConsoleCommands.cs](../../Console/ConsoleCommands.cs)、[GameConsole.cs](../../Ui/GameConsole.cs)。体素命令实现为 `Console/VoxelConsoleCommands.cs`，车辆命令实现为 `Console/CompositeConsoleCommands.cs`，轨道命令实现为 `Console/RailConsoleCommands.cs`，观察命令实现为 `Console/ObserveConsoleCommands.cs`（观察模型位于 `Domain/Playground/PlaygroundObservation.cs` 与 `PlaygroundInspection.cs`），`at <step>` 与 `voxel edits` 实现为 `Console/WorldEditConsole.cs`，世界修改命令与规范编码位于 `Domain/Commands/`，坐标、ID、朝向与伤害契约位于 `Domain/Voxels/`，车辆身份与校验位于 `Domain/Composites/`，正式直轨位于 `Domain/Rails/`；体素显示、TrackRenderer 与快捷键位于 `Ui/VoxelDeveloperScene.cs`。
