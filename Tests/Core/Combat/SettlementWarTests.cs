using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Construction;
using Train.Core.Tests.Inventory;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class SettlementWarTests
{
    [TestMethod]
    public void Declare_war_changes_relation_and_ownership_and_reports_the_change()
    {
        using var original = SettlementWarFixture.Session(SettlementWarFixture.Content(parked: true), parked: true);
        var settlement = SettlementWarFixture.Settlement(original);
        var parked = settlement.ParkedVehicles.Single();
        using var session = SettlementWarFixture.MoveTrain(original,
            SettlementWarFixture.AwayNode(original, settlement));

        var result = SettlementWarFixture.Accept(session, new CoreCommand.DeclareWar(settlement.Id));
        var changed = result.Events.Select(envelope => envelope.Event)
            .OfType<CoreEvent.SettlementRelationChanged>().Single();
        Assert.AreEqual(new CoreEvent.SettlementRelationChanged(settlement.Id,
            SettlementRelation.NonHostile, SettlementRelation.Hostile), changed);
        Assert.AreEqual(SettlementRelation.Hostile,
            SettlementWarFixture.Settlement(session, settlement.Id).Relation);
        Assert.AreEqual(new NodeOccupant.Settlement(settlement.Id, SettlementRelation.Hostile),
            session.State.World.Nodes.Single(node => node.Id == settlement.Node).Occupant);
        foreach (var plot in settlement.Plots.Where(plot => plot.Building is not null))
            Assert.AreEqual(SettlementRelation.Hostile, session.State.World.Instances
                .Single(instance => instance.Id == new InstanceId.Building(plot.Building!.Value)).Ownership);
        Assert.AreEqual(SettlementRelation.Hostile, Instance(session, parked).Ownership);
        Assert.AreEqual(1, SettlementWarFixture.View(session, settlement.Id).MoraleRatio, 0);
        Assert.IsFalse(SettlementWarFixture.View(session, settlement.Id).Surrendered);
    }

    [TestMethod]
    public void Declare_war_rejects_wrong_relation_a_commission_and_a_player_vehicle_at_the_node()
    {
        using var hostile = CombatFixture.Session();
        var hostileSettlement = SettlementWarFixture.HostileSettlement(hostile);
        SettlementWarFixture.Reject(hostile, new CoreCommand.DeclareWar(hostileSettlement.Id),
            "invalid_state", "Settlement.Relation");

        using var withVehicle = SettlementWarFixture.Session();
        var target = SettlementWarFixture.Settlement(withVehicle);
        using var atNode = SettlementWarFixture.MoveTrain(withVehicle, target.Node);
        SettlementWarFixture.Reject(atNode, new CoreCommand.DeclareWar(target.Id),
            "invalid_state", "Settlement.Node");

        using var parkedSession = SettlementWarFixture.Session(SettlementWarFixture.Content(parked: true),
            parked: true);
        var parkedTarget = SettlementWarFixture.Settlement(parkedSession);
        var parkedVehicle = parkedTarget.ParkedVehicles.Single();
        using var playerParked = new CoreSession(parkedSession.State with
        {
            World = parkedSession.State.World with
            {
                Instances = parkedSession.State.World.Instances.Select(instance =>
                    instance.Id == new InstanceId.Vehicle(parkedVehicle)
                        ? instance with { Ownership = SettlementRelation.PlayerOwned } : instance)
                    .ToImmutableArray()
            }
        });
        using var movedParked = SettlementWarFixture.MoveTrain(playerParked,
            SettlementWarFixture.AwayNode(playerParked, parkedTarget));
        SettlementWarFixture.Reject(movedParked, new CoreCommand.DeclareWar(parkedTarget.Id),
            "invalid_state", "Settlement.Node");
        Assert.AreEqual(SettlementRelation.PlayerOwned, Instance(movedParked, parkedVehicle).Ownership);

        var content = CommissionFixture.Content();
        using var commissioned = CommissionFixture.Session(content);
        SettlementWarFixture.Accept(commissioned, CommissionFixture.Commission(content));
        var commissionedTarget = SettlementWarFixture.Settlement(commissioned);
        SettlementWarFixture.Reject(commissioned, new CoreCommand.DeclareWar(commissionedTarget.Id),
            "invalid_state", "Settlement.CommissionJobs");
    }

    [TestMethod]
    public void Declaring_war_freezes_the_local_lease_without_deleting_it()
    {
        using var original = SettlementWarFixture.Session();
        var settlement = SettlementWarFixture.Settlement(original);
        using var session = SettlementWarFixture.MoveTrain(original,
            SettlementWarFixture.AwayNode(original, settlement));
        var lease = SettlementWarFixture.Accept(session, new CoreCommand.AcquireLease(
            new LeaseRequest.Rent(settlement.Id, 10, 1), new(1000))).Events
            .Select(envelope => envelope.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;

        SettlementWarFixture.Accept(session, new CoreCommand.DeclareWar(settlement.Id));

        Assert.AreEqual(InventoryAccess.Frozen, InventoryFixture.ReadInventory(session, lease.Inventory).Access);
        Assert.AreEqual(lease.Id, InventoryFixture.Lease(session, lease.Id).Id);
    }

    [TestMethod]
    public void Occupation_requires_an_enemy_an_absent_combat_and_no_intact_weapon()
    {
        using var relation = SettlementWarFixture.Session();
        var target = SettlementWarFixture.Settlement(relation);
        SettlementWarFixture.Reject(relation, new CoreCommand.OccupySettlement(target.Id,
            SettlementWarFixture.Train(relation)), "invalid_state", "Settlement.Relation");

        using var original = SettlementWarFixture.Session();
        var settlement = SettlementWarFixture.Settlement(original);
        using var moved = SettlementWarFixture.MoveTrain(original,
            SettlementWarFixture.AwayNode(original, settlement));
        SettlementWarFixture.Accept(moved, new CoreCommand.DeclareWar(settlement.Id));
        SettlementWarFixture.Reject(moved, new CoreCommand.OccupySettlement(settlement.Id, new(999)),
            "object_missing", "Train");
        SettlementWarFixture.Reject(moved, new CoreCommand.OccupySettlement(settlement.Id,
            SettlementWarFixture.Train(moved)), "invalid_state", "Train.Position");

        using var armed = CombatFixture.Session();
        var hostileNode = CombatFixture.HostileNode(armed);
        var hostileSettlement = ((NodeOccupant.Settlement)armed.State.World.Nodes
            .Single(node => node.Id == hostileNode).Occupant).Id;
        var train = new TrainId(1);
        CombatFixture.Send(armed, hostileNode, train);
        CombatFixture.AdvanceUntil(armed, view => view.World.Combats.Any());
        SettlementWarFixture.Reject(armed, new CoreCommand.OccupySettlement(hostileSettlement, train),
            "invalid_state", "Settlement.Node");

        using var defended = SettlementWarFixture.Session(SettlementWarFixture.Content(armedBuilding: true));
        var defendedSettlement = SettlementWarFixture.HostileSettlement(defended);
        using var defendedAtNode = SettlementWarFixture.MoveTrain(defended, defendedSettlement.Node);
        SettlementWarFixture.Reject(defendedAtNode, new CoreCommand.OccupySettlement(defendedSettlement.Id,
            SettlementWarFixture.Train(defendedAtNode)), "conditions_not_met", "Settlement");
    }

    [TestMethod]
    public void A_parked_npc_vehicle_is_transferred_on_occupation_and_blocks_it_until_surrender()
    {
        using var original = SettlementWarFixture.Session(SettlementWarFixture.Content(parked: true),
            parked: true);
        var settlement = SettlementWarFixture.Settlement(original);
        var parked = settlement.ParkedVehicles.Single();
        using var away = SettlementWarFixture.MoveTrain(original,
            SettlementWarFixture.AwayNode(original, settlement));
        SettlementWarFixture.Accept(away, new CoreCommand.DeclareWar(settlement.Id));
        Assert.AreEqual(SettlementRelation.Hostile, Instance(away, parked).Ownership);
        using var atNode = SettlementWarFixture.MoveTrain(away, settlement.Node);
        SettlementWarFixture.Reject(atNode, new CoreCommand.OccupySettlement(settlement.Id,
            SettlementWarFixture.Train(atNode)), "conditions_not_met", "Settlement");

        using var surrendered = new CoreSession(atNode.State with
        {
            World = atNode.State.World with
            {
                Settlements = atNode.State.World.Settlements.Select(item => item.Id == settlement.Id
                    ? item with { Surrendered = true } : item).ToImmutableArray()
            }
        });
        SettlementWarFixture.Accept(surrendered,
            new CoreCommand.OccupySettlement(settlement.Id, SettlementWarFixture.Train(surrendered)));

        Assert.AreEqual(SettlementRelation.PlayerOwned, Instance(surrendered, parked).Ownership);
        Assert.IsFalse(SettlementWarFixture.View(surrendered, settlement.Id).Surrendered);
    }

    [TestMethod]
    public void Surrender_ends_the_combat_in_the_same_step_and_keeps_the_settlement()
    {
        var content = SettlementWarFixture.MoraleRules(CombatFixture.Content(armedBuilding: true, damage: 100),
            coefficient: 10, weaponLoss: 0, recovery: 0, threshold: .9);
        content = content with
        {
            Designs = content.Designs.Select(design => design.Design == CombatFixture.ArmedBuilding
                ? design with
                {
                    MaxStructureDurability = 1000,
                    Parts = design.Parts.Select(part => part with { MaxDurability = 1000 }).ToImmutableArray()
                }
                : design).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content, vehicles: 2);
        var hostile = CombatFixture.HostileNode(session);
        var settlement = ((NodeOccupant.Settlement)session.State.World.Nodes
            .Single(node => node.Id == hostile).Occupant).Id;
        var train = SettlementWarFixture.Train(session);
        var station = session.State.World.Settlements.Single(item => item.Id == settlement).Plots
            .Single(plot => plot.TemplateNumber == 1).Building!.Value;
        var armed = session.State.World.Settlements.Single(item => item.Id == settlement).Plots
            .Single(plot => plot.TemplateNumber == 2).Building!.Value;
        using var atNode = SettlementWarFixture.MoveTrain(session, hostile);

        CoreEvent.SettlementSurrendered? surrendered = null;
        for (var step = 0; step < 1000; step++)
        {
            var before = atNode.State.World.Combats.SingleOrDefault()?.Id;
            var result = atNode.Advance(1);
            Assert.IsNull(result.Fault, result.Diagnostic?.Message);
            var events = result.Events.Select(envelope => envelope.Event).ToArray();
            var found = events.OfType<CoreEvent.SettlementSurrendered>().ToArray();
            Assert.IsTrue(found.Length <= 1);
            if (found.Length == 0) continue;
            surrendered = found.Single();
            var combat = events.OfType<CoreEvent.CombatStarted>().SingleOrDefault()?.Combat ?? before;
            Assert.AreEqual(settlement, surrendered.Settlement);
            Assert.AreEqual(combat, surrendered.Combat);
            var index = Array.IndexOf(events, surrendered);
            Assert.IsTrue(events.Skip(index).Any(value => value is CoreEvent.CombatEnded));
            Assert.IsEmpty(atNode.Read().World.Combats);
            Assert.IsTrue(SettlementWarFixture.View(atNode, settlement).Surrendered);
            Assert.IsInstanceOfType<Lookup<InstanceView>.Found>(atNode.Read().FindInstance(
                new InstanceId.Building(station)));
            Assert.IsTrue(Instance(atNode, armed).Parts.All(part => part.Current > 0));
            break;
        }
        Assert.IsNotNull(surrendered);

        var morale = SettlementWarFixture.Settlement(atNode, settlement).Morale;
        var durability = Instance(atNode, armed).StructureDurability;
        var occupation = SettlementWarFixture.Accept(atNode,
            new CoreCommand.OccupySettlement(settlement, train));
        var occupationEvents = occupation.Events.Select(envelope => envelope.Event).ToArray();
        Assert.AreEqual(new CoreEvent.SettlementRelationChanged(settlement,
            SettlementRelation.Hostile, SettlementRelation.PlayerOwned),
            occupationEvents.OfType<CoreEvent.SettlementRelationChanged>().Single());
        Assert.IsFalse(occupationEvents.Any(value => value is CoreEvent.LeaseExpired));
        Assert.AreEqual(morale, SettlementWarFixture.Settlement(atNode, settlement).Morale, 0);
        Assert.AreEqual(durability, Instance(atNode, armed).StructureDurability);
        Assert.IsFalse(SettlementWarFixture.View(atNode, settlement).Surrendered);
        Assert.AreEqual(SettlementRelation.PlayerOwned,
            SettlementWarFixture.Settlement(atNode, settlement).Relation);
    }

    [TestMethod]
    public void A_trade_rule_referencing_a_merged_lease_reports_the_reason_without_faulting()
    {
        using var original = SettlementWarFixture.Session(SettlementWarFixture.Content(),
            [new GoodsQuantity(ConstructionFixture.Material, 5)]);
        var settlement = SettlementWarFixture.Settlement(original);
        var train = SettlementWarFixture.Train(original);
        using var atSettlement = SettlementWarFixture.MoveTrain(original, settlement.Node, servicing: true);
        var lease = SettlementWarFixture.Accept(atSettlement, new CoreCommand.AcquireLease(
            new LeaseRequest.Rent(settlement.Id, 10, 1), new(1000))).Events
            .Select(envelope => envelope.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        SettlementWarFixture.Accept(atSettlement, new CoreCommand.UnloadGoods(train, settlement.Id,
            lease.Inventory, ConstructionFixture.Material, 5));
        using var moved = SettlementWarFixture.MoveTrain(atSettlement,
            SettlementWarFixture.AwayNode(atSettlement, settlement));
        SettlementWarFixture.Accept(moved, new CoreCommand.DeclareWar(settlement.Id));
        using var atNode = SettlementWarFixture.MoveTrain(moved, settlement.Node, servicing: true);
        SettlementWarFixture.Accept(atNode, new CoreCommand.OccupySettlement(settlement.Id, train));

        var result = SettlementWarFixture.Accept(atNode, new CoreCommand.ReplaceRunPlan(train, new(false,
            [new PlanStop.Station(settlement.Node, 1, [new AutomaticGoodsRule(new AutomaticGoodsAction.Load(),
                ConstructionFixture.Material, lease.Inventory, 5)])])));
        var evaluated = result.Events.Select(envelope => envelope.Event)
            .OfType<CoreEvent.AutomaticTradeEvaluated>().Last();
        var incomplete = evaluated.Results.Single().IncompleteReasons.Single();

        Assert.AreEqual("object_missing", incomplete.Code);
        Assert.AreEqual("LocalInventory", incomplete.Field);
        Assert.IsNull(atNode.Read().Fault);
    }

    [TestMethod]
    public void Occupation_transfers_plots_population_leases_and_inventory_without_refunding_rent()
    {
        using var original = SettlementWarFixture.Session(SettlementWarFixture.Content(),
            [new GoodsQuantity(ConstructionFixture.Material, 5)]);
        var settlement = SettlementWarFixture.Settlement(original);
        var train = SettlementWarFixture.Train(original);
        using var atSettlement = SettlementWarFixture.MoveTrain(original, settlement.Node, servicing: true);
        var moneyBeforeLease = atSettlement.State.World.Money;
        var lease = SettlementWarFixture.Accept(atSettlement, new CoreCommand.AcquireLease(
            new LeaseRequest.Rent(settlement.Id, 10, 1), new(1000))).Events
            .Select(envelope => envelope.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        SettlementWarFixture.Accept(atSettlement, new CoreCommand.UnloadGoods(train, settlement.Id,
            lease.Inventory, ConstructionFixture.Material, 5));
        Assert.AreEqual(5, InventoryFixture.ReadInventory(atSettlement, lease.Inventory).Used);
        using var moved = SettlementWarFixture.MoveTrain(atSettlement,
            SettlementWarFixture.AwayNode(atSettlement, settlement));
        var expired = moved.Advance(1);
        Assert.IsNull(expired.Fault,
            $"{expired.Diagnostic?.Message}\n{expired.Diagnostic?.StackTrace}");
        Assert.AreEqual(lease.Id,
            expired.Events.Select(envelope => envelope.Event).OfType<CoreEvent.LeaseExpired>().Single().Lease);
        Assert.IsTrue(InventoryFixture.Lease(moved, lease.Id).Expired);
        var inventoryBefore = InventoryFixture.ReadInventory(moved, settlement.Inventory).Used;
        var populationBefore = SettlementWarFixture.Settlement(moved, settlement.Id).PopulationState;
        var fuelBefore = SettlementWarFixture.Settlement(moved, settlement.Id).FuelEnergyRemaining;
        SettlementWarFixture.Accept(moved, new CoreCommand.DeclareWar(settlement.Id));
        var moneyBeforeOccupation = moved.State.World.Money;
        using var atNode = SettlementWarFixture.MoveTrain(moved, settlement.Node);

        var occupation = SettlementWarFixture.Accept(atNode,
            new CoreCommand.OccupySettlement(settlement.Id, train));

        var after = SettlementWarFixture.Settlement(atNode, settlement.Id);
        Assert.IsFalse(occupation.Events.Select(envelope => envelope.Event)
            .Any(value => value is CoreEvent.LeaseExpired));
        Assert.AreEqual(SettlementRelation.PlayerOwned, after.Relation);
        Assert.AreEqual(new NodeOccupant.Settlement(settlement.Id, SettlementRelation.PlayerOwned),
            atNode.State.World.Nodes.Single(node => node.Id == settlement.Node).Occupant);
        foreach (var plot in settlement.Plots.Where(plot => plot.Building is not null))
            Assert.AreEqual(SettlementRelation.PlayerOwned, atNode.State.World.Instances
                .Single(instance => instance.Id == new InstanceId.Building(plot.Building!.Value)).Ownership);
        Assert.AreEqual(populationBefore, after.PopulationState);
        Assert.AreEqual(fuelBefore, after.FuelEnergyRemaining);
        Assert.AreEqual(settlement.Plots, after.Plots);
        Assert.AreEqual(moneyBeforeOccupation, atNode.State.World.Money);
        Assert.IsTrue(moneyBeforeOccupation.Value < moneyBeforeLease.Value);
        Assert.IsInstanceOfType<Lookup<LeaseView>.Missing>(atNode.Read().FindLease(lease.Id));
        Assert.IsInstanceOfType<Lookup<InventoryView>.Missing>(atNode.Read().FindInventory(lease.Inventory));
        Assert.AreEqual(inventoryBefore + 5,
            InventoryFixture.ReadInventory(atNode, after.Inventory).Used);
    }

    private static InstanceState Instance(CoreSession session, BuildingId building)
        => session.State.World.Instances.Single(item => item.Id == new InstanceId.Building(building));

    private static InstanceState Instance(CoreSession session, VehicleId vehicle)
        => session.State.World.Instances.Single(item => item.Id == new InstanceId.Vehicle(vehicle));
}
