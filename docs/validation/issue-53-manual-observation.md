# Issue #53 manual observation

This is the current-round record for #53 world save, process restart, and resume. It **replaces** the previous “Not yet run / Pending” notes on head `84081a0`. Automated collision proof after a real process restart remains the slice 2 two-process smoke (`IntersectShape` markers). Windowed resume observation is a **new** save → quit → new-process load sequence. Historical `issue50-*.png` / `issue52-*.png` and `run-godot-voxel-observe.ps1 -Slice 2` are not resume evidence.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe-resume.ps1
```

The script owns a temporary `TRAIN_MODS_SMOKE_USER_DATA` directory shared by both processes and deletes it afterwards. Worktree `builtin/core/resources/voxels` iron-ore / ledge hashes must stay unchanged.

**Who ran what:** the coordinator/reviewer ran the script on this worktree (`84081a0` + SDK `9.0.318`) and viewed the five PNGs. This agent checked that the five files exist, that their SHA256 matches `.godot/issue53-resume-observe-report.md`, and read the images for the visual section below. It did not launch Godot again.

## Automated script facts

Source: `.godot/issue53-resume-observe-report.md` and `D:/Code/train/.git/review53-r2-evidence/observe-resume.log`. Godot `4.7.2.stable.mono.official.ed1daf0bf`, CPU AMD Ryzen 9 5900HX. Process 1 printed `VOXEL_OBSERVE_RESUME_WRITTEN id=2df8878b-7ea1-4391-9d1d-d294ffef605e bytes=1130 voxels=1089` and exited. Process 2 was a new Godot process with the same isolated user-data and `TRAIN_VOXEL_OBSERVE_SAVE_ID`; it printed `VOXEL_OBSERVE_RESUME_LOADED` for that UUID, `VOXEL_OBSERVE_RESUME_COMPLETE`, and exit 0. The script does not inspect pixels.

| File | SHA256 |
| --- | --- |
| `docs/validation/issue-53-resume/issue53-resume-restored.png` | `2958C571B7E96AF46ABB8C003169541C839036AD80B70336C4552DFFF61A0B05` |
| `docs/validation/issue-53-resume/issue53-resume-chunk-boundary.png` | `0271AC0761984216263C17605EDFC6D51F908855C40D35732546933ECFB8A9DD` |
| `docs/validation/issue-53-resume/issue53-resume-ground-hole.png` | `06EAD550E9384454D2858879CED96357D157508798D5FA8144EED326D381F6CC` |
| `docs/validation/issue-53-resume/issue53-resume-ledge.png` | `32A97C732DFA736682D7EBC9EC523DBA90D647BCC74109B814B50A8EC5DD4470` |
| `docs/validation/issue-53-resume/issue53-resume-continue-damage.png` | `0DBDFA5DE9ACF827B0BD06CEB276B173E1F6B3953892B12FF5C10653399134BA` |

Capture log (engine, not pixel inspection):

- `issue53-resume-restored` voxels=1089 collide=Hit HUD `(14,14,14)` occupied `core:iron-ore` durability 75 `full_box`
- `issue53-resume-chunk-boundary` voxels=1088 collide=Miss HUD `(16,0,8)` empty
- `issue53-resume-ground-hole` voxels=1089 collide=Miss HUD `(4,0,4)` empty
- `issue53-resume-ledge` voxels=1089 collide=Hit HUD `(15,1,8)` `core:ledge` `pz_py` `local_box`
- `issue53-resume-continue-damage` voxels=1088 collide=Miss HUD `(14,14,14)` empty; result `Removed (14,14,14) by 75 → 0`

## Visual conclusions

These are look-at-the-PNG conclusions, not physics tests. Collision correctness is the smoke `IntersectShape` markers and the domain `collide=` lines above. The reviewer viewed all five frames and reported no obvious missing faces or leftover deleted geometry. This agent opened the same five files and agrees with that reading and with the HUD text in each frame:

1. `issue53-resume-restored.png` — slice 2 HUD: target `(14,14,14)` occupied `core:iron-ore`, durability 75, count implied by restored cube + ground; orange ground and floating 14..17 cube present; no stale hole at the cube cell.
2. `issue53-resume-chunk-boundary.png` — HUD `(16,0,8)` empty / 未命中 after the restored-world X=15/16 ground remove; a one-cell ground gap is visible; no leftover face in the hole.
3. `issue53-resume-ground-hole.png` — HUD `(4,0,4)` empty / 未命中; one-cell hole in the orange ground with a newly exposed neighbor wall.
4. `issue53-resume-ledge.png` — HUD `(15,1,8)` occupied `core:ledge` `pz_py`, `碰撞：命中 local_box`; debug collision hint shows a box smaller than a full cell, offset with yaw.
5. `issue53-resume-continue-damage.png` — HUD `(14,14,14)` empty / 未命中; last command `Removed (14,14,14) by 75 → 0`; the cube cell is open, no leftover face in that cell.

This is an automatically driven windowed capture plus human/agent reading of the PNGs. It is not a claim that a person typed the menu and console path by hand.

## Manual console path

Use an isolated user-data directory if you need to avoid the real `user://saves` folder. From the start menu choose **进入切片 2 体素场景**, then the formal console steps in `docs/console/README.md` (§保存、退出进程并恢复后继续修改).

Do not edit worktree `builtin/core/resources/voxels/*.voxels.json`. Runtime mutation uses the isolated built-in overlay in smoke/observe; normal play writes `user://saves/<UUID>/save.json`.
