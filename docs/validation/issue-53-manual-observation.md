# Issue #53 manual observation

This is the current-round record for #53 world save, process restart, and resume. Automated collision proof after a real process restart remains the slice 2 two-process smoke (`IntersectShape` markers). Windowed resume observation below is a **new** save → quit → new-process load sequence. Historical `issue50-*.png` / `issue52-*.png` and `run-godot-voxel-observe.ps1 -Slice 2` are not resume evidence.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe-resume.ps1
```

The script owns a temporary `TRAIN_MODS_SMOKE_USER_DATA` directory shared by both processes and deletes it afterwards. Worktree `builtin/core/resources/voxels` iron-ore / ledge hashes must stay unchanged.

## Automated script facts

Not yet run on this revision. After a successful run, `.godot/issue53-resume-observe-report.md` holds engine version, `VOXEL_OBSERVE_RESUME_WRITTEN` / `LOADED` / `COMPLETE`, exit codes, and SHA256 of:

- `docs/validation/issue-53-resume/issue53-resume-restored.png`
- `docs/validation/issue-53-resume/issue53-resume-chunk-boundary.png`
- `docs/validation/issue-53-resume/issue53-resume-ground-hole.png`
- `docs/validation/issue-53-resume/issue53-resume-ledge.png`
- `docs/validation/issue-53-resume/issue53-resume-continue-damage.png`

The script does not inspect pixels.

## Visual conclusions

Pending a completed windowed run of the script above. These PNG files must be read from this revision's new-process load; do not substitute older observe frames or headless markers.

Expected after looking at the files:

1. `issue53-resume-restored.png` — HUD target `(14,14,14)` occupied `core:iron-ore`, durability 75, count 1089.
2. `issue53-resume-chunk-boundary.png` — after load, `(16,0,8)` removed on the x=15/16 ground seam; +X wall of `(15,0,8)` visible, hole has no leftover face.
3. `issue53-resume-ground-hole.png` — one-cell hole at `(4,0,4)` with a newly exposed neighbor wall; HUD says empty / 未命中.
4. `issue53-resume-ledge.png` — `(15,1,8)` `core:ledge` `pz_py`, HUD `碰撞：命中 local_box`, debug box smaller than the cell and shifted with yaw.
5. `issue53-resume-continue-damage.png` — `(14,14,14)` empty after damage 75, count 1088.

## Manual console path

Use an isolated user-data directory if you need to avoid the real `user://saves` folder. From the start menu choose **进入切片 2 体素场景**, then the formal console steps in `docs/console/README.md` (§保存、退出进程并恢复后继续修改).

Do not edit worktree `builtin/core/resources/voxels/*.voxels.json`. Runtime mutation uses the isolated built-in overlay in smoke/observe; normal play writes `user://saves/<UUID>/save.json`.
