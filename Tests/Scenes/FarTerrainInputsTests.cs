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
