# Issue #52 manual observation

This is the current-round record for #52 rotating local boxes. It extends the slice 2 windowed observe entry (`-Slice 2`) without replacing historical `issue50-*.png` or `docs/validation/issue-5x-*.md` baselines.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1 -Slice 2
```

Automated facts: exit 0, `VOXEL_OBSERVE_COMPLETE`, eight `issue50-*.png`, five `issue52-*.png`, `.godot/issue52-observe-report.md`. The script does not inspect pixels.

This-round windowed run: Godot 4.7.2.stable.mono Compatibility on NVIDIA GeForce RTX 3080 Laptop GPU. HUD on `issue52-local-identity.png` / `rotated.png` shows `(16,15,8)` occupied `core:ledge` with `px_py` then `pz_py` and `碰撞：命中 local_box`. `removed.png` shows the cell empty and `碰撞：未命中`. `reloaded.png` / `rejected.png` keep occupancy, `px_py`, and `命中 local_box` after the corner-box publish and the overflowing reject. Pixel-level debug-box size is easier to judge live with `DebugCollisionsHint`; automated hit-region proof is the slice 2 smoke `IntersectShape` markers.

## Human steps in slice 2

1. Enter **进入切片 2 体素场景**. HUD at `(15,16,15)` should still say `碰撞：命中 full_box`. Debug boxes fill `full_box` cells.
2. `voxel add 15 1 8 core:ledge px_py`
3. `voxel collide 15 1 8` → `Hit` / `local_box`
4. `voxel collide 15.18 1.08 8.48 15.22 1.12 8.52` → `Hit`
5. `voxel collide 15.68 1.38 8.48 15.72 1.42 8.52` → `Miss` (still inside the cell)
6. `voxel remove 15 1 8` then `voxel add 15 1 8 core:ledge pz_py`
7. Repeat steps 4–5: the first probe becomes `Miss`, `15.48 1.38 8.18 15.52 1.42 8.22` becomes `Hit`
8. `voxel remove 15 1 8` then both probes `Miss`; no leftover debug box
9. Re-add identity, restore the isolated probe definition (the previous reject left it unloadable), change the isolated overlay ledge geometry, `reload`, and confirm the old pocket misses while the far-corner pocket hits
10. Write an overflowing `local_box` into the isolated overlay and `reload`: reject, previous corner hits stay. The fifth capture is `issue52-local-rejected.png`

Do not edit worktree `builtin/core/resources/voxels/ledge.voxels.json` or `iron-ore` files. Runtime mutation uses the isolated built-in overlay.
