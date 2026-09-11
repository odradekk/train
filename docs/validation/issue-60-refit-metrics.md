# Issue #60 refit metrics

Measured numbers only. Automated checks (domain assertions, Godot smoke markers) are listed separately from these timings. No FPS threshold was invented. Final merge of save/bridge baselines stays on a later ticket. Engine time settings were not changed.

Environment: Windows 10, .NET SDK `9.0.318`, Godot `4.7.2.stable.mono.official.ed1daf0bf`, NVIDIA GeForce RTX 3080 Laptop GPU. Sequence on the official 168-cell hollow shell after the #60 success layout (169 parts, P at `(4,1,0)`, durability 75, `nz_py`): one 100-pair warm-up, then three recorded 100-pair move/return runs. Identity, durability, orientation and part count were checked after the warm-up and after every recorded run; no drift.

## Domain automatic check (not a Godot frame)

Command: `dotnet test Train.Tests/Train.Tests.csproj --nologo --filter FullyQualifiedName~Recorded_refit_metrics --logger "console;verbosity=detailed"`

This path has no rendered frame. It only records mutation time and GC memory.

| run | parts | world | mutation_ms | memory_bytes | memory_delta |
| --- | --- | --- | --- | --- | --- |
| 1 | 169 | 0 | 147.901 | 7097888 | 5779768 |
| 2 | 169 | 0 | 175.957 | 7129344 | 5779768 |
| 3 | 169 | 0 | 179.763 | 7129816 | 5779768 |

## Real Godot slice 3 smoke (headless wall-clock frame boundaries)

Command: `$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\scripts\run-godot-voxel-smoke.ps1 -Slice 3`

Headless Godot `--quit-after 380`. Sampling scope, using `Time.GetTicksUsec()` at the start of `_Process`:

- `A` is the entry of the `_Process` that then runs the 100-pair mutation (captured before the pairs).
- `B` is the next `_Process` entry, where presenter `Refresh()` runs.
- `C` is the following `_Process` entry, after that refresh frame has finished.
- `mutation_ms` is `Stopwatch` around `HollowShellRefit.RunMoveReturnPairs` (200 official `Refit` calls) inside the `A` frame.
- `refresh_ms` is `Stopwatch` around `Refresh()` on the `B` frame.
- `mutation_frame_ms` is wall clock `B − A`.
- `refresh_frame_ms` is wall clock `C − B`.
- `frame_ms` is the two-frame total `C − A` (`mutation_frame_ms + refresh_frame_ms`).
- `process_delta_ms` is `GetProcessDeltaTime()` of the print frame. That is the engine’s possibly capped/smoothed delta and is **not** the frame duration.
- `memory_*` is `GC.GetTotalMemory` around the mutation batch.

The three recorded runs are separate frame sequences. They are not three batches inside one `_Process`. These are not an FPS gate.

| run | parts | world | mutation_ms | refresh_ms | mutation_frame_ms | refresh_frame_ms | frame_ms | process_delta_ms | memory_bytes | memory_delta |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 169 | 0 | 179.072 | 4.105 | 189.633 | 4.224 | 193.857 | 4.341 | 7823760 | 6811720 |
| 2 | 169 | 0 | 162.795 | 3.905 | 165.375 | 4.054 | 169.429 | 16.667 | 7819392 | 6811720 |
| 3 | 169 | 0 | 140.195 | 4.444 | 142.582 | 4.595 | 147.177 | 7.620 | 7817336 | 6809664 |

Each `frame_ms` is longer than that run’s `mutation_ms`, because `C − A` includes the rest of the mutation frame plus the refresh frame. `process_delta_ms` remains a separate engine delta and is not used as `frame_ms`.

Smoke printed `VOXEL_SMOKE_SLICE3_REFIT_PAIRS id=170 pairs=100 parts=169` after the warm-up, then the three `REFIT_METRICS` lines, then `COMPOSITE_REFIT_SMOKE_OK`. World voxel count stayed 0.

Windowed observe (`run-godot-voxel-observe.ps1 -Slice 3`) does not reprint these timings; it is the visual record. This round did not change textures, camera, or observe capture logic; the R2 PNGs from head `25303bd` remain the visual evidence.

## Not recorded here

- Save/load or bridge-fracture timings (later ticket).
- A claimed minimum FPS. `process_delta_ms` is the engine’s reported previous-frame duration, not a requirement.
