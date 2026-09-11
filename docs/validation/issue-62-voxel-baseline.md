# Issue #62 voxel baseline

Command: $env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\\scripts\\run-godot-voxel-baseline.ps1

- Godot: 4.7.2.stable.mono.official.ed1daf0bf
- .NET: Microsoft.NETCore.App 8.0.31 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
- CPU: AMD Ryzen 9 5900HX with Radeon Graphics        
- Physical memory bytes: 34204610560
- Sequence: one warm-up of the slice 1 100-cell y=0 add/remove pairs; three recorded slice 1 runs (10 s each); then the same process switches to the slice 2 fixture and records three 100-pair cross-boundary add/remove runs (x=15/16, y=1); then one unrecorded save/load warm-up and three recorded world save/load timings; then one unrecorded slice 3 persist warm-up and three recorded 100-pair move/return (mutation_ms), bridge damage/structural resolution (structure_ms), display Refresh (refresh_ms), and save/restore timings (10 s frames each). Isolated temporary user-data; does not write the player's saves. No FPS or vehicle-count threshold is asserted.

## Slice 1 recorded runs

VOXEL_BASELINE_RUN run=1 definitions=9 voxels=512 mutation_ms=0.164 stable_seconds=10.005 frame_avg_ms=6.923 frame_max_ms=53.128 memory_bytes=51205872
VOXEL_BASELINE_RUN run=2 definitions=9 voxels=512 mutation_ms=0.205 stable_seconds=10.006 frame_avg_ms=6.902 frame_max_ms=23.458 memory_bytes=52490412
VOXEL_BASELINE_RUN run=3 definitions=9 voxels=512 mutation_ms=0.224 stable_seconds=10.006 frame_avg_ms=6.904 frame_max_ms=26.218 memory_bytes=53820184

## Slice 2 cross-boundary recorded runs

VOXEL_BASELINE_SLICE2_RUN run=1 definitions=9 voxels=1088 mutation_ms=0.095 stable_seconds=10.007 frame_avg_ms=6.921 frame_max_ms=51.105 memory_bytes=60352664
VOXEL_BASELINE_SLICE2_RUN run=2 definitions=9 voxels=1088 mutation_ms=0.089 stable_seconds=10.007 frame_avg_ms=6.920 frame_max_ms=50.128 memory_bytes=64895688
VOXEL_BASELINE_SLICE2_RUN run=3 definitions=9 voxels=1088 mutation_ms=0.206 stable_seconds=10.007 frame_avg_ms=6.932 frame_max_ms=66.504 memory_bytes=53899132

## World save / load recorded runs

VOXEL_BASELINE_SAVE_RUN run=1 voxels=1089 save_ms=6.542 load_ms=3.469 file_bytes=1213 memory_bytes=53899356
VOXEL_BASELINE_SAVE_RUN run=2 voxels=1089 save_ms=5.634 load_ms=2.649 file_bytes=1213 memory_bytes=53899468
VOXEL_BASELINE_SAVE_RUN run=3 voxels=1089 save_ms=8.583 load_ms=2.837 file_bytes=1213 memory_bytes=53899580

## Slice 3 persist recorded runs

VOXEL_BASELINE_SLICE3_RUN run=1 voxels=26 parts=196 mutation_ms=168.275 structure_ms=0.729 refresh_ms=5.739 save_ms=4.598 load_ms=4.041 file_bytes=56595 stable_seconds=10.003 frame_avg_ms=6.900 frame_max_ms=20.359 memory_bytes=50310704
VOXEL_BASELINE_SLICE3_RUN run=2 voxels=26 parts=196 mutation_ms=125.610 structure_ms=0.786 refresh_ms=6.532 save_ms=10.280 load_ms=4.315 file_bytes=56595 stable_seconds=10.005 frame_avg_ms=6.890 frame_max_ms=7.196 memory_bytes=50454424
VOXEL_BASELINE_SLICE3_RUN run=3 voxels=26 parts=196 mutation_ms=72.859 structure_ms=0.751 refresh_ms=6.760 save_ms=10.061 load_ms=2.396 file_bytes=56595 stable_seconds=10.005 frame_avg_ms=6.891 frame_max_ms=7.299 memory_bytes=51078040
