using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class DebugLineTests
{
    private const SceneDebugLayer Layers = SceneDebugLayer.Chunks | SceneDebugLayer.RailwaySegments |
        SceneDebugLayer.Origin | SceneDebugLayer.Centerline;
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static readonly SceneFrameStamp Stamp = new(new(Guid.NewGuid()), new(1), new(1), 1);

    [TestMethod]
    public async Task RailwayLayersComposeWithoutChangingFrameContentAndStopAtTheLineEnd()
    {
        var snapshot = RailwayFixtures.Snapshot(150, 150, 0,
            content: RailwayFixtures.Content(design: RailwayFixtures.Vehicle()),
            trains: [RailwayFixtures.Train(20)], instances: [RailwayFixtures.Instance()]);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(snapshot, vehicles);
        var target = new WorldSceneTarget.Follow(new(1), new(1));
        var before = await RailwayFixtures.Settled(scene, snapshot, target, Request, Stamp);
        SceneFrame Present(SceneDebugLayer layers) => scene.Present(snapshot, null, target,
            Request with { DebugLayers = layers }, before.Update.Frame, null);
        AssertLayers(before, Present);
        var centerline = Present(SceneDebugLayer.Centerline).DebugLines;
        var points = centerline.SelectMany(line => new[] { line.From, line.To }).ToArray();
        Assert.IsTrue(points.All(point => point.X == 0 && point.Y == 0));
        Assert.AreEqual(0f, points.Min(point => point.Z));
        Assert.AreEqual(300f, points.Max(point => point.Z));
        var boundaries = Present(SceneDebugLayer.RailwaySegments).DebugLines
            .Select(line => (line.From.Z + line.To.Z) / 2).Distinct().ToArray();
        CollectionAssert.AreEquivalent(new[] { 0f, 128f, 256f, 300f }, boundaries);
    }

    [TestMethod]
    public async Task NodeLayersComposeWithoutChangingFrameContentAndShowTheLayoutBeforeBuilding()
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 1);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ids = new Dictionary<string, SceneObjectId>();
        ulong next = 0;
        SceneObjectId ObjectId(string key)
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new(++next));
            return id;
        }
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), ObjectId, vehicles, new(), _ => null,
            (input, node, previous, token) =>
            {
                entered.SetResult(); release.Wait(token);
                return RailwayFixtures.EmptyNode(input, node, previous, token);
            });
        SceneFrame Present(SceneDebugLayer layers) => scene.Present(snapshot,
            Request with { DebugLayers = layers }, Stamp, null, default);
        try
        {
            await entered.Task;
            var loading = Present(SceneDebugLayer.Centerline);
            Assert.AreEqual(SceneReadiness.Loading, loading.Readiness);
            Assert.IsFalse(loading.DebugLines.IsEmpty);
            release.Set();
            await scene.BuildWork!;
            var before = Present(SceneDebugLayer.None);
            while (scene.PendingWork.Length > 0)
            {
                await SceneFixtures.AwaitPendingWork(scene.PendingWork);
                before = Present(SceneDebugLayer.None);
            }
            AssertLayers(before, Present, SceneDebugLayer.RailwaySegments);
            var centerline = Present(SceneDebugLayer.Centerline).DebugLines;
            CollectionAssert.AreEqual(loading.DebugLines.ToArray(), centerline.ToArray());
            var points = centerline.SelectMany(line => new[] { line.From, line.To }).ToArray();
            // The rotated 192-cell station plot spans z=-96..96; the through-lines
            // extend another 32 cells at both ends, at x=1 +/- 40.
            var stationLines = centerline.Where(line =>
                (Math.Abs(line.From.X + 39) < .001f || Math.Abs(line.From.X - 41) < .001f) &&
                Math.Abs(line.To.X - line.From.X) < .001f && Math.Abs(line.From.Y) < .001f && Math.Abs(line.To.Y) < .001f &&
                line.From.Z >= -128.001f && line.To.Z <= 128.001f && line.To.Z > line.From.Z).ToArray();
            Assert.HasCount(512, stationLines);
            foreach (var x in new[] { -39f, 41f })
            {
                Assert.IsTrue(points.Any(point => Vector3.Distance(point, new(x, 0, -128)) < .001f));
                Assert.IsTrue(points.Any(point => Vector3.Distance(point, new(x, 0, 128)) < .001f));
            }
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task UnchangedRailwayLinesStayInWorldSpaceAcrossAnOriginShiftAndOldFramesStayImmutable()
    {
        var content = RailwayFixtures.Content(design: RailwayFixtures.Vehicle());
        var beforeInput = RailwayFixtures.Snapshot(600, 600, 0, content: content,
            trains: [RailwayFixtures.Train(127)], instances: [RailwayFixtures.Instance()]);
        await using var vehicles = new SceneVehicles();
        await using var scene = RailwayFixtures.Controlled(beforeInput, vehicles);
        var target = new WorldSceneTarget.Follow(new(1), new(1));
        var request = Request with { DebugLayers = SceneDebugLayer.RailwaySegments | SceneDebugLayer.Centerline };
        var before = await RailwayFixtures.Settled(scene, beforeInput, target, request, Stamp);
        var retained = before.DebugLines;
        var afterInput = RailwayFixtures.Snapshot(600, 600, 0, content: content,
            trains: [RailwayFixtures.Train(129)], instances: [RailwayFixtures.Instance()]);
        var after = await RailwayFixtures.Settled(scene, afterInput, target, request,
            before.Update.Frame with { Revision = before.Update.Frame.Revision + 1 }, displayed: before);
        Assert.AreNotEqual(before.Origin, after.Origin);
        CollectionAssert.IsSubsetOf(WorldLines(before), WorldLines(after));
        before.Pick(new(new(0, 5, 254), -Vector3.UnitY, 10));
        CollectionAssert.AreEqual(retained.ToArray(), before.DebugLines.ToArray());
    }

    [TestMethod]
    public void RotatedVehicleChunkEdgesFollowItsPoseAndCoverTheWholeOccupiedChunk()
    {
        var design = RailwayFixtures.Vehicle();
        var pose = new ScenePose(new(10, 20, 30), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        var geometry = DesignGeometry.Build(RailwayFixtures.Content(design: design), design,
            new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))), pose, SceneFixtures.NextId());
        var frame = new SceneFrame(Stamp, default, 0, SceneReadiness.Ready,
            geometry.Objects, geometry.Picking, [], geometry.Diagnostics, debugLayers: SceneDebugLayer.Chunks);
        Assert.AreEqual(12, frame.DebugLines.Length);
        var edges = new HashSet<(Vector3, Vector3)>();
        foreach (var line in frame.DebugLines)
        {
            var from = Corner(line.From); var to = Corner(line.To);
            Assert.AreEqual(1f, Vector3.Abs(to - from).X + Vector3.Abs(to - from).Y + Vector3.Abs(to - from).Z);
            edges.Add((from, to));
        }
        Assert.AreEqual(12, edges.Count);

        Vector3 Corner(Vector3 point)
        {
            var relative = point - new Vector3(10, 20, 30);
            // A +90-degree Y rotation maps (x, y, z) to (z, y, -x).
            var local = new Vector3(-relative.Z, relative.Y, relative.X);
            var corner = new Vector3(MathF.Round(local.X / 32), MathF.Round(local.Y / 32), MathF.Round(local.Z / 32));
            Assert.IsTrue(Vector3.Distance(local, corner * 32) < .0001f);
            Assert.IsTrue(corner.X is 0 or 1 && corner.Y is 0 or 1 && corner.Z is 0 or 1);
            return corner;
        }
    }

    private static void AssertLayers(SceneFrame before, Func<SceneDebugLayer, SceneFrame> present,
        SceneDebugLayer empty = SceneDebugLayer.None)
    {
        Assert.AreEqual(SceneReadiness.Ready, before.Readiness);
        Assert.IsTrue(before.DebugLines.IsEmpty);
        var all = present(Layers);
        var body = ((Lookup<ScenePose>.Found)before.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))))).Value;
        var ray = new SceneRay(body.Position + Vector3.Transform(new(.5f, 5, .5f), body.Rotation), -Vector3.UnitY, 10);
        Assert.IsInstanceOfType<ScenePick.Hit>(before.Pick(ray));
        Assert.AreEqual(before.Pick(ray), all.Pick(ray));
        Assert.AreEqual(before.Origin, all.Origin);
        Assert.AreEqual(before.NodePanBounds, all.NodePanBounds);
        Assert.AreEqual(before.Readiness, all.Readiness);
        Assert.AreEqual(before.SimulationSeconds, all.SimulationSeconds);
        CollectionAssert.AreEqual(before.Diagnostics.ToArray(), all.Diagnostics.ToArray());
        Assert.AreEqual(before.Update.Frame, all.Update.Frame);
        Assert.AreEqual(before.Update.Base, all.Update.Base);
        Assert.AreEqual(before.Update.ShiftExistingRoots, all.Update.ShiftExistingRoots);
        CollectionAssert.AreEqual(before.Update.Upserts.ToArray(), all.Update.Upserts.ToArray());
        CollectionAssert.AreEqual(before.Update.Removed.ToArray(), all.Update.Removed.ToArray());
        CollectionAssert.AreEqual(before.Update.Environment.FoggedRegions.ToArray(), all.Update.Environment.FoggedRegions.ToArray());
        CollectionAssert.AreEqual(before.Update.Environment.TerrainCollision.ToArray(), all.Update.Environment.TerrainCollision.ToArray());
        CollectionAssert.AreEqual(before.Update.Effects.ToArray(), all.Update.Effects.ToArray());
        var combined = new List<SceneDebugLine>();
        foreach (var layer in new[] { SceneDebugLayer.Chunks, SceneDebugLayer.RailwaySegments, SceneDebugLayer.Origin, SceneDebugLayer.Centerline })
        {
            var lines = present(layer).DebugLines;
            Assert.AreEqual(layer == empty, lines.IsEmpty);
            combined.AddRange(lines);
        }
        CollectionAssert.AreEquivalent(combined.ToArray(), all.DebugLines.ToArray());
    }

    private static SceneDebugLine[] WorldLines(SceneFrame frame)
    {
        var origin = new Vector3((float)frame.Origin.X, (float)frame.Origin.Y, (float)frame.Origin.Z);
        return frame.DebugLines.Select(line => line with { From = line.From + origin, To = line.To + origin }).ToArray();
    }
}
