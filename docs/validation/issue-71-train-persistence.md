# Issue 71 验证记录：运行与停车状态的保存、重启和继续模拟

日期：2026-09-15。宿主 Fedora `7.2.4-200.fc44.x86_64`，.NET SDK `10.0.111`，PowerShell 7.6.6，Godot 4.7.2 .NET（`/usr/local/bin/godot-dotnet`，`4.7.2.stable.mono.official.ed1daf0bf`）。`GODOT` 只含可执行路径。环境快照：`.godot/validation/issue-71/env.txt`。原始日志：`.godot/validation/issue-71/slice1-smoke.log` … `slice4-smoke.log`。不覆盖 `.godot/validation/issue-70/` 或更早路径。

本记录是自动化命令与状态/哈希证据，不是人工观感。未做 Windows 导出或实机验证，不声称跨 OS / .NET 逐位一致。

## 命令

```text
export GODOT=/usr/local/bin/godot-dotnet
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 1
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 2
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 3
```

切片 4 使用独立用户数据前缀 `train-issue71-smoke-`（进程退出后由脚本删除）。切片 2 / 3 仍用既有 `train-issue53-smoke-` / `train-issue62-smoke-`。

## 结果

| 检查 | 结果 |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 通过，382 项。新增 `TrainPersistenceTests` 11 项：停车往返、0→900 保存 / 新会话 Load / 1800 检查点与稳定哈希、同一步命令 FIFO、开发停车与末端停车、损伤占格不重拟合、JSON 连续量往返与 NaN 拒绝、缺轨段/未知格式/坏 JSON/不兼容基底、动力适配与非法内容拒绝、高速正式文件 Load、Host 余量非权威 |
| `dotnet build Train.csproj` | 通过，0 错误 |
| `run-godot-voxel-smoke.ps1 -Slice 4` | 通过（真实 Godot，双进程）。进程 1：`STEP300` loco=`95.999999999999318` carriage=`86.999999999999318` speed=`4`；`TRANSFORM_ONLY rebuilt=0`；`STEP900` loco=`135.99999999999704` speed=`4` hash=`D31A1864517FABA887E6B6A8D40B5A095D467525F6AE417C55D26A5830BE173C`；`SAVE_WRITTEN id=e5010556-73a0-48d8-b3b5-68c0023a18cf bytes=521043 step=900`。进程 2：同一 UUID `RESUME_LOADED` front/speed/hash 与进程 1 的 900 步一致；`CROSS_SEAM` segment=2 distance=`51.999999999993634` front=`195.99999999999363`；`STEP1800`；`STOPPED`；`PAUSED step=1800`；`RESUME_TRANSFORM_ONLY rebuilt=0 meshes=2`；`END_STOP front=272 speed=0`；接触损毁与局部刷新 / 缺口 `16..110,111..144`；`RESUME_SAVED` 同一 UUID |
| `run-godot-voxel-smoke.ps1 -Slice 1` | 通过（真实 Godot） |
| `run-godot-voxel-smoke.ps1 -Slice 2` | 通过（真实 Godot，含跨进程保存/恢复） |
| `run-godot-voxel-smoke.ps1 -Slice 3` | 通过（真实 Godot，含制造/改装/伤害/热重载/跨进程恢复） |
| `scripts/run-godot-*.tests.ps1` harness | 未运行。本票只改 `run-godot-voxel-smoke.ps1` 的切片 4 双进程路径与标记；四个伪引擎 harness 测的是 content/mods/logging 脚本，不是真实游戏，也不覆盖 voxel smoke |

## 复现

独立用户数据。切片 4：`rail construct` / `rail assemble` / `rail start 0` / `rail step 900` / `voxel save`，退出进程，新进程 `voxel load` 同一 UUID 后 `rail step 900` 到 1800 并 `rail stop`。与不中断分支在 300/600/900/1200/1500/1800 比较 `CaptureTrainAuthority` 与 `WorldSaveAuthority.Fingerprint`。开发停车或末端停车恢复后为零速，须再 `rail start`。高速夹具：先加载允许目标 600 的测试动力，再正式 Load `x=80` 或 `x=140` 的文件；无产品调参入口，无 Load 后改内存后门。

## 未执行

- 人工窗口观感与插值/跟随相机（#74）
- Windows 导出与 Windows 实机
- 断开滑行（#72）、完整断轨安全停车（#73）、资源开采（#78）
