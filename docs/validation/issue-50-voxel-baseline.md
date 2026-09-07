# Issue #50 voxel baseline

Command: $env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\\scripts\\run-godot-voxel-baseline.ps1

- Godot: 4.7.2.stable.mono.official.ed1daf0bf
- .NET: Microsoft.NETCore.App 8.0.30 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
- CPU: AMD Ryzen 9 5900HX with Radeon Graphics        
- Physical memory bytes: 34204610560
- Sequence: one warm-up of the slice 1 100-cell y=0 add/remove pairs; three recorded slice 1 runs (10 s each); then the same process switches to the slice 2 fixture and records three 100-pair cross-boundary add/remove runs (x=15/16, y=1) through the formal world command boundary.

## Slice 1 recorded runs

VOXEL_BASELINE_RUN run=1 definitions=2 voxels=512 mutation_ms=0.176 stable_seconds=10.006 frame_avg_ms=7.559 frame_max_ms=23.428 memory_bytes=46483168
VOXEL_BASELINE_RUN run=2 definitions=2 voxels=512 mutation_ms=0.261 stable_seconds=10.006 frame_avg_ms=7.751 frame_max_ms=16.828 memory_bytes=47047492
VOXEL_BASELINE_RUN run=3 definitions=2 voxels=512 mutation_ms=0.308 stable_seconds=10.004 frame_avg_ms=7.768 frame_max_ms=19.064 memory_bytes=47611216

## Slice 2 cross-boundary recorded runs

VOXEL_BASELINE_SLICE2_RUN run=1 definitions=2 voxels=1088 mutation_ms=0.107 stable_seconds=10.001 frame_avg_ms=7.752 frame_max_ms=20.787 memory_bytes=53974520
VOXEL_BASELINE_SLICE2_RUN run=2 definitions=2 voxels=1088 mutation_ms=0.134 stable_seconds=10.006 frame_avg_ms=7.756 frame_max_ms=35.016 memory_bytes=48243104
VOXEL_BASELINE_SLICE2_RUN run=3 definitions=2 voxels=1088 mutation_ms=0.116 stable_seconds=10.007 frame_avg_ms=7.775 frame_max_ms=26.385 memory_bytes=51341136
