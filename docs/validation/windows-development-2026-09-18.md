# Windows 开发环境准备（2026-09-18）

源码基线：`0fd3d0447c73684a53278b941a0843a14de42258`，项目版本 `0.0.14`。本次修改开发说明和 VS Code 配置，游戏源码及 SDK 锁定版本保持原样。

## 本机环境

| 项目 | 实测结果 |
| --- | --- |
| Windows | x64，版本 `10.0.26200` |
| PowerShell | `7.6.6` |
| .NET SDK | `10.0.111`，安装到 `C:\Program Files\dotnet` |
| .NET Host / Runtime | `10.0.11` |
| Godot .NET | `4.7.2.stable.mono.official.ed1daf0bf` |
| Windows 导出模板 | `%APPDATA%\Godot\export_templates\4.7.2.stable.mono` |

从 [Microsoft .NET 发布元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)取得 x64 安装程序，核对 SHA-512，并确认 Authenticode 签名有效、签名者为 Microsoft Corporation。安装程序退出码为 0，无需重启 Windows。原有 SDK `9.0.318`、`10.0.400-preview.0.26322.102` 保留；在项目目录执行 `dotnet --version` 已返回 `10.0.111`。

已设置用户级环境变量：

```text
GODOT=D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe
GODOT_EDITOR=D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe
```

已有终端、IDE 和代理宿主需重新启动以继承这些变量。日常命令见 [Windows 开发与验证](../implement-sop.md#windows-development-and-validation)。VS Code 启动配置已改为运行 Godot，任务提供构建、测试和 Windows 导出；断点调试需要安装 C# 扩展。

## 旧验收环境处理

根目录的两个 Git 忽略文件已校验备份后停用：

- `NuGet.Config`：原先只读取 `../train-issue74-environment/packages`；现使用正常 NuGet 配置，已成功从 nuget.org 还原依赖。
- `override.cfg`：原先将游戏数据指向 `train-issue74-acceptance-fac5414`；正常开发恢复使用 `%APPDATA%\Godot\app_userdata\Train`。

备份目录：`C:\Users\s1n19\AppData\Local\Train\development-backups\20260918-201019`。旧验收存档、相邻的 `train-issue74-environment` 目录以及原有未跟踪验证文档/压缩包均保留。

本次 Godot 导入额外生成的 45 个 `.cs.uid` 和文档图片 `.import` 文件已移至上述备份目录的 `generated-import-files`，保留相对路径，不计入本次配置改动；再次扫描项目时 Godot 会重新生成这些元数据。

## 验证

以下检查使用系统安装的 SDK，不依赖旧验收目录里的便携 SDK。

| 检查 | 结果 |
| --- | --- |
| `dotnet build Train.sln --no-incremental --nologo` | 通过，0 警告、0 错误 |
| `dotnet test Train.Tests/Train.Tests.csproj --no-restore --no-build` | 331 通过、0 失败、0 跳过 |
| Godot `--headless --path . --editor --import` | 通过；首次导入重新生成了缺失的 UID 和文档图片导入元数据 |
| `run-godot-voxel-smoke.ps1 -Slice 4` | 通过，包含轨道建造、损伤、修复和菜单重新进入 |
| `run-godot-content-export-smoke.ps1 -OutputPath <绝对路径>` | 导出及 Windows 原生可执行文件运行通过 |
| `run-godot-mods-export-smoke.ps1`，使用同一导出程序 | 通过，验证空 Mod 目录下的内容加载 |
| VS Code 配置 JSON 解析、`git diff --check` | 通过 |

导出程序：`D:\Code\train\builds\windows-development-20260918\train.exe`。运行或复制时应保留整个导出目录。

首次导出命令使用带 `/` 的相对输出路径：导出成功，但验证脚本的 Windows 进程启动器报“系统找不到指定的文件”。对同一程序进行对照，绝对路径启动成功；已改用绝对路径并重新验证，开发说明和 VS Code 导出任务均采用绝对路径。

日志和 TRX：`.godot/validation/windows-development-20260918/`。安装日志：`%TEMP%\train-windows-dev-20260918\install.log`。本次检查以自动化无界面运行为准，未进行人工画面验收或 VS Code 断点调试。

## 同步上游后的复验

随后同步上游 `master` 的 35 个提交，源码基线更新为 `fefc6bec8b1cb6ca804935788f22d237d8d0803b`，项目版本为 `0.0.21`。本次开发环境改动应用到新基线时没有冲突。

- 重新构建整个解决方案：0 警告、0 错误。
- 全量测试：422 通过、0 失败、0 跳过，耗时 15 分 17 秒。
- Godot 资源导入通过。
- 切片 4 五进程检查通过，覆盖写入、滑行恢复、缺口停车恢复、支撑失效恢复和全毁轨段恢复。
- Windows 导出及内容、Mod 运行检查通过；新程序位于 `builds/windows-upstream-sync-20260918/train.exe`。

本轮日志和 TRX 位于 `.godot/validation/windows-upstream-sync-20260918/`。此前 `0.0.14` 的记录保留为环境准备时的历史结果。
