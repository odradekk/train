# Issue #44 visual observation record

Environment: Godot `4.7.2.stable.mono.official.ed1daf0bf`, fixed camera in the voxel development scene.

## Procedure

1. Start the project and choose **进入体素开发场景**.
2. Confirm the initial 8³ solid block has continuous outer faces and the HUD says `(4,4,4)` is occupied by `core:iron-ore`, orientation `px_py`, durability `100`.
3. Press Delete. Confirm the corner is empty in both HUD and mesh, with no stale exterior face.
4. Press Insert. Confirm the corner is restored at durability `100`; press Insert again and confirm the occupied failure.
5. Press R, then run `voxel remove 5 5 5` and `voxel query 5 5 5`. Confirm the internal cell is empty and the world count is 511. This preserves the fixed domain check; the closed cavity is not claimed to be visible.
6. Press R, then run `voxel remove 4 5 5` followed by `voxel remove 5 5 5`. These two face-adjacent cells form a one-cell-deep opening from the visible `-X` exterior into the internal cavity. Confirm the newly exposed neighboring faces are visible through the opening with no overlap, gap, or stale face.
7. Press R, then run `voxel remove 4 4 4` and `voxel add 4 4 4 core:iron-ore pz_py`. Confirm the HUD and console show `pz_py`, and the cube's colored local-axis faces have rotated from the `px_py` display. Run `voxel remove 4 4 4` and `voxel add 4 4 4 core:iron-ore px_pz` as a representative orientation that moves the local up axis, confirming the displayed orientation and face colors change again.
8. Run `voxel reset`, then verify `voxel add 16 4 4 core:iron-ore px_py`, `voxel add 0 0 0 core:unknown px_py`, and `voxel add 0 0 0 core:fixed pz_py` return `OutOfBounds`, `UnknownDefinition`, and `DisallowedOrientation`. Confirm each output keeps `Voxel count: 512`, then run `voxel query 0 0 0` and confirm the cell remains empty.

## Result

Run on 2026-09-06 using the Windows display server and the OpenGL Compatibility renderer on an NVIDIA GeForce RTX 3080 Laptop GPU (driver 596.49). A temporary, uncommitted observation driver sent the documented key inputs and console submissions through the running scene, waited for rendered frames, and saved the resulting viewport captures under `.godot/issue44-fixed-*.png`; the temporary driver is not part of the production or test flow.

Observed:

- The initial block showed three continuous outer surfaces without missing triangles. Delete exposed the selected corner without a stale face; Insert restored it, and the repeated Insert reported `Occupied`.
- Removing only `(5,5,5)` from reset reported and queried `empty` with `Voxel count: 511` while leaving the closed exterior visually unchanged, as expected.
- Removing face-adjacent `(4,5,5)` and then `(5,5,5)` produced a visible opening from the `-X` exterior. The orange neighboring interior face was visible through it without overlap, gaps, or stale exterior geometry; the command output ended at `Voxel count: 510`.
- Re-adding `(4,4,4)` as `pz_py` produced the red representative local-axis face, and both console output and HUD reported `pz_py`; replacing it as `px_pz` changed the representative face color again, and both displays reported `px_pz`.
- The three failure commands reported `OutOfBounds`, `UnknownDefinition`, and `DisallowedOrientation`; every result kept `Voxel count: 512`, and the final query showed `(0,0,0)` remained empty.

The viewport captures were visually inspected after the real-GPU run. The headless smoke remains separate automated integration evidence and is not treated as the visual observation itself. No item in the procedure remains recorded as passed without being run; final Acceptance-criteria confirmation remains the reviewer's responsibility.
