# Issue #44 voxel baseline

Command: $env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\\scripts\\run-godot-voxel-baseline.ps1

- Godot: 4.7.2.stable.mono.official.ed1daf0bf
- .NET: Microsoft.NETCore.App 8.0.30 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
- CPU: AMD Ryzen 9 5900HX with Radeon Graphics
- Physical memory bytes: 34204610560
- Sequence: reset; warm up once; y=0 positions ordered by x then z, first 100 cells; each cell is added then removed through the formal world command boundary; three recorded runs; each run observes frame timing for at least 10 seconds.

## Recorded runs

VOXEL_BASELINE_RUN run=1 definitions=2 voxels=512 mutation_ms=0.109 stable_seconds=10.003 frame_avg_ms=6.894 frame_max_ms=9.584 memory_bytes=46010380
VOXEL_BASELINE_RUN run=2 definitions=2 voxels=512 mutation_ms=0.184 stable_seconds=10.006 frame_avg_ms=6.896 frame_max_ms=8.817 memory_bytes=46512568
VOXEL_BASELINE_RUN run=3 definitions=2 voxels=512 mutation_ms=0.241 stable_seconds=10.006 frame_avg_ms=6.900 frame_max_ms=8.136 memory_bytes=47014468
