# 基础贯通示例

`basic-through/recipe.json` 固定生成配置身份、种子、生成器版本、存档元数据、命令位置和目标位置。它使用仓库已提交的 `builtin/core`，执行 `train.replace_run_plan`、`train.start` 和 `train.stop`。命令名和计划结构沿用核心输入格式 2：`plan` 包含 `loop` 与 `stops`，站点只支持 `{kind:"waypoint",node}`。工具只解析这三种命令，其他种类明确拒绝；命令裁决和模拟推进均由真实 Core 工厂执行。

在仓库根目录构建并运行：

```powershell
dotnet build Tools/Cli/Train.Cli.csproj
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll run . examples/basic-through/recipe.json TestResults/run.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll run . examples/basic-through/recipe.json TestResults/repeated.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll compare TestResults/run.json TestResults/repeated.json
dotnet Tools/Cli/bin/Debug/net10.0/Train.Cli.dll replay . examples/basic-through/recipe.json TestResults/replayed.json
```

输出目录须已存在。`run` 输出模拟位置、权威检查点、生成输入及运行身份；`compare` 先核对输入和当前构建／运行环境，再由核心比较检查点，失败返回非零退出码并报告首个差异位置与字段。`replay` 使用本次运行的录制及内容视图逐检查点核对，不读写复现包。

工具只加载内置内容。PNG 端口支持当前资产使用的 8 位 RGBA、非交错编码，校验签名、块 CRC、头部、解压长度；无效或不支持的 PNG 返回 `CannotDecode`，GLB 返回 `UnsupportedContent`。这不替代真实 Godot 资产解码或视觉验证。

普通存档的制作、恢复和已提交示例的重新制作检查等待 #321 的格式 6 容器接入；当前目录中的配方还不是完整示例存档交付。后续示例一致性检查使用当前已提交的内置内容，不重跑内容制备脚本，也不宣称跨构建或跨平台确定性。
