using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainControllerTests
{
    private static readonly ScenePoint Focus = new(102912, 0, 102912);

    [TestMethod]
    public void Swept_demand_covers_the_bend_middle_and_stays_inside_the_expanded_route_bounds()
    {
        var terrain = Bound(new());
        terrain.SetFineBuildAllowed(false);
        const double radius = 1;
        var bend = new ScenePoint(Focus.X + 2048, 0, Focus.Z + 2048);
        var end = new ScenePoint(Focus.X, 0, Focus.Z + 2048);
        terrain.Demand(Focus, radius, ahead: [bend, end]);
        while (!terrain.Advance(10000, CancellationToken.None)) { }

        Assert.IsNotNull(terrain.View.Height(new(Focus.X + 1024, 0, Focus.Z + 1024)),
            "Sweeping a route must fill its middle, not just the endpoint boxes.");
        var tiles = terrain.View.Tiles;
        Assert.AreEqual(9, tiles.Length);
        foreach (var tile in tiles)
        {
            Assert.IsTrue(tile.Origin.X < bend.X + radius && tile.Origin.X + tile.SizeCells > Focus.X - radius);
            Assert.IsTrue(tile.Origin.Z < end.Z + radius && tile.Origin.Z + tile.SizeCells > Focus.Z - radius);
        }
    }

    [TestMethod]
    public void Fine_work_can_pause_after_coarse_publication_and_resume_without_partial_children()
    {
        var terrain = Bound(new());
        terrain.SetFineBuildAllowed(false);
        terrain.Demand(Focus, 1);
        var finished = false;
        for (var step = 0; step < 1000 && !finished; step++)
            finished = terrain.Advance(128, CancellationToken.None);
        Assert.IsTrue(finished);
        var coarse = terrain.View.Tiles.Single();
        Assert.AreEqual(32, coarse.SpacingCells);
        Assert.IsFalse(terrain.HasPendingCoarse);
        Assert.IsFalse(terrain.HasRunnableWork);

        terrain.SetFineBuildAllowed(true);
        Assert.IsTrue(terrain.HasRunnableWork);
        Assert.IsFalse(terrain.Advance(128, CancellationToken.None));
        terrain.SetFineBuildAllowed(false);
        Assert.IsTrue(terrain.Advance(1_000_000, CancellationToken.None));
        Assert.AreSame(coarse, terrain.View.Tiles.Single());
        Assert.IsFalse(terrain.HasRunnableWork);

        terrain.SetFineBuildAllowed(true);
        finished = false;
        for (var step = 0; step < 10000 && !finished; step++)
        {
            finished = terrain.Advance(128, CancellationToken.None);
            Assert.IsTrue(terrain.View.Tiles.Length is 1 or 16);
        }
        Assert.IsTrue(finished);
        Assert.AreEqual(16, terrain.View.Tiles.Length);
        Assert.IsTrue(terrain.View.Tiles.All(tile => tile.SpacingCells == 4));
        Assert.IsFalse(terrain.HasRunnableWork);
    }

    [TestMethod]
    public void Parent_is_replaced_only_when_all_sixteen_children_are_ready_and_old_view_stays_unchanged()
    {
        var terrain = Bound(new());
        var coarse = FirstCoarse(terrain);
        var oldView = terrain.View;
        var oldHeight = oldView.Height(Focus);
        var finished = false;
        for (var step = 0; step < 10000; step++)
        {
            finished = terrain.Advance(128, CancellationToken.None);
            var tiles = terrain.View.Tiles;
            Assert.IsTrue(tiles.Length is 1 or 16, "A parent must never show a partial set of fine tiles.");
            if (tiles.Length == 1) Assert.AreSame(coarse, tiles[0]);
            if (finished) break;
        }
        Assert.IsTrue(finished, "The single requested parent must finish within the bounded work budget.");
        var fine = terrain.View.Tiles;
        Assert.AreEqual(16, fine.Length);
        CollectionAssert.AreEquivalent(new[] { 102400d, 102656d, 102912d, 103168d },
            fine.Select(tile => tile.Origin.X).Distinct().ToArray());
        CollectionAssert.AreEquivalent(new[] { 102400d, 102656d, 102912d, 103168d },
            fine.Select(tile => tile.Origin.Z).Distinct().ToArray());
        Assert.AreEqual(16, fine.Select(tile => (tile.Origin.X, tile.Origin.Z)).Distinct().Count());
        Assert.IsTrue(fine.All(tile => tile.SpacingCells == 4 && tile.SizeCells == 256));

        terrain.Demand(new(Focus.X + 2048, 0, Focus.Z), 1);
        Assert.AreEqual(0, terrain.View.Tiles.Length);
        Assert.AreEqual(1, oldView.Tiles.Length);
        Assert.AreSame(coarse, oldView.Tiles[0]);
        Assert.AreEqual(oldHeight, oldView.Height(Focus));
    }

    [TestMethod]
    public void Origin_shift_only_changes_object_pose_and_reuses_the_mesh()
    {
        var terrain = Bound(new());
        var tile = FirstCoarse(terrain);
        var ids = 0;
        SceneObjectId Id(string _) => new((ulong)++ids);
        var first = terrain.Objects(default, Id).Single();
        var origin = new ScenePoint(102400, 20, 102400);
        var shifted = terrain.Objects(origin, Id).Single();
        var unchanged = terrain.Objects(origin, Id).Single();

        Assert.AreEqual(new Vector3(102400, 0, 102400), first.Pose.Position);
        Assert.AreEqual(new Vector3(0, -20, 0), shifted.Pose.Position);
        Assert.AreEqual(first.Id, shifted.Id);
        Assert.AreEqual(1, ids);
        Assert.AreSame(tile.Mesh, ((SceneVisual.Geometry)first.Visuals.Single()).Mesh);
        Assert.AreSame(tile.Mesh, ((SceneVisual.Geometry)shifted.Visuals.Single()).Mesh);
        Assert.AreSame(first.Visuals.Single(), shifted.Visuals.Single());
        Assert.AreSame(shifted, unchanged);
        Assert.AreSame(tile, terrain.View.Tiles.Single());
    }

    [TestMethod]
    public void Cache_reuses_generated_tiles_for_equal_inputs_and_discards_them_when_terrain_changes()
    {
        var cache = new FarTerrain.Cache();
        var first = Bound(cache);
        var tile = FirstCoarse(first);
        var sameInputs = Bound(cache);
        sameInputs.Demand(Focus, 1);
        Assert.AreSame(tile, sameInputs.View.Tiles.Single());
        Assert.IsFalse(sameInputs.HasPendingCoarse);

        var changedInputs = Bound(cache, relief: 2);
        changedInputs.Demand(Focus, 1);
        Assert.AreEqual(0, changedInputs.View.Tiles.Length);
        Assert.IsTrue(changedInputs.HasPendingCoarse);
        Assert.AreNotSame(tile, FirstCoarse(changedInputs));
    }

    [TestMethod]
    public void Moving_along_a_line_evicts_tiles_and_objects_outside_the_current_demand()
    {
        var cache = new FarTerrain.Cache();
        var terrain = Bound(cache);
        terrain.SetFineBuildAllowed(true);
        for (var step = 0; step < 12; step++)
        {
            var focus = Focus with { X = Focus.X + step * TerrainTile.ParentCells * 2 };
            terrain.Demand(focus, 1);
            while (!terrain.Advance(10000, CancellationToken.None)) { }
            terrain.Objects(default, SceneFixtures.NextId());
            Assert.AreEqual(16, terrain.View.Tiles.Length);
            var saved = (FarTerrain.Saved)typeof(FarTerrain).GetField("saved",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(terrain)!;
            Assert.IsTrue(saved.Tiles.Count <= 17,
                "The session cache must retain the current requested parent and fine children, not the route history.");
            var objects = (System.Collections.IDictionary)typeof(FarTerrain).GetField("objects",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(terrain)!;
            Assert.IsTrue(objects.Count <= 16, "Evicted tile objects must not keep their meshes alive.");
        }
    }

    [TestMethod]
    public void Layout_change_reuses_only_tiles_that_do_not_read_layout_inputs()
    {
        var cache = new FarTerrain.Cache();
        var layout = NodeFixtures.Layout with { Plots = [] };
        var before = NodeFixtures.EmptyNodeSnapshot(layout, []);
        var after = NodeFixtures.EmptyNodeSnapshot(layout with
        {
            SceneModel = layout.SceneModel with { Scene = layout.SceneModel.Scene with
                { RootFlatten = new(new(0, 0), new(4, 4), 8) } }
        }, []);
        var old = Node(before);
        var outside = FirstCoarse(old);
        var changed = Node(after);
        changed.Demand(Focus, 1);
        Assert.AreSame(outside, changed.View.Tiles.Single(), "Unchanged outside tiles must be available immediately.");
        old.Demand(new(128, 0, 128), 1);
        while (!old.Advance(10000, CancellationToken.None)) { }
        var inside = old.View.Tiles.Single();

        changed.Demand(new(128, 0, 128), 1);
        Assert.IsTrue(changed.View.Tiles.IsEmpty, "Layout-dependent terrain must rebuild.");
        while (!changed.Advance(10000, CancellationToken.None)) { }
        Assert.AreNotSame(inside, changed.View.Tiles.Single());

        // This tile completes after the replacement was bound. Handover must also reuse it.
        old.Demand(Focus, 1);
        while (!old.Advance(10000, CancellationToken.None)) { }
        Assert.AreNotSame(outside, old.View.Tiles.Single(), "This tile must have completed after the replacement bound its cache.");
        outside = old.View.Tiles.Single();
        changed.Demand(Focus, 1);
        changed.ReuseTiles(old);
        Assert.AreSame(outside, changed.View.Tiles.Single());
        Assert.IsFalse(changed.HasPendingCoarse);

        FarTerrain Node(SceneWorldSnapshot snapshot)
        {
            var terrain = new FarTerrain(cache);
            terrain.BindNode(NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId)));
            terrain.SetFineBuildAllowed(false);
            return terrain;
        }
    }

    [TestMethod]
    public void Rebinding_between_initialization_and_tile_admission_retries_without_worker_failure()
    {
        var terrain = Bound(new());
        terrain.Demand(Focus, 1);
        terrain.Advance(128, CancellationToken.None);
        var changed = RailwayFixtures.Snapshot(relief: 2);
        terrain.BindRailway(changed, new RailwayPath(changed, RailwayFixtures.Edge));
        terrain.Demand(Focus, 1);

        // Reproduce the tile-admission phase after its earlier initialization read was invalidated.
        var admit = typeof(FarTerrain).GetMethod("AdvanceTile",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var completed = (bool)admit.Invoke(terrain, [128, CancellationToken.None])!;
        Assert.IsFalse(completed);
        Assert.IsTrue(terrain.View.Tiles.IsEmpty);
        Assert.IsNotNull(FirstCoarse(terrain));
    }

    private static FarTerrain Bound(FarTerrain.Cache cache, double relief = 0)
    {
        var snapshot = RailwayFixtures.Snapshot(relief: relief);
        var terrain = new FarTerrain(cache);
        terrain.BindRailway(snapshot, new RailwayPath(snapshot, RailwayFixtures.Edge));
        return terrain;
    }

    private static TerrainTile FirstCoarse(FarTerrain terrain)
    {
        terrain.Demand(Focus, 1);
        for (var step = 0; step < 1000 && terrain.View.Tiles.Length == 0; step++)
            terrain.Advance(128, CancellationToken.None);
        var tile = terrain.View.Tiles.Single();
        Assert.AreEqual(new ScenePoint(102400, 0, 102400), tile.Origin);
        Assert.AreEqual(32, tile.SpacingCells);
        Assert.AreEqual(1024, tile.SizeCells);
        return tile;
    }
}
