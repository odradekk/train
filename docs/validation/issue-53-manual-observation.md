# Issue #53 manual observation

This is the current-round record for #53 world save, process restart, and resume. Automated collision proof after a real process restart is the slice 2 two-process smoke (`IntersectShape` markers). Windowed observe below re-runs the existing slice 2 visual sequence; it does not replace a human save/quit/restart/load pass.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1 -Slice 2
```

Automated facts: exit 0, `VOXEL_OBSERVE_COMPLETE`, historical `issue50-*.png` and `issue52-*.png`, `.godot/issue50-observe-report.md` and `.godot/issue52-observe-report.md`. The script does not inspect pixels.

This-round windowed run: Godot 4.7.2.stable.mono Compatibility on NVIDIA GeForce RTX 3080 Laptop GPU. HUD on the initial slice 2 frame still shows `(15,16,15)` occupied `core:iron-ore` with `碰撞：命中 full_box`.

## Human save / quit / restart / load

Use an isolated user-data directory if you need to avoid the real `user://saves` folder. From the start menu choose **进入切片 2 体素场景**, then the formal console steps in `docs/console/README.md` (§保存、退出进程并恢复后继续修改):

1. `voxel reset`
2. Fixed sequence: remove `(4,0,4)`; add `core:hollow` at `(4,1,4)`; damage `(14,14,14)` by 25; remove and re-add `(17,17,17)` as `core:iron-ore nz_py`; add `core:ledge pz_py` at `(15,1,8)`
3. Confirm `voxel collide 4 1 4` is `Miss`, `14 14 14` is `Hit`/`full_box`, and `15.48 1.38 8.18 15.52 1.42 8.22` is `Hit`/`local_box`
4. `voxel save Slice 2 Resume` and copy the `id` line
5. `quit` — this must exit the game process, not only return to the menu
6. Start the same build again, enter slice 2 from the menu, `voxel load <id>`
7. Repeat the three collision queries; `(14,14,14)` durability stays 75
8. `voxel damage 14 14 14 75` — the cell is empty; compare with an uninterrupted session that applies the same extra 75 after the sequence
9. Optional: `reload` after raising iron-ore max to 150 in the isolated overlay — remaining instances keep absolute durability

Do not edit worktree `builtin/core/resources/voxels/*.voxels.json`. Runtime mutation uses the isolated built-in overlay in smoke/observe; normal play writes `user://saves/<UUID>/save.json`.
