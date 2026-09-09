# Issue #53 voxel baseline

This file **replaces** the first-delivery baseline numbers written before the R1–R4 revision. The numbers below are the second-round reviewer run on `84081a03c766fc3914b3cebb18c87ec8828d0cc1` plus `global.json` SDK `9.0.318`, originally written to `.godot/review53-r2-baseline.md` (`D:/Code/train/.git/review53-r2-evidence/baseline.log`). This agent copied those recorded lines here and did not re-run the baseline script.

Command: $env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\\scripts\\run-godot-voxel-baseline.ps1 -OutputPath .godot/review53-r2-baseline.md

- Godot: 4.7.2.stable.mono.official.ed1daf0bf
- .NET: Microsoft.NETCore.App 8.0.31 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
- CPU: AMD Ryzen 9 5900HX with Radeon Graphics
- Physical memory bytes: 34204610560
- Sequence: one warm-up of the slice 1 100-cell y=0 add/remove pairs; three recorded slice 1 runs (10 s each); then the same process switches to the slice 2 fixture and records three 100-pair cross-boundary add/remove runs (x=15/16, y=1); then one unrecorded save/load warm-up and three recorded save/load timings with file size. Isolated temporary user-data; does not write the player's saves.
- No FPS threshold was added.

## Slice 1 recorded runs

VOXEL_BASELINE_RUN run=1 definitions=4 voxels=512 mutation_ms=0.137 stable_seconds=10.001 frame_avg_ms=6.916 frame_max_ms=40.755 memory_bytes=50625808
VOXEL_BASELINE_RUN run=2 definitions=4 voxels=512 mutation_ms=0.310 stable_seconds=10.007 frame_avg_ms=6.911 frame_max_ms=36.484 memory_bytes=51910348
VOXEL_BASELINE_RUN run=3 definitions=4 voxels=512 mutation_ms=0.271 stable_seconds=10.006 frame_avg_ms=6.906 frame_max_ms=29.296 memory_bytes=53240120

## Slice 2 cross-boundary recorded runs

VOXEL_BASELINE_SLICE2_RUN run=1 definitions=4 voxels=1088 mutation_ms=0.101 stable_seconds=10.004 frame_avg_ms=6.933 frame_max_ms=67.387 memory_bytes=57807676
VOXEL_BASELINE_SLICE2_RUN run=2 definitions=4 voxels=1088 mutation_ms=0.140 stable_seconds=10.003 frame_avg_ms=6.938 frame_max_ms=75.291 memory_bytes=62350700
VOXEL_BASELINE_SLICE2_RUN run=3 definitions=4 voxels=1088 mutation_ms=0.101 stable_seconds=10.007 frame_avg_ms=6.928 frame_max_ms=61.393 memory_bytes=66967380

## Save / load recorded runs

VOXEL_BASELINE_SAVE_RUN run=1 voxels=1089 save_ms=8.253 load_ms=4.745 file_bytes=1130 memory_bytes=55651628
VOXEL_BASELINE_SAVE_RUN run=2 voxels=1089 save_ms=9.272 load_ms=3.171 file_bytes=1130 memory_bytes=55651740
VOXEL_BASELINE_SAVE_RUN run=3 voxels=1089 save_ms=12.082 load_ms=3.637 file_bytes=1130 memory_bytes=55651852
