# Issue #50 this-round validation record

Head that produced these commands is the task-branch commit that lands this file. Re-run after later revisions; do not reuse older tickets' numbers as this-round measurements.

Engine: Godot `4.7.2.stable.mono.official.ed1daf0bf` at `D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
.NET SDK: 9.0.316. Runtime used by the Godot/.NET project: Microsoft.NETCore.App 8.0.30 (`TargetFramework=net8.0`).

## Domain tests and build

```text
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
```

- `dotnet test`: 166 passed, 0 failed (157 inherited plus 9 in `VoxelMultiChunkTests`). R1 re-ran the same suite after the published-ArrayMesh smoke checks.
- `dotnet build Train.csproj`: 0 warnings, 0 errors.

Slice 1 related regressions re-run this round (not by old records): 512-cell development world, add/remove failures, 24 orientations and `core:fixed` restriction, internal and multi-target damage, #43 hash matrix, #46 80→80→60 / mixed reject / required-definition delete.

## Real Godot

Set `$env:GODOT` as above. Sibling smoke environment variables were cleared before each command.

| Command | Result |
| --- | --- |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 2` | Exit 0. Same base marker as before. Each of X/Y/Z/JUNCTION/CROSS_DAMAGE `_MESH` lines now includes `published=1` after reading the live Chunk `ArrayMesh` vertices (delete-side faces gone, neighbor faces present, restore hides them; dirty chunks increment `Revision`, others do not). Local uncommitted fault “skip Refresh when voxel count decreases” failed immediately with `Published chunk mesh is missing exposed face (15,16,15) PositiveX` and was reverted. |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 1` | Exit 0. `VOXEL_SMOKE_MULTI_SETTLE_MS=0.326`. Parent slice 1 markers unchanged. |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort` | Exit 0. `VOXEL_SMOKE_ISOLATION_WRITE_DONE`. Worktree iron-ore JSON/PNG SHA256 unchanged after the process wrote isolated content and quit immediately. |
| `.\scripts\run-godot-voxel-observe.ps1 -Slice 2` | Exit 0. `VOXEL_OBSERVE_COMPLETE`. Eight `.godot/issue50-*.png`. Worktree iron-ore hashes unchanged. See `docs/validation/issue-50-manual-observation.md`. |
| `.\scripts\run-godot-voxel-baseline.ps1` | Exit 0. Default output is now `docs/validation/issue-50-voxel-baseline.md` (no longer the historical #44 path). One slice-1 warm-up, three recorded slice-1 runs, then the same process switches to slice 2 and records three 100-pair cross-boundary add/remove runs. See that file. |

## Script harness (not a real engine)

`scripts/run-godot-*-smoke.tests.ps1` were not re-run in this revision. These use a fake Godot and do not satisfy real-engine Acceptance criteria.

## Not run / not claimed

- Real `logging-smoke`: known marker-format mismatch from [#45 final acceptance](https://github.com/odradekk/train/issues/45#issuecomment-5565883453). Not claimed passed. Not extended in this ticket.
- Content smoke and export smokes were not re-run. They are unchanged by this ticket's world/session work.
- Collision query, localBox, save, vehicle/Composite, rail, procgen, streaming, async mesh, and large-world performance certification were not implemented and are not reported as passed.
- Baseline numbers are this-machine observations only. They do not claim a large-world performance gate.

## Isolation

Voxel smoke/observe seed a physical copy of `res://builtin/core/resources` into `{isolatedUserData}/builtin-resources` and point `GodotContentSource` at that overlay. Formal `ExternalContent` list/read/decode is the only path used for that built-in package. `VoxelLiveReloadDriver` refuses `res://` roots and writes only the overlay plus the isolated probe Mod. There is no exit-time restore of worktree files. Normal completion, `-IsolationAbort`, and script hash checks all leave worktree iron-ore JSON/PNG unchanged.
