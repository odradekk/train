# Issue #60 manual observation

This is the current-round visual record for #60 atomic refit. Automated capture is not visual proof: `run-godot-voxel-observe.ps1 -Slice 3` only requires `VOXEL_OBSERVE_COMPLETE`, exit 0, and file presence. This file records an actual look at the PNGs.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1 -Slice 3
```

Godot `4.7.2.stable.mono.official.ed1daf0bf`, .NET SDK `9.0.318`, NVIDIA GeForce RTX 3080 Laptop GPU / OpenGL 3.3 Compatibility. Isolated smoke user-data. Worktree iron-ore and ledge hashes unchanged.

**Who viewed what:** this implementation agent launched the windowed observe script, then opened each `.godot/issue60-*.png` (and the retained issue58/issue59 frames). COMPLETE is not treated as a visual pass.

## Automated script facts

Source: `.godot/issue60-observe-report.md`. The script does not inspect pixels.

| File | SHA256 |
| --- | --- |
| `.godot/issue60-added.png` | `34D136D6B53F21B0902A771A7FA4EE430546A2BF755BAA9C4E75C13CF8045652` |
| `.godot/issue60-moved.png` | `28249AF48261178626916B6F2272829D39170C1E4C69E389B4383792328021FD` |
| `.godot/issue60-rotated.png` | `28249AF48261178626916B6F2272829D39170C1E4C69E389B4383792328021FD` |
| `.godot/issue60-swapped.png` | `19F15D5DE6FC431A921DC3DC49CE8FB6E0F2E8C3EAEE2D3C11C4FCE48759ABF6` |
| `.godot/issue60-replaced.png` | `51A4D13132D967EAAEE1F1EAA5B373D0706CCCB49C3CC7F8F27DBB06E3C30222` |
| `.godot/issue60-removed.png` | `C454E2A895CEA5FBBB16009BB79E3A5F631A21858EDF37B54D69860CE23E84B5` |
| `.godot/issue60-rejected.png` | `A8DA0F91D39F5F963EFF0DE624171CD850E2B33F951419B63D0D4F0D7A4B5E74` |

Capture log (engine, not pixel inspection): every issue60 frame has `voxels=0` world faces `0`, one composite mesh, HUD 目标格 `(4,4,4)` 空 / 碰撞未命中. Part counts: added/moved/rotated/removed/rejected = 169 and 346 composite faces; swapped/replaced = 170.

## Visual conclusions

Interior refit camera sits in the hollow cavity looking at local `(2,1,0)`–`(4,1,0)` on the peach inner floor. Marker definition: yellow top, cyan side, white +X arrow. Replacement definition: orange-red, max durability 150.

1. `issue60-added.png` — one yellow-top cyan cube with a white arrow on the visible side, sitting on the inner floor; HUD 零件 169 / 显示面 346 / 世界体素 0. Marker is distinct from the brown/peach shell.
2. `issue60-moved.png` — the same yellow-top cyan cube is closer to the camera (moved to `(3,1,0)`). Still one extra part, no world mesh.
3. `issue60-rotated.png` — pixel-identical to the moved frame from this camera. `+Y` stays world up after `nz_py`, and all four sides share `side.png`, so this angle does not show a new silhouette. Official query in smoke/domain recorded `orientation=nz_py`. The arrow is clearly readable on the added frame.
4. `issue60-swapped.png` — still a yellow-top cyan cube in the near cell (Q at `(3,1,0)` after the atomic swap). HUD 零件 170. No second world mesh.
5. `issue60-replaced.png` — the near cube is now solid orange-red (new R). A yellow sliver of P remains at the right (`(4,1,0)`). Replacement is a different definition and does not keep Q’s cyan/arrow look.
6. `issue60-removed.png` — orange cube gone; P remains at the right as cyan/yellow with the white arrow. HUD 零件 169. Interior floor empty where R was.
7. `issue60-rejected.png` — exterior three-quarter of one intact parked shell on the gray void. No world-grid fragments, no second mesh, HUD 零件 169 / 世界体素 0. Failed batches did not drop parts.

Retained regression frames: `issue58-empty` empty 32³; `issue58-exterior` closed brown/cream shell, world 0; `issue58-interior` peach inner corner; `issue58-reset` vehicle gone; `issue59-fracture` two independent parked boxes, world 0; `issue59-partial` / `issue59-drop` fragments appear once in the world mesh only after official damage.

No stretch, shear, or a vehicle part also drawn as a world voxel was visible in these frames.

## Manual console path

From the start menu choose **进入切片 3 静置车辆场景**, then the formal steps in `docs/console/README.md`（切片 3 五类原子改装）. Read `allocated` lines; do not hard-code runtime IDs.
