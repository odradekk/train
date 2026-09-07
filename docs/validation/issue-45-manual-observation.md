# Issue #45 visual observation record

Environment: Godot `4.7.2.stable.mono.official.ed1daf0bf`, Windows display server, OpenGL 3.3 Compatibility, NVIDIA GeForce RTX 3080 Laptop GPU (driver 596.49), CPU AMD Ryzen 9 5900HX.

Delivered observe command (frame-budget `--quit-after 180`, not a diagnostic 120-frame one-off):

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
.\scripts\run-godot-voxel-observe.ps1
```

The windowed development scene executed formal `VoxelDevelopmentSession.Damage` commands (not `remove`), waited hold frames after each derived refresh, then captured. PNGs under `.godot/issue45-*.png` are local evidence and are not committed. The auto-generated `.godot/issue45-observe-report.md` records only script exit, `VOXEL_OBSERVE_COMPLETE`, capture logs, and file presence.

## Procedure

1. Start the project and choose **进入体素开发场景**, or run the observe script above.
2. Confirm the initial 8³ block is continuous and HUD says `(4,4,4)` is `core:iron-ore`, `px_py`, durability `100`, count 512.
3. Run `voxel damage 4 4 4 25`. Confirm the cell remains and durability is `75`.
4. Run `voxel damage 4 4 4 75`. Confirm the corner is empty, count 511, and newly exposed neighbor faces are present with no stale exterior face.
5. Run `voxel reset` then `voxel damage 5 5 5 100` and `voxel query 5 5 5`. Confirm empty / 511 / 390 faces. The closed cavity is not claimed to be visible from the fixed camera.
6. Run `voxel damage 4 5 5 100`. Confirm a one-cell-deep opening from the visible `-X` exterior and newly exposed interior faces with no overlap, gap, or stale face.
7. Run `voxel reset` then `voxel damage 4 4 4 100 5 4 4 100 6 4 4 25` and `voxel query 6 4 4`. Confirm count 510 and durability 75 on the third cell.

## Automated observe result

Reran the delivered script after the R1/R2 fixes. Exit 0. Markers included `VOXEL_OBSERVE_OPEN_CAVITY`, `VOXEL_OBSERVE_COMPLETE`, and all six PNGs. Settlement `VOXEL_OBSERVE_MULTI_SETTLE_MS=0.552`. Capture log:

- `issue45-initial`: 512 voxels, 384 faces, HUD occupied durability 100, last command 场景已进入.
- `issue45-partial`: 512 voxels, 384 faces, HUD durability 75, last command `Damaged (4,4,4) by 25 → 75`.
- `issue45-destroyed`: 511 voxels, 384 faces, HUD empty, last command `Removed (4,4,4) by 75 → 0`.
- `issue45-internal-cavity`: 511 voxels, 390 faces, HUD occupied `(4,4,4)` durability 100, last command `Removed (5,5,5) by 100 → 0`.
- `issue45-internal-opening`: 510 voxels, 392 faces, HUD occupied durability 100, last command `Removed (4,5,5) by 100 → 0`.
- `issue45-multi`: 510 voxels, 384 faces, HUD empty, last command `Removed (4,4,4) by 100 → 0；Removed (5,4,4) by 100 → 0；Damaged (6,4,4) by 25 → 75`.

## Visual inspection of the six observe PNGs

Inspected every generated `.godot/issue45-*.png` after the successful delivered-script run:

- `issue45-initial.png`: continuous 8³ block; HUD occupied, durability 100, last command 场景已进入.
- `issue45-partial.png`: exterior still closed; HUD occupied, durability 75, last command Damaged by 25 → 75.
- `issue45-destroyed.png`: visible corner notch; HUD empty; last command Removed by 75 → 0. No leftover deleted-cell exterior face.
- `issue45-internal-cavity.png`: closed exterior unchanged; HUD still shows occupied `(4,4,4)` durability 100 and Removed `(5,5,5)`. Closed cavity is not visible, as expected.
- `issue45-internal-opening.png`: one-cell-deep opening with exposed interior orange face; HUD still occupied `(4,4,4)` durability 100 and Removed `(4,5,5)`. No overlap, gap, or stale exterior face recorded.
- `issue45-multi.png`: two missing surface cells at the visible corner; HUD is empty at `(4,4,4)` and the last-command line lists all three current targets, including `Damaged (6,4,4) by 25 → 75`. It does **not** still show occupied durability 100 or `Removed (4,5,5)`.

## Console display check (single and multi target)

Separate windowed run (worktree-local, not the delivered observe script) entered the development scene, submitted console commands, opened the console, and captured `.godot/issue45-console-*.png`. RichTextLabel parsed text and the PNGs both showed:

- Single: `Voxel Damaged at (4, 4, 4) by 25. Cell (4, 4, 4): core:iron-ore, orientation px_py, durability 75. Voxel count: 512.`
- Multi (one batch, three lines):
  `Voxel Removed at (4, 4, 4) by 100. ... Voxel count: 510.`
  `Voxel Removed at (5, 4, 4) by 100. ... Voxel count: 510.`
  `Voxel Damaged at (6, 4, 4) by 25. Cell (6, 4, 4): core:iron-ore, orientation px_py, durability 75. Voxel count: 510.`
- Follow-up `voxel query 6 4 4`: durability 75, count 510.

The displayed multi result is not the previous truncated `durability 7` / missing count line.

Legal long voxel definition IDs (canonical UUID namespace plus a 64-character local ID) are covered by the formal dispatcher → `FormatConsole` test, not by the observe script. Damage feedback is field-split so the full identity, remaining durability, and world count stay visible inside the existing 200-character Message clip.

Headless `scripts/run-godot-voxel-smoke.ps1 -Slice 1` remains separate automated integration evidence.

Final Acceptance-criteria confirmation remains the reviewer's responsibility.
