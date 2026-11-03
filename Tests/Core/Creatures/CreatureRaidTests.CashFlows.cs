using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Combat;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Sessions;
using Train.Core.World;
using Train.Core.Railway;
using Train.Core.Tests.Combat;
using Train.Core.Tests.World;
using static Train.Core.Tests.Creatures.CreatureTestSupport;

namespace Train.Core.Tests.Creatures;

public sealed partial class CreatureRaidTests
{
    [TestMethod]
    public void Clearing_the_lair_pays_once_and_orphans_groups_still_outside()
    {
        using var baseline = CombatFixture.Session(armedBuilding: false, damage: 10);
        var origin = baseline.State;
        var reward = new Money(40);
        var content = Tune(origin.Content, 10, 0, 1, Idle, null, maximum: 1) with
        {
            LairTypes = origin.Content.LairTypes.Select(type => type with { ClearanceReward = reward }).ToImmutableArray()
        };
        var world = StopTrains(StructureOnly(origin.World, 10, Tick));
        var home = EmptyNode(world);
        var away = EmptyNode(world, home);
        var (withLair, lair) = AddLair(world, home, content.LairTypes[0].Id);
        var engagement = new TrainEngagementRules(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0);
        world = PlaceTrain(withLair, new(home, null, null, null, null, NodeTravelPhase.Waiting, 0, null),
            TrainActivity.Stopped, 0, null, engagement);
        world = WithPacks(world,
            Pack(1, lair.Id, new WorldLocation.Node(home), null, CreatureActivity.Guarding, 0, null,
                Creature(1, "fixture:creature-a", 10)),
            Pack(2, lair.Id, new WorldLocation.Node(away), null, CreatureActivity.Wandering, 0, null,
                Creature(2, "fixture:creature-a", 10)));
        var money = world.Money.Value;
        using var session = Open(origin, world, content);
        var result = Step(session);
        Assert.IsFalse(session.State.World.Lairs.Any(item => item.Id == lair.Id));
        Assert.IsInstanceOfType<NodeOccupant.Empty>(session.State.World.Nodes.Single(node => node.Id == home).Occupant);
        Assert.AreEqual(money + reward.Value, session.State.World.Money.Value);
        var events = result.Events.Select(envelope => envelope.Event).ToArray();
        var cleared = events.OfType<CoreEvent.LairCleared>().Single();
        Assert.AreEqual(reward, cleared.Reward);
        var ended = events.OfType<CoreEvent.CombatEnded>().Single();
        Assert.AreEqual(reward, ended.Summary.Sides.Single(side => side.Owner is CombatOwner.Player).Reward);
        var orphan = State(session, 2);
        Assert.IsNull(orphan.Home);
        Assert.AreEqual(CreatureActivity.Wandering, orphan.Activity);
        Assert.IsNull(orphan.Objective);
    }

}
