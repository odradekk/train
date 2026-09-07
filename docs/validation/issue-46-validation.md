# Issue #46 this-round validation record

Head that produced these commands is the task-branch commit that lands this file. Re-run after later revisions; do not reuse older tickets' numbers, or the first #46 implementation pass, as this-round measurements.

Engine: Godot `4.7.2.stable.mono.official.ed1daf0bf` at `D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
.NET: Microsoft.NETCore.App 8.0.30.

## Domain tests and build

```text
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
```

- `dotnet test`: 157 passed, 0 failed (includes ContentReloaderTests, ModLoadingTests, VoxelWorldTests, VoxelConsoleCommandTests, VoxelLiveReloadTests, VoxelCubeUvTests, IsolatedBuiltInContentTests).
- `dotnet build Train.csproj`: 0 warnings, 0 errors.

Parent #39 sequences covered by the existing suite (re-run this round, not by old records): 512-cell development world, add/remove failures, 24 orientations and `core:fixed` restriction, internal and multi-target damage, #43 semantic/asset/publication hash matrix, plus #46 80→80→60 / mixed reject / required-definition delete / unchanged reload. This revision adds identity / Yaw90 / tilt cube UV mapping (Identity keeps the original CCW world-face UVs), generated-mesh top UV readback, and a physical overlay read that does not write a separate worktree source.

## Real Godot

Set `$env:GODOT` as above. Sibling smoke environment variables were cleared before each command.

| Command | Result |
| --- | --- |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 1 -IsolationAbort` | Exit 0. `VOXEL_SMOKE_ISOLATION_WRITE_DONE`. Worktree iron-ore JSON/PNG SHA256 unchanged after the process wrote isolated content and quit immediately. |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 1` | Exit 0. Required markers include add/remove, orientation, damage, menu reenter, live-reload 80 / 150 / text / asset / `VOXEL_SMOKE_RELOAD_ORIENTATION_MAPPED` / 60 / probe / reject / display retained, generated-mesh top UV readback for identity / yaw / tilt, and `VOXEL_RELOAD_TOP_UV identity=(0,0)=0,1 ... yaw90=(0,0)=0,0 ... tilt=(0,0)=0,1`. `VOXEL_SMOKE_MULTI_SETTLE_MS=0.355`. Worktree iron-ore hashes unchanged. |
| `.\scripts\run-godot-voxel-observe.ps1` | Exit 0. `VOXEL_OBSERVE_COMPLETE`. Six historical issue45 captures plus six current issue46 captures including `issue46-oriented.png`. Worktree iron-ore hashes unchanged. See `docs/validation/issue-46-manual-observation.md`. |
| `.\scripts\run-godot-voxel-baseline.ps1 -OutputPath docs/validation/issue-46-voxel-baseline.md` | Exit 0. See that file. |

Content smoke and export smokes were not re-run in this revision. They are unchanged by the isolation overlay (content smoke still writes `res://builtin/.../smoke-temp.voxels.json`, which is outside this ticket's iron-ore R1). Coordinator already treated those as prior-head evidence, not this-commit proof.

## Script harness (not a real engine)

`scripts/run-godot-*-smoke.tests.ps1` were not re-run in this revision. These use a fake Godot and do not satisfy real-engine Acceptance criteria.

## Not run / not claimed

- Real `logging-smoke`: known marker-format mismatch from [#45 final acceptance](https://github.com/odradekk/train/issues/45#issuecomment-5565883453). Not claimed passed. Not extended in this ticket.
- Cross-chunk, vehicle/Composite, rail, save, collision query, procgen, desktop dock, and trade were not added.
- S2 (deduplicating smoke vs observe reload drivers) was left as a non-blocking suggestion; no scene-architecture rewrite.

## Isolation

Voxel smoke/observe seed a physical copy of `res://builtin/core/resources` into `{isolatedUserData}/builtin-resources` and point `GodotContentSource` at that overlay. Formal `ExternalContent` list/read/decode is the only path used for that built-in package. `VoxelLiveReloadDriver` refuses `res://` roots and writes only the overlay plus the isolated probe Mod. There is no exit-time restore of worktree files. Normal completion, `-IsolationAbort`, and script hash checks all leave worktree iron-ore JSON/PNG unchanged.
