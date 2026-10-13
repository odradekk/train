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
            (input, node, _, token) =>
            {
                var layout = NodeLayout.Build(input, node, token);
                return new(layout, Enumerable.Range(0, layout.Platforms).ToImmutableDictionary(i => "platform:" + i,
                    i => NodeScene.PlatformPart(layout, i, NodeScene.Concrete(input.Content))));
            });
        await scene.BuildWork!;
        var frame = scene.Present(snapshot, new(1, null, SceneDebugLayer.None), new(new(Guid.NewGuid()), new(1), new(1), 1), null, default);
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.IsTrue(frame.Update.Environment.FoggedRegions.IsEmpty);
        Assert.IsTrue(frame.Diagnostics.All(problem => problem.Code == "content_missing"));
        Assert.AreEqual(1, frame.Diagnostics.Length);
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
        var frame = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.IsNotNull(frame.NodePanBounds);
        Assert.AreEqual(NodeFixtures.Location, ((Lookup<WorldLocation>.Found)frame.Locate(Vector3.Zero)).Value);
        Assert.IsInstanceOfType<Lookup<double>.Found>(frame.TerrainHeight(0.5, 0.5));
        var hit = (ScenePick.Hit)frame.Pick(new(new(20.5f, 10, 0.5f), -Vector3.UnitY, 30));
        Assert.AreEqual(NodeFixtures.Location, ((PickSubject.Terrain)hit.Value.Subject).Location);
        Assert.IsInstanceOfType<SceneResult<PickSubject>.Success>(session.Resolve(hit.Value));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingTerrainIsReportedOnceWithItsKnownSourceAndOneMarker(bool notFound)
    {
        var reference = new ContentReference.Core(new CoreDefinitionReference.Terrain(RailwayFixtures.Terrain));
        var source = new ContentDiagnostic("terrain_source_missing", ContentDiagnosticSeverity.Error, "lookup", reference,
            new(new("terrain-package"), "terrain.json"), "terrain", null, "Unavailable terrain.");
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(RailwayFixtures.Rules) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = notFound ? new ContentLookup<TerrainAppearanceProjection>.NotFound() :
                new ContentLookup<TerrainAppearanceProjection>.Missing([source]) }
        };
        await using var session = Open(Isolated(content));
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var frame = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.IsTrue(frame.Update.Environment.FoggedRegions.IsEmpty);
        var missing = frame.Diagnostics.Single(problem => problem.Code == "content_missing");
        Assert.AreEqual(reference, missing.Content);
        Assert.AreEqual(notFound ? null : source.Source!.Package, missing.Package);
        Assert.AreEqual(notFound ? 0 : 1, frame.Diagnostics.Count(problem => problem.Code == source.Code));
        Assert.AreEqual(1, frame.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Missing>().Count());
    }

    [TestMethod]
    public void ProductionDiagonalNodeHasNoMissingOrDuplicateSeamColumns()
    {
        var snapshot = DiagonalSnapshot();
        var built = NodeScene.Build(snapshot, NodeFixtures.NodeId);
        Assert.IsTrue(built.Parts["entry:1"].Geometry.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Any());
        Assert.IsTrue(built.Parts["station-tracks"].Geometry.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Any());
        Assert.IsFalse(built.Parts["platform:0"].Geometry.Geometry.Picking.Single().Grid.Cells.IsEmpty);
        foreach (var part in built.Parts.Values)
        {
            Assert.AreEqual(Math.Round(part.Origin.X), part.Origin.X);
            Assert.AreEqual(Math.Round(part.Origin.Z), part.Origin.Z);
        }
        var columns = built.Parts.Values.SelectMany(part => part.Heights.Keys.Select(at => new GridColumn(
            (int)Math.Round(at.X + part.Origin.X), (int)Math.Round(at.Z + part.Origin.Z)))).ToArray();
        Assert.AreEqual(columns.Length, columns.Distinct().Count(), "Every generated column has exactly one terrain owner.");
        var entry = built.Layout.EntryFor(new(1));
        var right = Vector3.Cross(Vector3.UnitY, entry.Outward);
        var columnSet = columns.ToHashSet();
        for (var inward = 0; inward <= 40; inward++)
        for (var lateral = -20; lateral <= 20; lateral++)
        {
            var point = RailwayCoordinates.Add(entry.Point, -entry.Outward * inward + right * lateral);
            Assert.IsTrue(columnSet.Contains(new((int)Math.Floor(point.X), (int)Math.Floor(point.Z))),
                $"Missing diagonal seam column at {point}.");
        }
        var origin = new ScenePoint(64, -256, 128);
        var bounds = built.Layout.PanBounds(origin);
        foreach (var at in columns)
            Assert.IsTrue(at.X - origin.X >= bounds.Minimum.X && at.X + 1 - origin.X <= bounds.Maximum.X &&
                at.Z - origin.Z >= bounds.Minimum.Y && at.Z + 1 - origin.Z <= bounds.Maximum.Y);
    }

    [TestMethod]
    public void DiagonalBoundaryHeightsMatchTheSameLineGridColumns()
    {
        var snapshot = DiagonalSnapshot();
        var entry = NodeLayout.BuildEntries(snapshot, NodeFixtures.NodeId).EntryFor(new(1));
        var path = new RailwayPath(snapshot, new(1));
        var line = RailwayTerrain.Build(snapshot, path, 0, default);
        var endpoint = path.Sample(0).Position;
        var shift = new ScenePoint(Math.Round(entry.Point.X - endpoint.X), -endpoint.Y, Math.Round(entry.Point.Z - endpoint.Z));
        var strip = RailwayTerrain.NodeBoundary(snapshot, path, NodeFixtures.NodeId, entry, default);
        var compared = 0;
        foreach (var value in line.Heights)
        {
            var at = new GridColumn((int)(value.Key.X + line.Origin.X), (int)(value.Key.Z + line.Origin.Z));
            if (!strip.Heights.TryGetValue(at, out var nodeHeight)) continue;
            Assert.AreEqual(value.Value + shift.Y, nodeHeight + strip.Origin.Y, 1e-8);
            compared++;
        }
        Assert.IsTrue(compared > 100);
    }

    [TestMethod]
    public void ProductionNodeRebindPreservesRailsAndPlatformsWhileRefreshingReliefDependencies()
    {
        var snapshot = DiagonalSnapshot();
        var built = NodeScene.Build(snapshot, NodeFixtures.NodeId);
        var same = NodeScene.Build(snapshot, NodeFixtures.NodeId, built);
        foreach (var part in built.Parts) Assert.AreSame(part.Value, same.Parts[part.Key]);
        var relief = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[0] with
        { Terrain = snapshot.Core.World.Nodes[0].Terrain with { Relief = 2 } });
        var changed = NodeScene.Build(relief, NodeFixtures.NodeId, same);
        Assert.AreNotSame(same.Parts["boundary:1"], changed.Parts["boundary:1"]);
        Assert.AreSame(same.Parts["entry:1"], changed.Parts["entry:1"]);
        Assert.AreSame(same.Parts["station-tracks"], changed.Parts["station-tracks"]);
        Assert.AreSame(same.Parts["platform:0"], changed.Parts["platform:0"]);
    }

    private static SceneWorldSnapshot DiagonalSnapshot()
    {
        var content = new FixedContentView
        {
            Generation = new(1), Core = SceneFixtures.Content().Core,
            Voxels = [new(NodeFixtures.Concrete, null)],
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Missing([]) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [NodeFixtures.Concrete] = new ContentLookup<VoxelAppearanceProjection>.Found(NodeFixtures.ConcreteCube) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }) }
        };
        var snapshot = NodeFixtures.Snapshot(platforms: 1, trains: [], content: content);
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[1] with { Position = new(400.25, 400.125) });
        snapshot = snapshot with { Core = new FixedCoreReadView
        {
            Position = snapshot.Core.Position, InputIdentity = snapshot.Core.InputIdentity,
            World = snapshot.Core.World with { Edges = [snapshot.Core.World.Edges[0]] },
            Nodes = snapshot.Core.World.Nodes.ToDictionary(node => node.Id, node => snapshot.Core.FindNode(node.Id)),
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [new(1)] = snapshot.Core.FindEdge(new(1)) }
        } };
        return snapshot;
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
}
