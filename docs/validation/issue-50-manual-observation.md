# Issue #50 visual observation record

Environment: Godot `4.7.2.stable.mono.official.ed1daf0bf`, Windows display server, OpenGL 3.3 Compatibility, NVIDIA GeForce RTX 3080 Laptop GPU (driver 596.49), CPU AMD Ryzen 9 5900HX.

This is the current-round record for #50 slice 2. Historical #45 / #46 PNGs are not reused as #50 proof.

Delivered observe command (frame-budget `--quit-after 300`, not seconds). The scene quits itself after `VOXEL_OBSERVE_COMPLETE`; 300 is only the fail-closed safety timeout. Captures run after hold frames so HUD and mesh have finished the current interface frame.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1 -Slice 2
```

PNGs under `.godot/issue50-*.png` are local evidence and are not committed. The auto-generated `.godot/issue50-observe-report.md` records only script exit, `VOXEL_OBSERVE_COMPLETE`, capture logs, file presence, and worktree iron-ore hash stability. It does not inspect PNG pixels.

Worktree `builtin/core/resources/voxels/iron-ore.voxels.json` SHA256 `11B61B9E6650CDB66C6987FBE3EB8CCEA45C9B13F34BF9C61D1B4557E4494483` and `iron-ore/top.png` SHA256 `2F70C1C2AADD6BCBDD4BFB2436AEA595AA3C9B852C84518CEF3E189B0AE7AD65` were unchanged after observe. Mutable iron-ore JSON/PNG and the probe Mod lived only under the isolated temp user-data overlay.

The floating `14..17` cube's 15/16 faces sit inside the solid 4³, so they cannot be seen from the outside after an internal delete. Headless smoke and the no-Godot suite cover those cube-internal 15/16 and junction faces. Windowed observation uses visible ground holes on the X/Z 15/16 lines, a constructed Y 15/16 pillar near the camera, and the four-chunk ground meeting at `(16,0,16)`.

## Procedure

1. Start the project and choose **进入切片 2 体素场景**, or run the observe script above.
2. Confirm the y=0 ground covering `0..31` plus the floating `14..17` cube, HUD target `(15,16,15)` occupied, count 1088.
3. Run `voxel remove 16 0 8`. Confirm a one-cell hole in the ground at the X=15/16 line and the +X wall of `(15,0,8)` with no leftover face at the removed cell.
4. `voxel reset`, add `(24,15,8)` and `(24,16,8)`, then `voxel remove 24 16 8`. Confirm a one-cell pillar near the camera whose top is the newly exposed +Y face, with no leftover cell at `(24,16,8)`.
5. `voxel reset` then `voxel remove 8 0 16`. Confirm a ground hole at the Z=15/16 line and the +Z wall of `(8,0,15)`.
6. `voxel reset` then `voxel remove 16 0 16`. Confirm the four-chunk ground meeting hole without ghosting.
7. `voxel reset` then `voxel damage 15 0 8 100 16 0 8 100 15 0 16 25`. Confirm count 1086, a two-cell hole on the X=15/16 line, and durability 75 on `(15,0,16)` via query.
8. Reset, damage `(15,16,15)` by 20 so durability is 80, reload max 150, then reject a missing probe definition and confirm the published world stays.

## Automated observe result

Reran the delivered `-Slice 2` script on this worktree after the visible-hole camera/sequence revision. Exit 0. Markers included `VOXEL_OBSERVE_COMPLETE` and eight `.godot/issue50-*.png`. Capture log:

- `issue50-initial`: 1088 voxels, 2272 faces, HUD occupied `(15,16,15)` durability 100 / max 100, last command 场景已进入.
- `issue50-x-boundary`: 1087 voxels, 2274 faces, last command `Removed (16,0,8)`.
- `issue50-y-boundary`: 1089 voxels, 2278 faces, last command `Removed (24,16,8)`.
- `issue50-z-boundary`: 1087 voxels, 2274 faces, last command `Removed (8,0,16)`.
- `issue50-junction`: 1087 voxels, 2274 faces, last command `Removed (16,0,16)`.
- `issue50-cross-damage`: 1086 voxels, 2274 faces, last command `Removed (15,0,8) by 100 → 0；Removed (16,0,8) by 100 → 0；Damaged (15,0,16) by 25 → 75`.
- `issue50-reload-150`: 1088 voxels, 2272 faces, HUD durability 80 / max 150, last command `Damaged (15,16,15) by 20 → 80`.
- `issue50-rejected`: 1089 voxels, 2276 faces, HUD still durability 80 / max 150. The isolated probe remains; failure did not revert the published world.

## Visual inspection of the current #50 PNGs

Inspected every generated `.godot/issue50-*.png` after the successful delivered-script run. HUD in every frame says **切片 2 · 2×2×2 Chunk · 32³**. The near-camera ground edge at the bottom of several frames is clipped by the fixed camera; that silhouette is the ground slab, not a leftover hole.

- `issue50-initial.png`: continuous orange y=0 ground covering the 32×32 footprint; a dark floating 4³ sits above the center. HUD target `(15,16,15)` occupied, `core:iron-ore`, `px_py`, durability 100 / max 100. No seam or leftover face at the chunk lines.
- `issue50-x-boundary.png`: one square hole in the orange ground at the X=15/16 line after `Removed (16,0,8)`. The hole is a single cell; the adjacent +X wall of `(15,0,8)` is present; no leftover exterior face at the removed cell. Face count rose 2272 → 2274.
- `issue50-y-boundary.png`: after reset, a lone cell remains at `(24,15,8)` (1089 voxels) following `Removed (24,16,8)`. No second stacked cell at y=16. The isolated pillar sits in the near field of the fixed camera; the in-scene mesh query required the +Y face of `(24,15,8)` before capture. Ground is intact (the dark shape at the bottom edge is camera near-clip of the slab).
- `issue50-z-boundary.png`: one square hole at the Z=15/16 line after `Removed (8,0,16)`. Adjacent +Z wall of `(8,0,15)` is present; no leftover face at the removed cell.
- `issue50-junction.png`: one square hole at the center of the ground after `Removed (16,0,16)`, where four ground Chunks meet. Edges are a single-cell opening; no overlapping or stale faces around the meeting.
- `issue50-cross-damage.png`: a two-cell rectangular hole on the X=15/16 line after the same-event removals of `(15,0,8)` and `(16,0,8)`. Interior walls of the remaining ground cells are visible. HUD lists both removals plus `Damaged (15,0,16) by 25 → 75`. Count 1086. No ghost face at either removed cell.
- `issue50-reload-150.png`: closed 1088-voxel fixture again (ground plus floating cube). HUD at `(15,16,15)` still occupied, durability 80, max now 150. Exterior closed; no heal, no extra instance.
- `issue50-rejected.png`: HUD still durability 80 / max 150 at `(15,16,15)`. World count 1089 from the isolated probe. Failure did not restore max 100, drop the probe, or rebuild a different published world.

Headless `scripts/run-godot-voxel-smoke.ps1 -Slice 2` remains separate automated integration evidence for the cube-internal 15/16 faces, dirty-chunk ranges, and the #46 live-reload contract on the slice 2 session. Collision and save were not exercised.

Final Acceptance-criteria confirmation remains the reviewer's responsibility.
