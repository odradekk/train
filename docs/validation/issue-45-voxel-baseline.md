# Issue #45 voxel baseline

Command: `$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\scripts\run-godot-voxel-baseline.ps1 -OutputPath docs/validation/issue-45-voxel-baseline.md`

This is a rerun of the same #44 100-cell add/remove load. It is not a damage-load comparison.

- Godot: 4.7.2.stable.mono.official.ed1daf0bf
- .NET: Microsoft.NETCore.App 8.0.30 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
- CPU: AMD Ryzen 9 5900HX with Radeon Graphics
- Physical memory bytes: 34204610560
- Sequence: reset; warm up once; y=0 positions ordered by x then z, first 100 cells; each cell is added then removed through the formal world command boundary; three recorded runs; each run observes frame timing for at least 10 seconds.

## Recorded runs

VOXEL_BASELINE_RUN run=1 definitions=2 voxels=512 mutation_ms=0.121 stable_seconds=10.005 frame_avg_ms=6.895 frame_max_ms=10.813 memory_bytes=46094076
VOXEL_BASELINE_RUN run=2 definitions=2 voxels=512 mutation_ms=0.151 stable_seconds=10.006 frame_avg_ms=6.892 frame_max_ms=7.178 memory_bytes=46596264
VOXEL_BASELINE_RUN run=3 definitions=2 voxels=512 mutation_ms=0.166 stable_seconds=10.006 frame_avg_ms=6.900 frame_max_ms=7.977 memory_bytes=47098164

## Comparison with the #44 same-load record

#44 committed headless record (`docs/validation/issue-44-voxel-baseline.md`): mutation 0.109 / 0.184 / 0.241 ms; frame average 6.894 / 6.896 / 6.900 ms; frame max 9.584 / 8.817 / 8.136 ms; static memory 46010380 / 46512568 / 47014468.

This rerun stayed on the same machine, engine, runtime and 100-cell add/remove sequence. Average frame time is unchanged to about 0.01 ms. Mutation time remains sub-millisecond. Memory is about 80 KB higher per run and still grows about 0.5 MB across the three observations, matching the previous pattern. No new hitch was isolated to the damage work; the 10.813 ms first-run max is within ordinary headless scatter and is not treated as a damage-path regression.

## Separate damage settlement timing

Not part of the 100-cell load. Formal same-event damage of `(4,4,4)=100`, `(5,4,4)=100`, `(6,4,4)=25` measured:

- Headless smoke: `VOXEL_SMOKE_MULTI_SETTLE_MS=0.021`
- Windowed observe: `VOXEL_OBSERVE_MULTI_SETTLE_MS=0.059`

Both are sub-millisecond. No damage-settlement hitch was observed.
