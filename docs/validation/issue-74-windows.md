# #74 Windows x64 验证记录

2026-09-16，受测提交 `fac541484ab4bdd3b94dfb09248896e3d650200b`，对应 [PR #90](https://github.com/odradekk/train/pull/90)。Windows 自动验证通过，满足 [#74](https://github.com/odradekk/train/issues/74) AC6。结论限定当前直轨、一车头一车厢。人工动态观感 AC9 未执行，汇总 AC12 仍须等待该证据；本记录不代表整个 Issue 技术验收通过。

本次验证源码构建的测试程序集和真实 Godot 开发入口。没有运行或重新导出旧目录中的 `train.exe`。游戏源码、既有测试、版本及受审提交均未修改。

| 环境 | 实际值 |
| --- | --- |
| OS / 架构 | Microsoft Windows 10.0.26200 / x64 |
| .NET SDK | 10.0.111，使用 `D:\Code\train-issue74-environment\tools\dotnet\dotnet.exe` |
| 实际 testhost 运行时 | `.NET 10.0.11` / `win-x64`，由进程内 StartupHook 记录，非从运行时列表推测 |
| Godot | `4.7.2.stable.mono.official.ed1daf0bf`，`D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe` |
| PowerShell | 首次自动入口 7.6.5；整理证据时系统已更新为 7.6.6。受测 .NET 进程仍固定为 10.0.11 |
| 生成器 | `slice-4-straight-rail-v1` |
| 隔离 | 使用既有独立验收配置；Godot 冒烟使用独立临时用户目录，未载入用户 Mods |

系统默认 SDK 不满足 `global.json`，本次复用已部署的离线 SDK；未由本次任务安装或升级系统 .NET。

| 检查 | 结果 |
| --- | --- |
| 离线还原、项目编译、Godot 资源导入 | 通过，项目编译 0 警告、0 错误 |
| 原 Windows 入口的三项测试 | 3/3 通过；另外以同一程序集、`--no-build --no-restore` 运行并保存 TRX，仍为 3/3，无跳过 |
| 切片 4 真实 Godot 冒烟 | 五进程通过：写入、滑行恢复、缺口停车恢复、支撑失效恢复、全毁轨段恢复 |
| 两次独立进程固定输入 | 各生成 52 行检查点记录；两个 CSV 字节完全相同 |
| 运行中第 900 步保存 | 900/1200/1500/1800，以及 1800 显式停车后，恢复分支与不中断分支原始哈希一致 |
| 第 330 步滑行保存 | 330/540/600/900/1200/1500/1800，恢复与不中断分支一致；保存时仍在滑行，540 步起速度满足既有零速容差 |
| 首次缺口停车保存 | 第 510 步 `Speed=0, HaltReason=ForwardGap`；保存点及 600/900/1200/1500/1800 全部一致 |

补充驱动位于证据包的 `supplement/`，只引用本次已构建的 `Train.Tests.dll`，通过反射复用原有内部 fixture 工厂，不复制或修改模拟实现。每个进程内部另建两条独立正常运行轨迹，在 0/300/600/900/1200/1500/1800 逐点比较；三类存档通过正式文件 Save/Load 在新会话恢复。真实跨进程恢复另由 Godot 五进程冒烟验证。

所有检查点直接比较 `WorldSaveAuthority.Fingerprint` 原始 SHA-256，没有数值取整或几何误差替代。附加快照比较仅排除 `Rails.DerivedGraphCurrent` 这一派生缓存标记，其余字段仍比较。

初次补充运行曾因直接比较完整诊断快照而失败，已确认为辅助断言误报：第 510 步保存前、保存后和读档后的原始哈希均为 `DBAE9AB3916C0E3F41AE95727B1127C712AF43526F457D4FDCEC2BA7DAF39475`。唯一差异是缓存标记从 false 变为 true；重建派生图后快照相等，原始哈希不变。AC 明确排除派生图，因此最终驱动只在局部快照副本中统一此标记，不修改实际会话。修正后重新构建驱动并完成两个进程全流程；初始失败记录与最小诊断探针均保留。

正常运行轨迹的原始权威哈希如下。每行在独立重复轨迹中一致；900 起同时与保存恢复分支一致。

| 步 | SHA-256 |
| --- | --- |
| 0 | `4DE7E9AA8758C35A804C5B28152A5FC0343266CE9ED0818F48BCDBE02E6F993A` |
| 300 | `820031B7D638471871A228E095FDC48FA423AA7DEB72A84E2FF51C0DF9304BDB` |
| 600 | `EEF4805BCB23CC8509F6B73E6F326F14CC46102F15C87C3DA705411BFE22EC03` |
| 900 | `D31A1864517FABA887E6B6A8D40B5A095D467525F6AE417C55D26A5830BE173C` |
| 1200 | `99443563FCAB8AE031E6CA2619054A8663AA3E15A09874FE9D894AFE67EED13A` |
| 1500 | `BF816806BF055EFB68BC7C76215C56A0A4A64ACF2B2756A3F7C021D773C31F2F` |
| 1800 | `B5A464F17D42AF439EE9BEB59BF1F528059FA1099CDBD277ACB4CE4EC87E9524` |

测试 fixture 的 voxel/archetype/composite ID 与语义修订按 ordinal 排序、LF 连接后 SHA-256 为 `8C6DED2F785A2051FE1C8099F03C18F091CE8C1E4A4345C5EAB7AD5FD8D2020E`，所有会话及两进程一致。其内容由原测试 fixture 和仓库内置定义共同提供，不冒充完整游戏内容；Godot 使用的完整内置内容单独记录逐文件 SHA-256、实际加载日志，执行前后文件校验不变。

构建身份：

- `Train.dll`：`8803EEA90746295548D3F6300FD9E5D97BAAC989D1A30DF95F5BAB38796BCB5F`。
- `Train.Tests.dll`：`2C8CE855BB54C20AD177A22F82B484B1FD95B419741F048CDD5873AE680AA075`；MVID `dc241870-b10f-4f00-b4e3-8fbe5d821c37`。辅助目录中的副本与原 DLL 字节一致。
- 最终 `Supplement.dll`：`093E62437EC147882C92010FE347CCF7614BCBF0CFA85F4C04B34540F7A75029`；两个最终进程使用同一构建。
- 两份检查点 CSV：`4113D9279E6315405612BA2BDB310F4911E876958164F54596736711DB1DE7AE`。

原始证据目录：`D:\Code\train\.godot\validation\windows-issue74-20260916`。首次自动入口另保留于 `D:\Code\train-issue74-environment\results\20260916-190534-474-Auto`。首次入口的 `console.log` 未完整捕获原生子进程输出，最终以直接重定向的 `domain-tests.log`、`godot-smoke.log` 和 `windows-authority.trx` 为原始结果证据。精简归档见 [Windows 验证证据包](issue-74-windows-evidence.zip)，包含日志、运行时记录、逐步 CSV、内容身份、辅助驱动源码和诊断记录，不包含 SDK、生成缓存或玩家存档。

关键复现命令（从受测仓库根目录，在 PowerShell 7 中执行；证据包解压至上述 `.godot/validation/windows-issue74-20260916`）：

```powershell
$dotnet = 'D:/Code/train-issue74-environment/tools/dotnet/dotnet.exe'
$env:DOTNET_ROOT = Split-Path $dotnet
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = "$env:DOTNET_ROOT;$PSHOME;$env:PATH"
$env:GODOT = 'D:/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe'
pwsh -NoProfile -File D:/Code/train-issue74-environment/Run-Issue74.ps1 -Mode Auto

$evidence = '.godot/validation/windows-issue74-20260916'
& $dotnet build "$evidence/supplement/Supplement.csproj" --nologo
& $dotnet "$evidence/supplement/bin/Debug/net10.0/Supplement.dll" "$evidence/checkpoints-run1.csv"
& $dotnet "$evidence/supplement/bin/Debug/net10.0/Supplement.dll" "$evidence/checkpoints-run2.csv"
Get-FileHash "$evidence/checkpoints-run1.csv", "$evidence/checkpoints-run2.csv"
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4
```

原入口运行的测试：`Start_at_zero_save_at_900_new_session_load_matches_uninterrupted_checkpoints`、`Formal_save_at_330_resumes_the_same_authority_and_keeps_zero_after_coast_stop`、`Formal_files_restore_halts_and_reject_missing_geometry_without_replacing_the_session`。精确 TRX 复跑命令与其余执行命令见证据包 `commands.ps1.txt`。

本次未重新验证 Fedora 性能基线或前三切片，沿用同一受审提交的既有结果。未作人工动态观感确认，未提交、推送、合并或关闭 Issue，未更新版本。经用户授权，已发布 [Windows 验证评论](https://github.com/odradekk/train/issues/74#issuecomment-5696690864)，仅勾选 AC6；回读确认 AC9、AC12 仍未勾选，Issue 保持开放。
