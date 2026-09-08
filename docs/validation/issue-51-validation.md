# Issue #51 this-round validation record

Head that produced these commands is the task-branch commit that lands this revision. Re-run after later revisions; do not reuse older tickets' numbers or the previous PR head as this-round measurements.

Engine: Godot `4.7.2.stable.mono.official.ed1daf0bf` at `D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
.NET SDK: 9.0.316. Runtime used by the Godot/.NET project: Microsoft.NETCore.App 8.0.30 (`TargetFramework=net8.0`).

## Agent — R1/R2 repair (this head)

This revision only fixes query-edge contract bugs from [review comment 5579659490](https://github.com/odradekk/train/issues/51#issuecomment-5579659490). Godot adapters, dirty queues, and built-in assets were not changed.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
.\scripts\run-godot-voxel-smoke.ps1 -Slice 1
.\scripts\run-godot-voxel-smoke.ps1 -Slice 2
.\scripts\run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort
git diff --check
```

| Command | Agent result on this head |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 177 passed, 0 failed (175 inherited plus 2 new public-query/console edge tests). |
| `dotnet build Train.csproj` | 0 warnings, 0 errors. |
| `-Slice 1` | Exit 0. `VOXEL_SMOKE_MULTI_SETTLE_MS=0.317`. Catalog still 3 built-in voxel definitions. |
| `-Slice 2` | Exit 0. Each X/Y/Z/JUNCTION/CROSS_DAMAGE `_MESH` and `_COLLIDE` line still includes `published=1`. Markers `VOXEL_SMOKE_NONE_OCCUPIED_NO_HIT`, `VOXEL_SMOKE_RELOAD_COLLISION_NONE`, `VOXEL_SMOKE_RELOAD_PRESENTATION_COLLISION_UNCHANGED`, `VOXEL_SMOKE_RELOAD_COLLISION_FULL_BOX` present. X-boundary `straddle=1`. |
| `-Slice 2 -IsolationAbort` | Exit 0. `VOXEL_SMOKE_ISOLATION_WRITE_DONE`. Worktree iron-ore JSON/PNG unchanged. |
| `git diff --check` | Clean (checked on the repair diff). |

New public-seam coverage: three-axis `int.MinValue` / `int.MaxValue` cell queries return `OutOfBounds` without throwing; `default(VoxelCollisionBox)`, zero/negative volume, and parsed `NaN` / `Infinity` return `InvalidQuery`; unparseable console text still returns `Usage`.

## Reviewer — first head `2a751de65943419bdfb9b7db106f98033e72150a`

Independent reviewer evidence lives in `D:/Code/train/.git/review51-evidence/` and [comment 5579659490](https://github.com/odradekk/train/issues/51#issuecomment-5579659490). Those numbers apply only to that head, not this repair.

Reviewer results on `2a751de`: `dotnet test` 175 passed; `dotnet build` 0 warnings; real `-Slice 1`, `-Slice 2`, `-Slice 2 -IsolationAbort` exit 0; observe `-Slice 2` exit 0 with eight PNGs (reviewer inspected X/Y boundary, junction, and cross-damage frames); `run-godot-voxel-baseline.ps1` exit 0 (one warm-up, two slices × three runs, `definitions=3`). The existing baseline script does **not** assert `definitions=2`; that count is not a blocker.

The first agent delivery comment incorrectly treated historical `#50` baseline files saying `definitions=2` as a reason to skip baseline. That explanation is withdrawn.

## Not run on this repair head

- `scripts/run-godot-voxel-observe.ps1` and `scripts/run-godot-voxel-baseline.ps1`: no Godot mesh/collider/presentation change in this revision. Reviewer already ran both on `2a751de`; this agent did not rerun them here.
- `scripts/run-godot-*-smoke.tests.ps1` (fake Godot harness).
- Real `logging-smoke`, content smoke, and export smokes.

These are not claimed passed for this head.

## Isolation

Voxel smoke still seeds a physical copy of `res://builtin/core/resources` into `{isolatedUserData}/builtin-resources`. Worktree `iron-ore.voxels.json` SHA256 `11B61B9E6650CDB66C6987FBE3EB8CCEA45C9B13F34BF9C61D1B4557E4494483` and `iron-ore/top.png` SHA256 `2F70C1C2AADD6BCBDD4BFB2436AEA595AA3C9B852C84518CEF3E189B0AE7AD65` were unchanged after this head's Slice 1 / Slice 2 / IsolationAbort runs.
