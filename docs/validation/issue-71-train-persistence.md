# Issue 71 验证记录：运行与停车状态的保存、重启和继续模拟

日期：2026-09-15。宿主 Fedora `7.2.4-200.fc44.x86_64`，.NET SDK `10.0.111`，PowerShell 7.6.6，Godot 4.7.2 .NET（`/usr/local/bin/godot-dotnet`，`4.7.2.stable.mono.official.ed1daf0bf`）。`GODOT` 只含可执行路径。本轮修订日志：`.godot/validation/issue-71-r1/`（不覆盖 `.godot/validation/issue-71/` 或 `.godot/validation/review71/`）。

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
| `dotnet test Train.Tests/Train.Tests.csproj` | 通过，384 项。含审查修订新增的真实文件坏候选拒绝（负距离、空车钩、停车带速度、空占格、错误轨枕数）以及 `scheduled_sequence` 越界拒绝 / 等于事务计数的合法边界 / 恢复后同一步 FIFO |
| `dotnet build Train.csproj` | 通过，0 警告 0 错误 |
| `run-godot-voxel-smoke.ps1 -Slice 4` | 通过（真实 Godot，双进程）。900 步 hash=`D31A1864517FABA887E6B6A8D40B5A095D467525F6AE417C55D26A5830BE173C`；`SAVE_WRITTEN` / `RESUME_LOADED` UUID=`2ae58166-37b2-460f-9812-d043b75a9303`；继续到 1800、停车、末端停车、再保存 |
| `run-godot-voxel-smoke.ps1 -Slice 1` / `-Slice 2` / `-Slice 3` | 通过 |
| 审查探针 `.godot/validation/review71/probe` | 六项坏档均为 `InvalidDelta` 且 `oldStatePreserved=True`。`scheduled_sequence=MaxValue` 拒绝后，旧会话上 `start@301` 再 `stop@301` 的队列为 `True#1,False#2`，推进后 `running=False` |
| `scripts/run-godot-*.tests.ps1` harness | 未运行。伪引擎 harness 不覆盖 voxel smoke |

## 复现

独立用户数据。切片 4：`rail construct` / `rail assemble` / `rail start 0` / `rail step 900` / `voxel save`，退出进程，新进程 `voxel load` 同一 UUID 后 `rail step 900` 到 1800 并 `rail stop`。坏档：对正式 Save 后的 JSON 只改一项（`distance=-1`、`connections=[]`、`running=false` 且速度仍为 4、`occupancy=[]`、`sleeper_count=0`、`scheduled_sequence=18446744073709551615` 或大于 `transaction_count`），再正式 `voxel load`，应得 `InvalidDelta` 且旧会话与计数不变。合法损伤占格与当前动力适配仍可 Load。

## 未执行

- 人工窗口观感与插值/跟随相机（#74）
- Windows 导出与 Windows 实机
- 断开滑行（#72）、完整断轨安全停车（#73）、资源开采（#78）
