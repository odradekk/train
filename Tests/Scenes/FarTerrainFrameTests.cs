using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class FarTerrainFrameTests
{
    private static TerrainTile Tile(double height, int spacing = 4, ScenePoint origin = default) =>
        TerrainTile.Build(origin, 32, spacing, _ => height, new SceneMaterial.BuiltIn(BuiltInMaterial.Water));

    private static SceneFrame Frame(FarTerrainView view, ScenePoint origin = default,
        ImmutableArray<PickGrid> picking = default, ImmutableArray<RailwaySegment> segments = default) =>
        new(new(new(Guid.NewGuid()), new(1), new(1), 1), origin, 0, SceneReadiness.Ready,
            [], picking.IsDefault ? [] : picking, [], [], segments: segments, farTerrain: view);

    [TestMethod]
    public void Display_height_uses_visible_triangles_and_finer_level_including_outer_edge()
    {
        var fine = TerrainTile.Build(default, 32, 4, p => p.X * p.Z,
            new SceneMaterial.BuiltIn(BuiltInMaterial.Water));
        var frame = Frame(new([Tile(100, 32), fine]));
        Assert.AreEqual(0d, ((Lookup<double>.Found)frame.DisplayTerrainHeight(1, 1)).Value);
        Assert.AreEqual(1024d, ((Lookup<double>.Found)frame.DisplayTerrainHeight(32, 32)).Value);
        Assert.IsInstanceOfType<Lookup<double>.Missing>(frame.DisplayTerrainHeight(33, 1));
        Assert.IsInstanceOfType<Lookup<double>.Missing>(frame.TerrainHeight(1, 1));
        Assert.IsInstanceOfType<Lookup<WorldLocation>.Missing>(frame.Locate(new(1, 0, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => frame.DisplayTerrainHeight(double.NaN, 0));
    }

    [TestMethod]
    public void Exact_height_takes_priority_over_remote_surface()
    {
        var segment = new RailwaySegment(0, default, null!,
            ImmutableDictionary<GridColumn, double>.Empty.Add(new(1, 1), 7), null!, []);
        var frame = Frame(new([Tile(20)]), segments: [segment]);
        Assert.AreEqual(7d, ((Lookup<double>.Found)frame.DisplayTerrainHeight(1, 1)).Value);
    }

    [TestMethod]
    public void Picking_includes_visible_skirts_and_closed_surface_edges()
    {
        var frame = Frame(new([Tile(0)]));
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(new(-1, -0.5f, 2), Vector3.UnitX, 2)));
        Assert.IsInstanceOfType<ScenePick.NotReady>(frame.Pick(new(new(32, 2, 32), -Vector3.UnitY, 3)));
    }

    [TestMethod]
    [DataRow(2d, true)]
    [DataRow(1d, false)]
    [DataRow(-2d, false)]
    public void Remote_surface_only_blocks_a_strictly_more_distant_business_hit(double height, bool blocked)
    {
        var grid = new VoxelGrid([new(new(1, 0, 1), new(VoxelOrientation.PxPy,
            SceneFixtures.Cube, null, null, new PickSubject.Terrain(NodeFixtures.Location), []))]);
        var frame = Frame(new([Tile(height)]), picking: [new(new(1), SceneFixtures.Identity, grid)]);
        var pick = frame.Pick(new(new(1.5f, 10, 1.5f), -Vector3.UnitY, 30));
        if (blocked) Assert.IsInstanceOfType<ScenePick.NotReady>(pick);
        else Assert.IsInstanceOfType<ScenePick.Hit>(pick);
    }

    [TestMethod]
    [DataRow(TerrainTile.NearSpacingCells)]
    [DataRow(TerrainTile.FarSpacingCells)]
    public void Flattened_L0_top_is_picked_before_the_strictly_lower_far_surface(int spacing)
    {
        var builder = TerrainHeightCap.Begin(new(new(0, 0), new(32, 32)), _ => 12);
        while (!builder.Complete) builder.Advance(128);
        var cap = builder.Result;
        var far = TerrainTile.Build(default, 32, spacing, _ => 100,
            new SceneMaterial.BuiltIn(BuiltInMaterial.Water), lowerBounds: [cap]);
        var properties = new CellProperties(VoxelOrientation.PxPy, SceneFixtures.Cube, null, null,
            new PickSubject.Terrain(NodeFixtures.Location), []);
        var grid = new VoxelGrid(from z in Enumerable.Range(8, 8)
            from x in Enumerable.Range(8, 8) select new SceneCell(new(x, 11, z), properties));
        var frame = Frame(new([far]), picking: [new(new(1), SceneFixtures.Identity, grid)]);
        foreach (var origin in new[] { new Vector3(10.25f, 13, 10.25f), new(12.4f, 13.7f, 9.8f), new(12.75f, 14.25f, 12.75f) })
        foreach (var direction in new[] { new Vector3(-.6f, -1, .7f), new(.8f, -1, -.6f), new(.1f, -1, .1f) })
            Assert.IsInstanceOfType<ScenePick.Hit>(frame.Pick(new(origin, Vector3.Normalize(direction), 10)));
    }

    [TestMethod]
    public void Frame_keeps_its_visible_tiles_and_queries_survive_origin_shift()
    {
        var tile = Tile(20, origin: new(1024, 0, 1024));
        var first = Frame(new([tile]));
        var shifted = Frame(new([tile]), new(1024, 10, 1024));
        var replacement = Frame(new([Tile(40, origin: tile.Origin)]));
        Assert.AreEqual(20d, ((Lookup<double>.Found)first.DisplayTerrainHeight(1025, 1025)).Value);
        Assert.AreEqual(10d, ((Lookup<double>.Found)shifted.DisplayTerrainHeight(1, 1)).Value);
        Assert.AreEqual(40d, ((Lookup<double>.Found)replacement.DisplayTerrainHeight(1025, 1025)).Value);
        Assert.IsInstanceOfType<ScenePick.NotReady>(first.Pick(new(new(1025, 30, 1025), -Vector3.UnitY, 50)));
        Assert.IsInstanceOfType<ScenePick.NotReady>(shifted.Pick(new(new(1, 20, 1), -Vector3.UnitY, 50)));
        Assert.IsInstanceOfType<ScenePick.Miss>(shifted.Pick(new(new(1, 20, 1), -Vector3.UnitY, 5)));
    }
}
