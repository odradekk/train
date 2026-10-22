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
        content = content with
        {
            Designs = content.Designs.Select(design => design.Design switch
            {
                var hash when hash == CombatFixture.ArmedBuilding => design with
                {
                    MaxStructureDurability = 1000, StructureHitWeight = 1,
                    Parts = design.Parts.Select(part => part with { HitWeight = 0 }).ToImmutableArray()
                },
                var hash when hash == CombatFixture.ArmedVehicle => design with
                {
                    Parts = design.Parts.Select(part => part with
                    {
                        Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                            ? (PartCapability)new PartCapability.Weapon(10, 1d / 60) : capability).ToImmutableArray()
                    }).ToImmutableArray()
                },
                _ => design
            }).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2);
        var settlement = Hostile(session);
        var cap = BuildingCap(session, settlement);
        using var atNode = SettlementWarFixture.MoveTrain(session, settlement.Node);

        var first = atNode.Advance(1);
        Assert.IsNull(first.Fault, first.Diagnostic?.Message);
        Assert.IsTrue(CurrentStructure(atNode, settlement) < cap);
        Assert.AreEqual(1 - 20d / cap, SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-9);

        var second = atNode.Advance(1);
        Assert.IsNull(second.Fault, second.Diagnostic?.Message);

        Assert.AreEqual(1 - 40d / cap, SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-9);
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
        content = SettlementWarFixture.MoraleRules(content, coefficient: 1, weaponLoss: .25, recovery: 0, threshold: 0);
        using var original = CombatFixture.SessionWithContent(content, vehicles: 1);
        var settlement = Hostile(original);
        var cap = BuildingCap(original, settlement);
        var building = ArmedBuilding(original, settlement);
        using var session = new CoreSession(original.State with
        {
            World = original.State.World with
            {
                Instances = original.State.World.Instances.Select(instance =>
                    instance.Id == new InstanceId.Building(building)
                        ? instance with { StructureDurability = 20 } : instance).ToImmutableArray()
            }
        });
        using var atNode = SettlementWarFixture.MoveTrain(session, settlement.Node);

        var result = atNode.Advance(1);

        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        var events = result.Events.Select(envelope => envelope.Event).ToArray();
        var destruction = events.OfType<CoreEvent.CombatDestruction>().Single();
        Assert.IsFalse(destruction.Parts.IsEmpty);
        Assert.IsTrue(destruction.Units.OfType<DestroyedUnit.Instance>()
            .Any(unit => unit.Value.Instance == new InstanceId.Building(building)));
        Assert.AreEqual(1 - 70d / cap - .25,
            SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-9);
        Assert.IsFalse(SettlementWarFixture.View(atNode, settlement.Id).Surrendered);
    }

    [TestMethod]
    public void Damage_to_a_parked_npc_vehicle_lowers_morale_by_the_building_cap_share()
    {
        var content = CombatFixture.Content(armedBuilding: false, damage: 10, parked: true);
        content = content with
        {
            Designs = content.Designs.Select(design => design.Design == CombatFixture.ParkedVehicle
                ? design with
                {
                    MaxStructureDurability = 100, StructureHitWeight = 1,
                    Parts = design.Parts.Select(part => part with { HitWeight = 0 }).ToImmutableArray()
                }
                : design).ToImmutableArray()
        };
        content = SettlementWarFixture.MoraleRules(content, coefficient: 1, weaponLoss: 0, recovery: 0, threshold: 0);
        using var original = CombatFixture.SessionWithContent(content);
        var design = original.State.Content.Designs.Single(item => item.Design == CombatFixture.ParkedVehicle);
        var target = original.State.World.Settlements
            .Where(item => item.Relation == SettlementRelation.Hostile)
            .OrderBy(item => item.Id.Value).First();
        var world = original.State.World;
        var vehicle = new VehicleId(world.Next.Vehicle);
        var instance = WorldState.CreateInstance(new InstanceId.Vehicle(vehicle), design,
            SettlementRelation.Hostile, new InstanceDetails.Vehicle(SizeClass.Small, null));
        world = world with
        {
            Designs = world.Designs.Add(design).OrderBy(item => item.Design.Value, StringComparer.Ordinal)
                .ToImmutableArray(),
            Instances = world.Instances.Add(instance).OrderBy(item => item.Id is InstanceId.Building ? 0 : 1)
                .ThenBy(item => item.Id is InstanceId.Building building
                    ? building.Id.Value : ((InstanceId.Vehicle)item.Id).Id.Value).ToImmutableArray(),
            Settlements = world.Settlements.Select(item => item.Id == target.Id
                ? item with
                {
                    ParkedVehicles = item.ParkedVehicles.Add(vehicle)
                        .OrderBy(id => id.Value).ToImmutableArray()
                }
                : item).ToImmutableArray(),
            Next = world.Next with { Vehicle = checked(world.Next.Vehicle + 1) }
        };
        using var session = new CoreSession(original.State with { World = world });
        var settlement = SettlementWarFixture.Settlement(session, target.Id);
        var cap = BuildingCap(session, settlement);
        var train = session.State.World.Trains.Single().Id;
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)),
            CommandSource.AutomationTest));
        using var atNode = SettlementWarFixture.MoveTrain(session, settlement.Node);

        var result = atNode.Advance(1);

        Assert.IsNull(result.Fault,
            $"{result.Diagnostic?.Message}\n{result.Diagnostic?.StackTrace}");
        Assert.AreEqual(90L, atNode.State.World.Instances
            .Single(item => item.Id == new InstanceId.Vehicle(vehicle)).StructureDurability);
        Assert.AreEqual(1 - 10d / cap,
            SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-9);
    }

    [TestMethod]
    public void A_surrendered_settlement_does_not_fire_or_recover_while_the_hostile_train_is_at_its_node()
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
        var vehicles = Vehicles(atNode);

        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        for (var step = 0; step < 200; step++)
        {
            var result = atNode.Advance(1);
            Assert.IsNull(result.Fault, result.Diagnostic?.Message);
            events.AddRange(result.Events.Select(envelope => envelope.Event));
        }
        Assert.AreEqual(morale, SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-12);
        Assert.IsTrue(SettlementWarFixture.View(atNode, settlement.Id).Surrendered);
        Assert.IsEmpty(atNode.Read().World.Combats);
        Assert.IsFalse(events.Any(value => value is CoreEvent.CombatStarted or CoreEvent.SettlementSurrendered));
        CollectionAssert.AreEqual(vehicles, Vehicles(atNode));

        using var left = SettlementWarFixture.MoveTrain(atNode,
            SettlementWarFixture.AwayNode(atNode, settlement));
        var released = 0;
        while (released < 200)
        {
            released++;
            var result = left.Advance(1);
            Assert.IsNull(result.Fault, result.Diagnostic?.Message);
            if (!SettlementWarFixture.Settlement(left, settlement.Id).Surrendered) break;
        }
        Assert.AreEqual(85, released, "解除投降的步数与阈值不符。");
        Assert.IsTrue(SettlementWarFixture.Settlement(left, settlement.Id).Morale >= .9);
        Assert.IsEmpty(left.Read().World.Combats);
    }

    [TestMethod]
    public void A_creature_group_at_the_node_freezes_recovery_until_it_leaves()
    {
        var baseline = CombatWorld;
        var unarmed = Animal(baseline.Content, "fixture:creature-b", 1000, 0, 1, Idle, null);
        var content = SettlementWarFixture.MoraleRules(
            Lairs(baseline.Content, unarmed, unarmed, 1, Idle, Idle, 0, 0),
            coefficient: 0, weaponLoss: 0, recovery: .6, threshold: .9);
        var settlement = baseline.World.Settlements.Where(item => item.Relation == SettlementRelation.Hostile)
            .OrderBy(item => item.Id.Value).First();
        var world = baseline.World with
        {
            Trains = [],
            Settlements = baseline.World.Settlements.Select(item => item.Id == settlement.Id
                ? item with { Morale = .5, Surrendered = true } : item).ToImmutableArray()
        };
        var (withLair, lair) = AddLair(world, EmptyNode(world, settlement.Node), content.LairTypes[0].Id);
        using var raided = Open(baseline, WithPacks(withLair,
            Pack(1, lair.Id, new WorldLocation.Node(settlement.Node), null,
                CreatureActivity.RaidingSettlement, 0, new CreatureObjective.Settlement(settlement.Node),
                Creature(1, "fixture:creature-b", 1000))), content);

        Step(raided);
        Assert.AreEqual(.5, SettlementWarFixture.Settlement(raided, settlement.Id).Morale, 1e-12);
        Assert.IsTrue(SettlementWarFixture.View(raided, settlement.Id).Surrendered);
        Assert.IsEmpty(raided.Read().World.Combats);

        using var cleared = Open(baseline, withLair, content);
        Step(cleared);
        Assert.AreEqual(.51, SettlementWarFixture.Settlement(cleared, settlement.Id).Morale, 1e-9);
    }

    [TestMethod]
    public void A_non_hostile_settlement_recovers_while_a_player_train_is_at_its_node()
    {
        var content = SettlementWarFixture.MoraleRules(SettlementWarFixture.Content(),
            coefficient: 0, weaponLoss: 0, recovery: .6, threshold: .9);
        using var original = CombatFixture.SessionWithContent(content);
        var settlement = SettlementWarFixture.Settlement(original);
        var world = original.State.World with
        {
            Settlements = original.State.World.Settlements.Select(item => item.Id == settlement.Id
                ? item with { Morale = .5 } : item).ToImmutableArray()
        };
        using var atNode = SettlementWarFixture.MoveTrain(
            new CoreSession(original.State with { World = world }), settlement.Node);

        var result = atNode.Advance(1);

        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        Assert.AreEqual(.51, SettlementWarFixture.Settlement(atNode, settlement.Id).Morale, 1e-9);
        Assert.IsEmpty(atNode.Read().World.Combats);
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
            coefficient: 1, weaponLoss: .25, recovery: 0, threshold: .95);
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
        var morale = SettlementWarFixture.Settlement(session, settlement.Id).Morale;
        var rule = content.CombatRules.Single(item => item.Id == WorldFixture.Rules.Combat);
        Assert.AreEqual(1 - 10 / (double)cap, morale, 1e-9);
        Assert.IsTrue(morale < rule.SurrenderThreshold);
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

        var result = atNode.Advance(1);

        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        Assert.IsTrue(result.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatHits>().Any());
        Assert.IsFalse(SettlementWarFixture.Settlement(atNode, settlement.Id).Surrendered);
        Assert.AreEqual(1, SettlementWarFixture.View(atNode, settlement.Id).MoraleRatio, 0);
    }

    [TestMethod]
    public void Missing_combat_rule_leaves_recovery_and_surrender_unchanged()
    {
        using var original = SettlementWarFixture.Session();
        var settlement = SettlementWarFixture.Settlement(original);
        using var away = SettlementWarFixture.MoveTrain(original,
            SettlementWarFixture.AwayNode(original, settlement));
        SettlementWarFixture.Accept(away, new CoreCommand.DeclareWar(settlement.Id));
        var world = away.State.World with
        {
            Settlements = away.State.World.Settlements.Select(item => item.Id == settlement.Id
                ? item with { Morale = .5, Surrendered = true } : item).ToImmutableArray()
        };
        var content = away.State.Content with
        {
            CombatRules = away.State.Content.CombatRules
                .Where(rule => rule.Id != WorldFixture.Rules.Combat).ToImmutableArray()
        };
        using var session = new CoreSession(away.State with { World = world, Content = content });

        var result = session.Advance(1);

        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        Assert.AreEqual(.5, SettlementWarFixture.Settlement(session, settlement.Id).Morale, 0);
        Assert.IsTrue(SettlementWarFixture.View(session, settlement.Id).Surrendered);
    }

    private static SettlementState Hostile(CoreSession session)
    {
        var node = CombatFixture.HostileNode(session);
        var id = ((NodeOccupant.Settlement)session.State.World.Nodes.Single(item => item.Id == node).Occupant).Id;
        return SettlementWarFixture.Settlement(session, id);
    }

    private static long BuildingCap(CoreSession session, SettlementState settlement)
        => settlement.Plots.Where(plot => plot.Building is not null)
            .Sum(plot => Design(session, plot.Building!.Value).MaxStructureDurability);

    private static string[] Vehicles(CoreSession session)
    {
        var world = session.State.World;
        return world.Trains.Single().Vehicles.Select(vehicle =>
        {
            var instance = world.Instances.Single(item => item.Id == new InstanceId.Vehicle(vehicle));
            return $"{instance.StructureDurability}/{string.Join(",", instance.Parts.Select(part => part.Current))}";
        }).ToArray();
    }

    private static long CurrentStructure(CoreSession session, SettlementState settlement)
        => settlement.Plots.Where(plot => plot.Building is not null)
            .Sum(plot => Math.Min(session.State.World.Instances.Single(item =>
                    item.Id == new InstanceId.Building(plot.Building!.Value)).StructureDurability,
                Design(session, plot.Building!.Value).MaxStructureDurability));

    private static BuildingId ArmedBuilding(CoreSession session, SettlementState settlement)
        => settlement.Plots.Where(plot => plot.Building is not null)
            .First(plot => Design(session, plot.Building!.Value).Design == CombatFixture.ArmedBuilding).Building!.Value;

    private static DesignProjection Design(CoreSession session, BuildingId building)
        => session.State.World.Designs.Single(item => item.Design == session.State.World.Instances
            .Single(instance => instance.Id == new InstanceId.Building(building)).Design);
}
