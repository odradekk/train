# Issue #53 this-round validation record

This file records the **second-round** checkout used for delivery: task-branch head `84081a03c766fc3914b3cebb18c87ec8828d0cc1` plus the authorized `global.json` SDK pin `9.0.318` (`rollForward=disable`). It **replaces** the previous “SDK 9.0.316 blocked / not re-run” table on that same head. Those earlier notes were true before the pin; they are not the current environment.

**Who ran what:** the coordinator/reviewer rebuilt and executed the commands below on this worktree after changing `global.json`. This agent verified `dotnet --version` = `9.0.318`, the TRX counters, PNG SHA256 against `.godot/issue53-resume-observe-report.md`, and `git diff --check`. It did **not** re-run `dotnet test`, `dotnet build`, or the Godot scripts.

Engine: Godot `4.7.2.stable.mono.official.ed1daf0bf` at `D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
Project: `TargetFramework=net8.0`, C# 12. Product version remains `0.0.7`.

```text
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
dotnet --version
dotnet test Train.Tests/Train.Tests.csproj --logger "trx;LogFileName=review53-r2.trx" --results-directory D:/Code/train/.git/review53-r2-evidence
dotnet build Train.sln
.\scripts\run-godot-voxel-smoke.ps1 -Slice 1
.\scripts\run-godot-voxel-smoke.ps1 -Slice 2
.\scripts\run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort
.\scripts\run-godot-voxel-observe-resume.ps1
.\scripts\run-godot-voxel-baseline.ps1 -OutputPath .godot/review53-r2-baseline.md
git diff --check
```

`run-godot-voxel-observe.ps1 -Slice 2` is the historical boundary / local-box sequence. It is not this-round resume evidence.

| Command | Result | Source |
| --- | --- | --- |
| `dotnet --version` | `9.0.318` | reviewer; rechecked by this agent |
| `dotnet test Train.Tests/Train.Tests.csproj` | 205 passed / 0 failed / 0 skipped | reviewer TRX `D:/Code/train/.git/review53-r2-evidence/review53-r2.trx` (`Counters total=205 passed=205`) |
| `dotnet build Train.sln` | 0 warnings / 0 errors | reviewer |
| `run-godot-voxel-smoke.ps1 -Slice 1` | passed | reviewer `smoke1.log` |
| `run-godot-voxel-smoke.ps1 -Slice 2` | passed; process 1 `VOXEL_SMOKE_SAVE_WRITTEN id=88b6f667-0ffc-4143-8be6-ea33eb0c1c36 bytes=1129`; process 2 loaded the same UUID; none/full_box/local_box `published=1`; continue damage → 1088; 100 pairs; same UUID saved (`bytes=101388` after max-150 reload, legal relative to the current generated base) | reviewer `smoke2.log` |
| `run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort` | passed; `VOXEL_SMOKE_ISOLATION_WRITE_DONE`; worktree built-in content unchanged | reviewer `isolation.log` |
| `run-godot-voxel-observe-resume.ps1` | passed; UUID `2df8878b-7ea1-4391-9d1d-d294ffef605e`; five `issue53-resume-*.png`; `VOXEL_OBSERVE_RESUME_COMPLETE` | reviewer `observe-resume.log` and `.godot/issue53-resume-observe-report.md` |
| `run-godot-voxel-baseline.ps1` | passed; warm-up plus slice 1 ×3, slice 2 ×3, save/load ×3 | reviewer `.godot/review53-r2-baseline.md` / `baseline.log` |
| `review53-r1-evidence/repro.ps1` | R1 `MismatchedId` / `other_file_overwritten=False`; R2 `MissingDefinition` `before=100 after=100`; R3 `UnknownFormat succeeded=False` | reviewer |
| `git diff --check` | passed | this agent, on the files in this delivery |

Slice 2 smoke and resume observe use a script-owned temporary user-data directory for both processes and delete it afterwards. Worktree `builtin/core/resources/voxels/iron-ore` and `ledge` hashes must stay unchanged. File-failure coverage is `WorldSaveTests` (mismatched id, missing definition records, unknown fields, truncated / unknown version / illegal delta / write failure keep the live world and previous file).

This record does not replace `docs/validation/issue-52-*.md` or earlier baseline files. The first-delivery 202-pass count on `1006993` is historical only.
