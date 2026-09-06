# Issue #45 visual observation record

Environment: Godot `4.7.2.stable.mono.official.ed1daf0bf`, Windows display server, OpenGL 3.3 Compatibility, NVIDIA GeForce RTX 3080 Laptop GPU (driver 596.49), CPU AMD Ryzen 9 5900HX.

Command: `$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'; .\scripts\run-godot-voxel-observe.ps1`

The windowed development scene executed formal `VoxelDevelopmentSession.Damage` commands (not `remove`), waited for derived mesh/HUD refresh, validated generated geometry, and saved viewport captures under `.godot/issue45-*.png`. Those PNGs are local evidence and are not committed.

## Procedure

1. Start the project and choose **进入体素开发场景**, or run the observe script above.
2. Confirm the initial 8³ block is continuous and HUD says `(4,4,4)` is `core:iron-ore`, `px_py`, durability `100`, count 512.
3. Run `voxel damage 4 4 4 25`. Confirm the cell remains and durability is `75`.
4. Run `voxel damage 4 4 4 75`. Confirm the corner is empty, count 511, and newly exposed neighbor faces are present with no stale exterior face.
5. Run `voxel reset` then `voxel damage 5 5 5 100` and `voxel query 5 5 5`. Confirm empty / 511 / 390 faces. The closed cavity is not claimed to be visible from the fixed camera.
6. Run `voxel damage 4 5 5 100`. Confirm a one-cell-deep opening from the visible `-X` exterior and newly exposed interior faces with no overlap, gap, or stale face.
7. Run `voxel reset` then `voxel damage 4 4 4 100 5 4 4 100 6 4 4 25` and `voxel query 6 4 4`. Confirm count 510 and durability 75 on the third cell.

## Result

Run on 2026-09-06. Observe markers ended with `VOXEL_OBSERVE_COMPLETE`. Capture log:

- `issue45-initial`: 512 voxels, 384 faces, HUD durability 100.
- `issue45-partial`: 512 voxels, 384 faces, HUD durability 75, recent command `Damaged (4,4,4) by 25 → 75`.
- `issue45-destroyed`: 511 voxels, 384 faces, HUD empty, recent command `Removed (4,4,4) by 75 → 0`, visible corner notch, no stale exterior face.
- `issue45-internal-cavity`: 511 voxels, 390 faces, HUD still shows occupied `(4,4,4)` durability 100, recent command `Removed (5,5,5) by 100 → 0`. Closed exterior unchanged, as expected.
- `issue45-internal-opening`: 510 voxels, 392 faces, recent command `Removed (4,5,5) by 100 → 0`. Visible opening with interior orange neighbor face; geometry validator passed.
- `issue45-multi`: formal query 510 voxels, `(4,4,4)` empty, `(6,4,4)` durability 75, 384 faces, HUD empty. Settlement 0.059 ms.

Inspected viewport captures after the real-GPU run. No leftover deleted-cell face or missing outer triangles were recorded on the single-target destroy or the damage-opened cavity. Headless `scripts/run-godot-voxel-smoke.ps1 -Slice 1` separately confirmed the same damage display markers and is not treated as the visual observation itself.

Final Acceptance-criteria confirmation remains the reviewer's responsibility.
