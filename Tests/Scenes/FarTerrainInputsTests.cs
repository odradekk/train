using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainInputsTests
{
    [TestMethod]
    public void Breaks_and_repairs_without_flatten_preserve_tiles()
    {
        var snapshot = RailwayFixtures.Snapshot(64, 64, relief: 0);
        var terrain = new FarTerrain(new());
        var point = new ScenePoint(16, 0, 64);
        var original = Tile(terrain, snapshot, point);
        var broken = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with
            { Breaks = [new(new(1), 1, 32)] });
        Assert.AreSame(original, Tile(terrain, broken, point));
        var repairing = RailwayFixtures.ReplaceCore(broken, edge: broken.Core.World.Edges[0] with
            { Works = [Repair(1)] });
        Assert.AreSame(original, Tile(terrain, repairing, point));
        var rebound = RailwayFixtures.ReplaceCore(repairing, edge: repairing.Core.World.Edges[0] with
            { Works = [Repair(2)] });
        Assert.AreSame(original, Tile(terrain, rebound, point));
    }

    [TestMethod]
    public void Long_diagonal_corner_outside_segment_bands_survives_track_changes()
    {
        var snapshot = RailwayFixtures.Snapshot(6010, 6000, relief: 0, trackLimit: 2);
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[1] with
            { Position = new(6000 / Math.Sqrt(2), 6000 / Math.Sqrt(2)) });
        var terrain = new FarTerrain(new());
        var point = new ScenePoint(512, 0, 7680);
        var original = Tile(terrain, snapshot, point);
        var changed = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with { Tracks = 2 });
        Assert.AreSame(original, Tile(terrain, changed, point));
    }

    [TestMethod]
    public void Content_generation_without_height_or_material_changes_preserves_tiles()
    {
        var snapshot = RailwayFixtures.Snapshot(64, 64, relief: 0);
        var terrain = new FarTerrain(new());
        var point = new ScenePoint(16, 0, 64);
        var original = Tile(terrain, snapshot, point);
        var changed = snapshot with { Content = RailwayFixtures.Content(generation: 2) };
        Assert.AreSame(original, Tile(terrain, changed, point));
    }

    [TestMethod]
    public void Track_count_changes_the_terrain_band_height()
    {
        var snapshot = RailwayFixtures.Snapshot(64, 64, relief: 0, trackLimit: 2);
        var terrain = new FarTerrain(new());
        var point = new ScenePoint(16, 0, 64);
        var original = Tile(terrain, snapshot, point);
        var changed = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with { Tracks = 2 });
        var widened = Tile(terrain, changed, point);
        Assert.AreNotSame(original, widened);
        Assert.IsFalse(original.Mesh.Vertices.Select(v => v.Position).SequenceEqual(widened.Mesh.Vertices.Select(v => v.Position)),
            "Changing tracks with fixed TrackLimit must change actual terrain, independently of path alignment.");
    }

    [TestMethod]
    public void Endpoint_height_inputs_only_invalidate_tiles_intersecting_endpoint_blend_segments()
    {
        var snapshot = RailwayFixtures.Snapshot(5000, 5000, relief: 0,
            content: RailwayFixtures.Content(terrain: RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(13, 1)] }));
        var changed = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[0] with
            { Terrain = snapshot.Core.World.Nodes[0].Terrain with { Relief = 2 } });
        var before = new RailwayTerrainCaps(snapshot, new(snapshot, RailwayFixtures.Edge));
        var after = new RailwayTerrainCaps(changed, new(changed, RailwayFixtures.Edge));
        var material = new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper);
        var oldInputs = before.CreateInputs(material, 1);
        var newInputs = after.CreateInputs(material, 1);
        var near = new ScenePanBounds(new(-36, -36), new(292, 292));
        var middle = new ScenePanBounds(new(-36, 4060), new(292, 4388));
        Assert.AreNotEqual(oldInputs(near), newInputs(near));
        Assert.AreEqual(oldInputs(middle), newInputs(middle));
        Assert.AreNotEqual(before.Height(new(40, 0, 16)), after.Height(new(40, 0, 16)),
            "The fixture must change the actual endpoint ground height.");
    }

    [TestMethod]
    public void Repair_flatten_invalidates_its_tile_but_job_identity_does_not()
    {
        var hash = new DesignHash("test:far-repair");
        var model = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]) with
            { Design = hash, RootFlatten = new(new(-4, -4), new(4, 4), -20) }) with { Design = hash };
        var snapshot = RailwayFixtures.Snapshot(64, 64, relief: 0,
            content: RailwayFixtures.Content(design: model, terrain: RailwayFixtures.TerrainAppearance with
                { RailwayModels = [new(1, hash, hash, hash)] }));
        snapshot = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with
            { Breaks = [new(new(1), 1, 32)] });
        var terrain = new FarTerrain(new());
        var point = new ScenePoint(16, 0, 64);
        var original = Tile(terrain, snapshot, point);
        var repairing = RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with
            { Works = [Repair(1)] });
        var flattened = Tile(terrain, repairing, point);
        Assert.AreNotSame(original, flattened);
        Assert.IsTrue(flattened.Mesh.Vertices.Min(v => v.Position.Y) < original.Mesh.Vertices.Min(v => v.Position.Y),
            "The fixture must change the actual conservative terrain height.");
        var rebound = RailwayFixtures.ReplaceCore(repairing, edge: repairing.Core.World.Edges[0] with
            { Works = [Repair(2)] });
        Assert.AreSame(flattened, Tile(terrain, rebound, point));
    }

    private static RailwayWorkView Repair(ulong id) =>
        new(new(id), RailwayWorkKind.RepairBreak, 1, new(1), new(1), [], .25, []);

    [TestMethod]
    public void Moving_repair_between_tracks_changes_pose_inputs_and_lowers_the_new_repair_site()
    {
        var before = RepairMoveScene(twoTracks: true);
        var after = MoveRepair(before);
        var material = new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper);
        var bounds = new ScenePanBounds(new(-36, -36), new(1060, 1060));
        var oldPath = new RailwayPath(before, RailwayFixtures.Edge);
        var newPath = new RailwayPath(after, RailwayFixtures.Edge);
        var oldPlacement = RailwayTerrain.HeightFlattens(before, oldPath, 0).Single();
        var newPlacement = RailwayTerrain.HeightFlattens(after, newPath, 0).Single();
        Assert.AreNotEqual(oldPlacement.Pose.Position, newPlacement.Pose.Position);
        Assert.AreNotEqual(oldPlacement.Pose.Rotation, newPlacement.Pose.Rotation,
            "The asymmetric repair footprint must also turn outward on the opposite track.");
        Assert.AreNotEqual(new RailwayTerrainCaps(before, oldPath).CreateInputs(material, 1)(bounds),
            new RailwayTerrainCaps(after, newPath).CreateInputs(material, 1)(bounds),
            "Equal flatten counts with different placement positions and rotations must not reuse a tile key.");
        AssertRepairMoveRebuildsTerrain(before, after);
    }

    [TestMethod]
    public void Moving_one_repair_job_between_breaks_rebinds_and_lowers_the_new_repair_site()
    {
        var before = RepairMoveScene(twoTracks: false);
        var after = MoveRepair(before);
        Assert.AreEqual(before.Core.World.Edges[0].Works.Length, after.Core.World.Edges[0].Works.Length);
        Assert.AreEqual(before.Core.World.Edges[0].Works[0].Id, after.Core.World.Edges[0].Works[0].Id);
        var oldPath = new RailwayPath(before, RailwayFixtures.Edge);
        var newPath = new RailwayPath(after, RailwayFixtures.Edge);
        var oldPlacement = RailwayTerrain.HeightFlattens(before, oldPath, 0).Single();
        var newPlacement = RailwayTerrain.HeightFlattens(after, newPath, 0).Single();
        Assert.AreEqual(oldPlacement.Pose.Rotation, newPlacement.Pose.Rotation,
            "This fixture isolates translation, so Quaternion.IsIdentity cannot distinguish the placements.");
        Assert.AreNotEqual(oldPlacement.Pose.Position, newPlacement.Pose.Position);
        var material = new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper);
        var bounds = new ScenePanBounds(new(-36, -36), new(1060, 1060));
        Assert.AreNotEqual(new RailwayTerrainCaps(before, oldPath).CreateInputs(material, 1)(bounds),
            new RailwayTerrainCaps(after, newPath).CreateInputs(material, 1)(bounds),
            "Same-track repair translation must change the tile key independently of BindRailway generation.");
        AssertRepairMoveRebuildsTerrain(before, after);
    }

    private static SceneWorldSnapshot RepairMoveScene(bool twoTracks)
    {
        var hash = new DesignHash("test:asymmetric-repair-flatten");
        var model = SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]) with
            { Design = hash, RootFlatten = new(new(0, -3), new(24, 3), -20) }) with { Design = hash };
        var tracks = twoTracks ? 2 : 1;
        var snapshot = RailwayFixtures.Snapshot(64, 64, relief: 0, tracks: tracks,
            content: RailwayFixtures.Content(design: model, terrain: RailwayFixtures.TerrainAppearance with
                { RailwayModels = [new(tracks, hash, hash, hash)] }));
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[0] with { Position = new(200, 200) });
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[1] with { Position = new(200, 264) });
        return RailwayFixtures.ReplaceCore(snapshot, edge: snapshot.Core.World.Edges[0] with
        {
            Breaks = [new(new(1), 1, 20), new(new(2), twoTracks ? 2 : 1, twoTracks ? 20 : 50)],
            Works = [Repair(1)]
        });
    }

    private static SceneWorldSnapshot MoveRepair(SceneWorldSnapshot before)
    {
        var edge = before.Core.World.Edges[0];
        var gap = edge.Breaks[1];
        return RailwayFixtures.ReplaceCore(before, edge: edge with
            { Works = [edge.Works[0] with { Track = gap.Track, RepairingBreak = gap.Id }] });
    }

    private static void AssertRepairMoveRebuildsTerrain(SceneWorldSnapshot before, SceneWorldSnapshot after)
    {
        var terrain = new FarTerrain(new());
        var point = new ScenePoint(400, 0, 464);
        var original = Tile(terrain, before, point);
        var rebuilt = Tile(terrain, after, point);
        Assert.AreNotSame(original, rebuilt,
            "A job moved to another existing break must actually prepare and publish a replacement tile.");
        var oldL0 = RailwayTerrain.Build(before, new(before, RailwayFixtures.Edge), 0, CancellationToken.None);
        var newL0 = RailwayTerrain.Build(after, new(after, RailwayFixtures.Edge), 0, CancellationToken.None);
        Assert.AreEqual(oldL0.Origin, newL0.Origin);
        var lowered = newL0.Heights.Where(pair => oldL0.Heights.TryGetValue(pair.Key, out var oldHeight) && pair.Value < oldHeight).ToArray();
        Assert.IsTrue(lowered.Length > 0, "The real L0 generator must lower columns at the new repair site.");
        Assert.IsTrue(lowered.Any(pair =>
        {
            var x = newL0.Origin.X + pair.Key.X + .5;
            var z = newL0.Origin.Z + pair.Key.Z + .5;
            var oldFar = original.Height(x - original.Origin.X, z - original.Origin.Z);
            var newFar = rebuilt.Height(x - rebuilt.Origin.X, z - rebuilt.Origin.Z);
            return oldFar is { } oldValue && newFar is { } newValue &&
                newValue < oldValue && newValue < pair.Value;
        }), "At a genuinely lowered L0 column, the replacement far surface must lower and stay below the new L0 top.");
    }

    private static TerrainTile Tile(FarTerrain terrain, SceneWorldSnapshot snapshot, ScenePoint point)
    {
        terrain.BindRailway(snapshot, new(snapshot, RailwayFixtures.Edge));
        terrain.SetFineBuildAllowed(false);
        terrain.Demand(point, 1);
        for (var step = 0; step < 20000 && terrain.HasRunnableWork; step++)
            terrain.Advance(4096, CancellationToken.None);
        Assert.IsFalse(terrain.HasRunnableWork);
        return terrain.View.Tiles.Single();
    }
}
