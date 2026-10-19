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
    internal static CoreContentProjection Content(bool armedBuilding = false, bool parked = false)
    {
        var content = CombatFixture.Content(armedBuilding: armedBuilding, builder: true, parked: parked);
        return content with
        {
            MarketRules = [content.MarketRules[0] with
            {
                RentableCapacityRatio = new(10000), LeaseDurationTicks = 1, RentMoneyPerCapacityHour = new(1)
            }, ..content.MarketRules.Skip(1)]
        };
    }

    internal static CoreContentProjection MoraleRules(CoreContentProjection content, double coefficient,
        double weaponLoss, double recovery, double threshold) => content with
    {
        CombatRules = content.CombatRules.Select(rule => rule.Id == WorldFixture.Rules.Combat
            ? rule with
            {
                MoraleDamageCoefficient = coefficient, WeaponDestroyedMoraleLoss = weaponLoss,
                MoraleRecoveryPerSecond = recovery, SurrenderThreshold = threshold
            }
            : rule).ToImmutableArray()
    };

    private static readonly object Gate = new();
    private static CoreState? _defaultState;

    internal static CoreSession Session(CoreContentProjection? content = null,
        ImmutableArray<GoodsQuantity> cargo = default, bool parked = false)
    {
        if (content is not null || !cargo.IsDefault || parked)
            return CombatFixture.SessionWithContent(content ?? Content(), cargo: cargo, parked: parked);
        lock (Gate)
        {
            if (_defaultState is null)
            {
                using var session = CombatFixture.SessionWithContent(Content());
                _defaultState = session.State;
            }
            return new CoreSession(_defaultState);
        }
    }

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

    internal static CoreSession MoveTrain(CoreSession session, NodeId node, bool servicing = false,
        TrainId? only = null)
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
                Trains = session.State.World.Trains.Select(train => only is { } id && train.Id != id
                    ? train
                    : train with
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
        => CombatFixture.Accepted(Submit(session, command));

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
}
