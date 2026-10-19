using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Combat;

internal static class SettlementWarFixture
{
    internal static CoreContentProjection Content(bool armedBuilding = false)
    {
        var content = CombatFixture.Content(armedBuilding: armedBuilding, builder: true);
        return content with
        {
            MarketRules = [content.MarketRules[0] with
            {
                RentableCapacityRatio = new(10000), LeaseDurationTicks = 1, RentMoneyPerCapacityHour = new(1)
            }, ..content.MarketRules.Skip(1)]
        };
    }

    internal static CoreSession Session(CoreContentProjection? content = null,
        ImmutableArray<GoodsQuantity> cargo = default)
        => CombatFixture.SessionWithContent(content ?? Content(), cargo: cargo);

    internal static SettlementState Settlement(CoreSession session)
        => session.State.World.Settlements.Where(item => item.Relation == SettlementRelation.NonHostile)
            .OrderBy(item => item.Id.Value).First();

    internal static SettlementState Settlement(CoreSession session, SettlementId id)
        => session.State.World.Settlements.Single(item => item.Id == id);

    internal static SettlementState HostileSettlement(CoreSession session)
        => session.State.World.Settlements.Where(item => item.Relation == SettlementRelation.Hostile)
            .OrderBy(item => item.Id.Value).First();

    internal static TrainId Train(CoreSession session) => session.State.World.Trains.Single().Id;

    internal static NodeId AwayNode(CoreSession session, SettlementState settlement)
        => session.State.World.Nodes.Where(node => node.Id != settlement.Node
            && node.Occupant is NodeOccupant.Empty).OrderBy(node => node.Id.Value).First().Id;

    internal static CoreSession MoveTrain(CoreSession session, NodeId node, bool servicing = false)
    {
        var platforms = session.State.World.Nodes.Single(item => item.Id == node).Platforms;
        var position = servicing && platforms > 0
            ? WorldFixture.Node(node, NodeTravelPhase.Docked,
                WorldFixture.ApproachLength + WorldFixture.PlatformLength / 2, platform: 1)
            : WorldFixture.Node(node);
        return new(session.State with
        {
            World = session.State.World with
            {
                Trains = session.State.World.Trains.Select(train => train with
                {
                    Position = position,
                    Activity = servicing ? TrainActivity.Servicing : TrainActivity.Stopped,
                    SpeedMetersPerSecond = 0,
                    Plan = null,
                    NextStopIndex = null,
                    RemainingDwellSteps = 0,
                    StopRequested = false,
                    QueueEnteredStep = null,
                    RetreatDestination = null
                }).ToImmutableArray()
            }
        });
    }

    internal static SubmissionResult.Completed Submit(ICoreSession session, CoreCommand command)
        => (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);

    internal static SubmissionResult.Completed Accept(ICoreSession session, CoreCommand command)
    {
        var result = Submit(session, command);
        if (result.Receipt.Decision is CommandDecision.Rejected rejected)
            Assert.Fail(string.Join("; ", rejected.Problems.Select(problem =>
                $"{problem.Code} {problem.Field} [{string.Join(",", problem.RelatedIdentities)}]")));
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        return result;
    }

    internal static void Reject(CoreSession session, CoreCommand command, string code, string field)
    {
        var before = session.State.World;
        var result = Submit(session, command);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        Assert.AreEqual(code, rejected.Problems.Single().Code);
        Assert.AreEqual(field, rejected.Problems.Single().Field);
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.AreEqual(before, session.State.World);
    }

    internal static SettlementView View(ICoreSession session, SettlementId settlement)
        => ((Lookup<SettlementView>.Found)session.Read().FindSettlement(settlement)).Value;

    internal static InventoryView Inventory(ICoreSession session, InventoryId inventory)
        => ((Lookup<InventoryView>.Found)session.Read().FindInventory(inventory)).Value;

    internal static LeaseView Lease(ICoreSession session, LeaseId lease)
        => ((Lookup<LeaseView>.Found)session.Read().FindLease(lease)).Value;
}
