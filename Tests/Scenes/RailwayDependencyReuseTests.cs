using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayDependencyReuseTests
{
    [TestMethod]
    public void SameContentDoesNotHideChangedTrackCount()
    {
        var original = RailwayFixtures.Snapshot(130, 130, relief: 0, trackLimit: 2, content: Content());
        var edge = original.Core.World.Edges[0];
        var changed = RailwayFixtures.ReplaceCore(original, edge: edge with { Tracks = 2 });
        Assert.AreSame(original.Content, changed.Content);

        Assert.IsFalse(Capture(original).Matches(changed, new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void SameContentDoesNotHideChangedAdjacentNodeEntry()
    {
        var original = NodeFixtures.Snapshot(platforms: 0, trains: []);
        var adjacent = original.Core.World.Edges.Single(edge => edge.Id != RailwayFixtures.Edge);
        var changed = RailwayFixtures.ReplaceCore(original, edge: adjacent with { TrackLimit = 2 });
        Assert.AreSame(original.Content, changed.Content);
        Assert.AreEqual(original.Core.World.Edges[0], changed.Core.World.Edges[0],
            "Only the neighboring edge changes; the selected railway itself is unchanged.");

        Assert.IsFalse(Capture(original, startTerrain: true).Matches(changed, new(changed, RailwayFixtures.Edge), 0));
        Assert.IsTrue(Capture(original).Matches(changed, new(changed, RailwayFixtures.Edge), 0),
            "A segment that did not read the node layout can still reuse its geometry.");
    }

    [TestMethod]
    public void EquivalentContentInstancesProduceReusableDependenciesAndTheSameKey()
    {
        var original = RailwayFixtures.Snapshot(content: Content());
        var equivalent = original with { Content = Content() };
        Assert.AreNotSame(original.Content, equivalent.Content);

        var dependencies = Capture(original);
        Assert.IsTrue(dependencies.Matches(equivalent, new(equivalent, RailwayFixtures.Edge), 0));
        Assert.AreEqual(dependencies.Key, Capture(equivalent).Key,
            "Node boundary cache inputs must remain equal across equivalent content views.");
    }

    [TestMethod]
    public void DependenciesDoNotRetainTheirSourceContentAndStillMatchEquivalentValues()
    {
        var (dependencies, source) = CaptureWithoutRetainingSource();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(source.TryGetTarget(out _), "Cached dependencies must not retain the old content view.");

        var equivalent = RailwayFixtures.Snapshot(content: Content());
        Assert.IsTrue(dependencies.Matches(equivalent, new(equivalent, RailwayFixtures.Edge), 0));
        Assert.AreEqual(dependencies.Key, Capture(equivalent).Key);
        GC.KeepAlive(dependencies);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RailwayDependencies Dependencies, WeakReference<IContentView> Source) CaptureWithoutRetainingSource()
    {
        var snapshot = RailwayFixtures.Snapshot(content: Content());
        return (Capture(snapshot), new(snapshot.Content));
    }

    [TestMethod]
    [DataRow("terrain")]
    [DataRow("model")]
    [DataRow("voxel")]
    public void SameGenerationNewContentChecksPreviouslyReadNestedValues(string changedValue)
    {
        var original = RailwayFixtures.Snapshot(content: Content());
        var changed = original with { Content = Content(changedValue) };
        Assert.AreNotSame(original.Content, changed.Content);
        Assert.AreEqual(original.Content.Generation, changed.Content.Generation);

        Assert.IsFalse(Capture(original).Matches(changed, new(changed, RailwayFixtures.Edge), 0));
    }

    private static RailwayDependencies Capture(SceneWorldSnapshot snapshot, bool startTerrain = false) =>
        RailwayDependencies.Capture(snapshot, new(snapshot, RailwayFixtures.Edge), 0,
            [RailwayFixtures.Terrain], [], [SceneFixtures.Design], [SceneFixtures.Voxel], startTerrain, false);

    private static IContentView Content(string? changedValue = null)
    {
        var cell = SceneFixtures.Cell(new(0, 0, 0));
        var model = SceneFixtures.Evaluation(SceneFixtures.Projection([
            changedValue == "model" ? cell with { LocalAt = new(1, 0, 0) } : cell]));
        return RailwayFixtures.Content(
            terrain: RailwayFixtures.TerrainAppearance with
            { LocalHeightNoise = [new(8, changedValue == "terrain" ? 2 : 1)] },
            design: model,
            voxels: new Dictionary<Train.Core.Contracts.VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(
                    SceneFixtures.TexturedCube(changedValue == "voxel" ? 2 : 1))
            });
    }
}
