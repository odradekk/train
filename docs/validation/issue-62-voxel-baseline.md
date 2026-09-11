# Issue #62 voxel baseline

Command: $env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\\scripts\\run-godot-voxel-baseline.ps1

- Godot: 4.7.2.stable.mono.official.ed1daf0bf
- .NET: Microsoft.NETCore.App 8.0.31 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
- CPU: AMD Ryzen 9 5900HX with Radeon Graphics        
- Physical memory bytes: 34204610560
- Sequence: one warm-up of the slice 1 100-cell y=0 add/remove pairs; three recorded slice 1 runs (10 s each); then the same process switches to the slice 2 fixture and records three 100-pair cross-boundary add/remove runs (x=15/16, y=1); then one unrecorded save/load warm-up and three recorded world save/load timings; then one unrecorded slice 3 persist warm-up and three recorded 100-pair move/return + bridge damage + save/restore timings (10 s frames each). Isolated temporary user-data; does not write the player's saves. No FPS or vehicle-count threshold is asserted.

## Slice 1 recorded runs

VOXEL_BASELINE_RUN run=1 definitions=9 voxels=512 mutation_ms=0.153 stable_seconds=10.001 frame_avg_ms=6.921 frame_max_ms=50.003 memory_bytes=51202512
VOXEL_BASELINE_RUN run=2 definitions=9 voxels=512 mutation_ms=0.257 stable_seconds=10.006 frame_avg_ms=6.905 frame_max_ms=27.139 memory_bytes=52487052
VOXEL_BASELINE_RUN run=3 definitions=9 voxels=512 mutation_ms=0.312 stable_seconds=10.006 frame_avg_ms=6.908 frame_max_ms=32.493 memory_bytes=53816824

## Slice 2 cross-boundary recorded runs

VOXEL_BASELINE_SLICE2_RUN run=1 definitions=9 voxels=1088 mutation_ms=0.130 stable_seconds=10.007 frame_avg_ms=6.929 frame_max_ms=62.336 memory_bytes=60349304
VOXEL_BASELINE_SLICE2_RUN run=2 definitions=9 voxels=1088 mutation_ms=0.131 stable_seconds=10.000 frame_avg_ms=6.931 frame_max_ms=65.504 memory_bytes=64892328
VOXEL_BASELINE_SLICE2_RUN run=3 definitions=9 voxels=1088 mutation_ms=0.115 stable_seconds=10.000 frame_avg_ms=6.938 frame_max_ms=74.116 memory_bytes=53895772

## World save / load recorded runs

VOXEL_BASELINE_SAVE_RUN run=1 voxels=1089 save_ms=7.293 load_ms=5.003 file_bytes=1213 memory_bytes=53895996
VOXEL_BASELINE_SAVE_RUN run=2 voxels=1089 save_ms=7.635 load_ms=2.593 file_bytes=1213 memory_bytes=53896108
VOXEL_BASELINE_SAVE_RUN run=3 voxels=1089 save_ms=9.039 load_ms=4.177 file_bytes=1213 memory_bytes=53896220

## Slice 3 persist recorded runs

VOXEL_BASELINE_SLICE3_RUN run=1 voxels=26 parts=196 mutation_ms=225.656 structure_ms=7.792 save_ms=5.810 load_ms=3.950 file_bytes=56595 stable_seconds=10.001 frame_avg_ms=6.905 frame_max_ms=28.098 memory_bytes=50308912
VOXEL_BASELINE_SLICE3_RUN run=2 voxels=26 parts=196 mutation_ms=159.592 structure_ms=6.649 save_ms=4.457 load_ms=5.436 file_bytes=56595 stable_seconds=10.001 frame_avg_ms=6.892 frame_max_ms=8.807 memory_bytes=50451064
VOXEL_BASELINE_SLICE3_RUN run=3 voxels=26 parts=196 mutation_ms=98.369 structure_ms=8.809 save_ms=5.320 load_ms=2.675 file_bytes=56595 stable_seconds=10.007 frame_avg_ms=6.892 frame_max_ms=8.766 memory_bytes=51271020
