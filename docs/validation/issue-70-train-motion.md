# Issue #70 validation

切片 4.3 固定步长牵引、起停与跨轨段运行。本记录是新的验收证据路径，不改写 `docs/validation/issue-68-physical-rail.md` 或更早文件。

Headless Godot smoke 是官方引擎集成证据，只证明标记与领域命令结果；不能当作人工画面、插值或跟随相机验收。伪引擎 PowerShell harness 与真实 Godot 分开记录。未承诺跨 OS 或 .NET 运行时逐位一致。

## Host

- Fedora Linux 44（Workstation Edition），kernel `7.2.4-200.fc44.x86_64`
- .NET SDK `10.0.111`（`dotnet --version` 与 `global.json` 一致）
- Godot `4.7.2.stable.mono.official.ed1daf0bf` at `/usr/local/bin/godot-dotnet`
- PowerShell 7.6.6 at `/usr/local/bin/pwsh`

```text
export GODOT=/usr/local/bin/godot-dotnet
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 1
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 2
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 3
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4
```

原始日志写在 gitignored 的 `.godot/validation/issue-70/`（`env.txt`、`slice1-smoke.log` … `slice4-smoke.log`），不覆盖历史 observe/baseline 路径。

| Check | Result |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 367 passed, 0 failed, 0 skipped |
| `dotnet build Train.csproj` | 0 warnings, 0 errors |
| `TrainMotionTests` | 13 passed: 300/1800 步位姿与速度、反向拒绝、停车清零、末端 272、明确跨段/未连接截止、缺口整条路径、派生图重建不移动编组、步号溢出原子失败、身份耗尽仍可启动、30/60/144 FPS 与 Pause/恢复同权威、动力热更新保留位置并夹速度、非法候选整次拒绝、运动存档 `UnsupportedRailway` 且旧档不变、控制台同一入口 |
| `run-godot-voxel-smoke.ps1 -Slice 1` | passed（真实 Godot） |
| `run-godot-voxel-smoke.ps1 -Slice 2` | passed（真实 Godot，含跨进程保存/恢复） |
| `run-godot-voxel-smoke.ps1 -Slice 3` | passed（真实 Godot，含制造/改装/伤害/热重载/跨进程恢复） |
| `run-godot-voxel-smoke.ps1 -Slice 4` | passed（真实 Godot，单进程）。运动标记：`REVERSE_REJECTED`；`STEP300` loco=`95.999999999999318` carriage=`86.999999999999318` speed=`4`（与 96/87/4 的差 < 10⁻⁸）；`TRANSFORM_ONLY rebuilt=0`；`CROSS_SEAM` segment=2 distance=`51.999999999993634` front=`195.99999999999363`；`STEP1800` 同上；`STOPPED`；`PAUSED step=1800`；`END_STOP front=272 speed=0`。随后恢复静态夹具后仍走接触损毁、局部刷新 `rebuilt=1 retained=35 far_kept=1 z_kept=1`、缺口 `16..110,111..144`、保存 `UnsupportedRailway` |
| `scripts/run-godot-*.tests.ps1` harness | 未运行。本票只改 `run-godot-voxel-smoke.ps1` 的切片 4 标记列表；四个伪引擎 harness 测的是 content/mods/logging 脚本，不是真实游戏，也不覆盖 voxel smoke |
| Windows export / Windows 实机 | 未执行。导出成功与 Windows 运行须分开报告；本机未导出到 `/mnt` |

## Domain evidence (not Godot)

`TrainMotionTests` 在正式 `VoxelDevelopmentSession` 上比较 `CaptureTrainAuthority()`。Pause 标志不进入权威快照。30 FPS×150 帧、60 FPS×300 帧、144 FPS×720 帧都折成 300 步，并与直接 `AdvanceSimulation(300)` 的权威状态相等。Pause 期间 `CollectHostSteps(1/30)` 为 0；恢复后再推进到同一步结果。

固定夹具字面量：dt=1/60，目标速度 4，加速度 2；300 步前接触 96/87、速度 4；1800 步前接触 196、第二段 distance 52；末端前接触 272 后再 Advance 不越界、不反向；未连接相邻段最远前接触 144；缺口 `(110,1,15)` 前接触 ≤110 且不进入 110–111 内部。

## Real Godot vs visual observation

切片 4 smoke 是 headless 单进程。它用正式 `StartTrain` / `AdvanceSimulation` / `StopTrain` / `PauseSimulation` 打印标记，并检查运动后车辆 Mesh 实例未替换。这不是窗口里的人工观感、平滑插值或跟随相机证据；那些属于后续票。

首次切片 4 跑次在运动后 `Reset` 未等轨道 Mesh 稳定，沿用施工时的远段实例 ID，边缘伤害检查报 `Slice 4 local refresh replaced an unrelated section mesh`。已改为复位后再采集无关轨段 Mesh，本记录对应修复后的通过跑次。

## Reproduction

进入切片 4 后：

```text
loglevel info
voxel reset
voxel save Slice 4 Empty
rail construct
rail assemble
rail start reverse
rail start
rail step 300
rail query assembly
rail query motion
rail stop
rail pause
rail resume
rail step 1500
rail query assembly
voxel save After Motion
```

`rail start reverse` 为 `ReverseUnsupported` 且停在 80。`rail step 300` 后前接触 96/87、速度 4。再 `rail step 1500` 后跨 x=144，参考落到第二段。有铁路或运动后再保存为 `UnsupportedRailway`，空世界文件不变。快捷键 `T`/`Y`/`P` 与控制台走同一会话命令。

## Revision R1

补齐指定模拟步起停，并在一帧多步批次内部按入队顺序执行。不覆盖 `.godot/validation/review70/`。本轮日志：`.godot/validation/issue-70-r1/slice4-smoke.log`。

| Check | Result |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 369 passed, 0 failed（新增 `Scheduled_start_and_stop_match_across_display_batches`、`Same_step_commands_keep_enqueue_order_and_past_steps_are_atomic`） |
| `dotnet build Train.csproj` | 0 warnings, 0 errors |
| `run-godot-voxel-smoke.ps1 -Slice 4` | 真实 Godot 通过 |
| Slice 1–3 | 本轮未重跑；协调者已在旧 head `df9a0eb` 于 `.godot/validation/review70/` 通过。本修订不改这些切片路径 |
| Windows / 人工观感 | 未执行 |

同一 `start@0` / `stop@101` 在 30/60/144 FPS 按帧 `CollectHostSteps` + `AdvanceSimulation`，以及 Pause/恢复后，状态 102 权威快照一致：前接触 `82.833611111111111` / `73.833611111111111`，速度 0。`PastStep` 与入队时事务耗尽整次拒绝。

## Revision R2

统一当前步与未来步的入队/执行路径，并让 `ulong.MaxValue` 成为可执行终态步。不覆盖 `.godot/validation/review70/`、`.godot/validation/review70-r2/` 或 `.godot/validation/issue-70-r1/`。本轮日志：`.godot/validation/issue-70-r2/`。

| Check | Result |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 371 passed, 0 failed（新增 `Same_step_submit_timing_keeps_order_counts_and_display_batches`、`Terminal_step_commands_run_when_the_clock_reaches_max_value`） |
| `dotnet build Train.csproj` | 0 warnings, 0 errors |
| `run-godot-voxel-smoke.ps1 -Slice 4` | 真实 Godot 通过 |
| Slice 1–3 | 本轮未重跑；协调者已在更早 head 于 `.godot/validation/review70/` 通过。本修订只改按步命令路径与手册 |
| Windows / 人工观感 | 未执行 |

`start 0; stop 101; start 101; step 102` 与 `start 0; stop 101; step 101; start 101; step 1` 在状态 102 的完整权威快照一致：运行中、前接触 `82.833888888888893`、速度 `0.033333333333333333`、事务计数 5。同一对序列经 30/60/144 FPS 与 Pause/恢复后快照仍相等。仅幂等 `start@101` 提前入队与到步提交的事务计数均为 4。从 `MaxValue-1` 入队 `stop@MaxValue` 再推 1 步后 pending=0、停车；再推进为 `StepOverflow`。
