using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeSceneTests
{
    [TestMethod]
    public void GeneratedPlatformsHaveCapacityClearanceAndOnlyTrackSideRowsAreYellow()
    {
        var snapshot = NodeFixtures.Snapshot();
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        for (var station = 0; station < 2; station++)
        {
            var part = NodeScene.PlatformPart(layout, station, NodeFixtures.ConcreteCube);
            var grid = part.Geometry.Geometry.Picking.Single().Grid;
            var cells = grid.Cells;
            Assert.IsTrue(cells.Max(cell => cell.At.Z) - cells.Min(cell => cell.At.Z) + 1 >= 104 + 24);
            Assert.AreEqual(8, cells.Max(cell => cell.At.X) - cells.Min(cell => cell.At.X) + 1);
            Assert.AreEqual(1, cells.Max(cell => cell.At.Y) + 1);
            var center = station == 0 ? -20 : 20;
            foreach (var cell in cells)
            {
                Assert.IsTrue(cell.At.X >= center + 16 && cell.At.X + 1 <= center + 24);
                var yellow = cell.At.X == center + 16 || station == 0 && cell.At.X == center + 23;
                Assert.AreEqual(yellow ? "#d9a62b" : null, cell.Paint?.Value);
                Assert.AreEqual(MaterialCategory.Concrete, ((VoxelPresentation.Cube)cell.Appearance!.Presentation).Material);
            }
        }
    }

    [TestMethod]
    public void MaterialSelectionIsStableByIdAndMissingMaterialIsAnExplicitDiagnostic()
    {
        var ids = new[] { new VoxelDefinitionId("z"), new VoxelDefinitionId("b"), new VoxelDefinitionId("a") };
        var content = new FixedContentView
        {
            Voxels = ids.Select(id => new VoxelCatalogEntry(id, null)).ToImmutableArray(),
            VoxelAppearances = ids.ToDictionary(id => id, id => (ContentLookup<VoxelAppearanceProjection>)new ContentLookup<VoxelAppearanceProjection>.Found(
                NodeFixtures.ConcreteCube with { Definition = id, Presentation = id.Value == "a" ?
                    ((VoxelPresentation.Cube)NodeFixtures.ConcreteCube.Presentation) with { Shapes = [new("slope")] } : NodeFixtures.ConcreteCube.Presentation }))
        };
        Assert.AreEqual(ids[1], NodeScene.Concrete(content)!.Definition);
        Assert.IsNull(NodeScene.Concrete(NodeFixtures.Content(missing: true)));
        var layout = NodeLayout.Build(NodeFixtures.Snapshot(), NodeFixtures.NodeId);
        var part = NodeScene.PlatformPart(layout, 0, null);
        Assert.IsTrue(part.Geometry.Geometry.Objects.IsEmpty);
        Assert.AreEqual("content_missing", part.Geometry.Geometry.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task MissingConcreteDiagnosticDoesNotFogAnOtherwiseCompletedNode()
    {
        var snapshot = NodeFixtures.Snapshot(trains: [], content: NodeFixtures.Content(missing: true,
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        ulong next = 0;
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), _ => new(++next),
            (input, node, _, token, ready) =>
            {
                var layout = NodeLayout.Build(input, node, token); ready?.Invoke(layout);
                return new(layout, Enumerable.Range(0, layout.Platforms).ToImmutableDictionary(i => "platform:" + i,
                    i => NodeScene.PlatformPart(layout, i, NodeScene.Concrete(input.Content))));
            });
        await scene.BuildWork;
        var frame = scene.Present(snapshot, new(1, null, SceneDebugLayer.None), new(new(Guid.NewGuid()), new(1), new(1), 1), null, default);
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.IsTrue(frame.Update.Environment.FoggedRegions.IsEmpty);
        Assert.IsTrue(frame.Diagnostics.All(problem => problem.Code == "content_missing"));
        Assert.AreEqual(2, frame.Diagnostics.Length);
    }

    [TestMethod]
    public void StaticRebindUsesValuesAndRebuildsOnlyTerrainWhenItsVoxelAppearanceChanges()
    {
        var snapshot = Isolated(NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        var before = NodeScene.Build(snapshot, new(1));
        var equivalent = Isolated(NodeFixtures.Content(generation: 2, rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 },
            terrain: RailwayFixtures.TerrainAppearance with { Display = new("new label", "new description"), LocalHeightNoise = [], Decorations = [] }));
        var same = NodeScene.Build(equivalent, new(1), before);
        foreach (var part in before.Parts) Assert.AreSame(part.Value, same.Parts[part.Key]);
        var appearance = SceneFixtures.Cube with { Presentation = ((VoxelPresentation.Cube)SceneFixtures.Cube.Presentation) with { Side = new("changed") } };
        var changed = NodeScene.Build(Isolated(NodeFixtures.Content(generation: 3,
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }, voxel: appearance)), new(1), same);
        foreach (var part in before.Parts)
            if (part.Key.StartsWith("terrain:", StringComparison.Ordinal)) Assert.AreNotSame(part.Value, changed.Parts[part.Key]);
            else Assert.AreSame(part.Value, changed.Parts[part.Key]);
    }

    [TestMethod]
    public async Task ProductionFactoryBuildsAnIsolatedEmptyNodeWithTerrainAndWorldMapping()
    {
        var snapshot = Isolated(NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        await using var session = Open(snapshot);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var frame = await Settled(session);
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.IsNotNull(frame.NodePanBounds);
        Assert.AreEqual(NodeFixtures.Location, ((Lookup<WorldLocation>.Found)frame.Locate(Vector3.Zero)).Value);
        Assert.IsInstanceOfType<Lookup<double>.Found>(frame.TerrainHeight(0.5, 0.5));
        var hit = (ScenePick.Hit)frame.Pick(new(new(20.5f, 10, 0.5f), -Vector3.UnitY, 30));
        Assert.AreEqual(NodeFixtures.Location, ((PickSubject.Terrain)hit.Value.Subject).Location);
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(hit.Value));
    }

    private static SceneWorldSnapshot Isolated(IContentView content)
    {
        var snapshot = RailwayFixtures.Snapshot(4, 4, 0, content: content);
        var map = snapshot.Core.World.Nodes[0];
        return snapshot with { Core = new FixedCoreReadView
        {
            Position = snapshot.Core.Position, InputIdentity = snapshot.Core.InputIdentity,
            World = snapshot.Core.World with { Nodes = [map], Edges = [] },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [map.Id] = new Lookup<NodeView>.Found(new(map, [])) }
        } };
    }

    internal static WorldSceneSession Open(SceneWorldSnapshot snapshot, IScenesFactory? factory = null) =>
        (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)(factory ?? ScenesFactory.Create()).OpenWorld(new(Guid.NewGuid()), snapshot)).Value;
    internal static async Task<SceneFrame> Settled(WorldSceneSession session, ISceneFrame? displayed = null)
    {
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        SceneFrame Present() => (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(request, displayed)).Value;
        var frame = Present();
        while (session.PendingNodeBuilds.Length > 0 || session.PendingRailwayBuilds.Length > 0)
        {
            await Task.WhenAll(session.PendingNodeBuilds.Concat(session.PendingRailwayBuilds));
            frame = Present();
        }
        return frame;
    }
}
