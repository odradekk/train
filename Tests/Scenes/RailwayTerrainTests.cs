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
public sealed class RailwayTerrainTests
{
    private static readonly DesignHash ModelHash = new("terrain-model");
    private static readonly VoxelDefinitionId ModelVoxel = new("test:model-voxel");

    [TestMethod]
    public void NodeMaterialTransitionDoesNotReplaceTheLineRockInTrackbed()
    {
        var nodeType = new TerrainTypeId("test:node-terrain");
        var lineTerrain = RailwayFixtures.TerrainAppearance;
        var nodeTerrain = lineTerrain with
        {
            Id = nodeType, SurfaceVoxel = ModelVoxel, SubsurfaceVoxel = ModelVoxel, RockVoxel = ModelVoxel
        };
        var content = new FixedContentView
        {
            Core = SceneFixtures.Content().Core,
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            {
                [lineTerrain.Id] = new ContentLookup<TerrainAppearanceProjection>.Found(lineTerrain),
                [nodeTerrain.Id] = new ContentLookup<TerrainAppearanceProjection>.Found(nodeTerrain)
            },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(RailwayFixtures.Rules) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube),
                [ModelVoxel] = new ContentLookup<VoxelAppearanceProjection>.Found(ModelAppearance("node-rock"))
            }
        };
        var basis = Short(content);
        var nodes = basis.Core.World.Nodes.Select(node => node with
        { Terrain = node.Terrain with { TypeId = nodeType } }).ToImmutableArray();
        var edge = ((Lookup<EdgeView>.Found)basis.Core.FindEdge(RailwayFixtures.Edge)).Value;
        var snapshot = basis with { Core = new FixedCoreReadView
        {
            World = basis.Core.World with { Nodes = nodes },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [edge.Map.Id]))),
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [edge.Map.Id] = new Lookup<EdgeView>.Found(edge) }
        } };

        var cells = Build(snapshot).Geometry.Picking.Single().Grid.Cells;
        Assert.IsTrue(cells.Any(cell => Math.Abs(cell.At.X + 0.5) > 24 && cell.Appearance?.Definition == ModelVoxel),
            "The fixture must actually exercise the node material transition.");
        var trackbed = cells.Where(cell => Math.Abs(cell.At.X + 0.5) < 9.5 && cell.At.Y is -1 or -2).ToArray();
        Assert.IsTrue(trackbed.Length > 0);
        Assert.IsTrue(trackbed.All(cell => cell.Appearance?.Definition == SceneFixtures.Voxel),
            "Trackbed must retain the line rock even where node rock differs.");
    }

    [TestMethod]
    public void TunnelKeepsItsRoofAboveTheTrackAndLeavesTheInteriorClear()
    {
        var terrain = RailwayFixtures.TerrainAppearance with
        {
            LocalHeightNoise = [new(1_000_000, 100)],
            RailwayModels = [new(1, ModelHash, ModelHash)]
        };
        var basis = RailwayFixtures.Snapshot(4, 4, relief: 1, content: Content(terrain, Model()));
        var nodes = basis.Core.World.Nodes.Select(node => node with
        { Terrain = node.Terrain with { Relief = 0 } }).ToImmutableArray();
        var edge = ((Lookup<EdgeView>.Found)basis.Core.FindEdge(RailwayFixtures.Edge)).Value;
        var snapshot = basis with { Core = new FixedCoreReadView
        {
            World = basis.Core.World with { WorldSeed = 4, Nodes = nodes },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [edge.Map.Id]))),
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [edge.Map.Id] = new Lookup<EdgeView>.Found(edge) }
        } };

        // Level endpoints surround a high, slowly varying ridge on this short straight route.
        var segment = Build(snapshot);
        Assert.IsTrue(segment.Tunnels.Any(interval => interval.Start < 4 && interval.End > 4));
        Assert.IsTrue(segment.Heights[new(0, 3)] > 20, "The tunnel roof must survive trackbed cutting.");
        var centerColumn = segment.Geometry.Picking.OrderByDescending(grid => grid.Grid.Cells.Length).First().Grid.Cells
            .Where(cell => cell.At.X == 0 && cell.At.Z == 3).ToArray();
        Assert.IsTrue(centerColumn.Length > 0, "The ridge must retain real roof voxels.");
        Assert.IsTrue(centerColumn.All(cell => cell.At.Y < 0 || cell.At.Y >= 20), "Tunnel clearance contains terrain voxels.");
        var portals = segment.Geometry.Objects.Where(item => item.Parent is null && item.Id.Value != 0).ToArray();
        Assert.AreEqual(2, portals.Length, "The finite ridge needs an entrance and an exit.");
        Assert.IsTrue(portals.All(item => item.Visuals.OfType<SceneVisual.Geometry>().Any()));
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var expected = segment.Tunnels.SelectMany(interval => new[] { interval.Start, interval.End }).Order().ToArray();
        var ordered = portals.OrderBy(item => item.Pose.Position.Z).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            Assert.IsTrue(Vector3.Distance(ordered[i].Pose.Position,
                RailwayCoordinates.Relative(path.Sample(expected[i]).Position, segment.Origin)) < 1e-5);
        Assert.AreEqual(0, segment.Geometry.Diagnostics.Length);
    }

    [TestMethod]
    public void IndependentEquivalentContentRebuildsTheSameGeometryAndLeavesOldSegmentUnchanged()
    {
        var terrain = RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(8, 1)] };
        var snapshot = Short(Content(terrain));
        var original = Build(snapshot);
        var oldHeights = original.Heights.ToArray();
        var replacement = Short(Content(terrain with { LocalHeightNoise = [new(8, 1)], Decorations = [] }));
        Assert.IsTrue(original.Dependencies.Matches(replacement, new(replacement, RailwayFixtures.Edge), 0));
        AssertGeometry(original, Build(replacement));
        CollectionAssert.AreEquivalent(oldHeights, original.Heights.ToArray());
    }

    [TestMethod]
    public void ZeroTracksProducesPickableTerrainWithoutRailwayMeshes()
    {
        var segment = Build(Short(tracks: 0));
        Assert.IsTrue(segment.Heights.Count > 0);
        Assert.IsTrue(segment.Geometry.Picking.Single().Grid.Cells.All(cell => cell.Subject is PickSubject.Terrain));
        Assert.IsTrue(Meshes(segment).All(mesh => mesh.Material is SceneMaterial.Voxel));
        Assert.AreEqual(0, segment.Tunnels.Length);
    }

    [TestMethod]
    public void RiverProducesWaterAndTheModelForTheActualTrackCount()
    {
        var terrain = RailwayFixtures.TerrainAppearance with { RailwayModels = [new(1, ModelHash, ModelHash)] };
        var snapshot = WithFeatures(Short(Content(terrain, Model())), ["river"]);
        var segment = Build(snapshot);
        Assert.IsTrue(Meshes(segment).Any(mesh => mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.Water }));
        var bridge = segment.Geometry.Objects.Single(item => item.Parent is null && item.Id.Value != 0);
        Assert.AreEqual(0f, bridge.Pose.Position.Y + (float)segment.Origin.Y, 0.0001f);
        Assert.IsTrue(bridge.Visuals.OfType<SceneVisual.Geometry>().Any());
        Assert.AreEqual(0, segment.Geometry.Diagnostics.Length);
    }

    [TestMethod]
    public void DecorationsRespectTrackClearanceDensityAndModelFlatten()
    {
        var model = Model(flatten: new(new(0, 0), new(0, 0), 3));
        var terrain = RailwayFixtures.TerrainAppearance with
        {
            Decorations = [new(ModelHash, 1)], DecorationTargetPerSquareMeter = 0.03
        };
        var segment = Build(Short(Content(terrain, model)));
        var decorations = segment.Geometry.Objects.Where(item => item.Parent is null && item.Id.Value != 0).ToArray();
        Assert.IsTrue(decorations.Length > 0);
        foreach (var decoration in decorations)
        {
            Assert.IsTrue(Math.Abs(decoration.Pose.Position.X + segment.Origin.X) >= 24, "Decoration entered the track clearance.");
            var column = new GridColumn((int)decoration.Pose.Position.X, (int)decoration.Pose.Position.Z);
            Assert.IsTrue(segment.Heights[column] >= decoration.Pose.Position.Y + segment.Origin.Y + 3,
                "Flatten must raise the model footprint to its declared height.");
        }
        var withoutDecorations = Build(Short(Content(terrain with { DecorationTargetPerSquareMeter = 0 }, model)));
        Assert.AreEqual(1, withoutDecorations.Geometry.Objects.Length);
        Assert.AreEqual(0, withoutDecorations.Geometry.Diagnostics.Length);
    }

    [TestMethod]
    public void MissingVoxelMarkersUseTheActualCellPlacement()
    {
        var content = Content(RailwayFixtures.TerrainAppearance, missingGround: true);
        var segment = Build(Short(content, tracks: 0));
        var cells = segment.Geometry.Picking.Single().Grid.Cells;
        var markers = segment.Geometry.Objects.Where(item => item.Parent is not null).ToArray();
        Assert.AreEqual(cells.Length, markers.Length);
        var positions = markers.Select(item => item.Pose.Position).ToHashSet();
        Assert.AreEqual(cells.Length, positions.Count);
        foreach (var cell in cells)
            Assert.IsTrue(positions.Contains(new(cell.At.X, cell.At.Y, cell.At.Z)));
        Assert.IsTrue(markers.All(item => item.Visuals.Single() is SceneVisual.Missing { KnownCell: true }));
        Assert.IsTrue(segment.Geometry.Diagnostics.Any(item => item.Code == "content_missing"));
    }

    [TestMethod]
    public void NodeTerrainChangeInvalidatesOnlySegmentsThatReadThatEndpoint()
    {
        var snapshot = RailwayFixtures.Snapshot(132, 132, relief: 0);
        var start = Build(snapshot, 0);
        var end = Build(snapshot, 2);
        var first = snapshot.Core.World.Nodes[0];
        var changed = ReplaceCore(snapshot, first with { Terrain = first.Terrain with { Relief = 2 } });
        var path = new RailwayPath(changed, RailwayFixtures.Edge);
        Assert.IsFalse(start.Dependencies.Matches(changed, path, 0));
        Assert.IsTrue(end.Dependencies.Matches(changed, path, 2));
    }

    [TestMethod]
    public void ModelLayoutAndAppearanceChangesInvalidateOnlySegmentsThatActuallyUseTheModel()
    {
        var terrain = RailwayFixtures.TerrainAppearance with { RailwayModels = [new(1, ModelHash, ModelHash)] };
        var snapshot = WithFeatures(RailwayFixtures.Snapshot(132, 132, relief: 0, content: Content(terrain, Model())), ["river"]);
        var bridgeSegment = Build(snapshot, 1);
        var distantSegment = Build(snapshot, 2);
        var changedLayout = snapshot with { Content = Content(terrain, Model(at: new(1, 0, 0))) };
        var changedAppearance = snapshot with
        {
            Content = Content(terrain, Model(), modelAppearance: ModelAppearance("replacement"))
        };
        foreach (var changed in new[] { changedLayout, changedAppearance })
        {
            var path = new RailwayPath(changed, RailwayFixtures.Edge);
            Assert.IsFalse(bridgeSegment.Dependencies.Matches(changed, path, 1));
            Assert.IsTrue(distantSegment.Dependencies.Matches(changed, path, 2));
        }
    }

    [TestMethod]
    public void CacheReuseEqualsRebuildingAndLatePublishCannotReplaceTheCurrentTable()
    {
        var snapshot = Short(Content(RailwayFixtures.TerrainAppearance));
        var segment = Build(snapshot);
        var cache = new RailwayCache();
        var oldOwner = cache.OpenSession();
        cache.Publish(oldOwner, [segment]);
        var currentOwner = cache.OpenSession();
        var equivalent = Short(Content(RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [], Decorations = [] }));
        var path = new RailwayPath(equivalent, RailwayFixtures.Edge);
        var reused = cache.Find(equivalent, path, 0);
        Assert.AreSame(segment, reused);
        AssertGeometry(reused!, Build(equivalent));
        cache.Publish(currentOwner, []);
        cache.Publish(oldOwner, [segment]);
        Assert.IsNull(cache.Find(equivalent, path, 0));
        cache.Publish(currentOwner, [segment]);
        Assert.AreSame(segment, cache.Find(equivalent, path, 0));
        cache.Publish(currentOwner, []);
        Assert.IsNull(cache.Find(equivalent, path, 0));
    }

    private static SceneWorldSnapshot Short(IContentView? content = null, int tracks = 1) =>
        RailwayFixtures.Snapshot(4, 4, relief: 0, tracks: tracks, content: content);

    private static RailwaySegment Build(SceneWorldSnapshot snapshot, int index = 0) =>
        RailwayTerrain.Build(snapshot, new(snapshot, RailwayFixtures.Edge), index, CancellationToken.None);

    private static FixedContentView Content(TerrainAppearanceProjection terrain, DesignEvaluation.Complete? model = null,
        VoxelAppearanceProjection? modelAppearance = null, bool missingGround = false) => new()
    {
        Generation = new(2), Core = SceneFixtures.Content().Core,
        Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
        { [terrain.Id] = new ContentLookup<TerrainAppearanceProjection>.Found(terrain) },
        RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
        { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(RailwayFixtures.Rules with { }) },
        Designs = model is null ? new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>() :
            new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [model.Design] = new ContentLookup<DesignEvaluation>.Found(model) },
        VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        {
            [SceneFixtures.Voxel] = missingGround ? new ContentLookup<VoxelAppearanceProjection>.Missing([]) :
                new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
                { Presentation = new VoxelPresentation.Cube([new("cube")], MaterialCategory.PaintedMetal, 1,
                    new("tile"), new("tile"), new("tile"), new("tile")) }),
            [ModelVoxel] = new ContentLookup<VoxelAppearanceProjection>.Found(modelAppearance ?? ModelAppearance("model"))
        }
    };

    private static VoxelAppearanceProjection ModelAppearance(string texture) => SceneFixtures.Cube with
    {
        Definition = ModelVoxel,
        Presentation = new VoxelPresentation.Cube([], MaterialCategory.Organic, 1,
            new(texture), new(texture), new(texture), new(texture))
    };

    private static DesignEvaluation.Complete Model(GridPosition? at = null, FlattenRegion? flatten = null) =>
        SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(at ?? new(0, 0, 0)) with { Voxel = ModelVoxel }]) with
        { Design = ModelHash, RootFlatten = flatten }) with { Design = ModelHash };

    private static SceneWorldSnapshot WithFeatures(SceneWorldSnapshot snapshot, ImmutableArray<string> features)
    {
        var edge = snapshot.Core.World.Edges.Single();
        return ReplaceCore(snapshot, edge: edge with { Terrain = edge.Terrain with { FeatureIds = features } });
    }

    private static SceneWorldSnapshot ReplaceCore(SceneWorldSnapshot snapshot, NodeMapItem? first = null, EdgeMapItem? edge = null)
    {
        var nodes = snapshot.Core.World.Nodes.SetItem(0, first ?? snapshot.Core.World.Nodes[0]);
        var current = edge ?? snapshot.Core.World.Edges.Single();
        var oldEdge = ((Lookup<EdgeView>.Found)snapshot.Core.FindEdge(current.Id)).Value;
        return snapshot with { Core = new FixedCoreReadView
        {
            World = snapshot.Core.World with { Nodes = nodes, Edges = [current] },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [current.Id]))),
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [current.Id] = new Lookup<EdgeView>.Found(oldEdge with { Map = current }) }
        } };
    }

    private static IEnumerable<SceneMesh> Meshes(RailwaySegment segment) =>
        segment.Geometry.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Mesh);

    private static void AssertGeometry(RailwaySegment expected, RailwaySegment actual)
    {
        Assert.AreEqual(expected.Origin, actual.Origin);
        CollectionAssert.AreEquivalent(expected.Heights.ToArray(), actual.Heights.ToArray());
        Assert.IsTrue(expected.Tunnels.SequenceEqual(actual.Tunnels));
        Assert.IsTrue(expected.Geometry.Diagnostics.SequenceEqual(actual.Geometry.Diagnostics));
        Assert.AreEqual(expected.Geometry.Objects.Length, actual.Geometry.Objects.Length);
        for (var i = 0; i < expected.Geometry.Objects.Length; i++)
        {
            var a = expected.Geometry.Objects[i]; var b = actual.Geometry.Objects[i];
            Assert.AreEqual(a.Id, b.Id); Assert.AreEqual(a.Parent, b.Parent); Assert.AreEqual(a.Pose, b.Pose);
            Assert.AreEqual(a.Subject, b.Subject);
            Assert.IsTrue(a.Lights.SequenceEqual(b.Lights)); Assert.IsTrue(a.Effects.SequenceEqual(b.Effects));
            Assert.AreEqual(a.Visuals.Length, b.Visuals.Length);
            for (var j = 0; j < a.Visuals.Length; j++)
            {
                if (a.Visuals[j] is SceneVisual.Geometry ag && b.Visuals[j] is SceneVisual.Geometry bg)
                {
                    Assert.AreEqual(ag.Surface, bg.Surface); Assert.AreEqual(ag.Mesh.Material, bg.Mesh.Material);
                    Assert.IsTrue(ag.Mesh.Vertices.SequenceEqual(bg.Mesh.Vertices));
                    Assert.IsTrue(ag.Mesh.TriangleIndices.SequenceEqual(bg.Mesh.TriangleIndices));
                }
                else Assert.AreEqual(a.Visuals[j], b.Visuals[j]);
            }
        }
        Assert.AreEqual(expected.Geometry.Picking.Length, actual.Geometry.Picking.Length);
        for (var i = 0; i < expected.Geometry.Picking.Length; i++)
        {
            var a = expected.Geometry.Picking[i]; var b = actual.Geometry.Picking[i];
            Assert.AreEqual(a.Object, b.Object); Assert.AreEqual(a.Pose, b.Pose);
            Assert.IsTrue(a.Grid.Cells.Select(cell => (cell.At, cell.Orientation, cell.Subject, cell.Shape, cell.Paint, cell.MissingReference))
                .SequenceEqual(b.Grid.Cells.Select(cell => (cell.At, cell.Orientation, cell.Subject, cell.Shape, cell.Paint, cell.MissingReference))));
        }
    }
}
