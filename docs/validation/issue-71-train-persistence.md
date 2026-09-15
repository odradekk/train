# Issue 71 验证记录：运行与停车状态的保存、重启和继续模拟

日期：2026-09-15。宿主 Fedora `7.2.4-200.fc44.x86_64`，.NET SDK `10.0.111`，PowerShell 7.6.6，Godot 4.7.2 .NET（`/usr/local/bin/godot-dotnet`，`4.7.2.stable.mono.official.ed1daf0bf`）。`GODOT` 只含可执行路径。第三轮修订日志：`.godot/validation/issue-71-r3/`（不覆盖 `issue-71/`、`issue-71-r1/`、`issue-71-r2/`、`review71/`、`review71-r2/` 或 `review71-r3/`）。

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
| `dotnet test Train.Tests/Train.Tests.csproj` | 通过，384 项。含两个各自合法双车编组的真实文件拒绝，以及合法双车、空编组（格式 2）、损伤、FIFO |
| `dotnet build Train.csproj` | 通过，0 警告 0 错误 |
| `run-godot-voxel-smoke.ps1 -Slice 4` | 通过。900 步 hash=`D31A1864517FABA887E6B6A8D40B5A095D467525F6AE417C55D26A5830BE173C`；`SAVE_WRITTEN` / `RESUME_LOADED` UUID=`61713e8c-17d3-4a69-ac48-5acb21ab1a81` |
| `run-godot-voxel-smoke.ps1 -Slice 1` / `-Slice 2` / `-Slice 3` | 通过 |
| 审查探针 `.godot/validation/review71-r3/multi-probe` | `Load=InvalidDelta assemblies=1`；旧会话 `Advance=Advanced`，仅原编组 341 移动 |
| `scripts/run-godot-*.tests.ps1` harness | 未运行 |

## 复现

正式 Save 后复制第二组双车（X+40、唯一 ID、`next_entity_id=2000`）再正式 Load，应得 `InvalidDelta`，旧会话编组数仍为 1，可继续推进。空 `assemblies`（格式 2 / 前三切片）仍可 Load。

## 未执行

- 人工窗口观感与插值/跟随相机（#74）
- Windows 导出与 Windows 实机
- 断开滑行（#72）、完整断轨安全停车（#73）、资源开采（#78）
