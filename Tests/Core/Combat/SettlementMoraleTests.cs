using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Creatures;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Creatures.CreatureTestSupport;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class SettlementMoraleTests
{
    [TestMethod]
    public void Structure_damage_lowers_morale_by_the_share_of_the_step_start_cap()
    {
        var content = SettlementWarFixture.MoraleRules(CombatFixture.Content(armedBuilding: true, damage: 10),
            coefficient: 1, weaponLoss: 0, recovery: 0, threshold: 0);
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2);
        var settlement = Hostile(session);
        var cap = settlement.Plots.Where(plot => plot.Building is not null)
            .Select(plot => content.Designs.Single(design => design.Design == session.State.World.Instances
                .Single(instance => instance.Id == new InstanceId.Building(plot.Building!.Value)).Design)
                .MaxStructureDurability)
            .Sum();
        using var atNode = SettlementWarFixture.MoveTrain(session, settlement.Node);

        atNode.Advance(1);

        var expected = 1 - 2 * 10 / (double)cap;
        Assert.AreEqual(expected, SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-9);
        Assert.IsFalse(SettlementWarFixture.View(atNode, settlement.Id).Surrendered);
    }

    [TestMethod]
    public void A_weapon_zeroed_with_its_unit_in_the_same_step_is_counted_once()
    {
        var content = CombatFixture.Content(armedBuilding: true, damage: 100);
        content = content with
        {
            Designs = content.Designs.Select(design => design.Design == CombatFixture.ArmedBuilding
                ? design with
                {
                    MaxStructureDurability = 100, StructureHitWeight = 0,
                    Parts = design.Parts.Select(part => part with
                        { MaxDurability = 50, HitWeight = 1 }).ToImmutableArray()
                }
                : design).ToImmutableArray()
        };
        content = SettlementWarFixture.MoraleRules(content, coefficient: 0, weaponLoss: .25, recovery: 0, threshold: .5);
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2);
        var settlement = Hostile(session);
        using var atNode = SettlementWarFixture.MoveTrain(session, settlement.Node);

        var result = atNode.Advance(1);

        var destruction = result.Events.Select(envelope => envelope.Event)
            .OfType<CoreEvent.CombatDestruction>().Single();
        Assert.IsFalse(destruction.Parts.IsEmpty);
        Assert.IsFalse(destruction.Units.IsEmpty);
        Assert.AreEqual(1 - .25, SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-9);
        Assert.IsFalse(SettlementWarFixture.View(atNode, settlement.Id).Surrendered);
    }

    [TestMethod]
    [TestCategory("integration")]
    public void A_surrendered_settlement_recovers_and_reengages_when_morale_reaches_the_threshold()
    {
        var content = SettlementWarFixture.MoraleRules(CombatFixture.Content(armedBuilding: true, damage: 100),
            coefficient: 0, weaponLoss: .95, recovery: .6, threshold: .9);
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2);
        var settlement = Hostile(session);
        var train = session.State.World.Trains.Single().Id;
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)),
            CommandSource.AutomationTest));
        using var atNode = SettlementWarFixture.MoveTrain(session, settlement.Node);

        var surrender = false;
        for (var step = 0; step < 10 && !surrender; step++)
        {
            atNode.Advance(1);
            surrender = SettlementWarFixture.View(atNode, settlement.Id).Surrendered;
        }
        Assert.IsTrue(surrender, "聚居地未投降。");
        var morale = SettlementWarFixture.Settlement(atNode, settlement.Id).Morale;
        Assert.AreEqual(1 - .95, morale, 1e-9);

        atNode.Advance(1);
        Assert.AreEqual(morale + .01, SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-9);
        Assert.IsTrue(SettlementWarFixture.View(atNode, settlement.Id).Surrendered);
        Assert.IsEmpty(atNode.Read().World.Combats);

        var released = false;
        for (var step = 0; step < 200 && !released; step++)
        {
            var result = atNode.Advance(1);
            var after = SettlementWarFixture.Settlement(atNode, settlement.Id);
            if (after.Surrendered) continue;
            released = true;
            Assert.IsTrue(after.Morale >= .9);
            Assert.IsTrue(result.Events.Select(envelope => envelope.Event).Any(value => value is CoreEvent.CombatStarted));
        }
        Assert.IsTrue(released, "聚居地未解除投降。");
    }

    [TestMethod]
    public void A_player_settlement_loses_morale_but_does_not_surrender()
    {
        var baseline = CombatWorld;
        var settlement = Friendly(baseline.World);
        var primary = Animal(baseline.Content, "fixture:creature-a", 1000, 0, 1, Idle,
            new PartCapability.Weapon(10, Tick));
        var secondary = Animal(baseline.Content, "fixture:creature-b", 1000, 0, 1, Idle, null);
        var content = SettlementWarFixture.MoraleRules(Lairs(baseline.Content, primary, secondary, 1, Idle, Idle, 0, 0),
            coefficient: 1, weaponLoss: .25, recovery: 0, threshold: .5);
        var world = PlayerOwned(StopTrains(baseline.World), settlement.Id);
        world = world with { Trains = [] };
        var (withLair, lair) = AddLair(world, EmptyNode(world, settlement.Node), content.LairTypes[0].Id);
        world = WithPacks(withLair, Pack(1, lair.Id, new WorldLocation.Node(settlement.Node), null,
            CreatureActivity.RaidingSettlement, 0, new CreatureObjective.Settlement(settlement.Node),
            Creature(1, "fixture:creature-a", 1000)));
        using var session = Open(baseline, world, content);

        Step(session);

        var cap = content.Designs.Single(design => design.Design == session.State.World.Instances
            .Single(instance => instance.Id == new InstanceId.Building(settlement.Plots
                .Single(plot => plot.Building is not null).Building!.Value)).Design).MaxStructureDurability;
        Assert.AreEqual(1 - 10 / (double)cap, SettlementWarFixture.Settlement(session, settlement.Id).Morale, 1e-9);
        Assert.IsFalse(SettlementWarFixture.View(session, settlement.Id).Surrendered);
    }

    [TestMethod]
    public void Missing_combat_rule_leaves_morale_and_surrender_unchanged()
    {
        using var original = CombatFixture.SessionWithContent(
            CombatFixture.Content(armedBuilding: true, damage: 100), vehicles: 2);
        var content = original.State.Content with
        {
            CombatRules = original.State.Content.CombatRules
                .Where(rule => rule.Id != WorldFixture.Rules.Combat).ToImmutableArray()
        };
        using var session = new CoreSession(original.State with { Content = content });
        var settlement = Hostile(session);
        using var atNode = SettlementWarFixture.MoveTrain(session, settlement.Node);

        atNode.Advance(1);

        Assert.IsFalse(SettlementWarFixture.Settlement(atNode, settlement.Id).Surrendered);
        Assert.AreEqual(1, SettlementWarFixture.View(atNode, settlement.Id).MoraleRatio, 0);
    }

    private static SettlementState Hostile(CoreSession session)
    {
        var node = CombatFixture.HostileNode(session);
        var id = ((NodeOccupant.Settlement)session.State.World.Nodes.Single(item => item.Id == node).Occupant).Id;
        return SettlementWarFixture.Settlement(session, id);
    }
}
