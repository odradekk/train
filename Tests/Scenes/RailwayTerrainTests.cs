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
    public void EqualTerrainLayersPreserveColumnGeometryAndDifferentEdgeDistances()
    {
        var terrain = RailwayFixtures.TerrainAppearance with
        { SurfaceVoxel = SceneFixtures.Voxel, SubsurfaceVoxel = SceneFixtures.Voxel, RockVoxel = SceneFixtures.Voxel };
        var cells = ImmutableArray.CreateBuilder<SceneCell>();
        foreach (var x in new[] { 0, 2 })
        {
            var subject = new PickSubject.Terrain(new WorldLocation.Edge(new(1), x + 0.25));
            var layers = RailwayTerrain.PrepareLayers(terrain, _ => SceneFixtures.Cube, subject);
            RailwayTerrain.FillColumn(cells, new(x, 0), 0, layers, layers!.Rock, 0, x == 0);
        }
        var grid = new VoxelGrid(cells.ToImmutable());
        var meshes = DesignGeometry.Meshes(grid, false).OfType<SceneVisual.Geometry>().ToArray();
        var surfaceArea = meshes.Sum(visual => visual.Mesh.TriangleIndices.Chunk(3).Sum(triangle =>
        {
            var a = visual.Mesh.Vertices[triangle[0]].Position;
            var b = visual.Mesh.Vertices[triangle[1]].Position;
            var c = visual.Mesh.Vertices[triangle[2]].Position;
            return Vector3.Cross(b - a, c - a).Length() / 2;
        }));
        Assert.AreEqual(2 * (RailwayCoordinates.TerrainDepthCells * 4 + 2), surfaceArea);
        var basis = SceneFixtures.Build(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]));
        var frame = SceneFixtures.Frame(basis with { Picking = [new(basis.Objects[0].Id, SceneFixtures.Identity, grid)] });
        foreach (var x in new[] { 0, 2 })
        {
            var subject = new PickSubject.Terrain(new WorldLocation.Edge(new(1), x + 0.25));
            Assert.AreEqual(SceneFixtures.Cube, grid.Find(new(x, 0, 0))?.Appearance);
            var hit = ((ScenePick.Hit)frame.Pick(new(new(x + 0.5f, 2, 0.5f), -Vector3.UnitY, 4))).Value;
            Assert.AreEqual(new GridPosition(x, 0, 0), hit.Cell);
            Assert.AreEqual(subject, hit.Subject);
        }
    }

    [TestMethod]
    [DataRow(9.5, true, true)]
    [DataRow(10.0, true, false)]
    [DataRow(10.5, true, false)]
    [DataRow(11.5, false, false)]
    public void SharedColumnUsesTheSameBedLayersAtTrackShoulder(double distance, bool firstBallast, bool secondBallast)
    {
        var terrain = RailwayFixtures.TerrainAppearance with
        {
            SurfaceVoxel = new("test:surface"), SubsurfaceVoxel = new("test:subsurface"), RockVoxel = new("test:rock")
        };
        var cells = ImmutableArray.CreateBuilder<SceneCell>();
        var height = RailwayTerrain.BedHeight(0, distance);
        var layers = RailwayTerrain.PrepareLayers(terrain, id => SceneFixtures.Cube with { Definition = id },
            new PickSubject.Terrain(new WorldLocation.Node(new(1))));
        var ballastId = new VoxelDefinitionId("test:ballast");
        var ballast = RailwayTerrain.PrepareVoxel(ballastId, id => SceneFixtures.Cube with { Definition = id },
            new PickSubject.Terrain(new WorldLocation.Node(new(1))));
        RailwayTerrain.FillColumn(cells, new(1, 2), height, layers, ballast, distance, true);
        Assert.AreEqual(firstBallast ? ballastId : terrain.SurfaceVoxel, cells[0].Appearance!.Definition);
        Assert.AreEqual(secondBallast ? ballastId : terrain.SubsurfaceVoxel, cells[1].Appearance!.Definition);
        Assert.AreEqual((int)height, cells[0].At.Y);
        Assert.AreEqual(RailwayCoordinates.TerrainDepthCells, cells.Count);
    }

    [TestMethod]
    public void RepeatedMissingReportsKeepOneProblemAndTheSourcePackage()
    {
        var reference = new ContentReference.Core(new CoreDefinitionReference.Terrain(RailwayFixtures.Terrain));
        var package = new PackageId("test:source");
        ImmutableArray<ContentDiagnostic> source = [new("source_failure", ContentDiagnosticSeverity.Error,
            "content", reference, new(package, "terrain.json"), "terrain", null, "Missing source.")];
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        RailwayTerrain.ReportMissing(reference, source, "Missing terrain.", problems);
        RailwayTerrain.ReportMissing(reference, source, "Missing terrain again.", problems);
        Assert.AreEqual(1, problems.Count(problem => problem.Code == "content_missing"));
        Assert.AreEqual(1, problems.Count(problem => problem.Code == "source_failure"));
        Assert.IsTrue(problems.All(problem => problem.Package == package));
    }

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
        var problems = segment.Geometry.Diagnostics.Where(item => item.Code == "content_missing" && item.Content ==
            new ContentReference.Core(new CoreDefinitionReference.Terrain(RailwayFixtures.Terrain))).ToArray();
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
        Assert.AreEqual(1, missing.Length);
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
            if (Math.Abs(offset) >= RailwayCoordinates.TunnelShoulder(path.Edge.Tracks) ||
                segment.Heights[column.Key] <= rail.Position.Y + RailwayCoordinates.TunnelCrownCells) continue;
            highColumns++;
            var surface = segment.Heights[column.Key] - 1;
            Assert.IsTrue(column.All(cell =>
            {
                var y = cell.At.Y + segment.Origin.Y;
                return y == surface || y < rail.Position.Y || y >= rail.Position.Y + RailwayCoordinates.TunnelCrownCells;
            }), "A high column at a tunnel entrance or exit must not fill the bore.");
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
        var nodeHeight = Math.Floor(RailwayTerrain.NodeGround(snapshot, endNode,
            new(local.X + NodeLayout.Build(snapshot, path.End.Id).EntryPoint(path.Edge.Id).X, local.Y, local.Z + NodeLayout.Build(snapshot, path.End.Id).EntryPoint(path.Edge.Id).Z))) + 1;
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
        foreach (var pair in control.Heights.Where(pair => !wet.Contains(pair.Key) && Math.Abs(pair.Key.X + .5) < RailwayCoordinates.DecorationVoxelClearance))
            Assert.AreEqual(pair.Value, segment.Heights[pair.Key], "Decoration flattening changed the track clearance.");
    }

    [TestMethod]
    public void TrackbedUsesRuleBallastAcrossNodeAndLineMaterialTransitions()
    {
        var ballastId = new VoxelDefinitionId("test:ballast");
        var nodeType = new TerrainTypeId("test:node-terrain");
        var lineTerrain = RailwayFixtures.TerrainAppearance;
        var nodeTerrain = lineTerrain with
        {
            Id = nodeType,
            SurfaceVoxel = ModelVoxel,
            SubsurfaceVoxel = ModelVoxel,
            RockVoxel = ModelVoxel
        };
        var content = RailwayFixtures.Content(terrain: lineTerrain, rules: RailwayFixtures.Rules with { BallastVoxel = ballastId },
            terrains: new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [nodeTerrain.Id] = new ContentLookup<TerrainAppearanceProjection>.Found(nodeTerrain) },
            voxels: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            {
                [ModelVoxel] = new ContentLookup<VoxelAppearanceProjection>.Found(ModelAppearance("node-rock")),
                [ballastId] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with { Definition = ballastId })
            });
        var snapshot = Short(content);
        foreach (var node in snapshot.Core.World.Nodes)
            snapshot = RailwayFixtures.ReplaceCore(snapshot, node with { Terrain = node.Terrain with { TypeId = nodeType } });

        var segment = Build(snapshot);
        var cells = segment.Geometry.Picking.Single().Grid.Cells;
        Assert.IsTrue(cells.Any(cell => Math.Abs(cell.At.X + 0.5) > RailwayCoordinates.DecorationRootClearance && cell.Appearance?.Definition == ModelVoxel),
            "The fixture must actually exercise the node material transition.");
        var trackbed = cells.Where(cell => Math.Abs(cell.At.X + 0.5) < RailwayCoordinates.BedTopHalfWidth && cell.At.Y + segment.Origin.Y < 0 &&
            cell.At.Y + segment.Origin.Y >= segment.Heights[new(cell.At.X, cell.At.Z)] - 2).ToArray();
        Assert.IsTrue(trackbed.Length > 0);
        Assert.IsTrue(trackbed.All(cell => cell.Appearance?.Definition == ballastId),
            "Trackbed must use the rule's ballast instead of either terrain's rock.");
    }

    [TestMethod]
    public void TunnelKeepsItsRoofAboveTheTrackAndLeavesTheInteriorClear()
    {
        // Amplitude 30.3 puts a bore column of this ridge in [rail+44, rail+45), where keeping the cell under the surface fails.
        var snapshot = TunnelSnapshot(true, amplitude: 30.3);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var middleArc = path.LengthCells / 2;

        // Level endpoints surround a high, slowly varying ridge on this short straight route.
        var segment = Build(snapshot);
        Assert.IsTrue(segment.Tunnels.Any(interval => interval.Start < middleArc && interval.End > middleArc));
        var middle = path.Sample(middleArc).Position;
        var column = new GridColumn((int)Math.Floor(middle.X - segment.Origin.X), (int)Math.Floor(middle.Z - segment.Origin.Z));
        Assert.IsTrue(segment.Heights[column] > middle.Y + RailwayCoordinates.TunnelCrownCells, "The tunnel roof must clear the portal crown.");
        var centerColumn = segment.Geometry.Picking.OrderByDescending(grid => grid.Grid.Cells.Count()).First().Grid.Cells
            .Where(cell => cell.At.X == column.X && cell.At.Z == column.Z).ToArray();
        Assert.IsTrue(centerColumn.Length > 0, "The ridge must retain real roof voxels.");
        var surface = segment.Heights[column] - 1;
        Assert.IsTrue(centerColumn.Any(cell => cell.At.Y + segment.Origin.Y == surface), "The surface cell must survive the bore.");
        Assert.IsTrue(centerColumn.All(cell =>
        {
            var y = cell.At.Y + segment.Origin.Y;
            return y == surface || y < middle.Y || y >= middle.Y + RailwayCoordinates.TunnelCrownCells;
        }), "The bore contains terrain other than the surface cell.");
        var portals = segment.Geometry.Objects.Where(item => item.Parent is null && item.Id.Value != 0).ToArray();
        Assert.AreEqual(2, portals.Length, "The finite ridge needs an entrance and an exit.");
        Assert.IsTrue(portals.All(item => item.Visuals.OfType<SceneVisual.Geometry>().Any()));
        var expected = segment.Tunnels.SelectMany(interval => new[] { interval.Start, interval.End }).Order().ToArray();
        var ordered = portals.OrderBy(item => item.Pose.Position.Z).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            Assert.IsTrue(Vector3.Distance(ordered[i].Pose.Position,
                RailwayCoordinates.Relative(path.Sample(expected[i]).Position, segment.Origin)) < 1e-5);
        Assert.AreEqual(0, segment.Geometry.Diagnostics.Length);
        var boreColumns = 0;
        var boundaryColumns = 0;
        var terrain = segment.Geometry.Picking.OrderByDescending(grid => grid.Grid.Cells.Count()).First().Grid.Cells;
        foreach (var group in terrain.GroupBy(cell => new GridColumn(cell.At.X, cell.At.Z)))
        {
            var subject = (PickSubject.Terrain)group.First().Subject!;
            var arc = ((WorldLocation.Edge)subject.Location).DistanceMeters * RailwayCoordinates.CellsPerMeter;
            if (!segment.Tunnels.Any(interval => arc >= interval.Start && arc <= interval.End)) continue;
            var point = new ScenePoint(segment.Origin.X + group.Key.X + 0.5, 0, segment.Origin.Z + group.Key.Z + 0.5);
            var rail = path.Sample(path.Locate(point));
            var offset = (point.X - rail.Position.X) * rail.Right.X + (point.Z - rail.Position.Z) * rail.Right.Z;
            if (Math.Abs(offset) >= RailwayCoordinates.TunnelShoulder(path.Edge.Tracks)) continue;
            boreColumns++;
            var roof = segment.Heights[group.Key] - 1;
            if (roof >= rail.Position.Y + RailwayCoordinates.TunnelCrownCells &&
                roof < rail.Position.Y + RailwayCoordinates.TunnelCrownCells + 1) boundaryColumns++;
            Assert.IsTrue(group.All(cell =>
            {
                var y = cell.At.Y + segment.Origin.Y;
                return y == roof || y < rail.Position.Y || y >= rail.Position.Y + RailwayCoordinates.TunnelCrownCells;
            }), "The bore may keep only that column's surface cell.");
        }
        Assert.IsTrue(boreColumns > 0, "The ridge must cover the bore.");
        Assert.IsTrue(boundaryColumns > 0,
            "The fixture must include a bore column whose surface sits in [rail+44, rail+45).");
        foreach (var portal in portals)
        {
            var forward = Vector3.Transform(Vector3.UnitZ, portal.Pose.Rotation);
            var posePoint = new ScenePoint(segment.Origin.X + portal.Pose.Position.X,
                segment.Origin.Y + portal.Pose.Position.Y, segment.Origin.Z + portal.Pose.Position.Z);
            var poseArc = path.Locate(posePoint);
            var along = path.Sample(poseArc).Forward;
            var exit = Vector3.Dot(forward, along) < 0;
            var frameTop = posePoint.Y + RailwayCoordinates.TunnelCrownCells;
            var portalColumn = new GridColumn((int)Math.Floor(portal.Pose.Position.X), (int)Math.Floor(portal.Pose.Position.Z));
            Assert.IsTrue(segment.Heights[portalColumn] >= frameTop, "The portal frame top must not rise above the ground.");
            var deeper = segment.Geometry.Picking.Single(item => item.Object == portal.Id).Grid.Cells.Single(cell => cell.At.Z == 1);
            var moved = Vector3.Transform(new Vector3(deeper.At.X, deeper.At.Y, deeper.At.Z), portal.Pose.Rotation);
            var deeperArc = path.Locate(new(posePoint.X + moved.X, posePoint.Y + moved.Y, posePoint.Z + moved.Z));
            Assert.IsTrue(exit ? deeperArc < poseArc : deeperArc > poseArc, "The portal fill must sit inside the bore.");
        }
        Assert.AreEqual(1, portals.Count(portal => Vector3.Dot(Vector3.Transform(Vector3.UnitZ, portal.Pose.Rotation),
            path.Sample(path.Locate(new(segment.Origin.X + portal.Pose.Position.X, segment.Origin.Y + portal.Pose.Position.Y,
                segment.Origin.Z + portal.Pose.Position.Z))).Forward) < 0));
    }

    [TestMethod]
    public void LowRidgeKeepsItsCrownWithoutATunnel()
    {
        var snapshot = TunnelSnapshot(true, amplitude: 20);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var segment = Build(snapshot);
        Assert.AreEqual(0, segment.Tunnels.Length);
        Assert.IsFalse(segment.Geometry.Objects.Any(item => item.Parent is null && item.Id.Value != 0),
            "A ridge below the crown must not place a portal.");
        var crowned = 0;
        foreach (var group in segment.Geometry.Picking.Single().Grid.Cells.GroupBy(cell => new GridColumn(cell.At.X, cell.At.Z)))
        {
            var point = new ScenePoint(segment.Origin.X + group.Key.X + 0.5, 0, segment.Origin.Z + group.Key.Z + 0.5);
            var rail = path.Sample(path.Locate(point)).Position.Y;
            Assert.IsTrue(segment.Heights[group.Key] < rail + RailwayCoordinates.TunnelCrownCells + 1,
                "The fixture must stay below the crown or it is no longer a low ridge.");
            if (segment.Heights[group.Key] <= rail + 20) continue;
            crowned++;
            var surface = segment.Heights[group.Key] - 1;
            Assert.IsTrue(group.Any(cell => cell.At.Y + segment.Origin.Y == surface), "The crown cell must remain.");
        }
        Assert.IsTrue(crowned > 0, "The ridge must rise above the old 20-cell clearance.");
    }

    [TestMethod]
    public void DeepCuttingHoldsBedHeightAcrossTheTrackSpacing()
    {
        var terrain = RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [new(1_000_000, 20)] };
        var snapshot = RailwayFixtures.ReplaceCore(RailwayFixtures.Snapshot(400, 400, relief: 1, tracks: 2, content: Content(terrain)),
            worldSeed: 4UL);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var middle = path.LengthCells / 2;
        var segment = Build(snapshot, (int)(middle / RailwayCoordinates.SegmentLengthCells));
        Assert.AreEqual(0, segment.Tunnels.Length);
        var held = 0;
        var cutting = false;
        foreach (var group in segment.Geometry.Picking.Single().Grid.Cells.GroupBy(cell => new GridColumn(cell.At.X, cell.At.Z)))
        {
            var point = new ScenePoint(segment.Origin.X + group.Key.X + 0.5, 0, segment.Origin.Z + group.Key.Z + 0.5);
            var arc = path.Locate(point);
            if (Math.Abs(arc - middle) > 2) continue;
            var rail = path.Sample(arc);
            var lateral = (point.X - rail.Position.X) * rail.Right.X + (point.Z - rail.Position.Z) * rail.Right.Z;
            var distance = Enumerable.Range(1, 2).Min(number => Math.Abs(lateral - RailwayCoordinates.TrackOffset(number, 2)));
            var bed = Math.Floor(RailwayTerrain.BedHeight(rail.Position.Y, distance));
            if (distance <= RailwayCoordinates.TrackSpacing / 2)
            {
                held++;
                Assert.IsTrue(group.All(cell => cell.At.Y + segment.Origin.Y <= bed),
                    "Ground within 17 cells of a track center must not rise above the bed.");
            }
            else if (distance > RailwayCoordinates.TrackSpacing / 2 + RailwayCoordinates.BedBlendWidth &&
                segment.Heights[group.Key] - 1 > bed)
                cutting = true;
        }
        Assert.IsTrue(held > 0, "The midpoint must include the flat cutting.");
        Assert.IsTrue(cutting, "Ground beyond the slope must stay above the bed, or this is not a cutting.");
    }

    [TestMethod]
    public void HorizontalStraightBridgeMatchesOnePose()
    {
        GridPosition[] roots = [new(-1, 0, -9), new(0, 0, -8), new(1, 2, 7), new(4, 0, 8), new(0, 1, 24)];
        var nestedAt = new GridPosition(5, -1, 20);
        var childAt = new GridPosition(1, 0, 4);
        var nested = new NestedObjectProjection([0], nestedAt, VoxelOrientation.PxPy, new NestedObjectDetails.Assembly(null));
        var projection = SceneFixtures.Projection(
            roots.Select(at => SceneFixtures.Cell(at) with { Voxel = ModelVoxel })
                .Append(SceneFixtures.Cell(childAt, path: [0]) with { Voxel = ModelVoxel }).ToImmutableArray(),
            [nested]) with { Design = ModelHash };
        var model = SceneFixtures.Evaluation(projection) with { Design = ModelHash };
        var terrain = RailwayFixtures.TerrainAppearance with { RailwayModels = [new(1, ModelHash, ModelHash)] };
        var snapshot = WithFeatures(RailwayFixtures.Snapshot(400, 400, relief: 0, content: Content(terrain, model)), ["river"]);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var start = path.Sample(0);
        var finish = path.Sample(path.LengthCells);
        Assert.AreEqual(0, start.Position.Y, 1e-6);
        Assert.AreEqual(start.Position.Y, finish.Position.Y, 1e-6);
        Assert.IsTrue(Vector3.Dot(start.Forward, finish.Forward) > 0.999f, "The fixture must be a straight line.");
        var water = RiverCenter(path);
        var sample = path.Sample(water);
        var rotation = RailwayCoordinates.Rotation(sample.Forward);
        var rootPose = new ScenePose(RailwayCoordinates.Relative(sample.Position, default), rotation);
        var expected = roots.Select(at => rootPose.Position + Vector3.Transform(new Vector3(at.X, at.Y, at.Z), rotation)).ToList();
        var childPose = SceneGeometry.Compose(rootPose, SceneGeometry.Placement(nestedAt, VoxelOrientation.PxPy));
        expected.Add(childPose.Position + Vector3.Transform(new Vector3(childAt.X, childAt.Y, childAt.Z), childPose.Rotation));
        var actual = new List<Vector3>();
        var seeds = new HashSet<ulong>();
        foreach (var segment in BuildAround(snapshot, path, water, 80))
        {
            actual.AddRange(ModelCorners(segment));
            foreach (var seed in segment.Geometry.Objects.Where(item => item.Id.Value != 0)
                .SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Surface.WearSeed))
                seeds.Add(seed);
        }
        Assert.AreEqual(expected.Count, actual.Count, "Each design cell must appear once.");
        var used = new bool[expected.Count];
        foreach (var point in actual)
        {
            var match = -1;
            for (var i = 0; i < expected.Count; i++)
                if (!used[i] && Vector3.Distance(point, expected[i]) < 0.01f) { match = i; break; }
            Assert.AreNotEqual(-1, match, $"Bridge cell {point} is not the water-center pose applied to a design cell.");
            used[match] = true;
        }
        Assert.AreEqual(1, seeds.Count);
        Assert.AreEqual(DesignGeometry.PlacementWearSeed(path.Edge.Id, sample.Position), seeds.Single());
    }

    [TestMethod]
    public void MinimumRadiusBridgeDeckFollowsTheTracks()
    {
        var cells = ImmutableArray.CreateBuilder<SceneVoxelProjection>();
        int[] xs = [-44, -34, 0, 34, 44];
        for (var z = -56; z < 56; z++)
            foreach (var x in xs)
                cells.Add(SceneFixtures.Cell(new(x, -2, z)) with { Voxel = ModelVoxel });
        var halfWidth = xs.Max(x => Math.Abs(x + 0.5));
        var model = SceneFixtures.Evaluation(SceneFixtures.Projection(cells.ToImmutable()) with { Design = ModelHash }) with { Design = ModelHash };
        var terrain = RailwayFixtures.TerrainAppearance with { RailwayModels = [new(3, ModelHash, ModelHash)] };
        var snapshot = WithFeatures(RailwayFixtures.ReplaceCore(RailwayFixtures.Snapshot(400, 50, relief: 0, tracks: 3,
            content: Content(terrain, model, rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 80 })), worldSeed: 3UL), ["river"]);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var water = RiverCenter(path);
        Assert.IsTrue(Vector3.Dot(path.Sample(water - 56).Forward, path.Sample(water + 56).Forward) < 0.98f,
            "The bridge must sit on a curve.");
        var deck = new List<(ScenePoint Origin, ScenePose Pose, GridPosition At, Vector3 Center)>();
        foreach (var segment in BuildAround(snapshot, path, water, 80))
            foreach (var grid in segment.Geometry.Picking)
                foreach (var cell in grid.Grid.Cells)
                {
                    if (cell.Appearance?.Definition != ModelVoxel) continue;
                    var centerLocal = Vector3.Transform(new Vector3(cell.At.X + 0.5f, cell.At.Y + 0.5f, cell.At.Z + 0.5f), grid.Pose.Rotation);
                    var center = new Vector3((float)segment.Origin.X + grid.Pose.Position.X + centerLocal.X,
                        (float)segment.Origin.Y + grid.Pose.Position.Y + centerLocal.Y,
                        (float)segment.Origin.Z + grid.Pose.Position.Z + centerLocal.Z);
                    deck.Add((segment.Origin, grid.Pose, cell.At, center));
                }
        Assert.IsTrue(deck.Count > 0, "The bridge must place deck cells.");
        foreach (var item in deck)
        {
            var arc = path.Locate(new(item.Center.X, item.Center.Y, item.Center.Z));
            var rail = path.Sample(arc);
            var lateral = (item.Center.X - rail.Position.X) * rail.Right.X + (item.Center.Z - rail.Position.Z) * rail.Right.Z;
            Assert.IsTrue(Math.Abs(lateral) <= halfWidth + 0.75, "Deck cell sticks out past the deck.");
        }
        var arcs = new List<double>();
        var first = Math.Max(0, water - 12);
        var last = Math.Min(path.LengthCells, water + 12);
        for (var n = 0; first + n * 0.5 <= last + 1e-9; n++) arcs.Add(first + n * 0.5);
        foreach (var joint in new[] { -40d, -24, -8, 8, 24, 40 })
            for (var n = 0; n <= 4; n++) arcs.Add(water + joint - 1 + n * 0.5);
        foreach (var offset in new[] { 0d, RailwayCoordinates.TrackOffset(1, 3), RailwayCoordinates.TrackOffset(3, 3) })
            foreach (var along in arcs)
            {
                var at = path.Sample(along, offset).Position;
                var nearest = deck.Min(item => HorizontalDistance(at, item.Origin, item.Pose, item.At));
                Assert.IsTrue(nearest <= 0.75,
                    $"The deck has a gap of {nearest:0.00} cells at arc {along:0.0}, offset {offset:0.0}.");
            }
    }

    [TestMethod]
    public void BridgeSpansBelongToTheSegmentThatOwnsTheirCenter()
    {
        GridPosition[] roots = [new(0, 0, -48), new(0, 0, 0), new(0, 0, 48)];
        var projection = SceneFixtures.Projection(roots.Select(at => SceneFixtures.Cell(at) with { Voxel = ModelVoxel }).ToImmutableArray())
            with { Design = ModelHash };
        var model = SceneFixtures.Evaluation(projection) with { Design = ModelHash };
        var terrain = RailwayFixtures.TerrainAppearance with { RailwayModels = [new(1, ModelHash, ModelHash)] };
        var snapshot = WithFeatures(RailwayFixtures.Snapshot(400, 400, relief: 0, content: Content(terrain, model)), ["river"]);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var water = RiverCenter(path);
        var owners = new List<(int Segment, double Arc)>();
        foreach (var segment in BuildAround(snapshot, path, water, 80))
            foreach (var bridge in segment.Geometry.Objects.Where(item => item.Parent is null && item.Id.Value != 0))
            {
                var point = new ScenePoint(segment.Origin.X + bridge.Pose.Position.X,
                    segment.Origin.Y + bridge.Pose.Position.Y, segment.Origin.Z + bridge.Pose.Position.Z);
                var arc = path.Locate(point);
                var clamped = Math.Clamp(arc, 0, path.LengthCells);
                var owner = clamped >= path.LengthCells ? path.SegmentCount - 1
                    : (int)(clamped / RailwayCoordinates.SegmentLengthCells);
                Assert.AreEqual(segment.Index, owner, "A span was built by a segment that does not own its center.");
                owners.Add((segment.Index, arc));
            }
        Assert.IsTrue(owners.Select(item => item.Segment).Distinct().Count() >= 2,
            "The fixture must place spans on both sides of a segment boundary.");
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
    public void GradedBridgeFlattenFootprintUsesHorizontalProjection()
    {
        var model = Model(flatten: new(new(30, 0), new(31, 0), 10));
        var terrain = RailwayFixtures.TerrainAppearance with { RailwayModels = [new(1, ModelHash, ModelHash)] };
        var snapshot = WithFeatures(RailwayFixtures.Snapshot(1000, 1000, relief: 2, content: Content(terrain, model)), ["river"]);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        const ulong riverPlacementPurpose = 20;
        var bridgeArc = path.LengthCells * (0.5 + 0.2 * RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, 0, riverPlacementPurpose));
        var segment = Build(snapshot, (int)(bridgeArc / RailwayCoordinates.SegmentLengthCells));
        var bridge = segment.Geometry.Objects.Single(item => item.Parent is null && item.Id.Value != 0);
        var forward = Vector3.Transform(Vector3.UnitZ, bridge.Pose.Rotation);
        Assert.IsTrue(Math.Abs(forward.Y) > .01, "The bridge must retain the rail pitch.");
        var column = new GridColumn((int)Math.Floor(bridge.Pose.Position.X + 30.5),
            (int)Math.Floor(bridge.Pose.Position.Z + .5));
        var expected = Math.Floor(bridge.Pose.Position.Y + segment.Origin.Y + model.Scene.RootFlatten!.Height);
        Assert.AreEqual(expected, segment.Heights[column],
            "A footprint interior must flatten to the declared height independently of bridge pitch and segment origin Y.");
    }

    [TestMethod]
    public void SegmentBoundaryRegistersEndpointLookbackEvenWithoutATunnel()
    {
        var length = (2 * RailwayCoordinates.NodeTransitionCells + 2) / RailwayCoordinates.CellsPerMeter;
        var snapshot = RailwayFixtures.Snapshot(length, length, relief: 0);
        var segment = Build(snapshot, 1);
        Assert.IsTrue(segment.Tunnels.IsEmpty);
        var start = snapshot.Core.World.Nodes[0];
        var changed = RailwayFixtures.ReplaceCore(snapshot, start with { Terrain = start.Terrain with { Relief = 2 } });
        Assert.IsFalse(segment.Dependencies.Matches(changed, new(changed, RailwayFixtures.Edge), 1),
            "The boundary segment must register its endpoint lookback before tunnel sampling.");
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
            Assert.IsTrue(Math.Abs(decoration.Pose.Position.X + segment.Origin.X) >= RailwayCoordinates.DecorationRootClearance, "Decoration entered the track clearance.");
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
        var grid = segment.Geometry.Picking.Single().Grid;
        var cells = grid.Cells;
        var markers = segment.Geometry.Objects.Where(item => item.Parent is not null).ToArray();
        Assert.AreEqual(grid.Cells.Count(), markers.Length);
        var positions = markers.Select(item => item.Pose.Position).ToHashSet();
        Assert.AreEqual(grid.Cells.Count(), positions.Count);
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
    public void BridgeDesignChangesInvalidateSegmentsThatResolvedIt()
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
        var layoutPath = new RailwayPath(changedLayout, RailwayFixtures.Edge);
        Assert.IsFalse(bridgeSegment.Dependencies.Matches(changedLayout, layoutPath, 1));
        Assert.IsFalse(distantSegment.Dependencies.Matches(changedLayout, layoutPath, 2),
            "Resolving the bridge design records its hash even when this segment places no span.");
        var appearancePath = new RailwayPath(changedAppearance, RailwayFixtures.Edge);
        Assert.IsFalse(bridgeSegment.Dependencies.Matches(changedAppearance, appearancePath, 1));
        Assert.IsTrue(distantSegment.Dependencies.Matches(changedAppearance, appearancePath, 2),
            "A segment that never placed the model does not read its voxel appearance.");
    }

    [TestMethod]
    public void LongerBridgeDesignInvalidatesTheSegmentThatOwnedNoSpan()
    {
        var terrain = RailwayFixtures.TerrainAppearance with { RailwayModels = [new(1, ModelHash, ModelHash)] };
        var snapshot = WithFeatures(RailwayFixtures.Snapshot(400, 400, relief: 0, content: Content(terrain, Model())), ["river"]);
        var path = new RailwayPath(snapshot, RailwayFixtures.Edge);
        var water = RiverCenter(path);
        var owner = (int)(Math.Clamp(water, 0, path.LengthCells - 1e-9) / RailwayCoordinates.SegmentLengthCells);
        var neighbor = owner + 1 < path.SegmentCount ? owner + 1 : owner - 1;
        int? extend = null;
        for (var k = -40; k <= 40; k++)
        {
            if (k == 0) continue;
            var center = water + k * 16;
            var clamped = Math.Clamp(center, 0, path.LengthCells);
            var segment = clamped >= path.LengthCells ? path.SegmentCount - 1 : (int)(clamped / RailwayCoordinates.SegmentLengthCells);
            if (segment != neighbor) continue;
            extend = k;
            break;
        }
        Assert.IsNotNull(extend, "The line must have room for a span in the neighboring segment.");
        var neighborSegment = Build(snapshot, neighbor);
        Assert.IsFalse(neighborSegment.Geometry.Objects.Any(item => item.Parent is null && item.Id.Value != 0),
            "The short bridge must place no span in the neighboring segment.");
        var longer = SceneFixtures.Evaluation(SceneFixtures.Projection([
            SceneFixtures.Cell(new(0, 0, 0)) with { Voxel = ModelVoxel },
            SceneFixtures.Cell(new(0, 0, extend.Value * 16)) with { Voxel = ModelVoxel }
        ]) with { Design = ModelHash }) with { Design = ModelHash };
        var changed = snapshot with { Content = Content(terrain, longer) };
        Assert.IsFalse(neighborSegment.Dependencies.Matches(changed, new(changed, RailwayFixtures.Edge), neighbor),
            "Growing the bridge into a span this segment owns must invalidate its cached dependencies.");
    }

    [TestMethod]
    public void CacheReuseEqualsRebuildingAndLatePublishCannotReplaceTheCurrentTable()
    {
        var snapshot = Short(Content(RailwayFixtures.TerrainAppearance));
        var segment = Build(snapshot);
        var cache = new RailwayCache();
        var oldOwner = cache.OpenSession(RailwayFixtures.Edge);
        cache.Publish(RailwayFixtures.Edge, oldOwner, [segment]);
        var currentOwner = cache.OpenSession(RailwayFixtures.Edge);
        var equivalent = Short(Content(RailwayFixtures.TerrainAppearance with { LocalHeightNoise = [], Decorations = [] }));
        var path = new RailwayPath(equivalent, RailwayFixtures.Edge);
        var reused = cache.Find(equivalent, path, 0);
        Assert.AreSame(segment, reused);
        AssertGeometry(reused!, Build(equivalent));
        cache.Publish(RailwayFixtures.Edge, currentOwner, []);
        cache.Publish(RailwayFixtures.Edge, oldOwner, [segment]);
        Assert.IsNull(cache.Find(equivalent, path, 0));
        cache.Publish(RailwayFixtures.Edge, currentOwner, [segment]);
        Assert.AreSame(segment, cache.Find(equivalent, path, 0));
        cache.Publish(RailwayFixtures.Edge, currentOwner, []);
        Assert.IsNull(cache.Find(equivalent, path, 0));
    }

    private static SceneWorldSnapshot TunnelSnapshot(bool includeModel, bool diagonal = false, double amplitude = 100)
    {
        var terrain = RailwayFixtures.TerrainAppearance with
        {
            LocalHeightNoise = [new(1_000_000, amplitude)],
            RailwayModels = includeModel ? [new(1, ModelHash, ModelHash)] : []
        };
        var basis = RailwayFixtures.Snapshot(4, 4, relief: 1, content: Content(terrain, PortalModel()));
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

    private static double RiverCenter(RailwayPath path) =>
        path.LengthCells * (0.5 + 0.2 * RailwayRandom.Unit(path.Seed, path.Edge.Id.Value, 0, 20));

    private static IEnumerable<RailwaySegment> BuildAround(SceneWorldSnapshot snapshot, RailwayPath path, double arc, double margin)
    {
        var first = Math.Max(0, (int)((arc - margin) / RailwayCoordinates.SegmentLengthCells));
        var last = Math.Min(path.SegmentCount - 1, (int)((arc + margin) / RailwayCoordinates.SegmentLengthCells));
        for (var index = first; index <= last; index++) yield return Build(snapshot, index);
    }

    private static IEnumerable<Vector3> ModelCorners(RailwaySegment segment)
    {
        foreach (var grid in segment.Geometry.Picking)
            foreach (var cell in grid.Grid.Cells)
            {
                if (cell.Appearance?.Definition != ModelVoxel) continue;
                var local = Vector3.Transform(new Vector3(cell.At.X, cell.At.Y, cell.At.Z), grid.Pose.Rotation);
                yield return new((float)segment.Origin.X + grid.Pose.Position.X + local.X,
                    (float)segment.Origin.Y + grid.Pose.Position.Y + local.Y,
                    (float)segment.Origin.Z + grid.Pose.Position.Z + local.Z);
            }
    }

    private static float HorizontalDistance(ScenePoint point, ScenePoint origin, ScenePose pose, GridPosition at)
    {
        var relative = new Vector3((float)(point.X - origin.X) - pose.Position.X, 0,
            (float)(point.Z - origin.Z) - pose.Position.Z);
        var local = Vector3.Transform(relative, Quaternion.Conjugate(pose.Rotation));
        return MathF.Sqrt(AxisDistance(local.X, at.X) * AxisDistance(local.X, at.X) + AxisDistance(local.Z, at.Z) * AxisDistance(local.Z, at.Z));
        static float AxisDistance(float value, int start) => value < start ? start - value : value > start + 1 ? value - (start + 1) : 0;
    }

    private static DesignEvaluation.Complete PortalModel() =>
        SceneFixtures.Evaluation(SceneFixtures.Projection([
            SceneFixtures.Cell(new(0, 0, 0)) with { Voxel = ModelVoxel },
            SceneFixtures.Cell(new(0, 0, 1)) with { Voxel = ModelVoxel }
        ]) with { Design = ModelHash }) with { Design = ModelHash };

    private static FixedContentView Content(TerrainAppearanceProjection terrain, DesignEvaluation.Complete? model = null,
        VoxelAppearanceProjection? modelAppearance = null, bool missingGround = false,
        RailwayAppearanceProjection? rules = null) => RailwayFixtures.Content(
            terrain: terrain, rules: rules, design: model, voxels: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
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
