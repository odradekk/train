# Issue #60 refit metrics

Measured numbers only. Automated checks (domain assertions, Godot smoke markers) are listed separately from these timings. No FPS threshold was invented. Final merge of save/bridge baselines stays on a later ticket.

Environment: Windows 10, .NET SDK `9.0.318`, Godot `4.7.2.stable.mono.official.ed1daf0bf`, NVIDIA GeForce RTX 3080 Laptop GPU. Sequence on the official 168-cell hollow shell after the #60 success layout (169 parts, P at `(4,1,0)`, durability 75, `nz_py`): one 100-pair warm-up, then three recorded 100-pair move/return runs. Identity, durability, orientation and part count were checked after the warm-up and after every recorded run; no drift.

## Domain automatic check (not a Godot frame)

Command: `dotnet test Train.Tests/Train.Tests.csproj --nologo --filter FullyQualifiedName~Recorded_refit_metrics --logger "console;verbosity=detailed"`

This path has no rendered frame. It only records mutation time and GC memory.

| run | parts | world | mutation_ms | memory_bytes | memory_delta |
| --- | --- | --- | --- | --- | --- |
| 1 | 169 | 0 | 157.186 | 7094736 | 5779768 |
| 2 | 169 | 0 | 183.493 | 7126192 | 5779768 |
| 3 | 169 | 0 | 168.259 | 7126664 | 5779768 |

## Real Godot slice 3 smoke (headless frame boundaries)

Command: `$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\scripts\run-godot-voxel-smoke.ps1 -Slice 3`

Headless Godot `--quit-after 380`. Sampling scope:

- `mutation_ms` is `Stopwatch` around one `HollowShellRefit.RunMoveReturnPairs` (200 official `Refit` calls) inside a dedicated `_Process`.
- `refresh_ms` is `Stopwatch` around the presenter `Refresh()` on the next dirty `_Process`.
- `frame_ms` is the sum of two engine `_Process` deltas after that mutation enables sampling: the reported duration of the mutation frame plus the following refresh frame. It is not the Refresh CPU time.
- `process_delta_ms` is `GetProcessDeltaTime()` of the await frame that printed the line (the refresh frame’s reported duration).
- `memory_*` is `GC.GetTotalMemory` around the mutation batch.

The three recorded runs are separate frame sequences. They are not three batches inside one `_Process`. These are not an FPS gate.

| run | parts | world | mutation_ms | refresh_ms | frame_ms | frame_samples | process_delta_ms | memory_bytes | memory_delta |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 169 | 0 | 192.836 | 4.594 | 141.912 | 2 | 7.645 | 7831512 | 6811720 |
| 2 | 169 | 0 | 184.416 | 4.181 | 155.498 | 2 | 5.498 | 7827144 | 6811720 |
| 3 | 169 | 0 | 117.581 | 4.677 | 120.014 | 2 | 4.927 | 7825088 | 6809664 |

Smoke printed `VOXEL_SMOKE_SLICE3_REFIT_PAIRS id=170 pairs=100 parts=169` after the warm-up, then the three `REFIT_METRICS` lines, then `COMPOSITE_REFIT_SMOKE_OK`. World voxel count stayed 0.

Windowed observe (`run-godot-voxel-observe.ps1 -Slice 3`) does not reprint these timings; it is the visual record.

## Not recorded here

- Save/load or bridge-fracture timings (later ticket).
- A claimed minimum FPS. Headless `process_delta_ms` here is the engine’s reported previous-frame duration, not a requirement.
