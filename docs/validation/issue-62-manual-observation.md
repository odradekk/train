# Issue #62 manual observation

This is the current-round visual record for #62 actual vehicle save and cross-process restore. Automated capture is not visual proof: `run-godot-voxel-observe-resume.ps1 -Slice 3` only requires `VOXEL_OBSERVE_RESUME_COMPLETE`, exit 0, and file presence. This file records an actual look at the PNGs generated **after** the new process loaded the restored session. These frames are not reused `issue58-*.png` / `issue60-*.png` / `issue61-*.png`.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe-resume.ps1 -Slice 3
```

Godot `4.7.2.stable.mono.official.ed1daf0bf`, .NET SDK `9.0.318`, AMD Ryzen 9 5900HX. Isolated `TRAIN_MODS_SMOKE_USER_DATA` and isolated built-in copies. Worktree iron-ore, ledge, refit-marker, and hollow-shell hashes unchanged.

**Who viewed what:** the implementation agent opened each `docs/validation/issue-62-resume/issue62-resume-*.png` after process 2 printed `VOXEL_OBSERVE_RESUME_COMPLETE`. COMPLETE is not treated as a visual pass.

## Automated script facts

Source: `.godot/issue62-resume-observe-report.md`. The script does not inspect pixels.

Process 1 wrote UUID `b379112c-87e6-4ab7-aea1-1dd621552767` (`bytes=68001`, vehicles=4, world=26) and exited. Process 2 loaded the same UUID (`name=Slice 3 Persist`) before any capture.

| File | SHA256 |
| --- | --- |
| `docs/validation/issue-62-resume/issue62-resume-hollow.png` | `55911D958D18120C6413D538F031DFBFFCFDE0DF83587ADE7877C74726EC82A2` |
| `docs/validation/issue-62-resume/issue62-resume-refit.png` | `5EB6656705479CC70626C1AC9DCA15D594031C9A4A916D5AD9C76FFFA7E7660C` |
| `docs/validation/issue-62-resume/issue62-resume-fracture.png` | `D9E298FFDFBE7D6F2363F55A1960D220D9DE6F31D44756403A29702CF8AB0CFD` |
| `docs/validation/issue-62-resume/issue62-resume-dropped.png` | `5BFFE1CF5F66D24751936E5556C4F01F0948FE910360081E24379A97403E16B2` |

Capture log (engine, not pixel inspection): all four frames keep `voxels=26` world faces `60`, four vehicles (`1` 169-part Retained/Unevaluated; `173`/`229`/`230` each 27-part Retained/Disabled), 4 mesh batches / 508 composite faces. Hollow/refit/fracture domain `collide=Miss` at empty `(4,4,4)`. Dropped frame `collide=Hit` `full_box` at occupied `(8,8,20)` `core:vehicle-shell`.

## Visual conclusions

1. `issue62-resume-hollow.png` — camera is inside the restored parked shell. The cavity is empty peach/orange interior, with no second copy of the shell, no leftover world-grid fill, and no stretched or sheared faces. HUD 车辆 4 / 零件 169+27+27+27 / 显示批次 4 面 508 / 世界体素 26.
2. `issue62-resume-refit.png` — close-up of restored marker P: one yellow cube, white T on the top, blue side, no ghost cube beside it. Axis-aligned, not stretched. This is the post-restore P at local `(4,1,0)` after the five-class refit (Q/R are not a second marker in this shot).
3. `issue62-resume-fracture.png` — two separate tan/brown successor blocks with a visible gap where the bridge was, plus a smaller brown piece farther back. Two distinct meshes, no overlapping double-draw or residual bridge span.
4. `issue62-resume-dropped.png` — a 3×3×3-ish world-grid `vehicle-shell` stack at `(8,8,20)` and a separate tan vehicle fragment to the right. Cubes stay on the world grid (no shear). HUD reports occupied `core:vehicle-shell` and `full_box` hit. No duplicate occupancy of the same cells as the fragment.

Headless `run-godot-voxel-smoke.ps1 -Slice 3` is the automated two-process official query / ownership / continue-refit / reload / resave evidence (`COMPOSITE_PERSIST_SMOKE_OK` after domain and display-ownership checks).
