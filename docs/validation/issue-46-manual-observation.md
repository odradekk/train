# Issue #46 visual observation record

Environment: Godot `4.7.2.stable.mono.official.ed1daf0bf`, Windows display server, OpenGL 3.3 Compatibility, NVIDIA GeForce RTX 3080 Laptop GPU (driver 596.49), CPU AMD Ryzen 9 5900HX.

This is the current-round record for #46 after the R1/R2 revision. Historical #45 PNGs captured in the same windowed run are listed separately and are not reused as #46 proof. The previous implementation pass used a solid green top and wrote `res://builtin` iron-ore files; that is not this-round evidence.

Delivered observe command (frame-budget `--quit-after 300`, not a diagnostic 120-frame one-off). The scene quits itself after `VOXEL_OBSERVE_COMPLETE`; 300 is only the fail-closed safety timeout. Captures run after hold frames so HUD and mesh have finished the current interface frame.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1
```

PNGs under `.godot/issue46-*.png` and `.godot/issue45-*.png` are local evidence and are not committed. The auto-generated `.godot/issue45-observe-report.md` records only script exit, `VOXEL_OBSERVE_COMPLETE`, capture logs, file presence, and worktree iron-ore hash stability. It does not inspect PNG pixels.

Worktree `builtin/core/resources/voxels/iron-ore.voxels.json` SHA256 `11B61B9E6650CDB66C6987FBE3EB8CCEA45C9B13F34BF9C61D1B4557E4494483` and `iron-ore/top.png` SHA256 `2F70C1C2AADD6BCBDD4BFB2436AEA595AA3C9B852C84518CEF3E189B0AE7AD65` were unchanged after observe. Mutable iron-ore JSON/PNG and the probe Mod lived only under the isolated temp user-data overlay.

## Procedure

Parent #39 slice 1 sequences (same as #45):

1. Start the project and choose **进入体素开发场景**, or run the observe script above.
2. Confirm the initial 8³ block is continuous and HUD says `(4,4,4)` is `core:iron-ore`, `px_py`, durability `100`, count 512.
3. Run `voxel damage 4 4 4 25`. Confirm the cell remains and durability is `75`.
4. Run `voxel damage 4 4 4 75`. Confirm the corner is empty, count 511, and newly exposed neighbor faces are present with no stale exterior face.
5. Run `voxel reset` then `voxel damage 5 5 5 100` and `voxel query 5 5 5`. Confirm empty / 511 / 390 faces. The closed cavity is not claimed to be visible from the fixed camera.
6. Run `voxel damage 4 5 5 100`. Confirm a one-cell-deep opening from the visible `-X` exterior and newly exposed interior faces with no overlap, gap, or stale face.
7. Run `voxel reset` then `voxel damage 4 4 4 100 5 4 4 100 6 4 4 25` and `voxel query 6 4 4`. Confirm count 510 and durability 75 on the third cell.

Current #46 live-reload sequence:

8. Reset, then formally damage `(4,4,4)` by 20 so durability is 80.
9. Reload the published iron-ore definition with max durability 150. Confirm occupancy, `px_py`, and absolute durability 80; HUD max becomes 150. Do not expect a heal.
10. Reload display text to `热重载铁矿` and replace isolated `top.png` / `side.png` with distinct 16×16 patterns (top: rust, yellow strip on V=0, cyan on U=1; side: rust, green strip on V=0, magenta on U=1). Confirm existing instances keep durability 80 / max 150. Identity tops show yellow toward +Z with cyan at +X/+Z; identity sides show the green/magenta pattern.
11. Reorient visible top cell `(4,11,4)` to `pz_py` (Yaw90) and `(5,11,4)` to `px_pz`. Confirm the yaw cell's top pattern rotates, and the tilted cell's world top shows the mapped side pattern (green/magenta) because local up is now world +Z. After mesh refresh, generated +Y UVs at identity `(6,11,4)`, yaw `(4,11,4)`, and tilt `(5,11,4)` match `VoxelCubeUv`. Engine log: `VOXEL_RELOAD_TOP_UV identity=(0,0)=0,1 ... yaw90=(0,0)=0,0 ... tilt=(0,0)=0,1`.
12. Reload max durability 60. Confirm absolute durability clamps to 60 and the asymmetric top / new name / reoriented cells remain.
13. Add an isolated probe voxel from a smoke-only Mod, then make that required definition unloadable. Confirm the whole reload is rejected, durability stays 60, the probe instance remains, orientations stay `pz_py` / `px_pz`, and the published asymmetric display does not revert or advance.

## Automated observe result

Reran the delivered script on this worktree after the isolation and UV revision. Exit 0. Markers included `VOXEL_OBSERVE_OPEN_CAVITY`, `VOXEL_OBSERVE_COMPLETE`, six historical `.godot/issue45-*.png`, and six current `.godot/issue46-*.png`. Settlement `VOXEL_OBSERVE_MULTI_SETTLE_MS=0.328`. Capture log:

### Historical captures (same run, not #46 proof)

- `issue45-initial`: 512 voxels, 384 faces, HUD occupied durability 100 / max 100, last command 场景已进入.
- `issue45-partial`: 512 voxels, 384 faces, HUD durability 75, last command `Damaged (4,4,4) by 25 → 75`.
- `issue45-destroyed`: 511 voxels, 384 faces, HUD empty, last command `Removed (4,4,4) by 75 → 0`.
- `issue45-internal-cavity`: 511 voxels, 390 faces, HUD occupied `(4,4,4)` durability 100, last command `Removed (5,5,5) by 100 → 0`.
- `issue45-internal-opening`: 510 voxels, 392 faces, HUD occupied durability 100, last command `Removed (4,5,5) by 100 → 0`.
- `issue45-multi`: 510 voxels, 384 faces, HUD empty, last command `Removed (4,4,4) by 100 → 0；Removed (5,4,4) by 100 → 0；Damaged (6,4,4) by 25 → 75`.

### Current #46 captures

- `issue46-damaged-80`: 512 voxels, 384 faces, HUD `core:iron-ore` / 铁矿 / `px_py` / durability 80 / value 10 / mass 1 / max 100, last command `Damaged (4,4,4) by 20 → 80`.
- `issue46-reload-150`: 512 voxels, 384 faces, same occupancy and orientation, durability still 80, max 150. Exterior still closed; no heal.
- `issue46-display`: 512 voxels, 384 faces, HUD name `热重载铁矿`, durability 80 / max 150. Identity tops show rust / yellow / cyan; the visible +X side shows rust / green / magenta.
- `issue46-oriented`: 512 voxels, 384 faces, same HUD at `(4,4,4)`. Yaw90 `(4,11,4)` keeps the top pattern but rotates the yellow/cyan corners. Tilt `px_pz` `(5,11,4)` world top shows the green/magenta side pattern because that world face is now the local −Z side.
- `issue46-reload-60`: 512 voxels, 384 faces, HUD durability 60 / max 60, name and rotated/tilted tops retained.
- `issue46-rejected`: 513 voxels, 390 faces, HUD still `热重载铁矿` / durability 60 / max 60 at `(4,4,4)`. The isolated probe at `(0,0,0)` remains. Failure did not restore the pre-display rust-only top, drop the probe, or reset the two reoriented cells.

## Visual inspection of the current #46 PNGs

Inspected every generated `.godot/issue46-*.png` after the successful delivered-script run:

- `issue46-damaged-80.png`: continuous 8³ iron-ore block; HUD occupied durability 80 / max 100. Top is the published rust iron-ore face, not the later test pattern.
- `issue46-reload-150.png`: same closed exterior and rust cube faces; HUD still durability 80, max now 150. No new instance, no heal bar, no orientation change.
- `issue46-display.png`: HUD name is `热重载铁矿`. Identity tops show a rust field, a yellow strip along the local +Z edge, and a cyan block at local +X/+Z. The visible +X side shows the distinct green strip / magenta block. Occupancy and durability 80 / max 150 unchanged.
- `issue46-oriented.png`: most tops stay identity yellow/cyan. `(4,11,4)` Yaw90 rotates that top pattern. `(5,11,4)` `px_pz` replaces the world-top image with the green/magenta side pattern, which is the visible proof that tilt maps the local side onto world +Y. Generated-mesh UV readback matched `VoxelCubeUv` for identity, yaw, and tilt tops.
- `issue46-reload-60.png`: same asymmetric tops, mapped tilt side, and new name; HUD durability 60 / max 60. The block is still one occupied 8³ volume.
- `issue46-rejected.png`: `(4,4,4)` HUD still shows the previous published name, asymmetric top, and 60 / 60. The extra probe instance is still present (513 voxels / 390 faces). Failure did not restore the pre-display rust top, drop orientations, or delete the probe.

Inspected the same-run historical `.godot/issue45-*.png` only to confirm the parent sequence still matches #45: continuous start, durability 75 without a hole, corner removal, closed cavity not visible, one-cell interior opening, and the multi-target HUD listing both removals plus `Damaged (6,4,4) by 25 → 75`. Face cull and winding were re-checked by `ValidateGeneratedMesh` on the smoke path after orientation mapping.

Headless `scripts/run-godot-voxel-smoke.ps1 -Slice 1` and `-IsolationAbort` remain separate automated integration evidence.

Final Acceptance-criteria confirmation remains the reviewer's responsibility.
