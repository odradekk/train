using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Creatures;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class SettlementWarPersistenceTests
{
    [TestMethod]
    public void A_completed_declaration_restores_and_replays()
    {
        var content = SettlementWarFixture.Content();
        using var original = SettlementWarFixture.Session(content);
        var settlement = SettlementWarFixture.Settlement(original);
        using var session = SettlementWarFixture.MoveTrain(original,
            SettlementWarFixture.AwayNode(original, settlement));
        SettlementWarFixture.Accept(session, new CoreCommand.DeclareWar(settlement.Id));

        Verify(session, content);
    }

    [TestMethod]
    public void A_combat_with_lowered_morale_restores_and_replays()
    {
        var content = SettlementWarFixture.MoraleRules(
            CombatFixture.Content(armedBuilding: true, damage: 10),
            coefficient: 1, weaponLoss: 0, recovery: 0, threshold: .5);
        using var original = CombatFixture.SessionWithContent(content, vehicles: 2);
        var settlement = SettlementAt(original, CombatFixture.HostileNode(original));
        using var session = SettlementWarFixture.MoveTrain(original, settlement.Node);
        session.Advance(1);
        Assert.IsTrue(SettlementWarFixture.Settlement(session, settlement.Id).Morale < 1);
        Assert.IsFalse(session.Read().World.Combats.IsEmpty);
        Assert.IsFalse(SettlementWarFixture.Settlement(session, settlement.Id).Surrendered);

        Verify(session, content);
    }

    [TestMethod]
    public void A_surrendered_settlement_restores_and_replays()
    {
        var content = SettlementWarFixture.MoraleRules(
            CombatFixture.Content(armedBuilding: true, damage: 100),
            coefficient: 0, weaponLoss: .18, recovery: .012, threshold: .9);
        using var original = CombatFixture.SessionWithContent(content, vehicles: 2);
        var settlement = SettlementAt(original, CombatFixture.HostileNode(original));
        using var session = SettlementWarFixture.MoveTrain(original, settlement.Node);
        for (var step = 0; step < 10; step++)
        {
            session.Advance(1);
            if (SettlementWarFixture.Settlement(session, settlement.Id).Surrendered) break;
        }
        Assert.IsTrue(SettlementWarFixture.Settlement(session, settlement.Id).Surrendered);

        Verify(session, content);
    }

    [TestMethod]
    public void A_conquered_settlement_restores_and_replays()
    {
        var content = SettlementWarFixture.Content();
        using var original = SettlementWarFixture.Session(content);
        var settlement = SettlementWarFixture.Settlement(original);
        var train = SettlementWarFixture.Train(original);
        using var away = SettlementWarFixture.MoveTrain(original,
            SettlementWarFixture.AwayNode(original, settlement));
        SettlementWarFixture.Accept(away, new CoreCommand.DeclareWar(settlement.Id));
        using var atNode = SettlementWarFixture.MoveTrain(away, settlement.Node);
        SettlementWarFixture.Accept(atNode, new CoreCommand.OccupySettlement(settlement.Id, train));
        Assert.AreEqual(SettlementRelation.PlayerOwned,
            SettlementWarFixture.Settlement(atNode, settlement.Id).Relation);

        Verify(atNode, content);
    }

    [TestMethod]
    [DataRow("morale_below_zero", "/world/settlements/morale")]
    [DataRow("morale_above_one", "/world/settlements/morale")]
    [DataRow("surrendered_non_hostile", "/world/settlements/surrendered")]
    [DataRow("surrendered_player_owned", "/world/settlements/surrendered")]
    public void Corrupt_morale_payload_is_rejected_without_changing_the_active_session(string corruption,
        string expectedField)
    {
        using var session = corruption == "surrendered_player_owned"
            ? PlayerOwned()
            : SettlementWarFixture.Session();
        var factory = CoreFactory.Create();
        var original = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var payload = original.Encode();
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(factory.DecodeState(payload));
        var root = JsonNode.Parse(payload)!.AsObject();
        var settlements = root["world"]!["settlements"]!.AsArray();
        var relation = corruption == "surrendered_player_owned"
            ? SettlementRelation.PlayerOwned
            : SettlementRelation.NonHostile;
        var settlement = corruption.StartsWith("morale_", StringComparison.Ordinal)
            ? settlements[0]!
            : settlements.First(item => item!["relation"]!.GetValue<int>() == (int)relation)!;
        switch (corruption)
        {
            case "morale_below_zero":
                settlement["morale"] = -0.1;
                break;
            case "morale_above_one":
                settlement["morale"] = 1.1;
                break;
            case "surrendered_non_hostile":
            case "surrendered_player_owned":
                settlement["surrendered"] = true;
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
        var after = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(payload, after.Encode());
        Assert.AreEqual(original.Checkpoint, after.Checkpoint);
    }

    private static void Verify(CoreSession session, CoreContentProjection content)
    {
        var factory = CoreFactory.Create();
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var window = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, window.Starts[0].State.Encode(),
            window.Commands, window.Checkpoints, window.EndPosition, session.Read().Fault);
        using var replay = WorldFixture.Success(factory.PrepareReplay(input, WorldFixture.Options.Runtime, [content]));
        var result = replay.Advance(uint.MaxValue, session.Read().Position);
        Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
        Assert.IsNull(result.Difference);
        Assert.AreEqual(saved.Encode(), replay.CaptureBoundary().Encode());

        var decoded = WorldFixture.Success(factory.DecodeState(saved.Encode()));
        using var candidate = WorldFixture.Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = candidate.Activate();
        var restoredCapture = WorldFixture.Success(restored.Capture(CapturePurpose.Save));
        Assert.AreEqual(saved.Encode(), restoredCapture.Encode());
        Assert.IsInstanceOfType<CheckpointComparison.Equal>(WorldFixture.Success(
            factory.CompareCheckpoints(saved.Checkpoint, restoredCapture.Checkpoint)));

        var left = session.Advance(20);
        var right = restored.Advance(20);
        Assert.IsNull(left.Fault);
        Assert.IsNull(right.Fault);
        CollectionAssert.AreEqual(left.Events.Select(envelope => envelope.Event.GetType().Name).ToArray(),
            right.Events.Select(envelope => envelope.Event.GetType().Name).ToArray());
        Assert.AreEqual(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode(),
            WorldFixture.Success(restored.Capture(CapturePurpose.Save)).Encode());
    }

    private static CoreSession PlayerOwned()
    {
        var baseline = CreatureTestSupport.CombatWorld;
        var settlement = CreatureTestSupport.Friendly(baseline.World);
        return CreatureTestSupport.Open(baseline,
            CreatureTestSupport.PlayerOwned(CreatureTestSupport.StopTrains(baseline.World), settlement.Id));
    }

    private static SettlementState SettlementAt(CoreSession session, NodeId node)
    {
        var id = ((NodeOccupant.Settlement)session.State.World.Nodes.Single(item => item.Id == node).Occupant).Id;
        return SettlementWarFixture.Settlement(session, id);
    }
}
