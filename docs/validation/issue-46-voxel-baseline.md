# Issue #46 voxel baseline

Command: `$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\scripts\run-godot-voxel-baseline.ps1 -OutputPath docs/validation/issue-46-voxel-baseline.md`

This is a current-round rerun of the same #44 / #45 100-cell add/remove load after one warm-up. It is not a live-reload-load comparison. Isolation overlay is not used; baseline still reads `res://builtin`.

- Godot: 4.7.2.stable.mono.official.ed1daf0bf
- .NET: Microsoft.NETCore.App 8.0.30 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
- CPU: AMD Ryzen 9 5900HX with Radeon Graphics
- Physical memory bytes: 34204610560
- Sequence: reset; warm up once; y=0 positions ordered by x then z, first 100 cells; each cell is added then removed through the formal world command boundary; three recorded runs; each run observes frame timing for at least 10 seconds.

## Recorded runs

VOXEL_BASELINE_RUN run=1 definitions=2 voxels=512 mutation_ms=0.196 stable_seconds=10.007 frame_avg_ms=6.900 frame_max_ms=19.096 memory_bytes=46361048
VOXEL_BASELINE_RUN run=2 definitions=2 voxels=512 mutation_ms=0.333 stable_seconds=10.006 frame_avg_ms=6.894 frame_max_ms=11.700 memory_bytes=46925372
VOXEL_BASELINE_RUN run=3 definitions=2 voxels=512 mutation_ms=0.262 stable_seconds=10.002 frame_avg_ms=7.112 frame_max_ms=36.592 memory_bytes=47489096

## Comparison with the #45 same-load record

#45 committed headless record (`docs/validation/issue-45-voxel-baseline.md`): mutation 0.121 / 0.151 / 0.166 ms; frame average 6.895 / 6.892 / 6.900 ms; frame max 10.813 / 7.178 / 7.977 ms; static memory 46094076 / 46596264 / 47098164.

This rerun stayed on the same machine, engine, runtime and 100-cell add/remove sequence. Average frame time is unchanged to about 0.2 ms and remains about 7 ms. Mutation time remains sub-millisecond. Memory is about 0.3–0.4 MB higher per run and still grows about 0.5 MB across the three observations, matching the previous pattern.

The 10-second window still starts immediately after the 100 add/remove pairs, so `frame_max` includes the first dirty mesh rebuild. This slice rebuilds the chunk with published Cube face textures instead of the #45 vertex-color mesh, which accounts for the 19–37 ms first-frame spike. Later idle frames keep the ~7 ms average; no sustained hitch was isolated during the stable observation. This is not treated as a cross-chunk scale claim.

Compared with the first #46 implementation pass on the same machine (mutation 0.339 / 0.329 / 0.539 ms; frame average 7.106 / 7.492 / 7.236 ms; frame max 43–70 ms), this revision did not add a new idle-frame cost. Isolation overlay is smoke/observe-only and is not in this baseline path.

## Separate damage and live-reload settlement timing

Not part of the 100-cell load.

- Headless smoke multi-target damage: `VOXEL_SMOKE_MULTI_SETTLE_MS=0.355`
- Windowed observe multi-target damage: `VOXEL_OBSERVE_MULTI_SETTLE_MS=0.328`

Live-reload success/reject paths and isolation abort were exercised in smoke and observe, not in this 100-cell baseline.
