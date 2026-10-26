using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainControllerTests
{
    private static readonly ScenePoint Focus = new(102912, 0, 102912);

    [TestMethod]
    public void Repeating_parent_ranges_preserves_the_view_but_rebinding_requires_the_same_demand_again()
    {
        var terrain = Bound(new());
        terrain.SetFineBuildAllowed(false);
        var focus = Focus with { X = Focus.X + 16, Z = Focus.Z + 16 };
        terrain.Demand(focus, 1);
        while (terrain.HasRunnableWork) terrain.Advance(10000, CancellationToken.None);
        var visible = terrain.View;
        Assert.IsFalse(terrain.Demand(focus with { X = focus.X + 1 }, 2));
        Assert.AreSame(visible, terrain.View);

        var changed = RailwayFixtures.Snapshot(relief: 2);
        terrain.BindRailway(changed, new(changed, RailwayFixtures.Edge));
        Assert.IsTrue(terrain.Demand(focus, 1), "A new binding must accept even an unchanged parent range.");
        Assert.AreSame(visible, terrain.View, "The old surface remains visible during replacement.");
        while (terrain.HasRunnableWork) terrain.Advance(10000, CancellationToken.None);
        Assert.AreNotSame(visible.Tiles.Single(), terrain.View.Tiles.Single());
        var replacement = terrain.View;
        Assert.IsFalse(terrain.Demand(focus, 1));
        Assert.AreSame(replacement, terrain.View);
    }

    [TestMethod]
    public void Repeated_demand_observes_tiles_completed_by_another_terrain_sharing_the_cache()
    {
        var cache = new FarTerrain.Cache();
        var producer = Bound(cache);
        var observer = Bound(cache);
        foreach (var terrain in new[] { producer, observer })
        {
            terrain.SetFineBuildAllowed(false);
            terrain.Demand(Focus, 1);
            terrain.Advance(1, CancellationToken.None); // Bind to the shared cache.
            terrain.Advance(1, CancellationToken.None); // Resolve the requested parent's keys.
        }
        Assert.IsTrue(observer.View.Tiles.IsEmpty);
        while (producer.HasRunnableWork) producer.Advance(10000, CancellationToken.None);
        Assert.IsFalse(observer.HasRunnableWork, "Shared completed tiles satisfy the observer without its own build.");
        Assert.IsTrue(observer.View.Tiles.IsEmpty, "The observer has not published the shared completion yet.");
        Assert.IsFalse(observer.Demand(Focus, 1));
        Assert.AreSame(producer.View.Tiles.Single(), observer.View.Tiles.Single());
    }

    [TestMethod]
    public void Demand_uses_exclusive_parent_boundaries_and_updates_when_radius_domain_or_ahead_crosses_them()
    {
        var focus = new ScenePoint(512, 0, 512);
        var terrain = Bound(new());
        Assert.IsTrue(terrain.Demand(focus, 512));
        Assert.IsFalse(terrain.IntersectsDemand(new(new(1024, 0, 512), new(1025, 1, 513))),
            "An exact upper boundary must not include the next parent.");
        Assert.IsFalse(terrain.Demand(focus, 511));
        Assert.IsTrue(terrain.Demand(focus, 513));
        Assert.IsTrue(terrain.IntersectsDemand(new(new(1024, 0, 512), new(1025, 1, 513))));

        var domain = new ScenePanBounds(new(1, 1), new(1023, 1023));
        terrain = Bound(new());
        Assert.IsTrue(terrain.Demand(focus, 1, domain));
        Assert.IsFalse(terrain.Demand(focus, 1, new(new(2, 2), new(1022, 1022))));
        Assert.IsTrue(terrain.Demand(focus, 1, new(new(1, 1), new(1024, 1023))));
        Assert.IsTrue(terrain.IntersectsDemand(new(new(1024, 0, 512), new(1025, 1, 513))));

        terrain = Bound(new());
        Assert.IsTrue(terrain.Demand(focus, 1, ahead: []));
        Assert.IsFalse(terrain.Demand(focus, 1, ahead: [focus with { Y = 1 }]));
        Assert.IsTrue(terrain.Demand(focus, 1, ahead: [focus with { X = 513 }]),
            "Prefetch crosses the parent boundary even while visible terrain stays in the same parent.");
        Assert.IsTrue(terrain.IntersectsDemand(new(new(1024, 0, 512), new(1025, 1, 513))));
        Assert.IsTrue(terrain.Demand(focus, 1), "Removing ahead must remove its prefetch demand.");
        Assert.IsFalse(terrain.IntersectsDemand(new(new(1024, 0, 512), new(1025, 1, 513))));
        Assert.IsTrue(terrain.Demand(focus, 1, ahead: []), "An empty ahead list still enables prefetch around the focus.");
        Assert.IsFalse(terrain.Demand(focus, 1, ahead: []));
    }

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
    public void Cache_reuses_equal_inputs_and_keeps_changed_terrain_until_its_replacement_is_visible()
    {
        var cache = new FarTerrain.Cache();
        var first = Bound(cache);
        var tile = FirstCoarse(first);
        var sameInputs = Bound(cache);
        sameInputs.Demand(Focus, 1);
        Assert.AreSame(tile, sameInputs.View.Tiles.Single());
        Assert.IsTrue(sameInputs.HasPendingCoarse, "Cache identity is resolved by the worker.");
        sameInputs.Advance(128, CancellationToken.None);
        Assert.IsFalse(sameInputs.HasPendingCoarse);

        var changedInputs = Bound(cache, relief: 2);
        changedInputs.Demand(Focus, 1);
        Assert.AreSame(tile, changedInputs.View.Tiles.Single());
        Assert.IsTrue(changedInputs.HasPendingCoarse);
        while (changedInputs.HasPendingCoarse)
        {
            Assert.IsNotNull(changedInputs.View.Height(Focus));
            changedInputs.Advance(128, CancellationToken.None);
        }
        Assert.AreNotSame(tile, changedInputs.View.Tiles.Single());
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
        changed.ReuseTiles(old);
        Assert.AreSame(inside, changed.View.Tiles.Single(), "Changed terrain stays visible until its replacement appears.");
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
        Assert.IsTrue(changed.HasPendingCoarse, "The worker must resolve the newly demanded parent's inputs first.");
        Assert.IsFalse(changed.Advance(1, CancellationToken.None));
        Assert.AreSame(outside, changed.View.Tiles.Single(), "Resolving unchanged inputs must reuse the completed tile without rebuilding it.");
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
    public void Layout_change_keeps_unchanged_fine_tiles_visible_while_the_parent_rebuilds()
    {
        var cache = new FarTerrain.Cache();
        var layout = NodeFixtures.Layout with { Plots = [] };
        var old = Node(layout);
        old.Demand(new(128, 0, 128), 1);
        while (!old.Advance(10000, CancellationToken.None)) { }
        var unchanged = old.View.Tiles.Where(tile => tile.Origin.X > 0 || tile.Origin.Z > 0).ToArray();
        Assert.AreEqual(15, unchanged.Length);
        Assert.IsTrue(unchanged.All(tile => tile.SpacingCells == TerrainTile.NearSpacingCells));

        var next = Node(layout with { SceneModel = layout.SceneModel with { Scene = layout.SceneModel.Scene with
            { RootFlatten = new(new(0, 0), new(4, 4), 8) } } });
        next.SetFineBuildAllowed(false);
        next.Demand(new(128, 0, 128), 1);
        Assert.IsTrue(next.HasPendingCoarse);
        CollectionAssert.AreEquivalent(old.View.Tiles.ToArray(), next.View.Tiles.ToArray(),
            "The complete displayed fine group must stay visible until a replacement covers it.");

        while (next.HasPendingCoarse) next.Advance(10000, CancellationToken.None);
        Assert.AreEqual(TerrainTile.FarSpacingCells, next.View.Tiles.Single().SpacingCells,
            "An incomplete fine group must not replace a ready coarse parent.");
        next.SetFineBuildAllowed(true);
        while (!next.Advance(10000, CancellationToken.None)) { }
        Assert.AreEqual(16, next.View.Tiles.Length);
        foreach (var tile in unchanged) Assert.IsTrue(next.View.Tiles.Contains(tile));

        FarTerrain Node(SettlementLayoutProjection value)
        {
            var snapshot = NodeFixtures.EmptyNodeSnapshot(value, []);
            var terrain = new FarTerrain(cache);
            terrain.BindNode(NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId)));
            return terrain;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Rebinding_a_node_keeps_its_displayed_parent_covered_until_the_new_height_is_ready(bool fine)
    {
        var layout = NodeFixtures.Layout with { Plots = [] };
        var terrain = new FarTerrain(new());
        Bind(layout);
        terrain.SetFineBuildAllowed(fine);
        var focus = new ScenePoint(128, 0, 128);
        terrain.Demand(focus, 1);
        while (terrain.HasRunnableWork) terrain.Advance(4096, CancellationToken.None);
        var old = terrain.View;
        var points = Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 4)
            .Select(x => new ScenePoint(128 + x * 256, 0, 128 + z * 256))).ToArray();
        var oldMinimum = old.Tiles.Min(tile => tile.Mesh.Vertices.Min(vertex => vertex.Position.Y));
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);

        // Rebind twice before either generation can publish, as successive live changes may arrive together.
        Bind(Flatten(-10));
        terrain.Demand(focus, 1);
        Bind(Flatten(-20));
        terrain.Demand(focus, 1);
        Assert.IsTrue(terrain.HasPendingCoarse, "Old displayed tiles must not satisfy fresh L2 priority.");
        CollectionAssert.AreEquivalent(old.Tiles.ToArray(), terrain.View.Tiles.ToArray());
        for (var step = 0; step < 20000 && terrain.HasRunnableWork; step++)
        {
            var frame = new SceneFrame(stamp, default, 0, SceneReadiness.Partial, [], [], [], [], farTerrain: terrain.View);
            foreach (var point in points)
                Assert.IsInstanceOfType<Lookup<double>.Found>(frame.DisplayTerrainHeight(point.X, point.Z), "Rebinding must not open a hole.");
            Assert.IsTrue(terrain.View.Tiles.Length is 1 or 16, "A parent cannot mix coarse and partial fine coverage.");
            terrain.Advance(128, CancellationToken.None);
        }
        Assert.IsFalse(terrain.HasRunnableWork);
        foreach (var point in points) Assert.IsNotNull(terrain.View.Height(point));
        Assert.IsTrue(terrain.View.Tiles.Min(tile => tile.Mesh.Vertices.Min(vertex => vertex.Position.Y)) < oldMinimum,
            "The replacement must contain the changed height, rather than keep the old surface forever.");

        SettlementLayoutProjection Flatten(int height) => layout with
        { SceneModel = layout.SceneModel with { Scene = layout.SceneModel.Scene with
            { RootFlatten = new(new(0, 0), new(4, 4), height) } } };
        void Bind(SettlementLayoutProjection value)
        {
            var snapshot = NodeFixtures.EmptyNodeSnapshot(value, []);
            terrain.BindNode(NodeScene.Prepare(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId)));
        }
    }

    [TestMethod]
    public void Railway_break_rebinding_keeps_complete_fine_terrain_while_its_keys_are_resolved()
    {
        var snapshot = RailwayFixtures.Snapshot(64, 64, relief: 0);
        var terrain = new FarTerrain(new());
        var focus = new ScenePoint(512, 0, 512);
        terrain.BindRailway(snapshot, new(snapshot, RailwayFixtures.Edge));
        terrain.Demand(focus, 1);
        for (var step = 0; step < 20000 && terrain.HasRunnableWork; step++)
            terrain.Advance(65536, CancellationToken.None);
        Assert.IsFalse(terrain.HasRunnableWork);
        var fine = terrain.View.Tiles.ToArray();
        Assert.AreEqual(16, fine.Length);
        Assert.IsTrue(fine.All(tile => tile.SpacingCells == TerrainTile.NearSpacingCells));

        var broken = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with
            { Breaks = [new(new(1), 1, 32)] });
        terrain.BindRailway(broken, new(broken, RailwayFixtures.Edge));
        terrain.Demand(focus, 1);
        CollectionAssert.AreEquivalent(fine, terrain.View.Tiles.ToArray());
        for (var step = 0; step < 20000 && terrain.HasRunnableWork; step++)
        {
            terrain.Advance(128, CancellationToken.None);
            CollectionAssert.AreEquivalent(fine, terrain.View.Tiles.ToArray(),
                "Resolving unchanged input keys must not demote the displayed 2 m terrain to 16 m.");
        }
        Assert.IsFalse(terrain.HasRunnableWork);
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
        var completed = terrain.AdvanceTile(128, CancellationToken.None);
        Assert.IsFalse(completed);
        Assert.IsTrue(terrain.View.Tiles.IsEmpty);
        Assert.IsNotNull(FirstCoarse(terrain));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Binding_defers_dependency_reads_and_discards_initialization_completed_after_rebinding(bool node)
    {
        var original = node ? NodeFixtures.EmptyNodeSnapshot(NodeFixtures.Layout with { Plots = [] }, [])
            : RailwayFixtures.Snapshot(relief: 0);
        var path = node ? null : new RailwayPath(original, RailwayFixtures.Edge);
        var prepared = node ? NodeScene.Prepare(original, NodeLayout.Build(original, NodeFixtures.NodeId)) : null;
        using var content = new SceneFixtures.BlockedAppearanceView(original.Content, blockRailway: !node);
        var blocked = original with { Content = content };
        var terrain = new FarTerrain(new());
        Task? initialization = null;
        try
        {
            // Railway dependency capture and node source construction must both run outside Bind.
            await Task.Run(() =>
            {
                if (node) terrain.BindNode(prepared! with { Snapshot = blocked });
                else terrain.BindRailway(blocked, path!);
            }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(content.Entered.Task.IsCompleted, "Bind must only capture inputs and binding identity.");
            Assert.IsTrue(terrain.HasPendingCoarse);
            terrain.Demand(Focus, 1);
            initialization = Task.Run(() => terrain.Advance(128, CancellationToken.None));
            await content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var changed = node ? original : RailwayFixtures.Snapshot(relief: 2);
            if (node) terrain.BindNode(NodeScene.Prepare(changed, NodeLayout.Build(changed, NodeFixtures.NodeId)));
            else terrain.BindRailway(changed, new(changed, RailwayFixtures.Edge));
            var latestFocus = new ScenePoint(128, 0, 128);
            terrain.Demand(latestFocus, 1);
            terrain.SetFineBuildAllowed(false);
            content.Release.Set();
            await initialization.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(terrain.HasPendingCoarse, "The old initializer must not complete the new binding.");
            while (terrain.HasRunnableWork) terrain.Advance(10000, CancellationToken.None);
            var tile = terrain.View.Tiles.Single();
            Assert.AreEqual(new ScenePoint(0, 0, 0), tile.Origin, "Initialization must retain the latest demand.");

            var expected = new FarTerrain(new());
            if (node) expected.BindNode(NodeScene.Prepare(changed, NodeLayout.Build(changed, NodeFixtures.NodeId)));
            else expected.BindRailway(changed, new(changed, RailwayFixtures.Edge));
            expected.SetFineBuildAllowed(false);
            expected.Demand(latestFocus, 1);
            while (expected.HasRunnableWork) expected.Advance(10000, CancellationToken.None);
            CollectionAssert.AreEqual(expected.View.Tiles.Single().Mesh.Vertices.ToArray(), tile.Mesh.Vertices.ToArray());
        }
        finally
        {
            content.Release.Set();
            if (initialization is not null) await initialization;
        }
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
