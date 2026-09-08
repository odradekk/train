# Issue #51 this-round validation record

Head that produced these commands is the task-branch commit that lands this file. Re-run after later revisions; do not reuse older tickets' numbers as this-round measurements.

Engine: Godot `4.7.2.stable.mono.official.ed1daf0bf` at `D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
.NET SDK: 9.0.316. Runtime used by the Godot/.NET project: Microsoft.NETCore.App 8.0.30 (`TargetFramework=net8.0`).

## Domain tests and build

```text
dotnet test Train.Tests/Train.Tests.csproj
dotnet build Train.csproj
```

- `dotnet test`: 175 passed, 0 failed (166 inherited plus 9 in `VoxelCollisionTests`).
- `dotnet build Train.csproj`: 0 warnings, 0 errors.

Covered at the public command/query seam: `none` occupies but misses, `full_box` cell and box probes, out-of-bounds and invalid probes, immediate miss after remove/lethal damage, slice 2 15/16 and junction, straddling box, same-event cross-chunk damage and rejected batches, independent mesh/collision dirty queues, legal/rejected live reload, and `voxel collide` console output.

Slice 1 and #50 regressions re-ran in the same suite: 512-cell world, add/remove failures, orientations, internal and multi-target damage, #43 hash matrix, #46 80→80→60 / mixed reject, slice 2 occupancy/exposed faces/dirty chunks.

## Real Godot

Set `$env:GODOT` as above. Sibling smoke environment variables were cleared by the scripts before each command.

| Command | Result |
| --- | --- |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 2` | Exit 0. Base marker unchanged. Each of X/Y/Z/JUNCTION/CROSS_DAMAGE `_MESH` lines still includes `published=1` after reading the live Chunk `ArrayMesh`. Each matching `_COLLIDE` line includes `published=1` after `PhysicsDirectSpaceState3D.IntersectShape` agreed with the domain query (deleted cell miss, neighbor hit). X-boundary also compared an inset straddling box (`15.25..16.75 × 16.25..16.75 × 15.25..15.75`) via the same `IntersectShape` path (`straddle=1` after the +X delete). Additional markers: `VOXEL_SMOKE_NONE_OCCUPIED_NO_HIT`, `VOXEL_SMOKE_RELOAD_COLLISION_NONE`, `VOXEL_SMOKE_RELOAD_PRESENTATION_COLLISION_UNCHANGED`, `VOXEL_SMOKE_RELOAD_COLLISION_FULL_BOX`. After the later cube-asset reload, occupied iron-ore still hits. Collision dirty ranges were the occupied cell's chunk only (`1,1,0` / `0,0,0` / `0,1,1` / `1,1,1` / `0,1,0;1,1,1`). Parent #46 reload markers still printed after the collision sequence. |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 1` | Exit 0. `VOXEL_SMOKE_MULTI_SETTLE_MS=0.294`. Parent slice 1 markers unchanged. Catalog now accepts 3 built-in voxel definitions (`core:fixed`, `core:hollow`, `core:iron-ore`). |
| `.\scripts\run-godot-voxel-smoke.ps1 -Slice 2 -IsolationAbort` | Exit 0. `VOXEL_SMOKE_ISOLATION_WRITE_DONE`. Worktree iron-ore JSON/PNG SHA256 unchanged after the process wrote isolated content and quit immediately. |
| `.\scripts\run-godot-voxel-observe.ps1 -Slice 2` | Exit 0. `VOXEL_OBSERVE_COMPLETE`. Eight `.godot/issue50-*.png`. Capture lines include `collide=Hit` and HUD `碰撞：命中 full_box`. Worktree iron-ore hashes unchanged. See `docs/validation/issue-51-manual-observation.md`. |

## Script harness (not a real engine)

`scripts/run-godot-*-smoke.tests.ps1` were not re-run. These use a fake Godot and do not satisfy real-engine Acceptance criteria.

## Not run / not claimed

- Real `logging-smoke`: known marker-format mismatch from [#45 final acceptance](https://github.com/odradekk/train/issues/45#issuecomment-5565883453). Not claimed passed.
- Content smoke and export smokes were not re-run. This ticket added `builtin/core/resources/voxels/hollow.voxels.json` (reuses iron-ore cube PNGs). Those smokes still write `res://builtin/.../smoke-temp.voxels.json` and were not required here.
- `scripts/run-godot-voxel-baseline.ps1` was not re-run. Catalog definition count is now 3; historical #50 baseline files still say `definitions=2`.
- LocalBox (#52), save, vehicle/Composite, rail, procgen, streaming, async mesh, and large-world performance certification were not implemented and are not reported as passed.

## Isolation

Voxel smoke/observe seed a physical copy of `res://builtin/core/resources` into `{isolatedUserData}/builtin-resources` and point `GodotContentSource` at that overlay. Formal `ExternalContent` list/read/decode is the only path used for that built-in package. `VoxelLiveReloadDriver` refuses `res://` roots and writes only the overlay plus the isolated probe Mod. There is no exit-time restore of worktree files. Normal completion, `-IsolationAbort`, and script hash checks all left worktree iron-ore JSON SHA256 `11B61B9E6650CDB66C6987FBE3EB8CCEA45C9B13F34BF9C61D1B4557E4494483` and `iron-ore/top.png` SHA256 `2F70C1C2AADD6BCBDD4BFB2436AEA595AA3C9B852C84518CEF3E189B0AE7AD65` unchanged.
