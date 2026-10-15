using System.Collections.Immutable;
using System.Numerics;
using System.Diagnostics;
using System.Text.Json;
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
    public TestContext TestContext { get; set; } = null!;

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
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), _ => new(++next), vehicles,
            (input, node, _, token) =>
            {
                var layout = NodeLayout.Build(input, node, token);
                return new(layout, Enumerable.Range(0, layout.Platforms).ToImmutableDictionary(i => "platform:" + i,
                    i => NodeScene.PlatformPart(layout, i, NodeScene.Concrete(input.Content))), []);
            });
        await scene.BuildWork!;
        Assert.IsTrue(scene.PendingWork.Contains(scene.BuildWork!), "A completed build remains pending until Present attaches it.");
        var frame = scene.Present(snapshot, new(1, null, SceneDebugLayer.None), new(new(Guid.NewGuid()), new(1), new(1), 1), null, default);
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.IsTrue(frame.Update.Environment.FoggedRegions.IsEmpty);
        Assert.IsTrue(frame.Diagnostics.All(problem => problem.Code == "content_missing"));
        Assert.AreEqual(1, frame.Diagnostics.Length);
        Assert.IsEmpty(scene.PendingWork);
    }

    [TestMethod]
    public void StaticRebindUsesValuesAndRebuildsOnlyTerrainWhenItsVoxelAppearanceChanges()
    {
        var snapshot = Isolated(NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        var before = NodeScene.Build(snapshot, NodeLayout.Build(snapshot, new(1)));
        var equivalent = Isolated(NodeFixtures.Content(generation: 2, rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 },
            terrain: RailwayFixtures.TerrainAppearance with { Display = new("new label", "new description"), LocalHeightNoise = [], Decorations = [] }));
        var same = NodeScene.Build(equivalent, NodeLayout.Build(equivalent, new(1)), before);
        foreach (var part in before.Parts) Assert.AreSame(part.Value, same.Parts[part.Key]);
        var appearance = SceneFixtures.Cube with { Presentation = ((VoxelPresentation.Cube)SceneFixtures.Cube.Presentation) with { Side = new("changed") } };
        var changedSnapshot = Isolated(NodeFixtures.Content(generation: 3,
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }, voxel: appearance));
        var changed = NodeScene.Build(changedSnapshot, NodeLayout.Build(changedSnapshot, new(1)), same);
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
        var built = NodeScene.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
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
        var entry = NodeLayout.Build(snapshot, NodeFixtures.NodeId).EntryFor(new(1));
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
        var built = NodeScene.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var same = NodeScene.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId), built);
        Assert.AreSame(built.Boundaries[new(1)], same.Boundaries[new(1)], "Unchanged inputs reuse strip samples.");
        foreach (var part in built.Parts) Assert.AreSame(part.Value, same.Parts[part.Key]);
        var relief = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[0] with
        { Terrain = snapshot.Core.World.Nodes[0].Terrain with { Relief = 2 } });
        var changed = NodeScene.Build(relief, NodeLayout.Build(relief, NodeFixtures.NodeId), same);
        Assert.AreNotSame(same.Boundaries[new(1)], changed.Boundaries[new(1)], "Relief is read from the rebound snapshot.");
        Assert.AreNotSame(same.Parts["boundary:1"], changed.Parts["boundary:1"]);
        Assert.AreSame(same.Parts["entry:1"], changed.Parts["entry:1"]);
        Assert.AreSame(same.Parts["station-tracks"], changed.Parts["station-tracks"]);
        Assert.AreSame(same.Parts["platform:0"], changed.Parts["platform:0"]);
    }

    [TestMethod]
    [TestCategory("integration")]
    public async Task ProductionNodePreservesTrackBedsPlatformsAndObjectOrderAcrossProcesses()
    {
        var root = Path.GetTempPath();
        var directory = Path.Combine(root, "node-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var hostOutput = Path.Combine(directory, "host.json");
        await ObserveProductionNode(hostOutput);
        using var first = JsonDocument.Parse(await File.ReadAllTextAsync(hostOutput));
        using var second = await RunNodeProcess(TestContext, directory, "child");
        Assert.AreEqual(first.RootElement.GetProperty("Observation").GetRawText(), second.RootElement.GetProperty("Observation").GetRawText(),
            "Independent processes must preserve terrain observations and object IDs, parent IDs and frame order.");
        Directory.Delete(directory, recursive: true);
    }

    private static async Task<JsonDocument> RunNodeProcess(TestContext context, string directory, string name)
    {
        var output = Path.Combine(directory, name + ".json");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (var argument in new[] { typeof(NodeSceneTests).Assembly.Location, "--node-process", output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new AssertFailedException("无法启动节点子进程。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(context.CancellationToken); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".stderr.log"), await stderr);
        }
        Assert.AreEqual(0, process.ExitCode, $"节点子进程失败；证据：{directory}");
        var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.AreEqual(process.Id, result.RootElement.GetProperty("ProcessId").GetInt32());
        return result;
    }

    internal static async Task ObserveProductionNode(string output)
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 1, tracks: 2,
            trains: [NodeFixtures.Train(1, enteredFrom: new(1)), NodeFixtures.Train(2, enteredFrom: new(2))],
            content: NodeFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: 104),
                rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[2] with { Position = new(700, 0) });
        var built = NodeScene.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var heights = built.Parts.Values.SelectMany(part => part.Heights.Select(item => new KeyValuePair<GridColumn, double>(
            new((int)Math.Round(item.Key.X + part.Origin.X), (int)Math.Round(item.Key.Z + part.Origin.Z)), item.Value + part.Origin.Y))).ToDictionary();
        var seamColumns = new List<int>();
        foreach (var entry in built.Layout.Entries.OrderBy(entry => entry.Edge.Value))
        {
            var path = new RailwayPath(snapshot, entry.Edge);
            var isStart = path.Start.Id == NodeFixtures.NodeId;
            var line = RailwayTerrain.Build(snapshot, path, isStart ? 0 : path.SegmentCount - 1, default);
            var endpoint = path.Sample(isStart ? 0 : path.LengthCells).Position;
            var shift = new ScenePoint(Math.Round(entry.Point.X - endpoint.X), -endpoint.Y, Math.Round(entry.Point.Z - endpoint.Z));
            var right = Vector3.Cross(Vector3.UnitY, -entry.Outward);
            var compared = 0;
            foreach (var item in line.Heights)
            {
                var x = item.Key.X + line.Origin.X + shift.X;
                var z = item.Key.Z + line.Origin.Z + shift.Z;
                var relative = new Vector3((float)(x + .5 - entry.Point.X), 0, (float)(z + .5 - entry.Point.Z));
                var inward = Vector3.Dot(relative, -entry.Outward);
                var lateral = Vector3.Dot(relative, right);
                var distance = Enumerable.Range(1, 2).Min(number => Math.Abs(lateral - RailwayCoordinates.TrackOffset(number, 2)));
                if (inward < -1 || inward > 0 || distance > 12) continue;
                Assert.AreEqual(item.Value + shift.Y, heights[new((int)x, (int)z)], 1e-8,
                    $"Edge {entry.Edge.Value} seam column ({x}, {z}) must retain its own track bed.");
                compared++;
            }
            Assert.IsTrue(compared > 30, $"Edge {entry.Edge.Value} must compare both track corridors.");
            seamColumns.Add(compared);
        }
        var station = built.Layout.StationSample(0.5, 0.5).Position;
        Assert.AreEqual(-1.0, heights[new((int)Math.Floor(station.X), (int)Math.Floor(station.Z))],
            "The station track bed must be carved independently of the raw node ground.");
        var platform = built.Layout.StationSample(20.5, 0.5).Position;
        Assert.AreEqual(-1.0, heights[new((int)Math.Floor(platform.X), (int)Math.Floor(platform.Z))],
            "Terrain under the platform must be flattened to its supporting level.");
        var ground = built.Layout.StationSample(30.5, 0.5).Position;
        Assert.AreNotEqual(-1.0, heights[new((int)Math.Floor(ground.X), (int)Math.Floor(ground.Z))]);

        var keys = new Dictionary<SceneObjectId, string>();
        var ids = new Dictionary<string, SceneObjectId>();
        ulong next = 0;
        SceneObjectId ObjectId(string key)
        {
            if (ids.TryGetValue(key, out var existing)) return existing;
            var id = new SceneObjectId(++next);
            ids.Add(key, id); keys.Add(id, key);
            return id;
        }
        await using var vehicles = new SceneVehicles();
        foreach (var train in snapshot.Core.World.Trains)
            vehicles.Reconcile(snapshot.Content, ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(
                new InstanceId.Vehicle(new(train.Id.Value)))).Value, ObjectId);
        await Task.WhenAll(vehicles.PendingWork);
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), ObjectId, vehicles, (_, _, _, _) => built);
        await scene.BuildWork!;
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var frame = scene.Present(snapshot, new(1, null, SceneDebugLayer.None), stamp, null, default);
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        Assert.IsTrue(frame.Objects.Length > 0);
        var queueHeights = snapshot.Core.World.Trains.Select(train =>
        {
            var pose = ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(train.Id.Value))))).Value;
            Assert.AreEqual(NodeFixtures.Location, ((Lookup<WorldLocation>.Found)frame.Locate(pose.Position)).Value,
                "The first 104-cell queue member must sit on node terrain.");
            return ((Lookup<double>.Found)frame.TerrainHeight(pose.Position.X, pose.Position.Z)).Value;
        }).ToArray();
        var observation = new
        {
            PartKeys = built.Parts.Keys.Order(StringComparer.Ordinal).ToArray(),
            SeamColumns = seamColumns,
            QueueHeights = queueHeights,
            StationHeight = heights[new((int)Math.Floor(station.X), (int)Math.Floor(station.Z))],
            PlatformHeight = heights[new((int)Math.Floor(platform.X), (int)Math.Floor(platform.Z))],
            GroundHeight = heights[new((int)Math.Floor(ground.X), (int)Math.Floor(ground.Z))],
            Objects = frame.Objects.Select(item => new { Key = keys[item.Id], Id = item.Id.Value, Parent = item.Parent?.Value }).ToArray()
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, Observation = observation }));
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
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [map.Id] = new Lookup<NodeView>.Found(new(map, [], 80, 0, 80)) }
        } };
    }

    internal static WorldSceneSession Open(SceneWorldSnapshot snapshot, IScenesFactory? factory = null) =>
        (WorldSceneSession)((SceneResult<IWorldSceneSession>.Success)(factory ?? ScenesFactory.Create()).OpenWorld(new(Guid.NewGuid()), snapshot)).Value;
}
