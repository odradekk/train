# Issue #68 validation

Formal straight-rail construction, occupancy, local damage/repair, and save rejection for slice 4. Headless Godot smoke is the official engine evidence. Missing Godot is not a pass.

Host: Fedora Linux `7.2.4-200.fc44.x86_64`. .NET SDK `10.0.111`. Godot `4.7.2.stable.mono.official.ed1daf0bf` at `/usr/local/bin/godot-dotnet`. PowerShell `7.6.6`.

```text
export GODOT=/usr/local/bin/godot-dotnet
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 1
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 2
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 3
pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4
```

| Check | Result |
| --- | --- |
| `dotnet test Train.Tests/Train.Tests.csproj` | 331 passed, 0 failed |
| `dotnet build Train.csproj` | 0 warnings, 0 errors |
| `run-godot-voxel-smoke.ps1 -Slice 1` | passed |
| `run-godot-voxel-smoke.ps1 -Slice 2` | passed |
| `run-godot-voxel-smoke.ps1 -Slice 3` | passed |
| `run-godot-voxel-smoke.ps1 -Slice 4` | passed; 1032 occupancy cells, 257 sleepers, local refresh `rebuilt=1 retained=35 far_kept=1 z_kept=1`, edge clip `intact_sleeper=1`, gap `16..110,111..144`, save `UnsupportedRailway` |
| Windows export | not required for #68 |

Slice 4 smoke is a single process. It does not require later-ticket train motion. Isolated user-data and isolated built-in copies. Worktree `rail.voxels.json` / `rail/top.png` hashes are in the protected list.
