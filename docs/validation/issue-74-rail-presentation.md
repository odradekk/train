# Issue #74 沿轨显示插值、跟随相机与切片验收

正式报告。自动截图与 Godot 缓存在 `.godot/validation/issue74/`，不提交。本文件记录 Fedora 自动证据、人工待验步骤和 Windows 待验命令。Fedora 图形会话不等于人工确认；挂载导出不等于 Windows x64 运行时验收。

修订绑定审查评论 https://github.com/odradekk/train/issues/74#issuecomment-5679961084（R1–R3 / V1–V2）。

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
| `dotnet test Train.Tests/Train.Tests.csproj` | 420 通过 | Fedora 自动 |
| `dotnet build Train.csproj` | 成功，0 警告 | Fedora 自动 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4` | 通过，含 `DISPLAY` / `FOLLOW` / `DISPLAY_ENDED` | Fedora 自动 |
| 同上 `-Slice 1` / `2` / `3` | 回归通过 | Fedora 回归 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-observe.ps1 -Slice 4` | 通过，8 张自动 PNG | Fedora 自动截图，**不是人工确认** |
| `pwsh -NoProfile -File scripts/run-godot-voxel-baseline.ps1 -Slice 4 -OutputPath .godot/validation/issue74/baseline-report.md` | 预热 1 次 + 运行/损伤/保存恢复各 3 次 | Fedora 自动基线 |
| `pwsh -NoProfile -File scripts/run-windows-issue74-authority.ps1` | 本机非 Windows，exit 2，写出阻塞步骤 | **不是** Windows 通过 |

未运行：Windows x64 同 SHA / 同 .NET 运行时上的逐步哈希与保存分支；人工动态观感勾选。

## 1800 步直轨基线（Fedora，预热一次后记录三次）

相同环境、同一正式命令：`rail construct` / `rail assemble` / `rail start`。每种负载单独一行，不与切片 1–3 混比，也不把运行帧和静止帧比成同一种性能。

- `kind=running`：每帧 `Advance(30)` 并套用显示插值，直到 1800 步。`sim_ms` 是这些 Advance 的累计墙钟；`frame_*` 是包含推进+显示的进程帧，不是空闲呈现。完好施工体素 1032。
- `kind=damage`：单次缺口 `(110,1,15)` 的事件帧。体素 1031。`frame_ms` = `mutation_ms` + `refresh_ms`。
- `kind=saveload`：`voxel save` / 重建夹具 / `voxel load` / Refresh / 显示接续的事件帧。`file_bytes=520954`。

```
VOXEL_BASELINE_SLICE4_RUN kind=running run=1 voxels=1032 sim_ms=17939.007 frames=60 frame_avg_ms=393.898 frame_max_ms=443.682 frame_max_step=30 memory_bytes=45875051
VOXEL_BASELINE_SLICE4_RUN kind=damage run=1 voxels=1031 mutation_ms=1.527 refresh_ms=43.179 frame_ms=44.706 memory_bytes=45947627
VOXEL_BASELINE_SLICE4_RUN kind=saveload run=1 voxels=1031 save_ms=19.047 load_ms=50.025 file_bytes=520954 frame_ms=242.357 memory_bytes=47950351
VOXEL_BASELINE_SLICE4_RUN kind=running run=2 voxels=1032 sim_ms=18203.692 frames=60 frame_avg_ms=406.737 frame_max_ms=438.449 frame_max_step=240 memory_bytes=45875119
VOXEL_BASELINE_SLICE4_RUN kind=damage run=2 voxels=1031 mutation_ms=1.395 refresh_ms=44.213 frame_ms=45.607 memory_bytes=45937759
VOXEL_BASELINE_SLICE4_RUN kind=saveload run=2 voxels=1031 save_ms=8.267 load_ms=33.959 file_bytes=520954 frame_ms=210.285 memory_bytes=47917163
VOXEL_BASELINE_SLICE4_RUN kind=running run=3 voxels=1032 sim_ms=18578.004 frames=60 frame_avg_ms=411.909 frame_max_ms=445.211 frame_max_step=30 memory_bytes=45875119
VOXEL_BASELINE_SLICE4_RUN kind=damage run=3 voxels=1031 mutation_ms=1.404 refresh_ms=43.789 frame_ms=45.193 memory_bytes=45937759
VOXEL_BASELINE_SLICE4_RUN kind=saveload run=3 voxels=1031 save_ms=8.453 load_ms=28.424 file_bytes=520954 frame_ms=202.026 memory_bytes=47922131
```

尖峰定位：

- 运行 `frame_max≈438–445ms` 出现在 `frame_max_step=30`（第 1/3 次，首个 30 步块）或 `240`（第 2 次）。该帧同时做 30 步权威推进和显示套用，所以比空闲呈现大约一个数量级；不能和旧报告里暂停后 10s 静止采样的 ~11ms 比。
- 损伤事件约 45ms，其中 Refresh 约 43–44ms，`Damage` 本身约 1.4ms。
- 保存恢复事件约 202–242ms，第 1 次较高（`save_ms=19`、`load_ms=50`），随后两次 load 降到 28–34ms。该帧还包含 Reset、重新施工编组和显示接续。

离线 `sim_ms`（约 17.9–18.6s / 1800 步）只说明批量推进吞吐，不是实时 60Hz 帧预算。

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
- `observe-report.md`
- `baseline-report.md`

R2 重拍：侧向四分之三可以看到两车体、中间车钩空隙和轨面；标题/停因行写出「运行 / 支撑失效 / 前方缺口」。脚本不检查像素，也不等于动态平稳性的人工确认。审查备份仍在 `.godot/validation/review74/`。

## 人工观察步骤（待用户在窗口中确认）

从主菜单进入切片 4。关闭控制台后 `T` 启动、`Y` 停车、`P` 暂停、`F` 切换跟随。HUD 左上角换行，停因在标题下一行，不应出右边界。

1. 刚编组：侧视可见轴距/车钩空隙，无拉伸。停因「静止」。
2. `rail start` + `rail step 1`，`rail query display`：alpha 在两已完成态之间。
3. 到步 300：两车侧面与车钩可见；Mesh 只改变换。
4. `F` 跟车厢：仍能看到两车相对位置，不是只剩车尾。
5. 跨 `x=144`：连续，不像 Distance 对半混合。
6. 重置→300→毁后车钩：两编组；跟随保持所选车厢；能看出间隙变化。
7. `rail step 30` + `voxel damage 88 1 15 100`：HUD「支撑失效」，后车贴齐已提交停车。
8. 重置→300→`voxel damage 110 1 15 100`→步进停车：HUD「前方缺口」；未修拒绝启动。最后一步显示应仍能在两已完成态之间（R1）。
9. `voxel save` → 退出 → 新进程 `voxel load`：显示从已提交态接续。

请把动态观感记入审查评论。本报告不把自动 PNG / headless 标记写成人工通过。

## Windows x64 待验命令

证据种类必须写明，不能把 checkout 测试和导出 `train.exe` 混成同一次运行。

### A. 源码构建测试（Windows x64 上跑，绑定 SHA）

在 **Windows** 上检出与本报告相同的 head，安装 Godot 4.7.2 .NET、.NET 10、PowerShell 7：

```powershell
git rev-parse HEAD
dotnet --list-runtimes
$env:GODOT = "<Godot_v4.7.2-stable_mono_win64.exe>"
pwsh -NoProfile -File scripts/run-windows-issue74-authority.ps1
```

该脚本会记录 SHA / OS / SDK / 运行时 / Godot / 内容身份，并依次跑：

- `Start_at_zero_save_at_900_new_session_load_matches_uninterrupted_checkpoints`（0/300/600/900/1200/1500/1800 哈希，以及 900 保存分支）
- `Formal_save_at_330_resumes_the_same_authority_and_keeps_zero_after_coast_stop`
- `Formal_files_restore_halts_and_reject_missing_geometry_without_replacing_the_session`（含 `FirstSegmentDamageCell` 缺口停车保存）
- `scripts/run-godot-voxel-smoke.ps1 -Slice 4`

比较 `WorldSaveAuthority.Fingerprint` 与 `CaptureTrainAuthority`。几何误差不能代替原始权威。

Fedora 上跑同一脚本得到 `BLOCKED`（exit 2），这是预期，不是 Windows 通过。

### B. 导出构建（仅产物，不是本次权威运行）

```bash
export GODOT=/usr/local/bin/godot-dotnet
sha="$(git rev-parse HEAD)"
windows_build_dir="/mnt/windows-d/train-builds/issue74/${sha}"
mkdir -p "$windows_build_dir"
pwsh -NoProfile -File scripts/run-godot-content-export-smoke.ps1 -ExportOnly -OutputPath "$windows_build_dir/train.exe"
```

导出成功只证明 `train.exe` / `train.pck` 存在。要用导出构建做人工窗口观察时，另记 Windows 运行时版本，且不得把该观察写成 A 的哈希结果。

## #74 验收条件映射（审查者勾选）

1. 沿已完成态路径插值再约束求解；最多落后 1 步；无外推/回写；缺口/末端最后一步仍插值 — Fedora 自动有；人工待确认。
2. 跨缝走世界路径；固定轴距/车钩；运动 transform-only — Fedora 自动有；人工待确认。
3. 断开/身份/失支撑结束旧区间并重建；限程停车不误结束 — Fedora 自动有；人工待确认。
4. 相机跟显示姿态与车体朝向；侧向可观察车钩；断开保持选择 — Fedora 自动截图有构图；摧毁回退不在范围；**人工动态待确认**。
5. 30/60/144 与 Pause 同权威；哈希排除显示/相机 — Fedora 自动有。
6. Windows x64 每 300 步哈希与三类存档分支 — **Windows 未跑，阻塞。**
7. Fedora 单独记录，不替代 Windows — 本文件。
8. 切片 4 Godot 冒烟正式命令 — Fedora smoke 通过。
9. 人工观感与自动分开 — 自动 PNG 已重拍。**人工未确认，阻塞。**
10. 1800 步基线：运行/损伤/保存恢复分记，含事件帧 — Fedora 已按修订重跑。
11. 隔离存档/内容；命令手册覆盖 display/follow 与限程插值语义。
12. 父票 22 故事有证据映射；不关 #42；结论限于直轨一车头一车厢。

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
| 8 | 末端停止 | `VOXEL_SMOKE_SLICE4_END_STOP` + 末端最后一步插值测试 |
| 9 | 跨段连续 | 跨缝插值测试 + `issue74-cross-seam.png` |
| 10 | 派生图不改位置 | 既有 rebuild 测试；显示不写回；图代次不再单独结束插值 |
| 11 | 运行中结构伤害 | 车钩伤害 + 编组重建 |
| 12 | 独立滑行 | #72 冒烟 + 本票跟随不跳车 |
| 13 | 前方缺口截停 | `issue74-gap-stop` + 缺口最后一步插值测试 |
| 14 | 支撑失效 | `issue74-support-loss` + 拆分后续 |
| 15 | 修轨须显式启动 | 既有 #73 测试 / 手册 |
| 16 | 全毁仍可定位 | Slice 4 full-destroy 冒烟 |
| 17 | 缺引用失败 | 既有坏档 `InvalidDelta` |
| 18 | 保存运行/滑行/停车 | 900 / 330 / gap 存档测试与冒烟 |
| 19 | 沿路径插值 | `AlongRailDisplay` + 本票测试 |
| 20 | 相机跟显示姿态 | 侧向构图 `ApplyFollowCamera` |
| 21 | 帧率无关权威 | 30/60/144/Pause 测试 |
| 22 | 未覆盖弯坡/双车头 | 本报告结论限定直轨一车头一车厢 |

固定案例（施工、300、900、1800、末端、跨缝、车钩 330、缺口、支撑、全毁、读档）由 #68–#73 测试/冒烟保留，本票加上显示/相机与分负载 1800 基线。
