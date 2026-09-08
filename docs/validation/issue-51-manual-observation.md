# Issue #51 visual observation record

Environment: Godot `4.7.2.stable.mono.official.ed1daf0bf`, Windows display server, OpenGL 3.3 Compatibility, NVIDIA GeForce RTX 3080 Laptop GPU (driver 596.49), CPU AMD Ryzen 9 5900HX.

This is the current-round record for #51 slice 2 collision. It reuses the #50 windowed observe entry (`-Slice 2`, eight `.godot/issue50-*.png`) with `DebugCollisionsHint` on and HUD collision text. Cube-internal 15/16 and Godot `IntersectShape` cross-checks are in headless `scripts/run-godot-voxel-smoke.ps1 -Slice 2`, not in these ground-hole PNGs.

Delivered observe command (frame-budget `--quit-after 300`, not seconds). The scene quits itself after `VOXEL_OBSERVE_COMPLETE`; 300 is only the fail-closed safety timeout.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1 -Slice 2
```

PNGs under `.godot/issue50-*.png` are local evidence and are not committed. The auto-generated `.godot/issue50-observe-report.md` records script exit, `VOXEL_OBSERVE_COMPLETE`, capture logs (including domain `collide=`), file presence, and worktree iron-ore hash stability. It does not inspect PNG pixels.

Worktree `builtin/core/resources/voxels/iron-ore.voxels.json` SHA256 `11B61B9E6650CDB66C6987FBE3EB8CCEA45C9B13F34BF9C61D1B4557E4494483` and `iron-ore/top.png` SHA256 `2F70C1C2AADD6BCBDD4BFB2436AEA595AA3C9B852C84518CEF3E189B0AE7AD65` were unchanged after observe. Mutable iron-ore JSON/PNG and the probe Mod lived only under the isolated temp user-data overlay.

## Procedure

1. Start the project and choose **进入切片 2 体素场景**, or run the observe script above.
2. Confirm the y=0 ground covering `0..31` plus the floating `14..17` cube, HUD target `(15,16,15)` occupied, `碰撞：命中 full_box`, count 1088. Debug collision boxes fill occupied cells.
3. Run `voxel remove 16 0 8`. Confirm a one-cell hole at the X=15/16 line, the +X wall of `(15,0,8)`, and no leftover face or leftover collision box in the hole. `voxel collide 16 0 8` → `Miss`; `voxel collide 15 0 8` → `Hit`.
4. `voxel reset`, add `(24,15,8)` and `(24,16,8)`, then `voxel remove 24 16 8`. Confirm a one-cell pillar near the camera whose top is the newly exposed +Y face. The removed cell has no collision box.
5. `voxel reset` then `voxel remove 8 0 16`. Confirm a ground hole at the Z=15/16 line and no collision box in the hole.
6. `voxel reset` then `voxel remove 16 0 16`. Confirm the four-chunk ground meeting hole without ghosting faces or leftover colliders.
7. `voxel reset` then `voxel damage 15 0 8 100 16 0 8 100 15 0 16 25`. Confirm count 1086 and durability 75 on `(15,0,16)`. Removed cells miss; the damaged cell still hits.
8. Reset, damage `(15,16,15)` by 20 so durability is 80, reload max 150, then reject a missing probe definition and confirm the published world and `碰撞：命中 full_box` stay.

Console-only checks (not in the eight PNGs; covered by headless smoke and `docs/console/voxel-collide.md`):

```text
voxel add 0 1 0 core:hollow px_py
voxel query 0 1 0
voxel collide 0 1 0
voxel add 0 1 0 core:iron-ore px_py
```

`query` shows occupancy; `collide` is `Miss`; a second add is `Occupied`.

## Automated observe result

Reran the delivered `-Slice 2` script on this worktree after the collision HUD / `DebugCollisionsHint` revision. Exit 0. Markers included `VOXEL_OBSERVE_COMPLETE` and eight `.godot/issue50-*.png`. Capture log:

- `issue50-initial`: 1088 voxels, 2272 faces, `collide=Hit`, HUD occupied `(15,16,15)` durability 100 / max 100, `碰撞：命中 full_box`.
- `issue50-x-boundary`: 1087 voxels, 2274 faces, last command `Removed (16,0,8)`. HUD target still `Hit` (selected cell is the floating cube, not the ground hole).
- `issue50-y-boundary`: 1089 voxels, 2278 faces, last command `Removed (24,16,8)`.
- `issue50-z-boundary`: 1087 voxels, 2274 faces, last command `Removed (8,0,16)`.
- `issue50-junction`: 1087 voxels, 2274 faces, last command `Removed (16,0,16)`.
- `issue50-cross-damage`: 1086 voxels, 2274 faces, last command two removals plus `Damaged (15,0,16) by 25 → 75`.
- `issue50-reload-150`: 1088 voxels, 2272 faces, HUD durability 80 / max 150, still `碰撞：命中 full_box`.
- `issue50-rejected`: 1089 voxels, 2276 faces, HUD still durability 80 / max 150 and `碰撞：命中 full_box`. Isolated probe remains; failure did not revert the published world or collision.

## Visual inspection of the current PNGs

Inspected every generated `.godot/issue50-*.png` after the successful delivered-script run. HUD in every frame says **切片 2 · 2×2×2 Chunk · 32³** and **碰撞：命中 full_box** at the selected floating-cube cell `(15,16,15)`. Debug collision hint draws cell-sized boxes on occupied voxels. The near-camera ground edge at the bottom of several frames is clipped by the fixed camera; that silhouette is the ground slab, not a leftover hole.

- `issue50-initial.png`: continuous orange y=0 ground; dark floating 4³; HUD `core:iron-ore` / `碰撞：命中 full_box`. Occupied cells show debug boxes.
- `issue50-x-boundary.png`: one square hole at the X=15/16 line after `Removed (16,0,8)`. Adjacent +X wall of `(15,0,8)` present; no leftover exterior face or leftover box in the hole.
- `issue50-y-boundary.png`: lone cell at `(24,15,8)` (1089 voxels) after `Removed (24,16,8)`. No second stacked cell at y=16.
- `issue50-z-boundary.png`: one square hole at the Z=15/16 line after `Removed (8,0,16)`.
- `issue50-junction.png`: one square hole at the center of the ground after `Removed (16,0,16)`, where four ground Chunks meet. No overlapping faces or leftover collider in the opening.
- `issue50-cross-damage.png`: two-cell hole on the X=15/16 line after same-event removals of `(15,0,8)` and `(16,0,8)`. Interior walls visible. Count 1086. No ghost face or leftover box at either removed cell.
- `issue50-reload-150.png`: closed 1088-voxel fixture. HUD durability 80, max 150, still `碰撞：命中 full_box`. No heal.
- `issue50-rejected.png`: HUD still durability 80 / max 150 and `碰撞：命中 full_box`. Count 1089 from the isolated probe. Failure did not restore max 100 or drop the published collision.

Headless `scripts/run-godot-voxel-smoke.ps1 -Slice 2` remains the automated integration evidence for cube-internal 15/16 faces, domain-vs-`IntersectShape` agreement, none occupancy, and collision live reload. Save was not exercised.

Final Acceptance-criteria confirmation remains the reviewer's responsibility.
