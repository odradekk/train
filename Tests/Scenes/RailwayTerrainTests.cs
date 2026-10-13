using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayTerrainTests
{
    private static readonly DesignHash ModelHash = new("terrain-model");
    private static readonly VoxelDefinitionId ModelVoxel = new("test:model-voxel");

    [TestMethod]
    public void LongGradedLineWithoutTerrainNoiseHasNoTunnelOrHighEmbankment()
    {
        var snapshot = RailwayFixtures.Snapshot(4000, 4000, relief: 0.5);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var segment = RailwayTerrain.Build(snapshot, path, path.SegmentCount / 2, CancellationToken.None);
        Assert.AreEqual(0, segment.Tunnels.Length, "A flat terrain appearance must not create a tunnel from the rail profile alone.");
        foreach (var cell in segment.Geometry.Picking.Single().Grid.Cells.Where(cell =>
            cell.Subject is PickSubject.Terrain).GroupBy(cell => new GridColumn(cell.At.X, cell.At.Z)).Select(group => group.First()))
        {
            var location = (WorldLocation.Edge)((PickSubject.Terrain)cell.Subject!).Location;
            var rail = path.Sample(location.DistanceMeters * RailwayCoordinates.CellsPerMeter).Position;
            var groundTop = segment.Heights[new(cell.At.X, cell.At.Z)];
            Assert.IsTrue(groundTop <= rail.Y && groundTop >= rail.Y - RailwayCoordinates.TerrainBaselineOffset - 1,
                "Terrain must follow the local rail profile, with only the shallow trackbed above the ground baseline.");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingRailwayModelReportsItsTerrainReference(bool tunnel)
    {
        var segment = Build(tunnel ? TunnelSnapshot(false) : WithFeatures(Short(), ["river"]));
        var problems = segment.Geometry.Diagnostics.Where(item => item.Detail == "No railway scene model matches the track count.").ToArray();
        Assert.IsTrue(problems.Length > 0);
        Assert.IsTrue(problems.All(problem => problem.Content ==
            new ContentReference.Core(new CoreDefinitionReference.Terrain(RailwayFixtures.Terrain))));
    }

    [TestMethod]
    [DataRow("model_missing")]
    [DataRow("model_failed")]
    [DataRow("terrain")]
    [DataRow("voxel")]
    public void MissingContentPreservesDiagnosticsAndPackage(string kind)
    {
        ContentReference reference = kind switch
        {
            "terrain" => new ContentReference.Core(new CoreDefinitionReference.Terrain(RailwayFixtures.Terrain)),
            "voxel" => new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel)),
            _ => new ContentReference.Design(ModelHash)
        };
        var package = new PackageId("test:terrain-package");
        ImmutableArray<ContentDiagnostic> source = [new("source_failure", ContentDiagnosticSeverity.Error,
            "content", reference, new(package, "terrain.json"), "model", null, "Source content is unavailable.")];
        var content = kind switch
        {
            "terrain" => RailwayFixtures.Content(terrains: new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Missing(source) }),
            "voxel" => RailwayFixtures.Content(voxels: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Missing(source) }),
            _ => RailwayFixtures.Content(
                terrain: RailwayFixtures.TerrainAppearance with { RailwayModels = [new(1, ModelHash, ModelHash)] },
                designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                {
                    [ModelHash] = kind == "model_failed"
                    ? new ContentLookup<DesignEvaluation>.Found(new DesignEvaluation.Failed(source))
                    : new ContentLookup<DesignEvaluation>.Missing(source)
                })
        };
        var model = kind.StartsWith("model_");
        var snapshot = Short(content, tracks: model ? 1 : 0);
        if (model) snapshot = WithFeatures(snapshot, ["river"]);
        var problems = Build(snapshot).Geometry.Diagnostics;
        var forwarded = problems.Single(problem => problem.Code == "source_failure");
        Assert.AreEqual(reference, forwarded.Content);
        Assert.AreEqual(package, forwarded.Package);
        var missing = problems.Where(problem => problem.Code == "content_missing" && problem.Content == reference).ToArray();
        Assert.IsTrue(missing.Length > 0);
        Assert.IsTrue(missing.All(problem => problem.Package == package));
    }

    [TestMethod]
    public void TunnelTransitionColumnsKeepTheTrackClearanceEmpty()
    {
        var snapshot = TunnelSnapshot(false, diagonal: true);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var segment = Build(snapshot);
        var highColumns = 0;
        foreach (var column in segment.Geometry.Picking.Single().Grid.Cells.GroupBy(cell => new GridColumn(cell.At.X, cell.At.Z)))
        {
            var point = new ScenePoint(segment.Origin.X + column.Key.X + 0.5, 0,
                segment.Origin.Z + column.Key.Z + 0.5);
            var rail = path.Sample(path.Locate(point));
            var offset = (point.X - rail.Position.X) * rail.Right.X + (point.Z - rail.Position.Z) * rail.Right.Z;
            if (Math.Abs(offset) >= RailwayCoordinates.BedTopHalfWidth ||
                segment.Heights[column.Key] <= rail.Position.Y + RailwayCoordinates.TunnelClearance) continue;
            highColumns++;
            Assert.IsTrue(column.All(cell => cell.At.Y + segment.Origin.Y < rail.Position.Y ||
                cell.At.Y + segment.Origin.Y >= rail.Position.Y + RailwayCoordinates.TunnelClearance),
                "A high column at a tunnel entrance or exit must not fill the track clearance.");
        }
        Assert.IsTrue(highColumns > 0, "The fixture must include high terrain in the track corridor.");
    }

    [TestMethod]
    public void ExtendedChordSamplesTheEndNodeTerrainInNodeLocalCoordinates()
    {
        var nodeType = new TerrainTypeId("test:node-ground");
        var line = RailwayFixtures.TerrainAppearance;
        var nodeTerrain = line with { Id = nodeType, LocalHeightNoise = [new(4, 10)] };
        var content = RailwayFixtures.Content(terrains: new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
        { [nodeType] = new ContentLookup<TerrainAppearanceProjection>.Found(nodeTerrain) });
        var basis = RailwayFixtures.Snapshot(600, 28.224, relief: 0, content: content);
        var endNode = basis.Core.World.Nodes[1] with { Terrain = new(nodeType, 1, []) };
        var snapshot = RailwayFixtures.ReplaceCore(basis, endNode);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var endpoint = path.Sample(path.LengthCells).Position;
        Assert.IsTrue(endpoint.Z > endNode.Position.Y * 2);
        var segment = Build(snapshot, path.SegmentCount - 1);
        var point = new ScenePoint(40.5, 0, Math.Floor(endpoint.Z) + .5);
        var column = new GridColumn((int)Math.Floor(point.X - segment.Origin.X), (int)Math.Floor(point.Z - segment.Origin.Z));
        var local = new ScenePoint(point.X - endpoint.X, 0, point.Z - endpoint.Z);
        var nodeHeight = Math.Floor(RailwayTerrain.NodeGround(snapshot, endNode, local)) + 1;
        Assert.AreEqual(nodeHeight, segment.Heights[column], 1,
            "The endpoint blend must approach the same node-local sample used by the node scene.");
    }

    [TestMethod]
    public void TrackbedSupportsSleepersWithoutBuryingRailwayMeshes()
    {
        var segment = Build(Short());
        var meshes = Meshes(segment);
        var sleepers = meshes.Single(mesh => mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.Sleeper });
        var rails = meshes.Single(mesh => mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel });
        var top = segment.Heights[new(0, 3)] - segment.Origin.Y;
        Assert.IsTrue(top <= sleepers.Vertices.Min(vertex => vertex.Position.Y) + 1e-6,
            "The voxel trackbed must support the sleepers from below.");
        Assert.IsTrue(rails.Vertices.All(vertex => vertex.Position.Y > top), "The rails are buried in the trackbed.");
    }

    [TestMethod]
    public void DecorationFlattenPreservesTrackClearanceAndWaterSurface()
    {
        var model = Model(flatten: new(new(-32, 0), new(32, 0), 8));
        var terrain = RailwayFixtures.TerrainAppearance with
        { Decorations = [new(ModelHash, 1)], DecorationTargetPerSquareMeter = .03 };
        var snapshot = WithFeatures(RailwayFixtures.Snapshot(20, 20, relief: 0, content: Content(terrain, model)), ["river"]);
        var segment = Build(snapshot);
        Assert.IsTrue(segment.Geometry.Objects.Any(item => item.Parent is null && item.Id.Value != 0 &&
            item.Visuals.OfType<SceneVisual.Geometry>().Any()), "The fixture needs actual decorations.");
        var water = Meshes(segment).Single(mesh => mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.Water });
        var wet = new HashSet<GridColumn>();
        foreach (var quad in water.Vertices.Chunk(4))
        {
            var column = new GridColumn((int)quad.Min(vertex => vertex.Position.X), (int)quad.Min(vertex => vertex.Position.Z));
            wet.Add(column);
            Assert.IsTrue(segment.Heights[column] - segment.Origin.Y < quad[0].Position.Y,
                "Decoration flattening raised underwater terrain through the water surface.");
        }
        var control = Build(snapshot with { Content = Content(terrain with { DecorationTargetPerSquareMeter = 0 }, model) });
        foreach (var pair in control.Heights.Where(pair => !wet.Contains(pair.Key) && Math.Abs(pair.Key.X + .5) < 17))
            Assert.AreEqual(pair.Value, segment.Heights[pair.Key], "Decoration flattening changed the track clearance.");
    }

    [TestMethod]
    public void NodeMaterialTransitionDoesNotReplaceTheLineRockInTrackbed()
    {
        var nodeType = new TerrainTypeId("test:node-terrain");
        var lineTerrain = RailwayFixtures.TerrainAppearance;
        var nodeTerrain = lineTerrain with
        {
            Id = nodeType,
            SurfaceVoxel = ModelVoxel,
            SubsurfaceVoxel = ModelVoxel,
            RockVoxel = ModelVoxel
        };
        var content = RailwayFixtures.Content(terrain: lineTerrain,
            terrains: new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [nodeTerrain.Id] = new ContentLookup<TerrainAppearanceProjection>.Found(nodeTerrain) },
            voxels: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [ModelVoxel] = new ContentLookup<VoxelAppearanceProjection>.Found(ModelAppearance("node-rock")) });
        var snapshot = Short(content);
        foreach (var node in snapshot.Core.World.Nodes)
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node with { Terrain = node.Terrain with { TypeId = nodeType } });

        var cells = Build(snapshot).Geometry.Picking.Single().Grid.Cells;
        Assert.IsTrue(cells.Any(cell => Math.Abs(cell.At.X + 0.5) > 24 && cell.Appearance?.Definition == ModelVoxel),
            "The fixture must actually exercise the node material transition.");
        var trackbed = cells.Where(cell => Math.Abs(cell.At.X + 0.5) < 9.5 && cell.At.Y is -2 or -3).ToArray();
        Assert.IsTrue(trackbed.Length > 0);
        Assert.IsTrue(trackbed.All(cell => cell.Appearance?.Definition == SceneFixtures.Voxel),
            "Trackbed must retain the line rock even where node rock differs.");
    }

    [TestMethod]
    public void TunnelKeepsItsRoofAboveTheTrackAndLeavesTheInteriorClear()
    {
        var snapshot = TunnelSnapshot(true);

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
            Decorations = [new(ModelHash, 1)],
            DecorationTargetPerSquareMeter = 0.03
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
        var changed = RailwayFixtures.ReplaceCore(snapshot, first with { Terrain = first.Terrain with { Relief = 2 } });
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

    private static SceneWorldSnapshot TunnelSnapshot(bool includeModel, bool diagonal = false)
    {
        var terrain = RailwayFixtures.TerrainAppearance with
        {
            LocalHeightNoise = [new(1_000_000, 100)],
            RailwayModels = includeModel ? [new(1, ModelHash, ModelHash)] : []
        };
        var basis = RailwayFixtures.Snapshot(4, 4, relief: 1, content: Content(terrain, Model()));
        var snapshot = RailwayFixtures.ReplaceCore(basis, worldSeed: 4);
        foreach (var node in snapshot.Core.World.Nodes)
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node with { Terrain = node.Terrain with { Relief = 0 } });

        if (diagonal)
        {
            var end = snapshot.Core.World.Nodes[1];
            snapshot = RailwayFixtures.ReplaceCore(snapshot, end with { Position = new(2.4, 3.2) });
        }
        return snapshot;
    }

    private static SceneWorldSnapshot Short(IContentView? content = null, int tracks = 1) =>
        RailwayFixtures.Snapshot(4, 4, relief: 0, tracks: tracks, content: content);

    private static RailwaySegment Build(SceneWorldSnapshot snapshot, int index = 0) =>
        RailwayTerrain.Build(snapshot, new(snapshot, RailwayFixtures.Edge), index, CancellationToken.None);

    private static FixedContentView Content(TerrainAppearanceProjection terrain, DesignEvaluation.Complete? model = null,
        VoxelAppearanceProjection? modelAppearance = null, bool missingGround = false) => RailwayFixtures.Content(
            terrain: terrain, vehicle: model, voxels: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [SceneFixtures.Voxel] = missingGround ? new ContentLookup<VoxelAppearanceProjection>.Missing([]) :
                    new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
                    {
                        Presentation = new VoxelPresentation.Cube([new("cube")], MaterialCategory.PaintedMetal, 1,
                        new("tile"), new("tile"), new("tile"), new("tile"))
                    }),
                [ModelVoxel] = new ContentLookup<VoxelAppearanceProjection>.Found(modelAppearance ?? ModelAppearance("model"))
            });

    private static VoxelAppearanceProjection ModelAppearance(string texture) => SceneFixtures.Cube with
    {
        Definition = ModelVoxel,
        Presentation = new VoxelPresentation.Cube([], MaterialCategory.Organic, 1,
            new(texture), new(texture), new(texture), new(texture))
    };

    private static DesignEvaluation.Complete Model(GridPosition? at = null, FlattenRegion? flatten = null) =>
        SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(at ?? new(0, 0, 0)) with { Voxel = ModelVoxel }]) with
        { Design = ModelHash, RootFlatten = flatten }) with
        { Design = ModelHash };

    private static SceneWorldSnapshot WithFeatures(SceneWorldSnapshot snapshot, ImmutableArray<string> features)
    {
        var edge = snapshot.Core.World.Edges.Single();
        return RailwayFixtures.ReplaceCore(snapshot, edge: edge with { Terrain = edge.Terrain with { FeatureIds = features } });
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
