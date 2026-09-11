# Issue #60 manual observation

This is the current-round visual record for #60 atomic refit. Automated capture is not visual proof: `run-godot-voxel-observe.ps1 -Slice 3` only requires `VOXEL_OBSERVE_COMPLETE`, exit 0, and file presence. This file records an actual look at the PNGs.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1 -Slice 3
```

Godot `4.7.2.stable.mono.official.ed1daf0bf`, .NET SDK `9.0.318`, NVIDIA GeForce RTX 3080 Laptop GPU / OpenGL 3.3 Compatibility. Isolated smoke user-data. Worktree iron-ore and ledge hashes unchanged.

**Who viewed what:** the R2 windowed observe at head `25303bd` launched the script and opened each `.godot/issue60-*.png` (and the retained issue58/issue59 frames). This R3-only round did not change textures, camera, or observe capture logic, so those PNGs were reused; COMPLETE is not treated as a visual pass. `issue60-moved.png` and `issue60-rotated.png` were compared by opening both images and by SHA-256; they are not the same file. Coordinator re-opened the same frames in the second review and confirmed the top-arrow change.

## Automated script facts

Source: `.godot/issue60-observe-report.md`. The script does not inspect pixels.

| File | SHA256 |
| --- | --- |
| `.godot/issue60-added.png` | `35B993B8C9735F59443C10AEFDB57A53C80C49E95AE446F209A080F6A5548D41` |
| `.godot/issue60-moved.png` | `D286A063FC493145CC9938C13A6A3FBE31996CA020CA5703E2015A384663399A` |
| `.godot/issue60-rotated.png` | `02F8E96334C9DBBBBBC61F5BB8A934DAD126202D982F1DA3A84AB0202D096262` |
| `.godot/issue60-swapped.png` | `8574F4C031B42C5C76658B590277797C3555503FD49F95DF89F509EDF31F3433` |
| `.godot/issue60-replaced.png` | `B7EC79437EBC0289503695C0AF1D3CF14AF242C7CDD1EAD930DB21B5217372D0` |
| `.godot/issue60-removed.png` | `46E062CB3439C3C085F414AC9CDD8F9B6D95D03D30E1F0F4C614CFDB36CC82D8` |
| `.godot/issue60-rejected.png` | `F3B5C7AB42FBB8679F59B85C7E975D12AE03FB4A638BC0A9E2D5CDBCAB733E2F` |
| `.godot/issue60-refit-fracture.png` | `DF126E1BB13950653762BCE0B8712067881EE8BCA11F177E6E08A9DBC0C93A98` |

Capture log (engine, not pixel inspection): refit frames have `voxels=0` world faces `0`, one composite mesh except fracture (two). HUD 目标格 `(4,4,4)` 空 / 碰撞未命中. Part counts: added/moved/rotated/removed/rejected = 169; swapped/replaced = 170; refit-fracture = two vehicles × 27.

## Visual conclusions

Refit close-ups use a downward interior camera on the peach cavity floor so the marker **top** is readable. Marker definition: yellow top with a dark arrow toward local +X, cyan sides with a white +X arrow. Replacement definition: orange-red, max durability 150. `VoxelCubeUv.TopCorners` turns the top pattern with orientation.

1. `issue60-added.png` — one yellow-top cyan cube on the peach inner floor; top arrow points screen-right (world +X / `px_py`). HUD 零件 169 / 显示面 346 / 世界体素 0.
2. `issue60-moved.png` — same cube, same `px_py` top arrow pointing screen-right, now framed on `(3,1,0)`. SHA-256 `D286A063…`. Still one extra part, no world mesh.
3. `issue60-rotated.png` — **not** the moved frame (SHA-256 `02F8E963…`). Same cell and camera; the top arrow now points toward the top of the frame / away from the camera (world −Z after `nz_py`; the camera sits on the target’s +Z side). Side faces stay cyan. Query/`nz_py` was not used as a substitute for this image difference.
4. `issue60-swapped.png` — two yellow-top cyan cubes: Q at `(3,1,0)` with a right-pointing top arrow, P at `(4,1,0)` still `nz_py`. HUD 零件 170. No world mesh.
5. `issue60-replaced.png` — the near cube is solid orange-red (new R). P remains at the right with the `nz_py` top arrow. Replacement does not keep Q’s cyan/arrow look.
6. `issue60-removed.png` — orange cube gone; only P remains, still `nz_py` with the top arrow toward the top of the frame / away from the camera. HUD 零件 169.
7. `issue60-rejected.png` — exterior three-quarter of one intact parked shell on the gray void. No world-grid fragments, no second mesh, HUD 零件 169 / 世界体素 0. Failed batches did not drop parts.
8. `issue60-refit-fracture.png` — two separate 27-part parked boxes after adding a second bridge and cutting both in one event. HUD 车辆 2 / 世界体素 0 / 显示批次 2. No world-grid duplicate.

Retained regression frames: `issue58-empty` empty 32³; `issue58-exterior` closed brown/cream shell, world 0; `issue58-interior` peach inner corner; `issue58-reset` vehicle gone; `issue59-fracture` two independent parked boxes, world 0; `issue59-partial` / `issue59-drop` fragments appear once in the world mesh only after official damage.

No stretch, shear, or a vehicle part also drawn as a world voxel was visible in these frames.

## Manual console path

From the start menu choose **进入切片 3 静置车辆场景**, then the formal steps in `docs/console/README.md`（切片 3 五类原子改装，以及改装后双桥断裂）。Read `allocated` lines; do not hard-code runtime IDs.
