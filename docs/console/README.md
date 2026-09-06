# 控制台命令手册

本手册面向游戏内开发控制台，不是 PowerShell、Godot 命令行或归档 MVP 的命令说明。

适用范围：当前主分支的游戏内控制台，包括 `help`、`clear`、`quit`、`reload`、`loglevel`，以及 `voxel` 子命令（增删查询见 PR #47，伤害见本切片）。

## 打开控制台与输入规则

- 在游戏窗口中按反引号键（通常与 `~` 共用）打开或关闭控制台；打开后按 `Esc` 只关闭控制台。
- 输入一条命令后按 `Enter` 执行。下文代码块中的每一行都需分别提交。
- 命令不带 `/` 前缀。`<参数>` 表示必填，`[参数]` 表示可选；实际输入时去掉尖括号和方括号。
- 参数按空白分隔；连续空白和首尾空白会被忽略。没有引号转义、多命令分号串联或行内注释语法。
- 命令名、`voxel` 子命令、日志等级和朝向编码不区分大小写；定义 ID 必须使用小写。
- 空行无操作；未知命令报告 `Unknown command: ...`。
- 命令回显及多数结果使用 `info` 日志。如果看不到结果，先执行 `loglevel info`，再重试查询。

## 命令速查

| 命令 | 用途 | 可用条件 |
| --- | --- | --- |
| [help](help.md) | 列出命令名称 | 所有构建 |
| [clear](clear.md) | 清空控制台日志缓冲区 | 所有构建 |
| [quit](quit.md) | 退出游戏进程 | 所有构建 |
| [reload](reload.md) | 重新加载内容目录 | 仅 Debug 构建 |
| [loglevel](loglevel.md) `[debug\|info\|warning\|error]` | 查询或设置日志过滤等级 | 所有构建 |
| [voxel reset](voxel-reset.md) | 重建初始开发世界 | 包含 PR #47，且已进入体素开发场景 |
| [voxel query](voxel-query.md) `<x> <y> <z>` | 查询一个格子的实例状态 | 同上 |
| [voxel remove](voxel-remove.md) `<x> <y> <z>` | 移除一个体素 | 同上 |
| [voxel add](voxel-add.md) `<x> <y> <z> <namespace:id> <orientation>` | 在空格中添加指定体素 | 同上 |
| [voxel damage](voxel-damage.md) `<x> <y> <z> <amount> [<x> <y> <z> <amount> ...]` | 对一个或多个体素施加同一事件的指定伤害 | 同上 |

兼容性说明：目前 `help`、`clear`、`quit`、`reload` 会忽略额外参数，额外参数不会形成新功能。例如 `quit help` 仍会退出游戏。请按上述无参数语法使用。

## 使用前提与通用参数

运行包含 PR #47 的构建，从主菜单选择“进入体素开发场景”。未进入或已经离开该场景时，所有 `voxel` 命令都会提示 `Enter the voxel development scene before using voxel commands.`。

开发世界为单个 16×16×16 Chunk，每格最多一个体素。初始占用各轴 `4..11`（含端点），共 512 个体素；其余格为空。这些是开发测试参数，不是产品尺寸限制。

| 参数 | 格式与范围 | 示例 |
| --- | --- | --- |
| `x`、`y`、`z` | 三个单独的十进制整数，按世界 X/Y/Z 顺序填写；当前有效坐标各为 `0..15`。Y 是竖直轴，不接受小数、逗号三元组或括号 | `4 4 4` |
| `namespace:id` | 已加载体素定义的完整 ID，必须包含一个冒号。两部分各 1–64 字符，以小写 ASCII 字母或数字开头，后续可含小写字母、数字、`_`、`-` | `core:iron-ore` |
| `orientation` | 必填的正交朝向编码，不是角度；详细取值见下节 | `px_py` |

PR #47 内置测试定义 `core:iron-ore` 允许全部 24 种朝向；`core:fixed` 只允许默认朝向。两者原始配置均为 Value=10、Mass=1、MaxDurability=100。Mod 或后续定义变更可能改变可用 ID 和参数，以实际加载内容为准。

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
| `Added` / `Removed` / `Damaged` | 修改成功，后附修改后的正式查询和总数；`Damaged` 表示未归零，当前耐久已更新 |
| `OutOfBounds` | 增删或伤害坐标超出 `0..15`，改用有效坐标 |
| `Occupied` | 添加目标已占用；改选空格，或明确移除原体素后再添加 |
| `Empty` | 移除或伤害目标本来就是空格，无体素可操作，也不会创建实例 |
| `InvalidDamage` | 伤害值不是正整数，例如 `0` 或负数；世界不变 |
| `UnknownDefinition` | ID 格式合法，但当前内容目录没有该定义；检查拼写和内容加载诊断 |
| `DisallowedOrientation` | 编码是合法正交朝向，但该定义不允许；改用其允许的方向 |
| `Usage: voxel ...` | 子命令、参数个数、整数、ID 格式或朝向编码不合法；按本手册的完整语法重新输入 |

格式校验失败时不执行世界命令。例如缺少朝向、ID 使用大写、朝向写成 `90` 或 `px_px`，得到的是用法提示，而不是 `UnknownDefinition` / `DisallowedOrientation`。

格式全部合法后，添加按边界、占用、定义存在性、定义允许朝向的顺序检查；伤害按伤害值、边界、占用的顺序检查。一次只返回首先遇到的问题。例如在已占用格测试未知定义会先得到 `Occupied`，应选择空格验证目标错误。多目标伤害先校验全部目标，任一项失败则整件事失败。所有失败增删和伤害均保留原世界状态。

`voxel` 的返回文本（包括失败状态）统一以 `info` 级别写入日志，不能仅根据日志颜色或严重等级判断操作是否成功。

## 常用操作示例

### 验证移除、重加和重复占格

先进入体素开发场景，逐行执行：

```text
loglevel info
voxel reset
voxel query 4 4 4
voxel remove 4 4 4
voxel add 4 4 4 core:iron-ore px_py
voxel add 4 4 4 core:iron-ore px_py
```

原始定义下应依次观察到初始 512 格、移除后 511 格、重加后 512 格/耐久 100，最后一次为 `Occupied`，仍为 512 格。

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

前两格删除、第三格耐久 75，总数 510。把三组坐标和伤害值换成其他枚举顺序，最终占用、耐久和可见面相同。

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

## 相关快捷键与当前边界

体素场景中关闭控制台后，可用 `Delete` 移除固定目标 `(4,4,4)`，`Insert` 在该格添加默认朝向的 `core:iron-ore`，`R` 重置世界，`Esc` 返回主菜单。它们是场景快捷键，不是控制台命令；`quit` 则退出整个游戏。退出场景后再次进入会创建新的开发世界。

目前没有独立的旋转、保存、撤销、列车或铁路控制台命令，也没有通过 `voxel query` 更改 HUD 选中坐标的功能。

## 维护与实现依据

每个可执行命令各用一个文件，`voxel` 子命令也分别维护。新增、修改或移除命令时，同步更新对应文件和本页索引；共享参数、朝向编码与组合示例统一保留在本页。每份命令文档应覆盖语法、参数、使用方法、结果和限制，尚未交付的命令明确标注。

基础命令与输入行为见 [ConsoleCommands.cs](../../Console/ConsoleCommands.cs)、[GameConsole.cs](../../Ui/GameConsole.cs)。体素命令实现为 `Console/VoxelConsoleCommands.cs`，坐标、ID、朝向与伤害契约位于 `Domain/Voxels/`；体素显示与快捷键位于 `Ui/VoxelDeveloperScene.cs`。
