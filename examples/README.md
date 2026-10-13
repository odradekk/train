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

输出目录须已存在，制作存档要求目标文件不存在。`run` 输出模拟位置、权威检查点、生成输入及运行身份；`compare` 先核对输入和当前构建／运行环境，再由核心比较检查点，失败返回非零退出码并报告首个差异位置与字段。它也支持从连续运行中已有的检查点位置开始比较恢复运行的完整后缀，上例以 150:0 为保存边界。`replay` 使用本次运行的录制及内容视图逐检查点核对，不读写复现包。

工具只加载内置内容。PNG 端口支持当前资产使用的 8 位 RGBA、非交错编码，校验签名、块 CRC、头部、解压长度；无效或不支持的 PNG 返回 `CannotDecode`，GLB 返回 `UnsupportedContent`。这不替代真实 Godot 资产解码或视觉验证。

`basic-through/save.json` 是推进到 200:0 的普通格式 6 存档。`make` 推进到配方目标后保存，`save` 在指定步的序号 0 边界保存；该位置的命令由 `resume` 执行。保存同时捕获核心状态与内容依赖，UUID、名称和保存时间取配方固定值。容器编解码、临时文件读回校验及发布直接链接宿主同份源码；恢复依次读取容器、解码核心和内容、准备恢复内容、准备核心、激活后继续配方。

`save.input.json` 记录制作时的生成配置、种子、生成器版本和 `CoreInputId` 等输入身份，使用工具运行结果已有的输入结构。`check` 先核对该身份与当前解析的输入，不同则报“配方或内容已变，需要重新制作”；相同才重新运行配方并与普通存档恢复后的检查点比较。输入记录用于示例比较，普通存档恢复只需要 `save.json`。工具保持两个模块载荷不透明。

已提交示例的重新制作比较仅在 Windows 测试，同机跨进程比较在所有平台测试。示例一致性检查使用当前已提交的内置内容，不重跑内容制备脚本，也不宣称跨构建或跨平台确定性。篡改测试只改临时副本，已提交文件保持原样。
