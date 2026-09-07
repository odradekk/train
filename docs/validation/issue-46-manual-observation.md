# Issue #46 visual observation record

Environment: Godot `4.7.2.stable.mono.official.ed1daf0bf`, Windows display server, OpenGL 3.3 Compatibility, NVIDIA GeForce RTX 3080 Laptop GPU (driver 596.49), CPU AMD Ryzen 9 5900HX.

This is the current-round record for #46. Historical #45 PNGs captured in the same windowed run are listed separately and are not reused as #46 proof.

Delivered observe command (frame-budget `--quit-after 300`, not a diagnostic 120-frame one-off). The scene quits itself after `VOXEL_OBSERVE_COMPLETE`; 300 is only the fail-closed safety timeout. Captures run after hold frames so HUD and mesh have finished the current interface frame.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1
```

PNGs under `.godot/issue46-*.png` and `.godot/issue45-*.png` are local evidence and are not committed. The auto-generated `.godot/issue45-observe-report.md` records only script exit, `VOXEL_OBSERVE_COMPLETE`, capture logs, and file presence.

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
10. Reload display text to `热重载铁矿` and replace `top.png` with a solid green cube face. Confirm the existing instance keeps durability 80 / max 150 and the top face plus HUD name update.
11. Reload max durability 60. Confirm absolute durability clamps to 60 and the green top / new name remain.
12. Add an isolated probe voxel from a smoke-only Mod, then make that required definition unloadable. Confirm the whole reload is rejected, durability stays 60, the probe instance remains, and the published display does not revert or advance.

## Automated observe result

Reran the delivered script on this worktree after the live-reload implementation. Exit 0. Markers included `VOXEL_OBSERVE_OPEN_CAVITY`, `VOXEL_OBSERVE_COMPLETE`, six historical `.godot/issue45-*.png`, and five current `.godot/issue46-*.png`. Settlement `VOXEL_OBSERVE_MULTI_SETTLE_MS=0.780`. Capture log:

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
- `issue46-display`: 512 voxels, 384 faces, HUD name `热重载铁矿`, durability 80 / max 150, top face solid green, sides still iron-ore brown.
- `issue46-reload-60`: 512 voxels, 384 faces, HUD durability 60 / max 60, name and green top retained.
- `issue46-rejected`: 513 voxels, 390 faces, HUD still `热重载铁矿` / durability 60 / max 60 at `(4,4,4)`. A second occupied cell (the isolated probe at `(0,0,0)`) remains visible. Last command is still the earlier damage line, not a world reset.

## Visual inspection of the current #46 PNGs

Inspected every generated `.godot/issue46-*.png` after the successful delivered-script run:

- `issue46-damaged-80.png`: continuous 8³ iron-ore block; HUD occupied durability 80 / max 100.
- `issue46-reload-150.png`: same closed exterior and rust cube faces; HUD still durability 80, max now 150. No new instance, no heal bar, no orientation change.
- `issue46-display.png`: HUD name is `热重载铁矿`; the visible +Y face is green while the +X face stays brown. Occupancy and durability 80 / max 150 unchanged.
- `issue46-reload-60.png`: same green top and new name; HUD durability 60 / max 60. The block is still one occupied 8³ volume.
- `issue46-rejected.png`: `(4,4,4)` HUD still shows the previous published name, green top, and 60 / 60. The extra probe instance is still present (513 voxels / 390 faces). Failure did not restore the pre-display rust top or delete the probe.

Inspected the same-run historical `.godot/issue45-*.png` only to confirm the parent sequence still matches #45: continuous start, durability 75 without a hole, corner removal, closed cavity not visible, one-cell interior opening, and the multi-target HUD listing both removals plus `Damaged (6,4,4) by 25 → 75`.

Headless `scripts/run-godot-voxel-smoke.ps1 -Slice 1` remains separate automated integration evidence.

Final Acceptance-criteria confirmation remains the reviewer's responsibility.
