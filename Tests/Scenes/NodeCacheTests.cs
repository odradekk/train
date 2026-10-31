using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeCacheTests
{
    [TestMethod]
    public void Voxel_array_budget_includes_the_appearance_retained_by_picking()
    {
        var appearance = SceneFixtures.Cube with
        { Presentation = new VoxelPresentation.Mesh(new("large-model"), Enumerable.Repeat(MaterialCategory.Concrete, 4096).ToImmutableArray()) };
        var grid = new VoxelGrid([new(new(0, 0, 0), new(VoxelOrientation.PxPy, appearance, null, null, null, []))]);
        var smallerAppearance = appearance with { Presentation = new VoxelPresentation.Mesh(new("large-model"), []) };
        var smaller = new VoxelGrid([new(new(0, 0, 0), new(VoxelOrientation.PxPy, smallerAppearance, null, null, null, []))]);
        Assert.IsGreaterThan(smaller.ArrayBytes, grid.ArrayBytes);
    }

    [TestMethod]
    public void Closing_keeps_permanent_parts_then_the_parts_nearest_the_initial_focus()
    {
        var layout = Layout();
        // 初始焦点是节点原点。
        var permanent = Part();
        var near = Part(new(new(0, 0, 0), new(1, 1, 1)));
        var far = Part(new(new(100, 0, 0), new(101, 1, 1)));
        var budget = permanent.ArrayBytes + near.ArrayBytes;
        var cache = new NodeScene.Cache(budget);
        var owner = cache.Open(new(1));
        cache.Publish(new(1), owner, new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty
            .Add("z-permanent", permanent).Add("z-near", near).Add("a-far", far), []));
        Assert.AreEqual(0, cache.IdleBytes);
        Assert.AreEqual(3, cache.Read(new(1))!.Parts.Count);
        cache.Close(new(1), owner);
        Assert.IsTrue(cache.IdleBytes <= budget);
        CollectionAssert.AreEquivalent(new[] { "z-permanent", "z-near" }, cache.Read(new(1))!.Parts.Keys.ToArray());
    }

    [TestMethod]
    public void Oversized_parts_are_not_retained_and_closed_owners_cannot_publish()
    {
        var layout = Layout();
        var part = Part();
        var budget = part.ArrayBytes - 1;
        var cache = new NodeScene.Cache(budget);
        var owner = cache.Open(new(1));
        var built = new NodeScene.Built(layout, ImmutableDictionary<string, NodeScene.Part>.Empty.Add("large", part), []);
        cache.Publish(new(1), owner, built);
        cache.Close(new(1), owner);
        Assert.IsNull(cache.Read(new(1)));
        cache.Publish(new(1), owner, built);
        Assert.IsNull(cache.Read(new(1)));
        Assert.IsTrue(cache.IdleBytes <= budget);
    }

    [TestMethod]
    public void The_node_that_left_first_is_evicted_whole_before_the_newer_node_is_clipped()
    {
        var layout = Layout();
        var part = Part();
        var built = new NodeScene.Built(layout, ImmutableDictionary<string, NodeScene.Part>.Empty.Add("part", part), []);
        var budget = part.ArrayBytes;
        var cache = new NodeScene.Cache(budget);
        var first = cache.Open(new(1));
        var second = cache.Open(new(2));
        cache.Publish(new(1), first, built);
        cache.Publish(new(2), second, built);
        cache.Close(new(2), second);
        Assert.IsNotNull(cache.Read(new(1)), "Active geometry is outside the idle budget.");
        cache.Close(new(1), first);
        Assert.IsNull(cache.Read(new(2)), "Leave order, rather than node ID or open order, decides eviction.");
        Assert.AreSame(built.Parts, cache.Read(new(1))!.Parts);
        Assert.IsTrue(cache.IdleBytes <= budget);
    }

    [TestMethod]
    public void Closing_an_old_owner_does_not_retire_a_reopened_node()
    {
        var cache = new NodeScene.Cache(0);
        var old = cache.Open(new(1));
        var current = cache.Open(new(1));
        var built = new NodeScene.Built(Layout(), [], []);
        cache.Publish(new(1), current, built);
        cache.Close(new(1), old);
        Assert.AreSame(built.Parts, cache.Read(new(1))!.Parts);
        Assert.AreEqual(0, cache.IdleBytes);
        cache.Close(new(1), current);
        Assert.IsNull(cache.Read(new(1)));
    }

    private static NodeLayout Layout() => NodeLayout.Build(NodeFixtures.Snapshot(platforms: 0, trains: []), NodeFixtures.NodeId);

    private static NodeScene.Part Part(SceneBounds? bounds = null) => new("fixed",
        WorldSceneSession.BuildGeometry(id => DesignGeometry.Build(SceneFixtures.Content(),
            SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))])),
            null, SceneFixtures.Identity, id)), ImmutableDictionary<GridColumn, double>.Empty, Bounds: bounds);
}
