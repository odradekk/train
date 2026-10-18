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
    public void StaticRebindUsesValuesAndRebuildsOnlyTerrainWhenItsVoxelAppearanceChanges()
    {
        var snapshot = Isolated(NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        var before = NodeFixtures.Build(snapshot, NodeLayout.Build(snapshot, new(1)));
        var oldGeometry = before.Parts.OrderBy(part => part.Key, StringComparer.Ordinal)
            .First(part => part.Key.StartsWith("terrain:", StringComparison.Ordinal) &&
                !part.Value.Geometry.Geometry.Picking.IsEmpty).Value.Geometry.Geometry;
        var oldGrid = oldGeometry.Picking.Single();
        var top = oldGrid.Grid.Cells.MaxBy(cell => cell.At.Y);
        var ray = new SceneRay(new Vector3(top.At.X + 0.5f, top.At.Y + 2, top.At.Z + 0.5f) + oldGrid.Pose.Position,
            -Vector3.UnitY, 4);
        var oldFrame = SceneFixtures.Frame(oldGeometry);
        var oldHit = ((ScenePick.Hit)oldFrame.Pick(ray)).Value;
        var equivalent = Isolated(NodeFixtures.Content(generation: 2, rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 },
            terrain: RailwayFixtures.TerrainAppearance with { Display = new("new label", "new description"), LocalHeightNoise = [], Decorations = [] }));
        var same = NodeFixtures.Build(equivalent, NodeLayout.Build(equivalent, new(1)), before);
        foreach (var part in before.Parts) Assert.AreSame(part.Value, same.Parts[part.Key]);
        var appearance = SceneFixtures.Cube with { Presentation = ((VoxelPresentation.Cube)SceneFixtures.Cube.Presentation) with { Side = new("changed") } };
        var changedSnapshot = Isolated(NodeFixtures.Content(generation: 3,
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }, voxel: appearance));
        var changed = NodeFixtures.Build(changedSnapshot, NodeLayout.Build(changedSnapshot, new(1)), same);
        foreach (var part in before.Parts)
            if (part.Key.StartsWith("terrain:", StringComparison.Ordinal)) Assert.AreNotSame(part.Value, changed.Parts[part.Key]);
            else Assert.AreSame(part.Value, changed.Parts[part.Key]);
        Assert.AreEqual(oldHit, ((ScenePick.Hit)oldFrame.Pick(ray)).Value);
        Assert.AreEqual(top, oldGrid.Grid.Find(top.At));
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
        var built = NodeFixtures.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        Assert.IsTrue(built.Parts.Where(part => part.Key.StartsWith("entry:1:", StringComparison.Ordinal)).SelectMany(part => part.Value.Geometry.Geometry.Objects).SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Any());
        Assert.IsTrue(built.Parts.Where(part => part.Key.StartsWith("station-tracks:", StringComparison.Ordinal)).SelectMany(part => part.Value.Geometry.Geometry.Objects).SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Any());
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
        var built = NodeFixtures.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var same = NodeFixtures.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId), built);
        Assert.AreSame(built.Boundaries[new(1)], same.Boundaries[new(1)], "Unchanged inputs reuse strip samples.");
        foreach (var part in built.Parts) Assert.AreSame(part.Value, same.Parts[part.Key]);
        var relief = RailwayFixtures.ReplaceCore(snapshot, node: snapshot.Core.World.Nodes[0] with
        { Terrain = snapshot.Core.World.Nodes[0].Terrain with { Relief = 2 } });
        var changed = NodeFixtures.Build(relief, NodeLayout.Build(relief, NodeFixtures.NodeId), same);
        Assert.AreNotSame(same.Boundaries[new(1)], changed.Boundaries[new(1)], "Relief is read from the rebound snapshot.");
        Assert.AreNotSame(same.Parts["boundary:1"], changed.Parts["boundary:1"]);
        foreach (var part in same.Parts.Where(part => part.Key.StartsWith("entry:", StringComparison.Ordinal) ||
            part.Key.StartsWith("station-tracks:", StringComparison.Ordinal)))
            Assert.AreSame(part.Value, changed.Parts[part.Key]);
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
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var reference = NodeFixtures.Build(snapshot, layout);
        var prepared = NodeScene.Prepare(snapshot, layout, NodeScene.Cached.From(reference));
        var builds = new List<(string Key, Func<NodeScene.Part> Build)>();
        for (var z = (int)layout.Bounds.Minimum.Y; z < layout.Bounds.Maximum.Y; z += VoxelGrid.ChunkSize)
        for (var x = (int)layout.Bounds.Minimum.X; x < layout.Bounds.Maximum.X; x += VoxelGrid.ChunkSize)
        {
            var from = new GridColumn(x, z);
            builds.Add((NodeScene.ChunkKey(from), () => prepared.BuildChunk(from)));
        }
        // The first gated pair shares preparation data across terrain and rail construction.
        foreach (var segment in prepared.Rails.Values) builds.Insert(1, (segment.Key, () => segment.Build()));
        using var pair = new Barrier(RailwayCoordinates.MaxConcurrentBuilds);
        var started = 0;
        var collected = prepared.Global.Parts.ToBuilder();
        foreach (var batch in builds.Chunk(RailwayCoordinates.MaxConcurrentBuilds))
        {
            var parts = await Task.WhenAll(batch.Select(item => Task.Run(() =>
            {
                if (Interlocked.Increment(ref started) <= RailwayCoordinates.MaxConcurrentBuilds) pair.SignalAndWait();
                return (item.Key, Part: item.Build());
            })));
            foreach (var (key, part) in parts) collected.Add(key, part);
        }
        Assert.IsTrue(prepared.Rails.Count > 0);
        Assert.IsTrue(builds.Any(item => item.Key.StartsWith("terrain:", StringComparison.Ordinal)));
        var built = prepared.Global with { Parts = collected.ToImmutable() };
        CollectionAssert.AreEquivalent(reference.Parts.Keys.ToArray(), built.Parts.Keys.ToArray());
        foreach (var (key, part) in built.Parts) NodeFixtures.AssertPartEqual(reference.Parts[key], part);
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
        var stop = built.Layout.Stops[0];
        var station = new ScenePoint(stop.Center.X + 0.5, stop.Center.Y, stop.Center.Z + 0.5);
        Assert.AreEqual(-1.0, heights[new((int)Math.Floor(station.X), (int)Math.Floor(station.Z))],
            "The station track bed must be carved independently of the raw node ground.");

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
        await SceneFixtures.AwaitPendingWork(vehicles.PendingWork);
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), ObjectId, vehicles, new(), (_, _, _, _) => built);
        await scene.BuildWork!;
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var frame = scene.Present(snapshot, new(1, null, SceneDebugLayer.None), stamp, null, default);
        await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        frame = scene.Present(snapshot, new(1, null, SceneDebugLayer.None), stamp, null, default);
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
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>> { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(NodeFixtures.Layout) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [NodeFixtures.StationDesign] = new ContentLookup<DesignEvaluation>.Found(NodeFixtures.StationEvaluation()) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Missing([]) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [NodeFixtures.Concrete] = new ContentLookup<VoxelAppearanceProjection>.Found(NodeFixtures.ConcreteCube),
              [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) },
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
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = snapshot.Core.FindSettlement(new(1)) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> { [new InstanceId.Building(new(1))] = snapshot.Core.FindInstance(new InstanceId.Building(new(1))) },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [new(1)] = snapshot.Core.FindEdge(new(1)) }
        } };
        return snapshot;
    }

    [TestMethod]
    public async Task CopiedSettlementIgnoresSourceTemplateAndBlueprintChanges()
    {
        var before = CopiedSettlement(1, terrain: false, stationPlots: false);
        var after = CopiedSettlement(2, terrain: false, stationPlots: false);
        var built = NodeFixtures.Build(before, NodeLayout.Build(before, NodeFixtures.NodeId));
        var rebound = NodeFixtures.Build(after, NodeLayout.Build(after, NodeFixtures.NodeId), built);
        foreach (var part in built.Parts) Assert.AreSame(part.Value, rebound.Parts[part.Key]);
        var frames = new List<RenderedDesign>();
        foreach (var snapshot in new[] { before, after })
        {
            await using var objects = new SettlementObjects(SceneFixtures.NextId());
            RenderedDesign Present() => objects.Present(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId), default,
                ImmutableArray.CreateBuilder<SceneFog>());
            var frame = Present();
            while (objects.PendingWork.Length > 0) { await SceneFixtures.AwaitPendingWork(objects.PendingWork); frame = Present(); }
            frames.Add(frame);
        }
        CollectionAssert.AreEqual(frames[0].Objects.Select(item => item.Pose).ToArray(), frames[1].Objects.Select(item => item.Pose).ToArray());
        var meshes = frames.Select(frame => frame.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>()
            .SelectMany(item => item.Mesh.Vertices).ToArray()).ToArray();
        CollectionAssert.AreEqual(meshes[0], meshes[1]);
    }

    [TestMethod]
    public async Task StationBlueprintOwnsPlatformsAndResourceChangesReuseTerrain()
    {
        var snapshot = CopiedSettlement(1, terrain: false);
        await using var session = Open(snapshot);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var before = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        var station = new WorldObjectId.Instance(new InstanceId.Building(new(1)));
        Assert.IsTrue(before.Objects.Where(item => item.Subject == station).SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Any());
        Assert.IsFalse(before.Diagnostics.Any(problem => problem.Code == "content_missing" &&
            problem.Content is not ContentReference.Core { Definition: CoreDefinitionReference.Terrain }));
        var hit = (ScenePick.Hit)before.Pick(new(new(22.5f, 8, .5f), -Vector3.UnitY, 10));
        Assert.AreEqual(station, ((PickSubject.WorldObject)hit.Value.Subject).Object);
        var oldTerrain = before.Objects.Where(item => item.Subject is null).SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(v => v.Mesh).ToArray();
        Assert.IsTrue(oldTerrain.Length > 0, "The copied scene model still supplies terrain meshes without a terrain appearance.");
        var node = snapshot.Core.World.Nodes.Single() with
        { Resources = [new(new GoodId.Voxel(new("core:crude-oil")), 10, new ResourceRenewal.Renewable(10, 1))] };
        var changed = RailwayFixtures.ReplaceCore(snapshot, node: node);
        session.Observe(changed, []);
        var after = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        var meshes = after.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(v => v.Mesh).ToArray();
        var resourceMaterials = meshes.Select(mesh => mesh.Material).OfType<SceneMaterial.Voxel>()
            .Where(material => material.Texture == new AssetRevision("crude-oil")).ToArray();
        Assert.IsTrue(resourceMaterials.Length > 0, "The updated node renders its crude-oil resource pile.");
        var defaultPaint = BuiltInResourcePaints.For(new("core:crude-oil"));
        Assert.IsNotNull(defaultPaint);
        foreach (var material in resourceMaterials) Assert.AreEqual(defaultPaint, material.Paint);
        foreach (var mesh in oldTerrain) Assert.IsTrue(meshes.Any(current => ReferenceEquals(mesh, current)), "Resource changes retain every existing terrain mesh.");
        Assert.AreEqual(before.NodePanBounds, after.NodePanBounds);
        var afterHit = ((ScenePick.Hit)after.Pick(new(new(22.5f, 8, .5f), -Vector3.UnitY, 10))).Value;
        Assert.AreEqual(hit.Value with { Frame = afterHit.Frame }, afterHit);
    }

    [TestMethod]
    public void TemplateModelEntersTerrainAndRailwayClearsItsCells()
    {
        var snapshot = CopiedSettlement(1, ballast: NodeFixtures.Concrete);
        var built = NodeFixtures.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        var terrain = built.Parts.Where(part => part.Key.StartsWith("terrain:", StringComparison.Ordinal)).Select(part => part.Value).ToArray();
        var cells = terrain.SelectMany(part => part.Geometry.Geometry.Picking).SelectMany(pick => pick.Grid.Cells).ToDictionary(cell => cell.At);
        var paint = new PaintColor("#ff00ff");
        Assert.AreEqual(paint, cells[new(5, 3, 5)].Paint, "Model cells belong to the terrain chunk's pick grid.");
        Assert.IsTrue(terrain.SelectMany(part => part.Geometry.Geometry.Objects).SelectMany(item => item.Visuals)
            .OfType<SceneVisual.Geometry>().Any(visual => visual.Mesh.Material is SceneMaterial.Voxel material && material.Paint == paint));
        Assert.IsFalse(cells.TryGetValue(new(41, 0, 0), out var onTrack) && onTrack.Paint == paint,
            "The railway corridor removes the model cell on its centerline.");
        Assert.IsFalse(cells.TryGetValue(new(41, -2, 0), out var underTrack) && underTrack.Paint == paint,
            "The lower bed layer also overrides model cells.");
        Assert.AreEqual(NodeFixtures.Concrete, cells[new(41, -2, 0)].Appearance!.Definition,
            "The node bed uses the railway rule voxel rather than the terrain rock.");
        Assert.AreEqual(1d, terrain.SelectMany(part => part.Heights).Single(item => item.Key == new GridColumn(0, 0)).Value,
            "The actual ground is flattened before the scene model is placed.");
        Assert.IsFalse(built.Parts.Values.SelectMany(part => part.Geometry.Geometry.Diagnostics).Any(problem => problem.Code == "build_failed"));
    }

    [TestMethod]
    public async Task OutOfRangePlatformReportsTrainMismatchWithoutVehiclePose()
    {
        var snapshot = NodeFixtures.Snapshot(trains: [NodeFixtures.Train(platformNumber: 3)],
            content: NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        // Keep the scene small while exercising the production train rendering path.
        var basis = CopiedSettlement(1, terrain: false);
        var map = snapshot.Core.World.Nodes[0];
        snapshot = snapshot with { Core = new FixedCoreReadView
        {
            Position = snapshot.Core.Position, InputIdentity = snapshot.Core.InputIdentity,
            World = snapshot.Core.World with { Nodes = [map], Edges = [] },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [map.Id] = new Lookup<NodeView>.Found(new(map, [], 80, 0, 80)) },
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(1)] = snapshot.Core.FindTrain(new(1)) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = snapshot.Core.FindSettlement(new(1)) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            { [new InstanceId.Building(new(1))] = snapshot.Core.FindInstance(new InstanceId.Building(new(1))),
              [new InstanceId.Vehicle(new(1))] = snapshot.Core.FindInstance(new InstanceId.Vehicle(new(1))) }
        }, Content = basis.Content };
        await using var session = Open(snapshot);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var frame = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "station_platform_mismatch" &&
            problem.Subject == new WorldObjectId.Train(new(1))));
        Assert.IsNotInstanceOfType<Lookup<ScenePose>.Found>(frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)))));
    }

    [TestMethod]
    public async Task MissingSettlementCopyReportsMissingWithoutFallingBackToTemplate()
    {
        var basis = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 });
        var content = new FixedContentView
        {
            Generation = basis.Generation, Core = basis.Core with
            { SettlementTemplates = [new(NodeFixtures.Layout.SourceTemplate, new("test:type"), 1,
                [new(1, PlotSize.SmallStation, NodeFixtures.StationDesign)], [], [], [], 96)] },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Missing([]) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Missing([]) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
            { [RailwayFixtures.Rule] = basis.FindRailwayAppearance(RailwayFixtures.Rule) }
        };
        var snapshot = Isolated(content);
        var map = snapshot.Core.World.Nodes[0] with { Occupant = new NodeOccupant.Settlement(new(1), SettlementRelation.NonHostile) };
        snapshot = snapshot with { Core = new FixedCoreReadView
        {
            Position = snapshot.Core.Position, InputIdentity = snapshot.Core.InputIdentity,
            World = snapshot.Core.World with { Nodes = [map], Trains = [] },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [map.Id] = new Lookup<NodeView>.Found(new(map, [], 80, 0, 80)) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
            { [new(1)] = new Lookup<SettlementView>.Found(NodeFixtures.Settlement()) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            { [new InstanceId.Building(new(1))] = new Lookup<InstanceView>.Missing() }
        } };
        await using var session = Open(snapshot);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var frame = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "content_missing" && problem.Subject == new WorldObjectId.Node(NodeFixtures.NodeId)));
        Assert.IsFalse(frame.Objects.Any(item => item.Subject is WorldObjectId.Instance));
        Assert.IsFalse(frame.Diagnostics.Any(problem => problem.Code == "railway_update_pending"));
    }

    [TestMethod]
    public async Task SettlementFoundedAfterOpeningReplacesTheSceneInTheSameSpace()
    {
        var founded = CopiedSettlement(1, terrain: false);
        var empty = RailwayFixtures.ReplaceCore(founded, node: founded.Core.World.Nodes[0] with
            { Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        await using var session = Open(empty);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var before = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        session.Observe(founded, []);
        var held = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(new(1, null, SceneDebugLayer.None), before)).Value;
        Assert.AreEqual(before.NodePanBounds, held.NodePanBounds);
        Assert.AreEqual(before.Update.Frame.Session, held.Update.Frame.Session);
        Assert.AreEqual(before.Update.Frame.Request, held.Update.Frame.Request);
        Assert.AreEqual(before.Update.Frame.Space, held.Update.Frame.Space);
        Assert.IsFalse(held.Diagnostics.Any(problem => problem.Detail.Contains("layout copy")));
        var after = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None), before);
        Assert.AreEqual(before.Update.Frame.Session, after.Update.Frame.Session);
        Assert.AreEqual(before.Update.Frame.Request, after.Update.Frame.Request);
        Assert.AreEqual(before.Update.Frame.Space, after.Update.Frame.Space);
        Assert.AreNotEqual(SceneReadiness.Loading, after.Readiness);
        Assert.AreEqual(NodeLayout.Build(founded, NodeFixtures.NodeId).PanBounds(after.Origin), after.NodePanBounds);
        Assert.AreNotEqual(before.NodePanBounds, after.NodePanBounds);
        Assert.IsTrue(after.Objects.Any(item => item.Subject is WorldObjectId.Instance && before.Objects.All(old => old.Id != item.Id)));
        Assert.IsFalse(after.Diagnostics.Any(problem => problem.Code == "railway_update_pending"));
        Assert.IsFalse(after.Diagnostics.Any(problem => problem.Code == "content_missing" && problem.Detail.Contains("layout copy")));
    }

    [TestMethod]
    public async Task FoundedReplacementKeepsTheFocusedGroundUntilTheNewSceneIsReady()
    {
        var content = CopiedSettlement(1, terrain: false, radius: 20).Content;
        var founded = NodeFixtures.Snapshot(platforms: 0, trains: [], content: content);
        var empty = RailwayFixtures.ReplaceCore(founded, node: founded.Core.World.Nodes[0] with
            { Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        await using var session = Open(empty);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var opened = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(new(1, null, SceneDebugLayer.None), null)).Value;
        var entry = NodeLayout.Build(empty, NodeFixtures.NodeId).Entries[0].Point;
        var center = (opened.NodePanBounds!.Minimum.X + opened.NodePanBounds.Maximum.X) / 2;
        Assert.IsTrue(Math.Abs(entry.X - center) > NodeStreaming.NearCells,
            "The camera must sit outside the layout-center near disk.");
        var aim = new Vector3((float)entry.X + .5f, (float)entry.Y + 8, (float)entry.Z + .5f);
        var displayed = await Focused(session, opened, aim);
        var hit = displayed.Pick(new(aim, -Vector3.UnitY, 80));
        Assert.IsInstanceOfType<ScenePick.Hit>(hit, "The open scene has built the focused ground.");
        var focused = ((ScenePick.Hit)hit).Value.Object;
        session.Observe(founded, []);
        SceneFrame? swapped = null;
        var previous = displayed;
        for (var attempt = 0; attempt < 40 && swapped is null; attempt++)
        {
            await SceneFixtures.AwaitPendingWork(session.PendingNodeBuilds);
            var next = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(
                new(1, new(previous, aim), SceneDebugLayer.None), previous)).Value;
            if (!SamePan(previous, next)) swapped = next;
            else previous = next;
        }
        Assert.IsNotNull(swapped);
        Assert.IsInstanceOfType<ScenePick.Hit>(swapped.Pick(new(aim, -Vector3.UnitY, 80)));
        Assert.IsFalse(Covers(swapped, aim));
        Assert.IsTrue(previous.Objects.Any(item => item.Id == focused));
        Assert.IsFalse(swapped.Update.Removed.Contains(focused!.Value));
    }

    [TestMethod]
    public async Task RequestViewReleasesTheFoundingSuccessor()
    {
        var founded = WithNeighbor(CopiedSettlement(1, terrain: false));
        var empty = RailwayFixtures.ReplaceCore(founded, node: founded.Core.World.Nodes[0] with
            { Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), empty, WorldSceneSession.Generate, new(), new(),
            (input, node, _, token) => new(NodeLayout.Build(input, node, token),
                ImmutableDictionary<string, NodeScene.Part>.Empty.Add("ground", new("fixed",
                    WorldSceneSession.BuildGeometry(id => new([new(id(""), null, SceneFixtures.Identity, null, [], [], [])], [], [])),
                    ImmutableDictionary<GridColumn, double>.Empty)), []));
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var before = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(request, null)).Value;
        session.Observe(founded, []);
        session.Present(request, before);
        session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Node(new(2))));
        SceneFrame frame = before;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try { await SceneFixtures.AwaitPendingWork(session.PendingNodeBuilds); }
            catch (Exception) { }
            frame = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(request, null)).Value;
        }
        Assert.AreEqual(NodeLayout.Build(founded, new(2)).PanBounds(frame.Origin), frame.NodePanBounds);
    }

    private static async Task<SceneFrame> Focused(WorldSceneSession session, SceneFrame displayed, Vector3 aim)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await SceneFixtures.AwaitPendingWork(session.PendingNodeBuilds);
            var next = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(
                new(1, new(displayed, aim), SceneDebugLayer.None), displayed)).Value;
            var stable = next.Readiness is not SceneReadiness.Loading && session.PendingNodeBuilds.Length == 0 &&
                next.Objects.Select(item => item.Id).ToHashSet().SetEquals(displayed.Objects.Select(item => item.Id));
            if (next.Readiness is not SceneReadiness.Loading) displayed = next;
            if (stable && displayed.Pick(new(aim, -Vector3.UnitY, 80)) is ScenePick.Hit) return displayed;
        }
        return displayed;
    }

    private static bool SamePan(SceneFrame left, SceneFrame right) =>
        left.NodePanBounds?.Minimum == right.NodePanBounds?.Minimum && left.NodePanBounds?.Maximum == right.NodePanBounds?.Maximum;

    private static bool Covers(SceneFrame frame, Vector3 point) => frame.Update.Environment.FoggedRegions.Any(region =>
        region.Minimum.X <= point.X && point.X <= region.Maximum.X && region.Minimum.Z <= point.Z && point.Z <= region.Maximum.Z);

    [TestMethod]
    public async Task FoundedReplacementFailureStaysOnTheOpenScene()
    {
        var founded = CopiedSettlement(1, terrain: false);
        var empty = RailwayFixtures.ReplaceCore(founded, node: founded.Core.World.Nodes[0] with
            { Occupant = new NodeOccupant.Empty(), Platforms = 0 });
        var calls = 0;
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), empty, WorldSceneSession.Generate, new(), new(),
            (input, node, _, token) =>
            {
                if (Interlocked.Increment(ref calls) > 1) throw new RailwayBuildException("founded layout failed");
                var layout = NodeLayout.Build(input, node, token);
                return new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty.Add("ground",
                    new("fixed", WorldSceneSession.BuildGeometry(id => new([new(id(""), null, SceneFixtures.Identity, null, [], [], [])], [], [])),
                        ImmutableDictionary<GridColumn, double>.Empty)), []);
            });
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var before = await RailwayFixtures.Settled(session, new(1, null, SceneDebugLayer.None));
        session.Observe(founded, []);
        var frame = before;
        for (var attempt = 0; attempt < 8 && frame.Diagnostics.All(problem => problem.Code != "build_failed"); attempt++)
        {
            try { await SceneFixtures.AwaitPendingWork(session.PendingNodeBuilds); }
            catch (Exception) { }
            frame = (SceneFrame)((SceneResult<ISceneFrame>.Success)session.Present(new(1, null, SceneDebugLayer.None), before)).Value;
        }
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "build_failed"));
        Assert.AreEqual(before.NodePanBounds, frame.NodePanBounds);
        Assert.AreEqual(before.Update.Frame.Space, frame.Update.Frame.Space);
        Assert.IsTrue(frame.Objects.Length > 0);
    }

    private static SceneWorldSnapshot WithNeighbor(SceneWorldSnapshot snapshot)
    {
        var map = snapshot.Core.World.Nodes[0];
        var neighbor = map with { Id = new NodeId(2), Position = new(600, 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 };
        var nodes = snapshot.Core.World.Nodes.Add(neighbor);
        return snapshot with
        {
            Core = new FixedCoreReadView
            {
                Position = snapshot.Core.Position,
                InputIdentity = snapshot.Core.InputIdentity,
                World = snapshot.Core.World with { Nodes = nodes },
                Nodes = nodes.ToDictionary(item => item.Id, item => item.Id == neighbor.Id
                    ? (Lookup<NodeView>)new Lookup<NodeView>.Found(new(item, [], 80, 0, 80))
                    : snapshot.Core.FindNode(item.Id)),
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                    { [new InstanceId.Building(new(1))] = snapshot.Core.FindInstance(new InstanceId.Building(new(1))) },
                Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
                    { [new(1)] = snapshot.Core.FindSettlement(new(1)) }
            }
        };
    }

    private static SceneWorldSnapshot CopiedSettlement(ulong generation, bool terrain = true, bool stationPlots = true,
        VoxelDefinitionId? ballast = null, double radius = 1)
    {
        var basis = NodeFixtures.Content(generation, rules: RailwayFixtures.Rules with
        { MinimumCurveRadiusMeters = radius, BallastVoxel = ballast ?? RailwayFixtures.Rules.BallastVoxel });
        var model = SceneFixtures.Projection([SceneFixtures.Cell(new(5, 3, 5)) with { Paint = new("#ff00ff") },
            SceneFixtures.Cell(new(41, 0, 0), 2) with { Paint = new("#ff00ff") },
            SceneFixtures.Cell(new(41, -2, 0), 3) with { Paint = new("#ff00ff") }]) with
        { RootFlatten = new(new(-8, -8), new(48, 8), 1), OccupiedBounds = new(new(0, -2, 0), new(41, 3, 5)) };
        var plotSize = stationPlots ? PlotSize.SmallStation : PlotSize.Small;
        var plots = NodeFixtures.Layout.Plots.Select(plot => plot with { At = plot.At with { Y = 1 }, Size = plotSize }).ToImmutableArray().Add(new(99, PlotSize.Small, new(0, 1, 0), VoxelOrientation.PxPy));
        var copy = NodeFixtures.Layout with { Plots = generation == 1 ? plots : plots.Reverse().ToImmutableArray(),
            SceneModel = NodeFixtures.Layout.SceneModel with { Scene = model } };
        var sourceHash = new DesignHash(generation == 1 ? "old-blueprint" : "new-blueprint");
        var content = new FixedContentView
        {
            Generation = basis.Generation,
            Core = basis.Core with { SettlementTemplates = [new(copy.SourceTemplate, new("test:type"), 1, [new(1, PlotSize.SmallStation, sourceHash)], [], [], [], generation == 1 ? 96 : 128)] },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>> { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(copy) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = basis.FindDesign(SceneFixtures.Design), [NodeFixtures.StationDesign] = basis.FindDesign(NodeFixtures.StationDesign), [sourceHash] = new ContentLookup<DesignEvaluation>.Found(SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new((int)generation * 80, 0, 80))]))) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = basis.FindVoxelAppearance(SceneFixtures.Voxel),
              [NodeFixtures.Concrete] = basis.FindVoxelAppearance(NodeFixtures.Concrete),
              [new("core:crude-oil")] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
              {
                  Definition = new("core:crude-oil"),
                  Presentation = new VoxelPresentation.Cube([], MaterialCategory.PaintedMetal, 1,
                      new("crude-oil"), new("crude-oil"), new("crude-oil"), new("crude-oil"))
              }) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
            { [RailwayFixtures.Terrain] = terrain ? basis.FindTerrain(RailwayFixtures.Terrain) : new ContentLookup<TerrainAppearanceProjection>.Missing([]) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>> { [RailwayFixtures.Rule] = basis.FindRailwayAppearance(RailwayFixtures.Rule) }
        };
        var snapshot = NodeFixtures.Snapshot(trains: [], content: content);
        var station = ((Lookup<InstanceView>.Found)snapshot.Core.FindInstance(new InstanceId.Building(new(1)))).Value;
        var settlement = ((Lookup<SettlementView>.Found)snapshot.Core.FindSettlement(new(1))).Value;
        station = station with { Details = new InstanceDetails.Building(new(1), plotSize) };
        settlement = settlement with { Plots = settlement.Plots.Select(plot => plot with { Size = plotSize }).ToImmutableArray() };
        var map = snapshot.Core.World.Nodes[0];
        return snapshot with { Core = new FixedCoreReadView
        {
            Position = snapshot.Core.Position, InputIdentity = snapshot.Core.InputIdentity,
            World = snapshot.Core.World with { Nodes = [map], Edges = [] },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [map.Id] = new Lookup<NodeView>.Found(new(map, [], 80, 0, 80)) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> { [new InstanceId.Building(new(1))] = new Lookup<InstanceView>.Found(station) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = new Lookup<SettlementView>.Found(settlement) }
        } };
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
