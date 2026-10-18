using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeTrainPresentationTests
{
    private static TrainView Approaching(double distance) => NodeFixtures.Train(speed: 10) with
    { Position = new TrainPosition.Node(NodeFixtures.NodeId, new(2), 1, new(1), null, NodeTravelPhase.Approaching, distance, 1) };

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task FormedTrainUsesTheSmallestBuiltEntryOrTheStationAxis(int builtTracks)
    {
        var formed = NodeFixtures.Train();
        var basis = NodeFixtures.Snapshot(tracks: 0, trains: [formed]);
        var snapshot = RailwayFixtures.ReplaceCore(basis, edge: basis.Core.World.Edges[1] with { Tracks = builtTracks });
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(snapshot, vehicles);
        var frame = await Frame(scene, snapshot);
        Assert.IsFalse(frame.Diagnostics.Any(problem => problem.Code == "railway_update_pending"));
        var layout = scene.Layout!;
        var position = Pose(frame, 1).Position;
        if (builtTracks == 0)
            Assert.IsTrue(Vector3.Distance(position, RailwayCoordinates.Relative(layout.Bypass.Center, default) -
                layout.StationForward * (float)(96 + formed.LengthMeters)) < .003);
        else
        {
            var entry = layout.EntryFor(new(2));
            var inlet = layout.Approach(entry, layout.Bypass).Inlet;
            var expected = inlet.Sample(inlet.Length - formed.LengthMeters).Position;
            Assert.IsTrue(Vector3.Distance(position, RailwayCoordinates.Relative(expected, default)) < .003);
        }
    }

    [TestMethod]
    public async Task ContentSnapshotsAtTheSameStepKeepTheirPlatformMappingAndDiagnostics()
    {
        var valid = NodeFixtures.Snapshot(platforms: 1, trains: [Approaching(100)]);
        var missing = valid with { Content = NodeFixtures.Content(generation: 2,
            designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [NodeFixtures.StationDesign] = new ContentLookup<DesignEvaluation>.Missing([]) }) };
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(valid, vehicles);
        var before = await Frame(scene, valid);
        for (var i = 0; i < 2; i++)
        {
            var frame = await Frame(scene, missing, valid, .5);
            Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "station_platform_mismatch" &&
                problem.Subject == new WorldObjectId.Node(NodeFixtures.NodeId)));
            Assert.IsNotInstanceOfType<Lookup<ScenePose>.Found>(frame.FindPose(
                new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)))));
        }
        var restored = await Frame(scene, valid);
        Assert.AreEqual(Pose(before, 1), Pose(restored, 1), "Retaining another content snapshot must not replace the old mapping.");
    }

    [TestMethod]
    public async Task AdjacentNodeStepsInterpolateProgressAndShareTheRenderedPickPose()
    {
        var old = NodeFixtures.Snapshot(trains: [Approaching(20)], steps: 4);
        var current = NodeFixtures.Snapshot(trains: [Approaching(60)], steps: 5);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var first = await Frame(scene, old);
        var frame = await Frame(scene, current, old, .25);
        var entry = scene.Layout!.EntryFor(new(2));
        var expected = RailwayCoordinates.Relative(scene.Layout.TrainBoundary(entry).Position, default) -
            entry.Outward * (float)((16 + entry.StraightLength - 4) * 30 / 80);
        var subject = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
        var pose = ((Lookup<ScenePose>.Found)frame.FindPose(subject)).Value;
        Assert.IsTrue(Vector3.Distance(expected, pose.Position) < .003);
        Assert.AreEqual(4.25 * SimulationTime.StepSeconds, frame.SimulationSeconds, 1e-12);
        var body = frame.Objects.Single(item => item.Parent is null && item.Subject == subject);
        Assert.AreEqual(pose, body.Pose);
        var oldBody = first.Objects.Single(item => item.Parent is null && item.Subject == subject);
        Assert.AreSame(((SceneVisual.Geometry)oldBody.Visuals[0]).Mesh, ((SceneVisual.Geometry)body.Visuals[0]).Mesh);
        var cell = pose.Position + Vector3.Transform(new Vector3(.5f, .5f, .5f), pose.Rotation);
        var hit = (ScenePick.Hit)frame.Pick(new(cell + Vector3.UnitY * 10, -Vector3.UnitY, 20));
        Assert.AreEqual(subject, ((PickSubject.WorldObject)hit.Value.Subject).Object);
        Assert.AreEqual(frame.Update.Frame, hit.Value.Frame);
    }

    [TestMethod]
    [DataRow(9UL, false)]
    [DataRow(5UL, true)]
    public async Task SkippedStepsAndChangedFacingUseTheCurrentBoundary(ulong step, bool reverse)
    {
        var old = NodeFixtures.Snapshot(trains: [Approaching(20)], steps: 4);
        var train = Approaching(60) with { OrderFacing = reverse ? TrainOrderFacing.LastEndLeading : TrainOrderFacing.FirstEndLeading };
        var current = NodeFixtures.Snapshot(trains: [train], steps: step);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var frame = await Frame(scene, current, old, 0);
        var entry = scene.Layout!.EntryFor(new(2));
        var expected = RailwayCoordinates.Relative(scene.Layout.TrainBoundary(entry).Position, default) -
            entry.Outward * (float)((16 + entry.StraightLength - 4) * 60 / 80);
        Assert.IsTrue(Vector3.Distance(expected, Pose(frame, 1).Position) < .003);
    }

    [TestMethod]
    [DataRow(20.0)]
    [DataRow(60.0)]
    [DataRow(80.0)]
    public async Task PlatformGrantDoesNotMoveAnyMemberOfAThreeCarConsist(double distance)
    {
        var waiting = NodeFixtures.Train(vehicles: [new(1), new(2), new(3)], enteredFrom: new(1), nextEdge: new(2)) with
        {
            LengthMeters = 78, PlatformQueueEnteredStep = 1,
            Position = new TrainPosition.Node(NodeFixtures.NodeId, new(1), 1, new(2), null,
                distance == 80 ? NodeTravelPhase.Waiting : NodeTravelPhase.Approaching, distance, null)
        };
        var content = NodeFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: 52),
            rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 80 });
        var old = NodeFixtures.Snapshot(trains: [waiting], content: content, steps: 1);
        var granted = waiting with { PlatformQueueEnteredStep = null,
            Position = ((TrainPosition.Node)waiting.Position) with { Phase = NodeTravelPhase.Approaching, PlatformNumber = 1 } };
        var current = NodeFixtures.Snapshot(trains: [granted], content: content, steps: 2);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var after = await Frame(scene, current, old, .5);
        var entry = scene.Layout!.EntryFor(new(1));
        var inlet = scene.Layout.Approach(entry, scene.Layout.Bypass).Inlet.Length;
        Assert.IsTrue(inlet > 78, "This regression exercises the approved R9 range.");
        var unshiftedFront = inlet + 52;
        var bypass = new NodeTrainPath(scene.Layout, ((Lookup<NodeView>.Found)old.Core.FindNode(NodeFixtures.NodeId)).Value,
            (TrainPosition.Node)waiting.Position, scene.Layout.Bypass, 0, null);
        var platform = new NodeTrainPath(scene.Layout, ((Lookup<NodeView>.Found)old.Core.FindNode(NodeFixtures.NodeId)).Value,
            (TrainPosition.Node)granted.Position, scene.Layout.Stops[0], 0, null);
        Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(bypass.Sample(unshiftedFront).Position,
            platform.Sample(unshiftedFront).Position), Vector3.Zero) > .01,
            "The fixture must expose the original multi-car branch change at an unshifted W.");
        foreach (var id in new ulong[] { 1, 2, 3 })
        {
            Assert.IsTrue(Vector3.Distance(Pose(before, id).Position, Pose(after, id).Position) < .003);
            Assert.IsTrue(Math.Abs(Quaternion.Dot(Pose(before, id).Rotation, Pose(after, id).Rotation)) > .999999);
        }
    }

    [TestMethod]
    public async Task ApplicantsUseCoreAgeThenIdAndArriveAtTheirQueuePositionContinuously()
    {
        var first = NodeFixtures.Train(9, enteredFrom: new(2)) with { PlatformQueueEnteredStep = 3 };
        var second = NodeFixtures.Train(2, enteredFrom: new(2)) with { PlatformQueueEnteredStep = 4 };
        var third = NodeFixtures.Train(3, enteredFrom: new(2)) with { PlatformQueueEnteredStep = 4 };
        var generated = NodeFixtures.Train(1, enteredFrom: new(2));
        var arriving = second with
        {
            SpeedMetersPerSecond = 1,
            Position = ((TrainPosition.Node)second.Position) with { Phase = NodeTravelPhase.Approaching, DistanceMeters = 79.999999 }
        };
        var old = NodeFixtures.Snapshot(trains: [third, generated, arriving, first], steps: 5);
        var current = NodeFixtures.Snapshot(trains: [generated, third, first, second], steps: 6);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var after = await Frame(scene, current, old, 1);
        var entry = scene.Layout!.EntryFor(new(2));
        var head = Pose(after, 1).Position;
        Assert.IsTrue(Vector3.Distance(head + entry.Outward * 8, Pose(after, 9).Position) < .003);
        Assert.IsTrue(Vector3.Distance(head + entry.Outward * 16, Pose(after, 2).Position) < .003);
        Assert.IsTrue(Vector3.Distance(head + entry.Outward * 24, Pose(after, 3).Position) < .003);
        Assert.IsTrue(Vector3.Distance(Pose(before, 2).Position, Pose(after, 2).Position) < .003,
            "Becoming Waiting must not introduce a new queue offset.");
        await using var reopenedVehicles = new SceneVehicles();
        await using var reopened = Controlled(current, reopenedVehicles);
        var restored = await Frame(reopened, current);
        foreach (var id in new ulong[] { 1, 2, 3, 9 }) Assert.AreEqual(Pose(after, id), Pose(restored, id));
    }

    [TestMethod]
    public async Task PausingClearsCoreQueueAgeAndResumingWithANewAgeMovesBehindExistingApplicants()
    {
        var first = NodeFixtures.Train(9, enteredFrom: new(2), activity: TrainActivity.Waiting) with
        { PlatformQueueEnteredStep = 1 };
        var second = NodeFixtures.Train(2, enteredFrom: new(2), activity: TrainActivity.Waiting) with
        { PlatformQueueEnteredStep = 2 };
        var paused = second with { Activity = TrainActivity.Stopped, PlatformQueueEnteredStep = null };
        var resumed = second with { PlatformQueueEnteredStep = 4 };
        var old = NodeFixtures.Snapshot(trains: [first, second], steps: 2);
        var stopped = NodeFixtures.Snapshot(trains: [first, paused], steps: 3);
        var current = NodeFixtures.Snapshot(trains: [first, resumed], steps: 4);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var duringPause = await Frame(scene, stopped, old, 1);
        var after = await Frame(scene, current, stopped, 0);
        Assert.AreEqual(Pose(before, 9), Pose(duringPause, 2));
        Assert.AreEqual(Pose(before, 2), Pose(duringPause, 9),
            "Pausing the second applicant puts it before applicants and moves the first train backwards.");
        Assert.AreEqual(Pose(before, 9), Pose(after, 9));
        Assert.AreEqual(Pose(before, 2), Pose(after, 2),
            "Resuming receives a new core queue age and moves behind the existing applicant.");
    }

    [TestMethod]
    public async Task NewApplicantsDoNotPushAStationaryNonApplicantBackwards()
    {
        var generated = NodeFixtures.Train(9, enteredFrom: new(2));
        var applicant = NodeFixtures.Train(1, enteredFrom: new(2)) with { PlatformQueueEnteredStep = 1 };
        var old = NodeFixtures.Snapshot(trains: [generated], steps: 1);
        var current = NodeFixtures.Snapshot(trains: [applicant, generated], steps: 2);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var after = await Frame(scene, current, old, 1);
        Assert.AreEqual(Pose(before, 9), Pose(after, 9));
        var entry = scene.Layout!.EntryFor(new(2));
        Assert.IsTrue(Vector3.Distance(Pose(after, 9).Position + entry.Outward * 8, Pose(after, 1).Position) < .003);
    }

    [TestMethod]
    public async Task StoppingOnTheBypassDoesNotIntroduceAQueueOffset()
    {
        var stationary = NodeFixtures.Train(9, enteredFrom: new(2), nextEdge: new(1), activity: TrainActivity.Servicing);
        var arriving = NodeFixtures.Train(1, enteredFrom: new(2), nextEdge: new(1), speed: 1) with
        { Position = new TrainPosition.Node(NodeFixtures.NodeId, new(2), 1, new(1), null, NodeTravelPhase.Approaching, 127.999999, null) };
        var stopped = arriving with { SpeedMetersPerSecond = 0, Position = ((TrainPosition.Node)arriving.Position) with
            { Phase = NodeTravelPhase.Docked, DistanceMeters = 128 } };
        var old = NodeFixtures.Snapshot(trains: [stationary, arriving], steps: 1);
        var current = NodeFixtures.Snapshot(trains: [stationary, stopped], steps: 2);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var after = await Frame(scene, current, old, 1);
        Assert.IsTrue(Vector3.Distance(Pose(before, 1).Position, Pose(after, 1).Position) < .003);
        Assert.AreEqual(Pose(after, 9).Position, Pose(after, 1).Position);
    }

    [TestMethod]
    public async Task ThroughTrainHasNoQueueOffsetAndMixedConsistsAdvanceByTheLeavingTrainsLength()
    {
        var head = NodeFixtures.Train(9, enteredFrom: new(2)) with { PlatformQueueEnteredStep = 1 };
        var following = NodeFixtures.Train(2, vehicles: [new(2), new(20)], enteredFrom: new(2)) with
        { PlatformQueueEnteredStep = 2, LengthMeters = 8 };
        var through = NodeFixtures.Train(3, enteredFrom: new(2), speed: 10) with
        { Position = new TrainPosition.Node(NodeFixtures.NodeId, new(2), 1, new(1), null, NodeTravelPhase.Approaching, 40, null) };
        var old = NodeFixtures.Snapshot(trains: [following, head, through], steps: 1);
        var current = NodeFixtures.Snapshot(trains: [following, through], steps: 2);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var after = await Frame(scene, current, old, 0);
        Vector3 Center(SceneFrame frame) => (Pose(frame, 2).Position + Pose(frame, 20).Position) / 2;
        var entry = scene.Layout!.EntryFor(new(2));
        Assert.IsTrue(Vector3.Distance(Pose(before, 9).Position + entry.Outward * 12, Center(before)) < .003);
        Assert.IsTrue(Vector3.Distance(Pose(before, 20).Position - entry.Outward * 8, Pose(after, 20).Position) < .003,
            "Grant/removal advances the next train by the leaving train's full length, without interpolation.");
        Assert.AreEqual(Pose(before, 3), Pose(after, 3), "A through train must not inherit the waiting queue's offset.");
    }

    [TestMethod]
    public async Task QueueCompressionUsesCurrentPositionWithoutBlending()
    {
        var old = NodeFixtures.Snapshot(trains: [NodeFixtures.Train(1, enteredFrom: new(2)), NodeFixtures.Train(2, enteredFrom: new(2))], steps: 1);
        var current = NodeFixtures.Snapshot(trains: [NodeFixtures.Train(2, enteredFrom: new(2))], steps: 2);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var after = await Frame(scene, current, old, 0);
        var entry = scene.Layout!.EntryFor(new(2));
        var expected = RailwayCoordinates.Relative(scene.Layout.TrainBoundary(entry).Position, default) -
            entry.Outward * (float)(16 + entry.StraightLength - 4);
        Assert.IsTrue(Vector3.Distance(expected, Pose(after, 2).Position) < .003);
        Assert.AreEqual(8, Vector3.Distance(Pose(before, 2).Position, Pose(after, 2).Position), .003);
    }

    [TestMethod]
    public async Task AnOversizedHeadLeavesItsActualQueuePositionGap()
    {
        var content = NodeFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: 52));
        var basis = NodeFixtures.Snapshot(content: content);
        var layout = NodeLayout.Build(basis, NodeFixtures.NodeId);
        var entry = layout.EntryFor(new(2));
        var inlet = layout.Approach(entry, layout.Bypass).Inlet.Length;
        var count = 2 * (int)Math.Ceiling(inlet / 52) + 1;
        var head = NodeFixtures.Train(9, vehicles: Enumerable.Range(100, count).Select(id => new VehicleId((ulong)id)).ToImmutableArray(),
            enteredFrom: entry.Edge) with { LengthMeters = count * 26, PlatformQueueEnteredStep = 1 };
        var following = NodeFixtures.Train(2, vehicles: [new(2), new(20), new(21)], enteredFrom: entry.Edge) with
        { LengthMeters = 78, PlatformQueueEnteredStep = 2 };
        var old = NodeFixtures.Snapshot(trains: [head, following], content: content, steps: 1);
        var current = NodeFixtures.Snapshot(trains: [following], content: content, steps: 2);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var after = await Frame(scene, current, old, 0);
        var headHalf = head.LengthMeters;
        Assert.IsTrue(headHalf > inlet);
        var followerHalf = following.LengthMeters;
        var oldCenter = inlet - headHalf - followerHalf;
        var newCenter = followerHalf < inlet ? inlet - followerHalf : inlet;
        var dimensions = ((Lookup<NodeView>.Found)old.Core.FindNode(NodeFixtures.NodeId)).Value;
        var railway = new RailwayPath(old, entry.Edge);
        var route = new NodeTrainPath(layout, dimensions, (TrainPosition.Node)following.Position, layout.Bypass,
            following.LengthMeters * 2, _ => railway);
        var expectedBefore = RailwayVehicles.Pose(route.Sample(oldCenter), default, true);
        var expectedAfter = RailwayVehicles.Pose(route.Sample(newCenter), default, true);
        Assert.IsTrue(Vector3.Distance(Pose(before, 20).Position, expectedBefore.Position) < .003);
        Assert.IsTrue(Vector3.Distance(Pose(after, 20).Position, expectedAfter.Position) < .003);
    }

    private static ScenePose Pose(SceneFrame frame, ulong vehicle) =>
        ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle))))).Value;

    private static NodeScene Controlled(SceneWorldSnapshot snapshot, SceneVehicles vehicles)
    {
        var ids = new Dictionary<string, SceneObjectId>();
        ulong next = 0;
        return new(snapshot, NodeFixtures.NodeId, new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new(++next));
            return id;
        }, vehicles, new(), RailwayFixtures.EmptyNode);
    }

    private static async Task<SceneFrame> Frame(NodeScene scene, SceneWorldSnapshot snapshot,
        SceneWorldSnapshot? previous = null, double interpolation = 1)
    {
        await scene.BuildWork!;
        var request = new SceneFrameRequest(interpolation, null, SceneDebugLayer.None);
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        SceneFrame Present() => scene.Present(snapshot, request, stamp, null, default, previous: previous);
        var frame = Present();
        while (scene.PendingWork.Length > 0)
        {
            await Task.WhenAll(scene.PendingWork);
            frame = Present();
        }
        return frame;
    }
}
