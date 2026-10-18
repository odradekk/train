using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class ReadViewInvalidationTests
{
    [TestMethod]
    public async Task New_read_view_at_the_same_step_updates_plots_damage_and_train_platform_without_mutating_the_old_frame()
    {
        var content = NodeFixtures.Content();
        var initial = Snapshot(content, changed: false);
        var changed = Snapshot(content, changed: true);
        Assert.AreNotSame(initial.Core, changed.Core);
        Assert.AreEqual(initial.Core.Position.CompletedSteps, changed.Core.Position.CompletedSteps);
        Assert.AreEqual(initial.Core.Position.Sequence + 1, changed.Core.Position.Sequence);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), initial,
            new(), new(), RailwayFixtures.EmptyNode);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var before = await RailwayFixtures.Settled(session, request);
        var vehicle = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
        var oldBuilding = new WorldObjectId.Instance(new InstanceId.Building(new(1)));
        var newBuilding = new WorldObjectId.Instance(new InstanceId.Building(new(2)));
        var oldPose = ((Lookup<ScenePose>.Found)before.FindPose(vehicle)).Value;
        AssertPlatform(before, oldPose, 2);
        var oldHit = VehicleHit(before, vehicle, 0);
        Assert.IsNotNull(oldHit);
        Assert.IsNotNull(VehicleHit(before, vehicle, 7));
        Assert.IsTrue(before.Objects.Any(item => item.Subject == oldBuilding));
        Assert.IsFalse(before.Objects.Any(item => item.Subject == newBuilding));

        using var core = new ScriptedCoreSession(initial.Core);
        var command = new CoreCommand.StartTrain(new(1));
        core.QueueSubmit(command, CommandSource.AutomationTest, new SubmissionResult.Completed(
            new(1, initial.Core.Position, changed.Core.Position, new CommandDecision.Accepted([], [])), [], null), changed.Core);
        var submitted = (SubmissionResult.Completed)core.Submit(command, CommandSource.AutomationTest);
        session.Observe(changed with { Core = core.Read() }, submitted.Events);
        var after = await RailwayFixtures.Settled(session, request, before);

        Assert.IsFalse(after.Objects.Any(item => item.Subject == oldBuilding));
        Assert.IsTrue(after.Objects.Any(item => item.Subject == newBuilding));
        Assert.IsNull(VehicleHit(after, vehicle, 0), "The destroyed vehicle part must no longer be pickable.");
        Assert.IsNotNull(VehicleHit(after, vehicle, 7));
        AssertPlatform(after, ((Lookup<ScenePose>.Found)after.FindPose(vehicle)).Value, 1);
        Assert.IsFalse(after.Diagnostics.Any(problem => problem.Code == "station_platform_mismatch"));

        Assert.AreEqual(oldPose, ((Lookup<ScenePose>.Found)before.FindPose(vehicle)).Value);
        Assert.AreEqual(oldHit, VehicleHit(before, vehicle, 0));
        Assert.IsNotNull(VehicleHit(before, vehicle, 7));
        Assert.IsTrue(before.Objects.Any(item => item.Subject == oldBuilding));
        Assert.IsFalse(before.Objects.Any(item => item.Subject == newBuilding));
    }

    private static SceneHit? VehicleHit(SceneFrame frame, WorldObjectId vehicle, int z)
    {
        var pose = ((Lookup<ScenePose>.Found)frame.FindPose(vehicle)).Value;
        var cell = pose.Position + Vector3.Transform(new Vector3(.5f, .5f, z + .5f), pose.Rotation);
        return frame.Pick(new(cell + Vector3.UnitY * 2, -Vector3.UnitY, 3)) is ScenePick.Hit hit &&
            hit.Value.Subject is PickSubject.WorldObject subject && subject.Object == vehicle ? hit.Value : null;
    }

    private static void AssertPlatform(SceneFrame frame, ScenePose pose, int number)
    {
        var expected = RailwayCoordinates.Relative(frame.Node!.Stops[number - 1].Center, frame.Origin);
        Assert.IsTrue(Vector3.Distance(expected, pose.Position) < .001f,
            $"Expected platform {number} at {expected}, actual vehicle position {pose.Position}.");
    }

    private static SceneWorldSnapshot Snapshot(IContentView content, bool changed)
    {
        var train = NodeFixtures.Train(enteredFrom: new(2),
            activity: TrainActivity.Servicing, platformNumber: changed ? 1 : 2) with
        { Activity = changed ? TrainActivity.Servicing : TrainActivity.Stopped };
        var vehicle = RailwayFixtures.Instance(train: train.Id) with
        {
            Location = NodeFixtures.Location,
            Parts = [new(new(1), new(changed ? 0 : 100, 100), false, changed), new(new(2), new(100, 100), false, false)]
        };
        var snapshot = NodeFixtures.Snapshot(trains: [train], instances: [vehicle], content: content,
            steps: 7, sequence: changed ? 1UL : 0UL);
        var building = NodeFixtures.Station(2) with { Id = new InstanceId.Building(new(changed ? 2UL : 1UL)) };
        var settlement = NodeFixtures.Settlement() with
        {
            Plots = NodeFixtures.Settlement().Plots.Select(plot => plot with
            { Occupant = new PlotOccupant.Building(((InstanceId.Building)building.Id).Id) }).ToImmutableArray()
        };
        var core = snapshot.Core;
        return snapshot with
        {
            Core = new FixedCoreReadView
            {
                Position = core.Position, InputIdentity = core.InputIdentity, World = core.World,
                Nodes = core.World.Nodes.ToDictionary(item => item.Id, item => core.FindNode(item.Id)),
                Edges = core.World.Edges.ToDictionary(item => item.Id, item => core.FindEdge(item.Id)),
                Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
                Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
                { [settlement.Id] = new Lookup<SettlementView>.Found(settlement) },
                Instances = new[] { vehicle, building }.ToDictionary(item => item.Id,
                    item => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(item))
            }
        };
    }
}
