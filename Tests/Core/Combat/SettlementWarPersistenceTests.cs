using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Combat;

[TestClass]
[TestCategory("integration")]
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

    private static void Verify(CoreSession session, CoreContentProjection content)
    {
        var factory = CoreFactory.Create();
        var saved = Success(session.Capture(CapturePurpose.Save));
        var window = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, window.Starts[0].State.Encode(),
            window.Commands, window.Checkpoints, window.EndPosition, session.Read().Fault);
        using var replay = Success(factory.PrepareReplay(input, WorldFixture.Options.Runtime, [content]));
        var result = replay.Advance(uint.MaxValue, session.Read().Position);
        Assert.AreEqual(ReplayStop.RequestedPosition, result.Stop);
        Assert.IsNull(result.Difference);
        Assert.AreEqual(saved.Encode(), replay.CaptureBoundary().Encode());

        var decoded = Success(factory.DecodeState(saved.Encode()));
        using var candidate = Success(factory.PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = candidate.Activate();
        var restoredCapture = Success(restored.Capture(CapturePurpose.Save));
        Assert.AreEqual(saved.Encode(), restoredCapture.Encode());
        Assert.IsInstanceOfType<CheckpointComparison.Equal>(Success(
            factory.CompareCheckpoints(saved.Checkpoint, restoredCapture.Checkpoint)));

        var left = session.Advance(20);
        var right = restored.Advance(20);
        Assert.IsNull(left.Fault);
        Assert.IsNull(right.Fault);
        CollectionAssert.AreEqual(left.Events.Select(envelope => envelope.Event.GetType().Name).ToArray(),
            right.Events.Select(envelope => envelope.Event.GetType().Name).ToArray());
        Assert.AreEqual(Success(session.Capture(CapturePurpose.Save)).Encode(),
            Success(restored.Capture(CapturePurpose.Save)).Encode());
    }

    private static SettlementState SettlementAt(CoreSession session, NodeId node)
    {
        var id = ((NodeOccupant.Settlement)session.State.World.Nodes.Single(item => item.Id == node).Occupant).Id;
        return SettlementWarFixture.Settlement(session, id);
    }

    private static T Success<T>(CoreResult<T> result) where T : notnull
        => ((CoreResult<T>.Success)result).Value;
}
