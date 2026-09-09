# Issue #53 voxel baseline

Command: $env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\\scripts\\run-godot-voxel-baseline.ps1

- Godot: 4.7.2.stable.mono.official.ed1daf0bf
- .NET: Microsoft.NETCore.App 8.0.30 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
- CPU: AMD Ryzen 9 5900HX with Radeon Graphics        
- Physical memory bytes: 34204610560
- Sequence: one warm-up of the slice 1 100-cell y=0 add/remove pairs; three recorded slice 1 runs (10 s each); then the same process switches to the slice 2 fixture and records three 100-pair cross-boundary add/remove runs (x=15/16, y=1); then one unrecorded save/load warm-up and three recorded save/load timings with file size. Isolated temporary user-data; does not write the player's saves.

## Slice 1 recorded runs

VOXEL_BASELINE_RUN run=1 definitions=4 voxels=512 mutation_ms=0.158 stable_seconds=10.003 frame_avg_ms=7.576 frame_max_ms=47.563 memory_bytes=50581084
VOXEL_BASELINE_RUN run=2 definitions=4 voxels=512 mutation_ms=0.280 stable_seconds=10.006 frame_avg_ms=7.679 frame_max_ms=36.845 memory_bytes=51865624
VOXEL_BASELINE_RUN run=3 definitions=4 voxels=512 mutation_ms=0.361 stable_seconds=10.001 frame_avg_ms=7.652 frame_max_ms=38.164 memory_bytes=53195396

## Slice 2 cross-boundary recorded runs

VOXEL_BASELINE_SLICE2_RUN run=1 definitions=4 voxels=1088 mutation_ms=0.091 stable_seconds=10.003 frame_avg_ms=7.684 frame_max_ms=83.805 memory_bytes=57771332
VOXEL_BASELINE_SLICE2_RUN run=2 definitions=4 voxels=1088 mutation_ms=0.119 stable_seconds=10.006 frame_avg_ms=8.109 frame_max_ms=91.560 memory_bytes=62314356
VOXEL_BASELINE_SLICE2_RUN run=3 definitions=4 voxels=1088 mutation_ms=0.121 stable_seconds=10.006 frame_avg_ms=8.011 frame_max_ms=68.439 memory_bytes=66931072

## Save / load recorded runs

VOXEL_BASELINE_SAVE_RUN run=1 voxels=1089 save_ms=8.457 load_ms=4.388 file_bytes=1130 memory_bytes=55615512
VOXEL_BASELINE_SAVE_RUN run=2 voxels=1089 save_ms=8.773 load_ms=3.602 file_bytes=1130 memory_bytes=55615624
VOXEL_BASELINE_SAVE_RUN run=3 voxels=1089 save_ms=15.632 load_ms=3.510 file_bytes=1130 memory_bytes=55615736
