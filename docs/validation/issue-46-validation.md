# Issue #46 this-round validation record

Head that produced these commands is the task-branch commit that lands this file. Re-run after later revisions; do not reuse older tickets' numbers as this-round measurements.

Engine: Godot `4.7.2.stable.mono.official.ed1daf0bf` at `D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
.NET: Microsoft.NETCore.App 8.0.30.

## Domain tests and build

```text
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
```

- `dotnet test`: 152 passed, 0 failed (includes ContentReloaderTests, ModLoadingTests, VoxelWorldTests, VoxelConsoleCommandTests, VoxelLiveReloadTests).
- `dotnet build Train.csproj`: 0 warnings, 0 errors.

Parent #39 sequences covered by the existing suite (re-run this round, not by old records): 512-cell development world, add/remove failures, 24 orientations and `core:fixed` restriction, internal and multi-target damage, #43 semantic/asset/publication hash matrix, plus #46 80→80→60 / mixed reject / required-definition delete / unchanged reload.

## Real Godot

Set `$env:GODOT` as above. Sibling smoke environment variables were cleared before each command.

| Command | Result |
| --- | --- |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 1` | Exit 0. Required markers include add/remove, orientation, damage, menu reenter, and `VOXEL_SMOKE_RELOAD_DAMAGED_80` / `_MAX_150` / `_TEXT_UPDATED` / `_ASSET_UPDATED` / `_MAX_60` / `_PROBE_ADDED` / `_REJECTED` / `_DISPLAY_RETAINED`. Builtin iron-ore files restored on scene exit. |
| `.\scripts\run-godot-content-smoke.ps1` | Exit 0. `CONTENT_IRON_ORE_LOADED`, `CONTENT_SMOKE_INPUT_RELOADED`, `CONTENT_SMOKE_REMOVED`. Did not enter the voxel scene. |
| `.\scripts\run-godot-content-export-smoke.ps1` | Exit 0. |
| `.\scripts\run-godot-mods-export-smoke.ps1` | Exit 0. Isolated empty user-data, `MODS_EXPORT_ROOT_EMPTY`. |
| `.\scripts\run-godot-voxel-observe.ps1` | Exit 0. `VOXEL_OBSERVE_COMPLETE`. See `docs/validation/issue-46-manual-observation.md`. |
| `.\scripts\run-godot-voxel-baseline.ps1 -OutputPath docs/validation/issue-46-voxel-baseline.md` | Exit 0. See that file. |

## Script harness (not a real engine)

`scripts/run-godot-*-smoke.tests.ps1` exit 0. These use a fake Godot and do not satisfy real-engine Acceptance criteria.

## Not run / not claimed

- Real `logging-smoke`: known marker-format mismatch from [#45 final acceptance](https://github.com/odradekk/train/issues/45#issuecomment-5565883453). Not claimed passed. Not extended in this ticket.
- Cross-chunk, vehicle/Composite, rail, save, collision query, procgen, desktop dock, and trade were not added.

## Isolation

Voxel smoke/observe resolve mods to an isolated temp user-data directory. Probe Mod writes only there. Builtin `iron-ore` JSON/PNG are temporarily overwritten for the live-reload success path and restored in `_ExitTree`. A killed Godot process can leave those two builtin files dirty; they were clean after the successful runs.
