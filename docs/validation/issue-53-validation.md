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
.\scripts\run-godot-voxel-observe-resume.ps1
.\scripts\run-godot-voxel-baseline.ps1
git diff --check
```

`run-godot-voxel-observe.ps1 -Slice 2` is the historical boundary / local-box sequence. It is not this-round resume evidence.

This-round results must be taken from the new head after R1–R4. Do not reuse the previous 202-pass count as a result for a later commit.

| Command | Result |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | blocked unless SDK 9.0.316 is available (`global.json` rollForward=disable) |
| `dotnet build Train.sln` | same SDK lock |
| `run-godot-voxel-smoke.ps1 -Slice 1` | not re-run on this revision until the new head builds |
| `run-godot-voxel-smoke.ps1 -Slice 2` | not re-run on this revision until the new head builds |
| `run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort` | not re-run on this revision until the new head builds |
| `run-godot-voxel-observe-resume.ps1` | two-process windowed save/quit/load; writes `issue53-resume-*.png` |
| `run-godot-voxel-baseline.ps1` | not required for this revision unless the new head builds |

Slice 2 smoke and resume observe use a script-owned temporary user-data directory for both processes and delete it afterwards. Worktree `builtin/core/resources/voxels/iron-ore` and `ledge` hashes must stay unchanged. File-failure coverage is `WorldSaveTests` (mismatched id, missing definition records, unknown fields, truncated / unknown version / illegal delta / write failure keep the live world and previous file).

This record does not replace `docs/validation/issue-52-*.md` or earlier baseline files.
