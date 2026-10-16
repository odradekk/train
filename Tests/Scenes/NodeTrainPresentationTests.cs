using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeTrainPresentationTests
{
    private static TrainView Approaching(double distance) => NodeFixtures.Train(speed: 10) with
    { Position = new TrainPosition.Node(NodeFixtures.NodeId, new(2), new(1), NodeTravelPhase.Approaching, distance, 1) };

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
            entry.Outward * (float)((16 + entry.StraightLength) * 30 / 80);
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
            entry.Outward * (float)((16 + entry.StraightLength) * 60 / 80);
        Assert.IsTrue(Vector3.Distance(expected, Pose(frame, 1).Position) < .003);
    }

    [TestMethod]
    public async Task PlatformGrantAtWDoesNotMoveTheWaitingTrain()
    {
        var waiting = NodeFixtures.Train(enteredFrom: new(2));
        var old = NodeFixtures.Snapshot(trains: [waiting], steps: 1);
        var granted = Approaching(80) with { SpeedMetersPerSecond = 0 };
        var current = NodeFixtures.Snapshot(trains: [granted], steps: 2);
        await using var vehicles = new SceneVehicles();
        await using var scene = Controlled(old, vehicles);
        var before = await Frame(scene, old);
        var after = await Frame(scene, current, old, .5);
        Assert.AreEqual(Pose(before, 1), Pose(after, 1));
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
            entry.Outward * (float)(16 + entry.StraightLength);
        Assert.IsTrue(Vector3.Distance(expected, Pose(after, 2).Position) < .003);
        Assert.AreEqual(8, Vector3.Distance(Pose(before, 2).Position, Pose(after, 2).Position), .003);
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
        }, vehicles, RailwayFixtures.EmptyNode);
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
