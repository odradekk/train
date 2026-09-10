# Issue #60 refit metrics

Measured numbers only. Automated checks (domain assertions, Godot smoke markers) are listed separately from these timings. No FPS threshold was invented. Final merge of save/bridge baselines stays on a later ticket.

Environment: Windows 10, .NET SDK `9.0.318`, Godot `4.7.2.stable.mono.official.ed1daf0bf`, NVIDIA GeForce RTX 3080 Laptop GPU. Sequence on the official 168-cell hollow shell after the #60 success layout (169 parts, P at `(4,1,0)`, durability 75, `nz_py`): one 100-pair warm-up, then three recorded 100-pair move/return runs.

## Domain automatic check (not a Godot frame)

Command: `dotnet test Train.Tests/Train.Tests.csproj --nologo --filter FullyQualifiedName~Recorded_refit_metrics --logger "console;verbosity=detailed"`

This path has no rendered frame. It only records mutation time and GC memory.

| run | parts | world | mutation_ms | memory_bytes | memory_delta |
| --- | --- | --- | --- | --- | --- |
| 1 | 169 | 0 | 185.182 | 7126728 | 5779768 |
| 2 | 169 | 0 | 184.583 | 7158184 | 5779768 |
| 3 | 169 | 0 | 171.814 | 7158656 | 5779768 |

Identity/durability/orientation/layout matched the warm-up snapshot after each run (`CompositeRefitTests.One_hundred_legal_move_pairs_do_not_drift` and the metrics test’s post-run query).

## Real Godot slice 3 smoke (mutation + derived refresh)

Command: `$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\scripts\run-godot-voxel-smoke.ps1 -Slice 3`

`mutation_ms` is `Stopwatch` around `HollowShellRefit.RunMoveReturnPairs` (200 official `Refit` calls). `frame_ms` is `Stopwatch` around one `Refresh()` of the committed composite/world presenters after that batch. `process_delta_ms` is the engine `GetProcessDeltaTime()` of that process frame. These are not an FPS gate.

| run | parts | world | mutation_ms | frame_ms | process_delta_ms | memory_bytes | memory_delta |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 169 | 0 | 207.689 | 4.337 | 8.333 | 6802528 | 5781320 |
| 2 | 169 | 0 | 187.119 | 4.188 | 8.333 | 6815024 | 5781320 |
| 3 | 169 | 0 | 193.376 | 4.258 | 8.333 | 6815048 | 5781344 |

Smoke also printed `VOXEL_SMOKE_SLICE3_REFIT_PAIRS id=170 pairs=100 parts=169` after the warm-up, then `COMPOSITE_REFIT_SMOKE_OK`. World voxel count stayed 0.

## Not recorded here

- Save/load or bridge-fracture timings (later ticket).
- A claimed minimum FPS. `process_delta_ms=8.333` is the headless/vsync-ish frame budget observed on this run, not a requirement.
