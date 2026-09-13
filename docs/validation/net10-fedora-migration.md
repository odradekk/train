# Fedora / .NET 10 迁移验证

日期：2026-09-13。验证对象为基于 `d1cf36b0baec05118d2a65dd34f5fe50724db956` 的本次未提交工作区改动。

## 环境

- Fedora 44，AMD Ryzen 9 5900HX，Wayland 图形会话。
- .NET SDK `10.0.111`，运行时 `Microsoft.NETCore.App 10.0.11`；主项目和测试项目均为 `net10.0`，C# 语言版本保持 12。
- Godot `4.7.2.stable.mono.official.ed1daf0bf`，入口 `/usr/local/bin/godot-dotnet`。
- PowerShell `7.6.6`，安装于 `/opt/microsoft/powershell/7.6.6`，入口 `/usr/local/bin/pwsh`。使用官方 Linux x64 压缩包，安装前核对发布资产 SHA-256：`ddbc4a2d113bbd46d283cfedcbcd117a70caefd7673f41f2b4e0000badf103bc`。
- Windows x64 .NET 导出模板从本机挂载分区的 Godot `4.7.2.stable.mono` 模板目录复制到 Fedora 用户模板目录。

## 构建和领域测试

```bash
dotnet build Train.csproj --nologo
dotnet test Train.Tests/Train.Tests.csproj --nologo --no-restore --logger 'trx;LogFileName=net10-fedora.trx' --results-directory .godot/net10-validation/tests
```

主项目构建零警告、零错误。最终领域测试 307 项全部通过，零失败、零跳过。

首次运行暴露了 Windows 独占文件锁不能在 Unix 上阻止替换的测试假设。Windows 继续验证锁定目标文件导致的替换失败；Unix 使用候选文件路径被目录占用来触发真实写入失败，并验证原存档和会话未变化。目录链接用例在 Unix 创建符号链接，在 Windows 保留 junction。

## PowerShell 脚本自测

四个 `scripts/run-godot-*.tests.ps1` 通过 `pwsh -NoProfile -File` 执行。子进程复用当前 PowerShell 7；Unix 假程序使用可执行 shell 脚本，Windows 使用批处理或 SDK 构建的 GUI 程序。

覆盖成功标记、缺少标记、非零退出码、缺少引擎、含空格的新导出路径、导出失败和平台限制。Windows GUI 子系统的 stdout/退出码用例在 Fedora 显式跳过，尚未在 Windows 验证。

## 真实 Godot 检查

所有命令从仓库根目录执行，先设置 `export GODOT="$(command -v godot-dotnet)"`。

| 脚本及参数（均使用 `pwsh -NoProfile -File`） | 结果 |
| --- | --- |
| `scripts/run-godot-logging-smoke.ps1` | 通过：启动、后台日志、主线程排空、退出释放 |
| `scripts/run-godot-content-smoke.ps1` | 通过：内容扫描、加载和诊断 |
| `scripts/run-godot-voxel-smoke.ps1 -Slice 1` | 通过 |
| `scripts/run-godot-voxel-smoke.ps1 -Slice 2` | 通过，包含跨进程保存/恢复 |
| `scripts/run-godot-voxel-smoke.ps1 -Slice 3` | 通过，包含制造、改装、伤害、断裂、热重载和保存/恢复 |
| `scripts/run-godot-voxel-smoke.ps1 -Slice 1 -IsolationAbort` | 通过：中止路径未改写内置资源 |
| `scripts/run-godot-voxel-observe.ps1 -Slice N -OutputPath .godot/net10-validation/observe-N.md`，N 为 1、2、3 | 三项均通过，实际图形会话截图 |
| `scripts/run-godot-voxel-observe-resume.ps1 -Slice N -OutputPath .godot/net10-validation/resume-N.md -CaptureOutputPath .godot/net10-validation/resume-N`，N 为 2、3 | 两项均通过，退出后新进程加载并截图 |
| `scripts/run-godot-voxel-baseline.ps1 -OutputPath .godot/net10-validation/baseline.md` | 通过：四组各三次记录及完成标记 |

日志 smoke 的旧断言 `application/application_ready` 与实际日志格式不符，已修正为 `[application][application_ready]` 并重新通过。

主代理查看了 `issue50-junction.png`、`issue62-resume-refit.png`、`issue62-resume-fracture.png`：区块接缝处缺口、恢复后的改装件纹理及分离车体可见，HUD 与场景状态一致。这是选定截图的检查，不代表逐帧人工操作验收。

原始日志位于 `.godot/net10-validation/logs/`，TRX、观测报告、恢复截图及基线报告均位于 `.godot/net10-validation/`。基线记录当前环境的测量值，没有据此宣称比 Windows / .NET 8 更快或逐位一致。

## Windows 导出

```bash
GODOT=/usr/local/bin/godot-dotnet pwsh -NoProfile -File scripts/run-godot-content-export-smoke.ps1 -ExportOnly -OutputPath /mnt/windows-d/train-builds/net10-fedora-20260913/train.exe
```

导出成功，日志未发现错误或警告。输出包含 `train.exe`、`train.pck` 和 `data_Train_windows_x86_64/`；导出运行时配置为 `net10.0`，随包运行时为 `Microsoft.NETCore.App 10.0.11`。

## 验证边界

- 尚未在 Windows 启动导出包，因此 Windows 内容/Mod 运行 smoke 和 GUI 进程捕获仍待验证。
- 本轮保存/恢复发生在 .NET 10 下，没有拿 .NET 8 生成的历史存档进行跨版本验收，也没有建立跨运行时逐位确定性保证。
- 历史验证文件和归档内容未修改；导出时新生成的未跟踪 UID/import 附属文件已清理。
