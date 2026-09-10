# 控制台命令手册

本手册面向游戏内开发控制台，不是 PowerShell、Godot 命令行或归档 MVP 的命令说明。

适用范围：当前主分支的游戏内控制台，包括 `help`、`clear`、`quit`、`reload`、`loglevel`，以及 `voxel` 子命令（增删查询见 PR #47，伤害见 PR #48，已有世界热重载见切片 1，多 Chunk 边界见切片 2 / #50，基础碰撞查询见 #51，局部旋转碰撞盒见 #52，世界存档与进程重启恢复见 #53）和 `composite` 子命令（切片 3 静置车辆制造 / 查询 / 重置 / 伤害与脱落）。

## 打开控制台与输入规则

- 在游戏窗口中按反引号键（通常与 `~` 共用）打开或关闭控制台；打开后按 `Esc` 只关闭控制台。
- 输入一条命令后按 `Enter` 执行。下文代码块中的每一行都需分别提交。
- 命令不带 `/` 前缀。`<参数>` 表示必填，`[参数]` 表示可选；实际输入时去掉尖括号和方括号。
- 参数按空白分隔；连续空白和首尾空白会被忽略。没有引号转义、多命令分号串联或行内注释语法。
- 命令名、`voxel` / `composite` 子命令、日志等级和朝向编码不区分大小写；定义 ID 必须使用小写。
- 空行无操作；未知命令报告 `Unknown command: ...`。
- 命令回显及多数结果使用 `info` 日志。如果看不到结果，先执行 `loglevel info`，再重试查询。

## 命令速查

| 命令 | 用途 | 可用条件 |
| --- | --- | --- |
| [help](help.md) | 列出命令名称 | 所有构建 |
| [clear](clear.md) | 清空控制台日志缓冲区 | 所有构建 |
| [quit](quit.md) | 退出游戏进程 | 所有构建 |
| [reload](reload.md) | 重新加载内容目录，并原子适配已有世界实例 | 仅 Debug 构建 |
| [loglevel](loglevel.md) `[debug\|info\|warning\|error]` | 查询或设置日志过滤等级 | 所有构建 |
| [voxel reset](voxel-reset.md) | 重建初始开发世界 | 包含 PR #47，且已进入体素开发场景 |
| [voxel query](voxel-query.md) `<x> <y> <z>` | 查询一个格子的实例状态 | 同上 |
| [voxel collide](voxel-collide.md) `<x> <y> <z>` | 查询一格完整盒的领域碰撞（`local_box` 格内盒外须用六参数） | 同上 |
| [voxel collide](voxel-collide.md) `<minx> <miny> <minz> <maxx> <maxy> <maxz>` | 查询探测盒与当前碰撞形状的重叠 | 同上 |
| [voxel remove](voxel-remove.md) `<x> <y> <z>` | 移除一个体素 | 同上 |
| [voxel add](voxel-add.md) `<x> <y> <z> <namespace:id> <orientation>` | 在空格中添加指定体素 | 同上 |
| [voxel damage](voxel-damage.md) `<x> <y> <z> <amount> [<x> <y> <z> <amount> ...]` | 对一个或多个体素施加同一事件的指定伤害 | 同上 |
| [voxel save](voxel-save.md) `[<display-name>]` | 把当前世界原子写入 `user://saves/<UUID>/save.json` | 同上 |
| [voxel load](voxel-load.md) `<uuid>` | 按当前有效定义恢复存档并整体替换世界 | 同上 |
| [composite reset](composite-reset.md) `[hollow\|bridge\|bridge-right]` | 清空已制造车辆并重建当前切片世界，可选立即制造空心壳或桥接夹具 | 包含切片 3，且已进入体素开发场景 |
| [composite manufacture](composite-manufacture.md) `[<namespace:id>]` | 从已发布 Blueprint 制造一辆静置车辆 | 同上 |
| [composite damage](composite-damage.md) `<id> <x> <y> <z> <amount> [...]` | 对一辆车的一个或多个局部零件施加同一事件伤害并结算断裂/脱落 | 同上 |
| [composite query](composite-query.md) `[page [<page>] \| damaged [<page>] \| <id> [parts [<page>]]]` | 分页列出车辆摘要、受损零件，或查询单车接触与零件布局 | 同上 |

兼容性说明：目前 `help`、`clear`、`quit`、`reload` 会忽略额外参数，额外参数不会形成新功能。例如 `quit help` 仍会退出游戏。请按上述无参数语法使用。

## 使用前提与通用参数

运行当前构建，从主菜单选择“进入切片 1 体素场景”、“进入切片 2 体素场景”或“进入切片 3 静置车辆场景”。未进入或已经离开体素场景时，所有 `voxel` 命令都会提示 `Enter the voxel development scene before using voxel commands.`，所有 `composite` 命令都会提示 `Enter the voxel development scene before using composite commands.`。场景、控制台和测试共用同一世界会话与正式命令，没有单独的演示写入入口。

三个切片都是有限测试配置，不是产品尺寸限制。Chunk 只作空间分区，不改变占用、耐久或朝向规则。切片 3 车辆体素不属于世界占用。

| 切片 | 入口 | Chunk / 世界 | 初始占用 | 有效坐标 |
| --- | --- | --- | --- | --- |
| 1 | 进入切片 1 体素场景 | 一个 16³ Chunk | 各轴 `4..11`，共 512 格 | `0..15` |
| 2 | 进入切片 2 体素场景 | 边长 16，世界 32³，共 2×2×2 个 Chunk | 地面 `x,z=0..31`、`y=0`，加 `14..17` 三轴交汇 4³ 实心块，共 1,088 格 | `0..31` |
| 3 | 进入切片 3 静置车辆场景 | 边长 16，世界 32³，Seed=42 | 空世界；可制造 168 格空心壳或 55 格桥接夹具，车辆体素不进入世界网格；失去身份的碎块才转入世界 | `0..31` |

切片 2 基底带有明确的 Seed、`GeneratorVersion=slice-2-integer-fixture-v1` 和尺寸参数；切片 3 使用 Seed=42 与 `GeneratorVersion=slice-3-parked-vehicle-v1`。相同输入与有效内容重复生成同一基底。世界生成不消费随机流；切片 3 断裂平局使用 Seed 与事务 ID 的无状态 SplitMix64 键。`voxel reset` / `composite reset` 走同一正式生成入口。

| 参数 | 格式与范围 | 示例 |
| --- | --- | --- |
| `x`、`y`、`z` | 三个单独的十进制整数，按世界 X/Y/Z 顺序填写；有效范围取决于当前切片（上表）。Y 是竖直轴，不接受小数、逗号三元组或括号 | 切片 1：`4 4 4`；切片 2 边界：`15 16 15` |
| `namespace:id` | 已加载体素定义的完整 ID，必须包含一个冒号。两部分各 1–64 字符，以小写 ASCII 字母或数字开头，后续可含小写字母、数字、`_`、`-` | `core:iron-ore` |
| `orientation` | 必填的正交朝向编码，不是角度；详细取值见下节 | `px_py` |

PR #47 内置测试定义 `core:iron-ore` 允许全部 24 种朝向；`core:fixed` 只允许默认朝向。两者原始配置均为 Value=10、Mass=1、MaxDurability=100，碰撞为 `full_box`。`core:hollow` 同样占一整格，但碰撞为 `none`：不能重复放置，`voxel collide` 不命中。`core:ledge` 是偏心且不对称的单个 `local_box`：仍占一整格，只有盒内区域命中，并随实例正交朝向旋转。正式 `reload` 成功后，已有实例按当前目录采用 Mass、Value、耐久上限和碰撞规则；`voxel query` 显示规格，`voxel collide` 使用当前碰撞。Mod 或后续定义变更可能改变可用 ID 和参数，以实际加载内容为准。

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
| `Saved` / `Loaded` | 保存或恢复成功，后附 `id`、显示名和当前总数 |
| `UnsupportedVehicles` | 当前会话已有车辆，世界存档入口明确拒绝；不写文件、不读档、不丢弃车辆 |
| `InvalidName` / `NotFound` / `WriteFailed` / `ReadFailed` / `Truncated` | 显示名非法、没有该 UUID 文件、写入/读取失败或 JSON 不完整；世界与上一份有效文件保持不变 |
| `UnknownFormat` / `UnknownGenerator` / `IncompatibleBase` / `MismatchedId` / `MissingDefinition` / `InvalidDelta` | 版本、未知字段、路径与文档 UUID 不一致、缺少必要定义记录、基底或 Delta 不能安全恢复；不部分加载，也不报告成功 |

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
voxel add 4 4 4 core:iron-ore px_py
voxel add 4 4 4 core:iron-ore px_py
voxel add 0 0 0 core:hollow px_py
voxel query 0 0 0
voxel collide 0 0 0
voxel add 0 0 0 core:iron-ore px_py
voxel add 1 0 0 core:ledge px_py
voxel collide 1.18 0.08 0.48 1.22 0.12 0.52
voxel collide 1.68 0.38 0.48 1.72 0.42 0.52
voxel remove 1 0 0
voxel add 1 0 0 core:ledge pz_py
voxel collide 1.18 0.08 0.48 1.22 0.12 0.52
voxel collide 1.48 0.38 0.18 1.52 0.42 0.22
```

原始定义下应依次观察到初始 512 格、`(4,4,4)` 碰撞 `Hit`/`full_box`、移除后 511 格且该格 `Miss`、重加后 512 格/耐久 100，最后一次铁矿为 `Occupied`。空心占格后总数 513，`query` 占用而 `collide` 为 `Miss`，再放铁矿得到 `Occupied`。偏心台沿占 `(1,0,0)` 后，身份朝向下盒内探测 `Hit`/`local_box`、格内盒外 `Miss`；改成 `pz_py` 后原盒内探测变为 `Miss`，旋转后的新区域 `Hit`。

### 观察部分伤害、归零和内部空腔

```text
voxel reset
voxel damage 4 4 4 25
voxel damage 4 4 4 75
voxel add 4 4 4 core:iron-ore px_py
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
voxel add 16 4 4 core:iron-ore px_py
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

先把 `(4,4,4)` 打到耐久 80。把同一定义的上限改为 150 后再 `reload`，查询仍为耐久 80，但 `max` 为 150；再改为 60 后重载，耐久与 `max` 均为 60。`voxel query` 按字段分行，长合法 Mod ID 仍须完整显示耐久、当前规格和 `Voxel count`。候选缺少已放置定义或无法保留当前朝向时，整次 `reload` 拒绝，查询和画面保持旧发布结果。

## 相关快捷键与当前边界

体素场景中关闭控制台后，可用 `Delete` 移除当前 HUD 目标格（切片 1 为 `(4,4,4)`，切片 2 为 `(15,16,15)`），`Insert` 在该格添加默认朝向的 `core:iron-ore`，切片 3 可用 `M` 制造正式空心壳，`R` 重置当前切片基底（切片 3 同时清空车辆），`Esc` 返回主菜单。它们是场景快捷键，不是控制台命令；`quit` 则退出整个游戏。`R` / `voxel reset` / `composite reset` 不清空已分配的存档 UUID。退出场景后再次进入会创建新的开发世界，但磁盘上的 `user://saves/<UUID>/save.json` 仍在；要用 [`voxel load`](voxel-load.md) 才能恢复。当前会话存在车辆时，`voxel save` / `voxel load` 返回 `UnsupportedVehicles`，不会报告成功却丢车。

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
voxel add 16 16 15 core:iron-ore px_py
voxel remove 15 15 15
voxel add 15 15 15 core:iron-ore px_py
voxel remove 15 16 16
voxel add 15 16 16 core:iron-ore px_py
voxel remove 16 16 16
voxel add 16 16 16 core:iron-ore px_py
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
voxel add 17 17 17 core:iron-ore nz_py
voxel add 15 1 8 core:ledge pz_py
voxel query 14 14 14
voxel collide 4 1 4
voxel collide 14 14 14
voxel collide 15.48 1.38 8.18 15.52 1.42 8.22
voxel save Slice 2 Resume
```

记下 `id` 行的 UUID。`quit` 退出游戏进程，再启动同一构建，从主菜单重新进入切片 2，然后：

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
voxel save Should Fail
composite reset
```

原始夹具下应制造 1 辆 168 零件的空心壳。不带 ID 的查询只给出第 1 页紧凑摘要（含车辆 ID 和零件数）。`composite query 1` 给出连续 Transform `8,8,8 1,0,0 0,1,0 px_py`、包络 `[0,0,-1]->[8,6,4]`，以及接触点 `(1,-0.75,1.5)` / `(7,-0.75,1.5)`。世界体素数保持 0。有车辆时 `voxel save` 返回 `UnsupportedVehicles`。重置后车辆数为 0，`NextEntityId` 回到 1。

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

目前没有独立的旋转、撤销、列车或铁路控制台命令，也没有通过 `voxel query` 更改 HUD 选中坐标的功能。车辆存档和改装仍明确拒绝。

## 维护与实现依据

每个可执行命令各用一个文件，`voxel` 子命令也分别维护。新增、修改或移除命令时，同步更新对应文件和本页索引；共享参数、朝向编码与组合示例统一保留在本页。每份命令文档应覆盖语法、参数、使用方法、结果和限制，尚未交付的命令明确标注。

基础命令与输入行为见 [ConsoleCommands.cs](../../Console/ConsoleCommands.cs)、[GameConsole.cs](../../Ui/GameConsole.cs)。体素命令实现为 `Console/VoxelConsoleCommands.cs`，车辆命令实现为 `Console/CompositeConsoleCommands.cs`，坐标、ID、朝向与伤害契约位于 `Domain/Voxels/`，车辆身份与校验位于 `Domain/Composites/`；体素显示与快捷键位于 `Ui/VoxelDeveloperScene.cs`。
