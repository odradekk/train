# Issue #74 沿轨显示插值、跟随相机与切片验收

正式报告。自动截图与 Godot 缓存在 `.godot/validation/issue74/`，不提交。本文件记录 Fedora 自动证据、人工待验步骤和 Windows 待验命令。Fedora 图形会话不等于人工确认；挂载导出不等于 Windows x64 运行时验收。

## 环境

- Host OS: Fedora Linux 44 (`7.2.4-200.fc44.x86_64`)
- Godot: `4.7.2.stable.mono.official.ed1daf0bf`（`/usr/local/bin/godot-dotnet`）
- .NET SDK: `10.0.111`；运行时 `Microsoft.NETCore.App 10.0.11`
- PowerShell: `7.6.6`（`/usr/bin/pwsh` / `/usr/local/bin/pwsh`）
- CPU: AMD Ryzen 9 5900HX；内存 33482354688 bytes
- GPU（窗口观察）: NVIDIA GeForce RTX 3080 Laptop GPU，OpenGL 3.3 Compatibility
- 命令前缀：`export GODOT=/usr/local/bin/godot-dotnet`

## 已运行的自动命令

| 命令 | 结果 | 归类 |
| --- | --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 418 通过 | Fedora 自动 |
| `dotnet build Train.csproj` | 成功 | Fedora 自动 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4` | 通过，含 `VOXEL_SMOKE_SLICE4_DISPLAY` / `FOLLOW` / `DISPLAY_ENDED` | Fedora 自动 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 1` | 通过 | Fedora 回归 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 2` | 通过 | Fedora 回归 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 3` | 通过 | Fedora 回归 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-observe.ps1 -Slice 4` | 通过，8 张自动 PNG | Fedora 自动截图，**不是人工确认** |
| `pwsh -NoProfile -File scripts/run-godot-voxel-observe-resume.ps1 -Slice 4` | 通过，`step=900` 新进程恢复 | Fedora 自动截图，**不是人工确认** |
| `pwsh -NoProfile -File scripts/run-godot-voxel-baseline.ps1 -Slice 4` | 预热 1 次 + 记录 3 次 | Fedora 自动基线 |

未运行：Windows x64 同构建 / 同 .NET 运行时上的逐步哈希与保存分支；人工观感勾选。

## 1800 步直轨基线（Fedora，预热一次后记录三次）

相同环境、同一正式命令边界：`rail construct` / `rail assemble` / `rail start` / `Advance(1800)` / 单次缺口伤害 `(110,1,15)` / `voxel save` / `voxel load`。不与切片 1–3 不同负载混比。

- 稳定运行：`sim_ms` 为一次 1800 步 `AdvanceSimulation`
- 单次损伤：`mutation_ms` 为缺口 `Damage`；伤害后体素 1031（完好施工 1032）
- 保存恢复：`save_ms` / `load_ms` / `file_bytes=520954`
- 帧样本：暂停后 10 s 只刷新显示插值与跟随相机，不推进权威

```
VOXEL_BASELINE_SLICE4_RUN run=1 voxels=1031 sim_ms=17523.243 mutation_ms=3.599 refresh_ms=282.180 save_ms=26.307 load_ms=49.924 file_bytes=520954 stable_seconds=10.002 frame_avg_ms=11.239 frame_max_ms=103.989 memory_bytes=48120740
VOXEL_BASELINE_SLICE4_RUN run=2 voxels=1031 sim_ms=17443.890 mutation_ms=1.966 refresh_ms=137.634 save_ms=12.083 load_ms=39.714 file_bytes=520954 stable_seconds=10.007 frame_avg_ms=11.172 frame_max_ms=51.894 memory_bytes=48102056
VOXEL_BASELINE_SLICE4_RUN run=3 voxels=1031 sim_ms=17405.996 mutation_ms=3.336 refresh_ms=140.215 save_ms=8.101 load_ms=25.080 file_bytes=520954 stable_seconds=10.004 frame_avg_ms=11.165 frame_max_ms=53.731 memory_bytes=48109232
```

第 1 次记录的 `frame_max_ms=103.989` 出现在保存/恢复后的首段显示采样，第 2/3 次约 52 ms。平均帧约 11.2 ms。未把该尖峰与切片 1–3 基线比较。稳定 `sim_ms` 约 17.4–17.5 s / 1800 步，损伤与存档 I/O 远小于模拟。

## 自动观察路径（非人工）

`.godot/validation/issue74/`：

- `issue74-parked.png`
- `issue74-interpolate.png`
- `issue74-running-300.png`
- `issue74-follow-carriage.png`
- `issue74-cross-seam.png`
- `issue74-split.png`
- `issue74-support-loss.png`
- `issue74-gap-stop.png`
- `issue74-resume-running.png`
- `observe-report.md`
- `resume-observe-report.md`

脚本不检查像素。HUD 出现「支撑失效」「前方缺口」只证明正式查询文本，不代替人工看车体/车钩/相机。

## 人工观察步骤（待用户在窗口中确认）

从主菜单进入切片 4，或在本机执行 `pwsh -NoProfile -File scripts/run-godot-voxel-observe.ps1 -Slice 4` 后对照 PNG。每案先 `R` 或 `voxel reset` 再 `rail construct` / `rail assemble`（菜单进入已施工编组则不必）。关闭控制台后 `T` 启动、`Y` 停车、`P` 暂停、`F` 切换跟随。

1. 刚编组：轴距 6、车钩 1、无拉伸。相机跟车头。
2. `rail start` + `rail step 1`，再 `rail query display`：alpha 在两已完成态之间；车体不弯、不缩。
3. `rail step 299` 到步 300：前接触约 96/87，速度 4；运动中不重建车体 Mesh。
4. `rail follow vehicle <车厢 id>` 或 `F`：镜头跟所选车厢朝向；断开后不跳到前车。
5. 继续跨 `x=144`：位置连续，不像两段 Distance 对半混合。
6. 重置，跑 300，摧毁车头后车钩：两编组独立；跟随保持所选车厢。
7. 再 `rail step 30`，`voxel damage 88 1 15 100`：后车支撑失效提示，显示落到已提交停车姿态。
8. 重置，跑 300，`voxel damage 110 1 15 100`，步进至停车：停在缺口入侧；未修时 `rail start` 拒绝。
9. `voxel save` → `quit` → 新进程 `voxel load`：显示从已提交态接续，无外推。

请把观感与截图记入审查评论。本报告不勾选人工 AC。

## Windows x64 待验命令

Fedora 已核实 `/mnt/windows-d` 可写。导出（在提交后把 `<SHA>` 换成测试 head）：

```bash
export GODOT=/usr/local/bin/godot-dotnet
sha="$(git rev-parse HEAD)"
windows_build_dir="/mnt/windows-d/train-builds/issue74/${sha}"
mkdir -p "$windows_build_dir"
pwsh -NoProfile -File scripts/run-godot-content-export-smoke.ps1 -ExportOnly -OutputPath "$windows_build_dir/train.exe"
```

导出成功只证明产物存在。Windows 上必须用**同一 SHA**、Godot 4.7.2 .NET、.NET 10 运行时和同一有效内容：

```powershell
$env:GODOT = "<Godot_v4.7.2-stable_mono_win64 可执行文件>"
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4
dotnet test Train.Tests/Train.Tests.csproj --filter TrainDisplayPresentationTests
dotnet test Train.Tests/Train.Tests.csproj --filter FullyQualifiedName~Start_at_zero_save_at_900
dotnet test Train.Tests/Train.Tests.csproj --filter FullyQualifiedName~Formal_save_at_330
```

比较每 300 步权威哈希，以及运行 900 / 滑行 330 / 缺口停车三个保存分支续跑到共同步。几何误差不能代替哈希。没有 Windows 执行结果前，AC6 与「Windows 确定性」保持待验。

## #74 验收条件映射（审查者勾选）

1. 沿已完成态路径插值再约束求解；最多落后 1 步；无外推/回写 — `AlongRailDisplay` + `TrainDisplayPresentationTests` + `QueryDisplayPoses`。**Fedora 自动有；人工待确认。**
2. 跨缝走世界路径；固定轴距/车钩；运动 transform-only — 跨缝测试 + `VOXEL_SMOKE_SLICE4_TRANSFORM_ONLY`。**Fedora 自动有；人工待确认。**
3. 断开/身份/失支撑结束旧区间，事件后已提交态重建；其后仍运行的车头恢复插值 — 拆分/支撑测试 + `DISPLAY_ENDED`。**Fedora 自动有；人工待确认。**
4. 相机跟显示姿态与车体朝向；断开保持选择 — `QueryFollowedCamera` / `rail follow` / `F`。**Fedora 自动有；摧毁回退不在范围；人工待确认。**
5. 30/60/144 与 Pause 同权威；哈希排除显示/相机 — `Host_schedules_and_pause_*` + 既有 `WorldSaveAuthority.Fingerprint`。**Fedora 自动有。**
6. Windows x64 每 300 步哈希与三类存档分支 — Fedora 已有 `TrainPersistenceTests` / `TrainCouplerCoastTests` / 切片 4 冒烟。**Windows 未跑，阻塞。**
7. Fedora 单独记录，不替代 Windows — 本文件。**遵守。**
8. 切片 4 Godot 冒烟正式命令 — Slice 4 smoke 通过。**Fedora 自动有。**
9. 人工观感与自动分开 — 自动 PNG 已生成。**人工未确认，阻塞。**
10. 1800 步基线三次 — 见上表。**Fedora 自动有。**
11. 隔离存档/内容；命令手册覆盖 `rail query display` / `rail follow`。**已做。**
12. 父票 22 故事有证据映射；不关父票；结论限于直轨一车头一车厢。**映射如下；不关 #42。**

## #42 User Stories 证据

| # | 故事 | 证据 |
| --- | --- | --- |
| 1 | 正式施工 | 既有 `rail construct` + Slice 4 smoke |
| 2 | 占格 | 既有 occupancy 冒烟 / 测试 |
| 3 | 明确连接 | 既有 connections 测试 |
| 4 | 一车头一车厢 | `rail assemble` + 本票显示采样两车 |
| 5 | 刚性与车钩 | `AssertDisplayConstraints`；多 alpha |
| 6 | 正式起停 | `rail start` / `stop` / `T` / `Y` |
| 7 | 拒绝倒行 | `VOXEL_SMOKE_SLICE4_REVERSE_REJECTED` |
| 8 | 末端停止 | `VOXEL_SMOKE_SLICE4_END_STOP` |
| 9 | 跨段连续 | 跨缝插值测试 + `issue74-cross-seam.png` |
| 10 | 派生图不改位置 | 既有 rebuild 测试；显示不写回 |
| 11 | 运行中结构伤害 | 车钩伤害 + 编组重建 |
| 12 | 独立滑行 | #72 冒烟 + 本票跟随不跳车 |
| 13 | 前方缺口截停 | `issue74-gap-stop` + 既有 gap 测试 |
| 14 | 支撑失效 | `issue74-support-loss` + 拆分后续 |
| 15 | 修轨须显式启动 | 既有 #73 测试 / 手册 |
| 16 | 全毁仍可定位 | Slice 4 full-destroy 冒烟 |
| 17 | 缺引用失败 | 既有坏档 `InvalidDelta` |
| 18 | 保存运行/滑行/停车 | 900 / 330 / gap 存档测试与冒烟 |
| 19 | 沿路径插值 | `AlongRailDisplay` + 本票测试 |
| 20 | 相机跟显示姿态 | `QueryFollowedCamera` + `ApplyFollowCamera` |
| 21 | 帧率无关权威 | 30/60/144/Pause 测试 |
| 22 | 未覆盖弯坡/双车头 | 本报告结论限定直轨一车头一车厢 |

固定案例（施工、300、900、1800、末端、跨缝、车钩 330、缺口、支撑、全毁、读档）由 #68–#73 测试/冒烟保留，本票加上显示/相机与 1800 基线。
