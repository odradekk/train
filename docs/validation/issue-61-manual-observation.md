# Issue #61 manual observation

This is the current-round visual record for #61 existing-vehicle content hot reload. Automated capture is not visual proof: `run-godot-voxel-observe.ps1 -Slice 3` only requires `VOXEL_OBSERVE_COMPLETE`, exit 0, and file presence. This file records an actual look at the PNGs.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1 -Slice 3
```

Godot `4.7.2.stable.mono.official.ed1daf0bf`, .NET SDK `9.0.318`, AMD Ryzen 9 5900HX. Isolated smoke/observe user-data and isolated built-in copies. Worktree iron-ore, ledge, refit-marker, identity-core, vehicle-shell, and hollow-shell hashes unchanged.

**Who viewed what:** the implementation agent opened each `.godot/issue61-*.png` after the windowed observe completed with `VOXEL_OBSERVE_COMPLETE`. COMPLETE is not treated as a visual pass.

## Automated script facts

Source: `.godot/issue61-observe-report.md`. The script does not inspect pixels.

| File | SHA256 |
| --- | --- |
| `.godot/issue61-reload-150.png` | `1E95446BA7CFB5CC3A574BE8A086C32758ADBE9B34D3F439436D80692D369821` |
| `.godot/issue61-reload-60.png` | `CCDA6B735F1E5BADEA3F29FB6E71EC6DD180AA7FF550ED766D8002F46A8E431C` |
| `.godot/issue61-display.png` | `BC69D80C85DDE6679967F6DA128F5AFE43DFABCD7191EFB339EABEB932236579` |
| `.godot/issue61-template.png` | `DFAFC96255E949042E1BC2E0549A8FFBEE5A601A5928B75668ADEC8487BCF2CC` |
| `.godot/issue61-collision.png` | `225F5A00EBEADDF320BBE4EF7A75EF6C4D48C97734315920E196B66593027022` |
| `.godot/issue61-rejected.png` | `225F5A00EBEADDF320BBE4EF7A75EF6C4D48C97734315920E196B66593027022` |
| `.godot/issue61-value-fracture.png` | `237047391EF0146CCB9E16FA55015E04B9BDCA7AEA03D56F34514DBEA85421C2` |

Capture log (engine, not pixel inspection): reload-150/60/display keep `voxels=0` world faces `0`, one 169-part vehicle, 346 composite faces. Template and later frames have two 169-part vehicles / 692 faces until value-fracture (two 27-part Disabled successors / 108 faces). Collision and rejected frames have `voxels=1` world faces `6` and domain `collide=Miss`. HUD 目标格 `(4,4,4)` 空 / 碰撞未命中.

`issue61-collision.png` and `issue61-rejected.png` are the same file (identical SHA-256). Missing `identity-core` reject did not change the published vehicle or world image.

## Visual conclusions

1. `issue61-reload-150.png` — wide parked-shell view: large peach envelope, no world-grid mesh. HUD 车辆 1 / 零件 169 / 显示面 346 / 世界体素 0. Official query in the same sequence required P durability 75 / max 150; the marker close-up is not this frame.
2. `issue61-reload-60.png` — close-up of P: yellow top with a T / +X mark, cyan sides, peach cavity around it. Same 169 / 346 / world 0. Layout and marker identity are unchanged after the max-60 clamp.
3. `issue61-display.png` — same camera and 169-part vehicle. Marker top is no longer the uniform yellow T: a cyan strip and yellow block appear on the brown top after the isolated name/asset publish. Side faces stay cyan. Semantic layout did not grow.
4. `issue61-template.png` — the parked marker cube is still present; a second peach 169-part shell appears to the side (new manufacture after the extra interior cell). HUD 车辆 2，各 169 零件 / 显示面 692 / 世界体素 0. Existing refit was not rebuilt onto the new blueprint cell.
5. `issue61-collision.png` — same two vehicles and marker; HUD 世界体素 1, domain `collide=Miss` after `vehicle-shell` kind `none`. No extra world-sized collision hull in the supported cell.
6. `issue61-rejected.png` — pixel-identical to the collision frame. Vehicles, marker, world cell, and collision miss stay after the rejected missing-core candidate.
7. `issue61-value-fracture.png` — two separate 27-part peach fragments after Value=20 reload and bridge cut. HUD 车辆 2：1 零件 27 Retained/Disabled；57 零件 27 Retained/Disabled。显示面 108 / 世界体素 0. Primary id stays on the right fragment.

Headless `run-godot-voxel-smoke.ps1 -Slice 3` is the automated official reload / query / collision / reject evidence. Save of complete vehicles remains `UnsupportedVehicles`.
