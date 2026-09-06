# Issue #44 visual observation record

Environment: Godot `4.7.2.stable.mono.official.ed1daf0bf`, fixed camera in the voxel development scene.

## Procedure

1. Start the project and choose **进入体素开发场景**.
2. Confirm the initial 8³ solid block has continuous outer faces and the HUD says `(4,4,4)` is occupied by `core:iron-ore`, orientation `px_py`, durability `100`.
3. Press Delete. Confirm the corner is empty in both HUD and mesh, with no stale exterior face.
4. Press Insert. Confirm the corner is restored at durability `100`; press Insert again and confirm the occupied failure.
5. With the outer corner still removed, use `voxel remove 5 5 5` in the `~` console. Confirm the inner cavity is visible through that opening and its neighboring faces have no missing faces.
6. Press R, then run `voxel remove 4 4 4` and `voxel add 4 4 4 core:iron-ore pz_py`. Confirm the HUD and console show `pz_py`, and the cube's colored local-axis faces have rotated accordingly.
7. In the console, from a reset state, verify `voxel add 16 4 4 core:iron-ore px_py`, `voxel add 0 0 0 core:unknown px_py`, and `voxel add 0 0 0 core:fixed pz_py` return explicit failures without changing the queried cell or count.

## Result

Not run as a desktop visual inspection in this agent session. The real headless smoke exercises the same formal operations and verifies the query-driven display and mesh-refresh markers, but it is not recorded here as a substitute for this visual acceptance check. A reviewer should perform the procedure above before marking the visual Acceptance criterion accepted.
