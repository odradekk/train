# Issue #52 this-round validation record

Head that produced these commands is the task-branch commit that lands this revision. Re-run after later revisions; do not reuse older tickets' numbers as this-round measurements.

Engine: Godot `4.7.2.stable.mono.official.ed1daf0bf` at `D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
.NET SDK: recorded in the Issue completion comment. Runtime used by the Godot/.NET project: `TargetFramework=net8.0`.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.sln
.\scripts\run-godot-voxel-smoke.ps1 -Slice 1
.\scripts\run-godot-voxel-smoke.ps1 -Slice 2
.\scripts\run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort
.\scripts\run-godot-voxel-observe.ps1 -Slice 2
git diff --check
```

This-round results on the implementation worktree (before push; the Issue comment records the matching remote head SHA):

| Command | Result |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 190 passed, 0 failed (existing 177 + 13 `VoxelLocalBoxTests`) |
| `dotnet build Train.sln` | 0 warnings, 0 errors |
| `run-godot-voxel-smoke.ps1 -Slice 1` | passed; catalog accepted `core:ledge` (`F8DFDBFA…BE64`) |
| `run-godot-voxel-smoke.ps1 -Slice 2` | passed; `VOXEL_SMOKE_LOCAL_BOX_*` identity/rotated/removed/reloaded/translated_edge/adapter_underflow/rejected/lethal all `published=1`; existing mesh/collide markers kept |
| `run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort` | passed; worktree iron-ore and ledge hashes unchanged |
| `run-godot-voxel-observe.ps1 -Slice 2` | exit 0, `VOXEL_OBSERVE_COMPLETE`; eight `issue50-*.png` plus five `issue52-*.png`; `.godot/issue52-observe-report.md` |

Slice 2 smoke compares formal domain queries with `PhysicsDirectSpaceState3D.IntersectShape`. Observe keeps the eight historical `issue50-*.png` files and adds five `issue52-*.png` frames. Runtime writes stay in the isolated user-data overlay; worktree `iron-ore` and `ledge` JSON/PNG hashes must stay unchanged.

This record does not replace `docs/validation/issue-51-*.md` or earlier baseline files.
