# 基础贯通示例

`basic-through/recipe.json` 固定生成配置身份、种子、生成器版本、存档元数据、命令位置和目标位置。它使用仓库已提交的 `builtin/core`，执行 `train.replace_run_plan`、`train.start` 和 `train.stop`。命令名和计划结构沿用核心输入格式 2：`plan` 包含 `loop` 与 `stops`，站点只支持 `{kind:"waypoint",node}`。工具只解析这三种命令，其他种类明确拒绝；命令裁决和模拟推进均由真实 Core 工厂执行。

在仓库根目录构建并运行：

```powershell
dotnet build Tools/Cli/Train.Cli.csproj
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll run . examples/basic-through/recipe.json TestResults/run.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll run . examples/basic-through/recipe.json TestResults/repeated.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll compare TestResults/run.json TestResults/repeated.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll replay . examples/basic-through/recipe.json TestResults/replayed.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll make . examples/basic-through/recipe.json TestResults/remade.save.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll check . examples/basic-through/recipe.json examples/basic-through/save.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll save . examples/basic-through/recipe.json TestResults/prefix.save.json 150
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll resume . examples/basic-through/recipe.json TestResults/prefix.save.json TestResults/resumed.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll compare TestResults/run.json TestResults/resumed.json
```

输出目录须已存在，制作存档要求目标文件不存在。`run` 输出模拟位置、权威检查点、`CoreInputId` 及运行身份；`compare` 先核对 `CoreInputId` 和当前构建／运行环境，再由核心比较检查点，失败返回非零退出码并报告首个差异位置与字段。它也支持从连续运行中已有的检查点位置开始比较恢复运行的完整后缀，上例以 150:0 为保存边界。`replay` 使用本次运行的录制及内容视图逐检查点核对，不读写复现包。

工具只加载内置内容。PNG 端口支持当前资产使用的 8 位 RGBA、非交错编码，校验签名、块 CRC、头部、解压长度；无效或不支持的 PNG 返回 `CannotDecode`，GLB 返回 `UnsupportedContent`。这不替代真实 Godot 资产解码或视觉验证。

`basic-through/save.json` 是推进到 200:0 的普通格式 6 存档。`make` 推进到配方目标后保存，`save` 在指定步的序号 0 边界保存；该位置的命令由 `resume` 执行。保存同时捕获核心状态与内容依赖，UUID、名称和保存时间取配方固定值。容器编解码、临时文件读回校验及发布直接链接宿主同份源码；恢复依次读取容器、解码核心和内容、准备恢复内容、准备核心、激活后继续配方。

Godot 开发构建（编辑器运行和调试导出）的主菜单提供“从示例开始”，发布构建隐藏此入口。游戏从 `res://examples/basic-through/save.json` 读取同一份普通存档，经过正常恢复流程打开暂停的宏观地图，不执行配方。恢复后的对局使用新 UUID，沿用名称“基础贯通小世界”，标记为未保存；第一次手动或自动保存才写入新 UUID 的用户存档目录，原示例保持不变。

发布导出的 `--smoke` 从 PCK 验证示例恢复、新身份、200:0、暂停和未保存状态，并检查菜单入口隐藏；不保存副本或读回。副本保存由宿主测试在临时目录使用真实文件 I/O 验证；调试导出的菜单入口另做人工验证。这些证据不替代 #298 的四模块阶段验收。

`check` 按配方重新运行，并与普通存档恢复后的检查点比较。比较前只显式核对检查点之外的 `CoreInputId`：运行取当前内容视图的身份，恢复取存档捕获的 `InputIdentity`，不同则报“配方或内容已变，需要重新制作”。种子、生成器版本、生成内容修订和配置已包含在存档 `world.source` 中，交给核心检查点比较报告首个差异字段，例如 `/world/source/world_seed`。示例一致性以检查点摘要为准，容器头部或内容载荷的无关变化不要求字节一致；正常恢复只需要 `save.json`。工具保持两个模块载荷不透明。

已提交示例的重新制作比较仅在 Windows 测试，同机跨进程比较在所有平台测试。示例一致性检查使用当前已提交的内置内容，不重跑内容制备脚本，也不宣称跨构建或跨平台确定性。篡改测试只改临时副本，已提交文件保持原样。
