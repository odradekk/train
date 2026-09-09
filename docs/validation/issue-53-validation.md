# Issue #53 this-round validation record

Head that produced these commands is the task-branch commit that lands this revision. Re-run after later revisions; do not reuse older tickets' numbers as this-round measurements.

Engine: Godot `4.7.2.stable.mono.official.ed1daf0bf` at `D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
.NET SDK / runtime used by the Godot/.NET project: `TargetFramework=net8.0`.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.sln
.\scripts\run-godot-voxel-smoke.ps1 -Slice 1
.\scripts\run-godot-voxel-smoke.ps1 -Slice 2
.\scripts\run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort
.\scripts\run-godot-voxel-observe.ps1 -Slice 2
.\scripts\run-godot-voxel-baseline.ps1
git diff --check
```

This-round results on the implementation worktree (before push; the Issue comment records the matching remote head SHA):

| Command | Result |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 202 passed, 0 failed (existing 190 + 12 `WorldSaveTests`) |
| `dotnet build Train.sln` | 0 warnings, 0 errors |
| `run-godot-voxel-smoke.ps1 -Slice 1` | passed; existing slice 1 markers plus menu re-enter |
| `run-godot-voxel-smoke.ps1 -Slice 2` | passed; process 1 wrote isolated `saves/<UUID>/save.json` (`VOXEL_SMOKE_SAVE_WRITTEN`, 1129 bytes), exited; process 2 loaded the same UUID, published `none`/`full_box`/`local_box` hits, kept durability 75 after max-150 reload, continued damage to 1088, 100 cross-boundary pairs, saved the same UUID |
| `run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort` | passed; worktree iron-ore and ledge hashes unchanged |
| `run-godot-voxel-observe.ps1 -Slice 2` | exit 0, `VOXEL_OBSERVE_COMPLETE`; historical `issue50-*.png` / `issue52-*.png` plus reports under `.godot/` |
| `run-godot-voxel-baseline.ps1` | wrote `docs/validation/issue-53-voxel-baseline.md`; did not overwrite `issue-50-voxel-baseline.md`. One mutation warm-up, three slice 1 runs (512 voxels), three slice 2 boundary runs (1088), one save/load warm-up, three recorded save/load runs (1089 voxels, 1130 bytes) |

Slice 2 smoke uses a script-owned temporary user-data directory for both processes and deletes it afterwards. Worktree `builtin/core/resources/voxels/iron-ore` and `ledge` hashes must stay unchanged. No Godot service-boundary tests: file-failure coverage is `WorldSaveTests` (truncated / unknown version / missing definition / illegal delta / write failure keep the live world and previous file).

This record does not replace `docs/validation/issue-52-*.md` or earlier baseline files.
