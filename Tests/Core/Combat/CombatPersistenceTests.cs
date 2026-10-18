using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class CombatPersistenceTests
{
    [TestMethod]
    public void Depleting_the_combat_identity_freezes_at_the_pre_step_boundary()
    {
        var content = CombatFixture.Content(damage: 1);
        using var session = CombatFixture.SessionWithContent(content);
        var factory = CoreFactory.Create();
        var root = JsonNode.Parse(Success(session.Capture(CapturePurpose.Save)).Encode())!;
        root["world"]!["next"]!["combat"] = ulong.MaxValue;
        var decoded = Success(factory.DecodeState(root.ToJsonString()));
        using var candidate = Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        var hostile = CombatFixture.HostileNode(restored);
        var train = new TrainId(1);
        CombatFixture.Send(restored, hostile, train);
        for (var step = 0; step < 10_000; step++)
        {
            var before = restored.State;
            var result = restored.Advance(1);
            if (result.Fault is null)
            {
                Assert.AreEqual(1u, result.CompletedSteps);
                continue;
            }
            Assert.AreEqual(0u, result.CompletedSteps);
            Assert.AreEqual("internal_error", result.Fault.Code);
            Assert.AreEqual("Advance", result.Fault.Operation);
            Assert.AreEqual(before.Position, restored.State.Position);
            Assert.AreSame(before.World, restored.State.World);
            Assert.AreEqual(result.Fault, restored.Read().Fault);
            Assert.IsTrue(result.Events.IsEmpty);
            var frozen = restored.Advance(1);
            Assert.AreEqual(0u, frozen.CompletedSteps);
            Assert.AreEqual(result.Fault, frozen.Fault);
            var submission = restored.Submit(new CoreCommand.StartTrain(train), CommandSource.AutomationTest);
            Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(submission);
            Assert.AreEqual("frozen", ((SubmissionResult.NotAccepted)submission).Reason.Code);
            return;
        }
        Assert.Fail("未触发战斗身份耗尽故障。");
    }

    [TestMethod]
    public void Combat_in_progress_restores_and_continues_identically()
    {
        var content = RetreatSeconds(CombatFixture.Content(damage: 1), 1);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        FocusAndRetreat(session, hostile, train);
        session.Advance(5);
        var factory = CoreFactory.Create();
        var saved = Success(session.Capture(CapturePurpose.Save));
        var decoded = Success(factory.DecodeState(saved.Encode()));
        using var candidate = Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = candidate.Activate();
        var restoredCapture = Success(restored.Capture(CapturePurpose.Save));
        Assert.AreEqual(saved.Encode(), restoredCapture.Encode());
        Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(
            factory.CompareCheckpoints(saved.Checkpoint, restoredCapture.Checkpoint)));
        for (var round = 0; round < 3; round++)
        {
            var left = session.Advance(20);
            var right = restored.Advance(20);
            Assert.IsNull(left.Fault);
            Assert.IsNull(right.Fault);
            CollectionAssert.AreEqual(left.Events.Select(envelope => envelope.Event.GetType().Name).ToArray(),
                right.Events.Select(envelope => envelope.Event.GetType().Name).ToArray());
            Assert.AreEqual(Success(session.Capture(CapturePurpose.Save)).Encode(),
                Success(restored.Capture(CapturePurpose.Save)).Encode());
        }
    }

    [TestMethod]
    public void Combat_recording_replays_to_the_same_checkpoint()
    {
        var content = RetreatSeconds(CombatFixture.Content(damage: 1), 1);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        FocusAndRetreat(session, hostile, train);
        session.Advance(120);
        var window = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, window.Starts[0].State.Encode(),
            window.Commands, window.Checkpoints, window.EndPosition, session.Read().Fault);
        using var replay = Success(CoreFactory.Create().PrepareReplay(input, WorldFixture.Options.Runtime, [content]));
        var position = session.Read().Position;
        var result = replay.Advance(uint.MaxValue, position);
        Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
        Assert.IsNull(result.Difference);
        var saved = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(saved.Encode(), replay.CaptureBoundary().Encode());
        Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(CoreFactory.Create().CompareCheckpoints(
            saved.Checkpoint, replay.CaptureBoundary().Checkpoint)));
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
    }

    [TestMethod]
    [DataRow("duplicate_controller", "/world/combats/0/controllers")]
    [DataRow("controller_at_wrong_node", "/world/combats/0/controllers/train")]
    [DataRow("focus_not_participant", "/world/combats/0/controllers/focus_target")]
    [DataRow("next_combat_reused", "/world/next/combat")]
    [DataRow("sides_missing_owner", "/world/combats/0/sides/owner")]
    public void Corrupt_combat_payload_is_rejected_without_changing_the_active_session(string corruption,
        string expectedField)
    {
        var content = RetreatSeconds(CombatFixture.Content(damage: 1), 1);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Retreat, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        FocusAndRetreat(session, hostile, train);
        session.Advance(5);
        var original = Success(session.Capture(CapturePurpose.Save));
        var payload = original.Encode();
        var factory = CoreFactory.Create();
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(factory.DecodeState(payload));
        var root = JsonNode.Parse(payload)!.AsObject();
        var world = root["world"]!;
        var combat = world["combats"]![0]!;
        var controllers = combat["controllers"]!.AsArray();
        switch (corruption)
        {
            case "duplicate_controller":
                controllers.Add(controllers[0]!.DeepClone());
                break;
            case "controller_at_wrong_node":
                var location = combat["location"]!["id"]!["value"]!.GetValue<ulong>();
                combat["location"]!["id"]!["value"] = world["nodes"]!.AsArray()
                    .Select(node => node!["id"]!["value"]!.GetValue<ulong>()).First(id => id != location);
                break;
            case "focus_not_participant":
                var node = combat["location"]!["id"]!["value"]!.GetValue<ulong>();
                var otherSettlement = world["settlements"]!.AsArray().First(item =>
                    item!["node"]!["value"]!.GetValue<ulong>() != node)!;
                var building = otherSettlement["plots"]!.AsArray().Select(plot => plot!["building"])
                    .First(item => item is not null)!["value"]!.GetValue<ulong>();
                controllers[0]!["focus_target"] = new JsonObject
                {
                    ["$kind"] = "instance",
                    ["id"] = new JsonObject
                    {
                        ["$kind"] = "building",
                        ["id"] = new JsonObject { ["value"] = building }
                    }
                };
                break;
            case "next_combat_reused":
                world["next"]!["combat"] = combat["id"]!["value"]!.GetValue<ulong>();
                break;
            case "sides_missing_owner":
                combat["sides"]!.AsArray().RemoveAt(0);
                break;
            default:
                Assert.Fail("Unknown corruption case.");
                break;
        }
        var result = factory.DecodeState(root.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result, corruption);
        var problem = ((CoreResult<ICoreCapture>.Failure)result).Problems.Single();
        Assert.AreEqual("invalid_state", problem.Code, corruption);
        Assert.AreEqual(expectedField, problem.Field, corruption);
        var after = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(payload, after.Encode());
        Assert.AreEqual(original.Checkpoint, after.Checkpoint);
    }

    private static void FocusAndRetreat(ICoreSession session, NodeId hostile, TrainId train)
    {
        var combat = session.Read().World.Combats.Single();
        var building = ((Lookup<LocationView>.Found)session.Read().FindLocation(
                new WorldLocation.Node(hostile))).Value.Instances
            .Single(instance => ((Lookup<InstanceView>.Found)session.Read().FindInstance(instance)).Value.Design
                == CombatFixture.ArmedBuilding);
        CombatFixture.Accepted(session.Submit(new CoreCommand.FocusFire(combat.Id,
            new CombatController.Train(train), new WorldObjectId.Instance(building)), CommandSource.AutomationTest));
        CombatFixture.Accepted(session.Submit(new CoreCommand.Retreat(train), CommandSource.AutomationTest));
    }

    private static CoreContentProjection RetreatSeconds(CoreContentProjection content, double seconds)
        => content with { CombatRules = [content.CombatRules[0] with { RetreatSeconds = seconds }] };

    private static T Success<T>(CoreResult<T> result) where T : notnull
        => ((CoreResult<T>.Success)result).Value;
}
