# Issue #74 沿轨显示插值、跟随相机与切片验收

正式报告。自动截图与 Godot 缓存在 `.godot/validation/issue74/`，不提交。本文件记录 Fedora 自动证据、人工待验步骤和 Windows 待验命令。Fedora 图形会话不等于人工确认；挂载导出不等于 Windows x64 运行时验收。

修订绑定审查评论 https://github.com/odradekk/train/issues/74#issuecomment-5680440281（N1–N3 / V1–V2）。R1 已通过，不回退；R2 侧向构图与 HUD 换行保留。

## 环境

- Host OS: Fedora Linux 44 (`7.2.4-200.fc44.x86_64`)
- Godot: `4.7.2.stable.mono.official.ed1daf0bf`（`/usr/local/bin/godot-dotnet`）
- .NET SDK: `10.0.111`；本机选用运行时 `Microsoft.NETCore.App 10.0.11 [/usr/lib64/dotnet/shared/Microsoft.NETCore.App]`
- PowerShell: `7.6.6`（`/usr/bin/pwsh` / `/usr/local/bin/pwsh`）
- CPU: AMD Ryzen 9 5900HX；内存 33482354688 bytes
- GPU（窗口观察）: NVIDIA GeForce RTX 3080 Laptop GPU，OpenGL 3.3 Compatibility
- 内容身份：内置 `core` 体素/组合；世界生成器 `slice-4-straight-rail-v1`
- 命令前缀：`export GODOT=/usr/local/bin/godot-dotnet`

## 已运行的自动命令

| 命令 | 结果 | 归类 |
| --- | --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj --filter FullyQualifiedName~TrainDisplayPresentationTests` | 12 通过（含构图连续、运行/滑行/停止） | Fedora 自动 |
| `dotnet test Train.Tests/Train.Tests.csproj` | 422 通过 | Fedora 自动 |
| `dotnet build Train.csproj` | 成功，0 警告 | Fedora 自动 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4` | 通过；`CAMERA_*_SPLIT` 偏移 4，Camera3D 朝向不变、位置差约 0.07 格；HUD 滑行/停止 | Fedora 自动 |
| 同上 `-Slice 1` / `2` / `3` | 回归通过 | Fedora 回归 |
| `pwsh -NoProfile -File scripts/run-godot-voxel-observe.ps1 -Slice 4` | 通过，8 张自动 PNG；断钩 HUD「速 4 滑行」 | Fedora 自动截图，**不是人工确认** |
| `pwsh -NoProfile -File scripts/run-godot-voxel-baseline.ps1 -Slice 4 -OutputPath .godot/validation/issue74/baseline-report.md` | 预热 1 次 + 运行/损伤/保存恢复各 3 次；事件行含 `work_ms` 与 `frame_interval_ms` | Fedora 自动基线，headless + 每帧 30 步 |
| `pwsh -NoProfile -File scripts/run-windows-issue74-authority.ps1` | 本机非 Windows，exit 2，写出阻塞步骤与本机 10.x 运行时/生成器身份 | **不是** Windows 通过 |

未运行：Windows x64 同 SHA / 同实际 testhost 运行时上的逐步哈希与保存分支；人工动态观感勾选。切片 1–3 冒烟本轮已重跑通过。

独立审查备份仍在 `.godot/validation/review74-r2/`，未覆盖。

## 1800 步直轨基线（Fedora headless，预热一次后记录三次）

**运行方式：** `godot-dotnet --headless`。每种负载单独一行。运行阶段每个 `_Process` 人为 `Advance(30)` 再套用显示。这些数字是 headless 批量负载的进程帧间隔，**不是**窗口实时呈现，也**不是** GPU 帧耗时。

相同正式命令：`rail construct` / `rail assemble` / `rail start`。不与切片 1–3 混比。

- `kind=running`：每帧 `Advance(30)` 并套用显示插值，直到 1800 步。`sim_ms` 是这些 Advance 的累计墙钟；`frame_*` 是统一 `_Process` 边界上的间隔。完好施工体素 1032。
- `kind=damage`：单次缺口 `(110,1,15)`。`mutation_ms` / `refresh_ms` / `work_ms` 是处理 CPU 秒表（`work_ms = mutation + refresh`）。`frame_interval_ms` 是跨过该事件所在 `_Process` 之后采样的实际帧间隔。体素 1031。
- `kind=saveload`：`voxel save` / 重建夹具 / `voxel load` / Refresh / 显示接续。`save_ms` / `load_ms` / `work_ms` 是处理 CPU；`frame_interval_ms` 是事件帧的统一间隔。`file_bytes=520954`。

```
VOXEL_BASELINE_SLICE4_RUN kind=running run=1 voxels=1032 sim_ms=17594.962 frames=60 frame_avg_ms=389.187 frame_max_ms=635.178 frame_max_step=30 memory_bytes=48068191
VOXEL_BASELINE_SLICE4_RUN kind=damage run=1 voxels=1031 mutation_ms=1.563 refresh_ms=38.108 work_ms=39.672 frame_interval_ms=94.497 memory_bytes=48135583
VOXEL_BASELINE_SLICE4_RUN kind=saveload run=1 voxels=1031 save_ms=18.407 load_ms=50.075 file_bytes=520954 work_ms=227.075 frame_interval_ms=293.274 memory_bytes=50538187
VOXEL_BASELINE_SLICE4_RUN kind=running run=2 voxels=1032 sim_ms=17689.680 frames=60 frame_avg_ms=387.298 frame_max_ms=550.333 frame_max_step=30 memory_bytes=45722267
VOXEL_BASELINE_SLICE4_RUN kind=damage run=2 voxels=1031 mutation_ms=1.434 refresh_ms=38.475 work_ms=39.910 frame_interval_ms=96.412 memory_bytes=45789659
VOXEL_BASELINE_SLICE4_RUN kind=saveload run=2 voxels=1031 save_ms=9.322 load_ms=33.955 file_bytes=520954 work_ms=196.929 frame_interval_ms=255.626 memory_bytes=48206003
VOXEL_BASELINE_SLICE4_RUN kind=running run=3 voxels=1032 sim_ms=17900.003 frames=60 frame_avg_ms=391.959 frame_max_ms=516.052 frame_max_step=30 memory_bytes=45914595
VOXEL_BASELINE_SLICE4_RUN kind=damage run=3 voxels=1031 mutation_ms=1.586 refresh_ms=40.810 work_ms=42.396 frame_interval_ms=103.893 memory_bytes=45981987
VOXEL_BASELINE_SLICE4_RUN kind=saveload run=3 voxels=1031 save_ms=8.005 load_ms=27.444 file_bytes=520954 work_ms=201.351 frame_interval_ms=265.431 memory_bytes=48385451
```

尖峰说明：

- 运行 `frame_max≈516–635ms` 均在 `frame_max_step=30`（首个 30 步块）。该间隔包含 30 步权威推进和显示套用，不能和旧报告里暂停后 10s 静止采样的 ~11ms 比，也不能当成 60Hz 窗口帧。
- 损伤 `work_ms≈40–42ms`（其中 Refresh 约 38–41ms，`Damage` 约 1.4–1.6ms）。同一事件的 `frame_interval_ms≈94–104ms`，大于处理秒表，因为统一帧边界还计入该 `_Process` 的显示/HUD 与帧尾。
- 保存恢复 `work_ms≈197–227ms`（第 1 次 `save_ms=18`、`load_ms=50` 较高；随后 load 降到 27–34ms）。`frame_interval_ms≈256–293ms`，同样大于处理秒表，并包含 Reset、重新施工编组和显示接续所在帧。

离线 `sim_ms`（约 17.6–17.9s / 1800 步）只说明批量推进吞吐，不是实时 60Hz 帧预算。

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

本轮观察日志：跟随车厢 172、步 300 断钩前后观察偏移均为 4；`Camera3D.GlobalPosition` 差约 0.07 格（显示区间结束贴齐已提交姿态，不是编组序号造成的 9 格瞬移）。`issue74-split` HUD 为「速 4 滑行」；归零冒烟为「停止」。脚本不检查像素，也不等于动态平稳性的人工确认。审查备份仍在 `.godot/validation/review74-r2/`。

## 人工观察步骤（待用户在窗口中确认）

从主菜单进入切片 4。关闭控制台后 `T` 启动、`Y` 停车、`P` 暂停、`F` 切换跟随。HUD 左上角换行，停因在标题下一行，不应出右边界。

1. 刚编组：侧视可见轴距/车钩空隙，无拉伸。停因「停止」。
2. `rail start` + `rail step 1`，`rail query display`：alpha 在两已完成态之间。
3. 到步 300：两车侧面与车钩可见；Mesh 只改变换。
4. `F` 跟车厢：仍能看到两车相对位置，不是只剩车尾。
5. 跨 `x=144`：连续，不像 Distance 对半混合。
6. 重置→300→跟车厢→毁后车钩：两编组；跟随保持所选车厢；构图不因车厢成为新编组首车而跳 9 格；HUD 为「滑行」不是「停止」。
7. `rail step 30` + `voxel damage 88 1 15 100`：HUD「支撑失效」，后车贴齐已提交停车。
8. 重置→300→`voxel damage 110 1 15 100`→步进停车：HUD「前方缺口」；未修拒绝启动。最后一步显示应仍能在两已完成态之间（R1）。
9. `voxel save` → 退出 → 新进程 `voxel load`：显示从已提交态接续。

请把动态观感记入审查评论。本报告不把自动 PNG / headless 标记写成人工通过。

## Windows x64 待验命令

证据种类必须写明，不能把 checkout 测试和导出 `train.exe` 混成同一次运行。执行时记录实际 `git rev-parse HEAD`、`dotnet --list-runtimes` 中 testhost 真正选用的 `Microsoft.NETCore.App 10.x`、以及本 checkout 的 `slice-4-straight-rail-v1`。脚本不把某一 SHA 或补丁号写成通用门禁。

### A. 源码构建测试（Windows x64 上跑，绑定当时 SHA）

在 **Windows** 上检出与本报告相同的 head，安装 Godot 4.7.2 .NET、.NET 10、PowerShell 7：

```powershell
git rev-parse HEAD
dotnet --list-runtimes
$env:GODOT = "<Godot_v4.7.2-stable_mono_win64.exe>"
pwsh -NoProfile -File scripts/run-windows-issue74-authority.ps1
```

该脚本会记录 SHA / OS / SDK / 全部 10.x 运行时 / 实际选用运行时 / Godot / 生成器身份，并依次跑：

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
4. 相机跟显示姿态与车体朝向；侧向可观察车钩；断开保持选择；构图绑定车辆定义偏移 — Fedora 自动有 Camera3D 连续证据；摧毁回退不在范围；**人工动态待确认**。
5. 30/60/144 与 Pause 同权威；哈希排除显示/相机 — Fedora 自动有。
6. Windows x64 每 300 步哈希与三类存档分支 — **Windows 未跑，阻塞。**
7. Fedora 单独记录，不替代 Windows — 本文件。
8. 切片 4 Godot 冒烟正式命令 — Fedora smoke 通过。
9. 人工观感与自动分开 — 自动 PNG 已重拍，HUD 为运行/滑行/停止。**人工未确认，阻塞。**
10. 1800 步基线：运行/损伤/保存恢复分记；处理耗时与事件帧间隔分列；标明 headless + 30 步批量 — Fedora 已按 N3 重跑。
11. 隔离存档/内容；命令手册覆盖 display/follow 与定义绑定构图。
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
| 12 | 独立滑行 | #72 冒烟 + HUD「滑行」+ 跟随不跳车 |
| 13 | 前方缺口截停 | `issue74-gap-stop` + 缺口最后一步插值测试 |
| 14 | 支撑失效 | `issue74-support-loss` + 拆分后续 |
| 15 | 修轨须显式启动 | 既有 #73 测试 / 手册 |
| 16 | 全毁仍可定位 | Slice 4 full-destroy 冒烟 |
| 17 | 缺引用失败 | 既有坏档 `InvalidDelta` |
| 18 | 保存运行/滑行/停车 | 900 / 330 / gap 存档测试与冒烟 |
| 19 | 沿路径插值 | `AlongRailDisplay` + 本票测试 |
| 20 | 相机跟显示姿态 | 定义绑定构图 `FrameFollowCamera` |
| 21 | 帧率无关权威 | 30/60/144/Pause 测试 |
| 22 | 未覆盖弯坡/双车头 | 本报告结论限定直轨一车头一车厢 |

固定案例（施工、300、900、1800、末端、跨缝、车钩 330、缺口、支撑、全毁、读档）由 #68–#73 测试/冒烟保留，本票加上显示/相机与分负载 1800 基线。
