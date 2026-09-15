# Issue 71 验证记录：运行与停车状态的保存、重启和继续模拟

日期：2026-09-15。宿主 Fedora `7.2.4-200.fc44.x86_64`，.NET SDK `10.0.111`，PowerShell 7.6.6，Godot 4.7.2 .NET（`/usr/local/bin/godot-dotnet`，`4.7.2.stable.mono.official.ed1daf0bf`）。`GODOT` 只含可执行路径。第二轮修订日志：`.godot/validation/issue-71-r2/`（不覆盖 `.godot/validation/issue-71/`、`issue-71-r1/`、`review71/` 或 `review71-r2/`）。

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
| `dotnet test Train.Tests/Train.Tests.csproj` | 通过，384 项。含单成员+空连接双字段坏档拒绝，以及合法双车恢复、损伤占格、同一步 FIFO |
| `dotnet build Train.csproj` | 通过，0 警告 0 错误 |
| `run-godot-voxel-smoke.ps1 -Slice 4` | 通过（真实 Godot，双进程）。900 步 hash=`D31A1864517FABA887E6B6A8D40B5A095D467525F6AE417C55D26A5830BE173C`；`SAVE_WRITTEN` / `RESUME_LOADED` UUID=`cbcf1a61-dad1-4b3a-b718-03301ed4f1db` |
| `run-godot-voxel-smoke.ps1 -Slice 1` / `-Slice 2` / `-Slice 3` | 通过 |
| 审查探针 `.godot/validation/review71-r2/additional-probe` | `single_member_running: Load=InvalidDelta oldStatePreserved=True` |
| `scripts/run-godot-*.tests.ps1` harness | 未运行。伪引擎 harness 不覆盖 voxel smoke |

## 复现

独立用户数据。切片 4：`rail construct` / `rail assemble` / `rail start 0` / `rail step 900` / `voxel save`，退出进程，新进程 `voxel load` 同一 UUID 后继续。坏档：正式 Save 后只把 `vehicle_ids` 改成 `[active_locomotive_id]` 且 `connections=[]`（车辆列表不动），再正式 Load，应得 `InvalidDelta`，旧会话可继续 `AdvanceSimulation`。

## 未执行

- 人工窗口观感与插值/跟随相机（#74）
- Windows 导出与 Windows 实机
- 断开滑行（#72）、完整断轨安全停车（#73）、资源开采（#78）
