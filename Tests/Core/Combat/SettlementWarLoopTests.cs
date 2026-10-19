using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Construction;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class SettlementWarLoopTests
{
    [TestMethod]
    public void Conquest_supports_loading_repairing_and_restarting()
    {
        var content = Content();
        using var original = SettlementWarFixture.Session(content,
            [new GoodsQuantity(ConstructionFixture.Material, 20)]);
        var settlement = SettlementWarFixture.Settlement(original);
        var train = SettlementWarFixture.Train(original);
        using var atSettlement = SettlementWarFixture.MoveTrain(original, settlement.Node, servicing: true);
        var lease = SettlementWarFixture.Accept(atSettlement, new CoreCommand.AcquireLease(
            new LeaseRequest.Rent(settlement.Id, 20, 1), new(1000))).Events
            .Select(envelope => envelope.Event).OfType<CoreEvent.LeaseAcquired>().Single().Lease;
        SettlementWarFixture.Accept(atSettlement, new CoreCommand.UnloadGoods(train, settlement.Id,
            lease.Inventory, ConstructionFixture.Material, 15));
        using var away = SettlementWarFixture.MoveTrain(atSettlement,
            SettlementWarFixture.AwayNode(atSettlement, settlement));
        SettlementWarFixture.Accept(away, new CoreCommand.DeclareWar(settlement.Id));
        using var fought = SettlementWarFixture.MoveTrain(away, settlement.Node);
        CombatFixture.AdvanceUntil(fought, view => view.World.Combats.IsEmpty
            && SettlementWarFixture.View(fought, settlement.Id).Surrendered, 10);
        using var conquered = SettlementWarFixture.MoveTrain(fought, settlement.Node, servicing: true);
        SettlementWarFixture.Accept(conquered, new CoreCommand.OccupySettlement(settlement.Id, train));

        var beforeLoad = Cargo(conquered, train);
        SettlementWarFixture.Accept(conquered, new CoreCommand.LoadGoods(train, settlement.Id,
            settlement.Inventory, ConstructionFixture.Material, 5));
        Assert.AreEqual(beforeLoad + 5, Cargo(conquered, train));

        var facility = Facility(conquered, settlement.Id);
        var vehicle = conquered.State.World.Trains.Single().Vehicles.Single();
        using var repairing = SettlementWarFixture.MoveTrain(conquered, settlement.Node);
        Assert.IsTrue(Instance(repairing, vehicle).StructureDurability < 1000,
            $"战斗未损伤目标车辆：{Instance(repairing, vehicle).StructureDurability}");
        SettlementWarFixture.Accept(repairing, new CoreCommand.StartOwnedJob(new JobRequest.Repair(
            facility, new InstanceId.Vehicle(vehicle), [settlement.Inventory], 0)));
        CombatFixture.AdvanceUntil(repairing, _ => repairing.State.World.ConstructionJobs.IsEmpty
            && Instance(repairing, vehicle).StructureDurability >= 1000, 400);
        Assert.AreEqual(1000L, Instance(repairing, vehicle).StructureDurability);

        var edge = repairing.State.World.Edges.First(item =>
            (item.Start == settlement.Node || item.End == settlement.Node)
            && item.Tracks > 0 && item.Breaks.IsEmpty);
        var destination = edge.Start == settlement.Node ? edge.End : edge.Start;
        SettlementWarFixture.Accept(repairing, new CoreCommand.ReplaceRunPlan(train,
            new(false, [new PlanStop.Waypoint(destination)])));
        SettlementWarFixture.Accept(repairing, new CoreCommand.StartTrain(train));
        CombatFixture.AdvanceUntil(repairing, view => view.FindTrain(train) is Lookup<TrainView>.Found found
            && found.Value.Position is TrainPosition.Node node && node.Id == destination, 600);
    }

    [TestMethod]
    public void Destroyed_vehicles_are_not_relieved_and_a_poor_purchase_is_rejected()
    {
        var content = CombatFixture.Content(armedBuilding: true, damage: 100, parked: true);
        content = content with
        {
            Designs = content.Designs.Select(design => design.Design == CombatFixture.ArmedVehicle
                ? design with
                {
                    StructureHitWeight = 1,
                    Parts = design.Parts.Select(part => part with { HitWeight = 0 }).ToImmutableArray()
                }
                : design).ToImmutableArray()
        };
        using var session = CombatFixture.SessionWithContent(content, parked: true);
        using var atNode = SettlementWarFixture.MoveTrain(session, CombatFixture.HostileNode(session));
        var result = atNode.Advance(1);
        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        Assert.IsEmpty(atNode.State.World.Trains);

        var views = atNode.State.World.Settlements.Select(item => SettlementWarFixture.View(atNode, item.Id)).ToArray();
        var seller = views.First(item => item.Relation == SettlementRelation.NonHostile);
        var money = atNode.State.World.Money;
        var trains = atNode.State.World.Trains;
        var instances = atNode.State.World.Instances;

        var advanced = atNode.Advance(30);
        Assert.IsNull(advanced.Fault, advanced.Diagnostic?.Message);
        Assert.AreEqual(money, atNode.State.World.Money);
        CollectionAssert.AreEqual(trains.ToArray(), atNode.State.World.Trains.ToArray());
        CollectionAssert.AreEqual(instances.ToArray(), atNode.State.World.Instances.ToArray());

        var design = content.Designs.Single(item => item.Design == CombatFixture.ParkedVehicle);
        var world = atNode.State.World;
        var vehicle = new VehicleId(world.Next.Vehicle);
        var instance = WorldState.CreateInstance(new InstanceId.Vehicle(vehicle), design,
            SettlementRelation.NonHostile, new InstanceDetails.Vehicle(SizeClass.Small, null));
        var parked = world with
        {
            Instances = world.Instances.Add(instance).OrderBy(item => item.Id is InstanceId.Building ? 0 : 1)
                .ThenBy(item => item.Id is InstanceId.Building building
                    ? building.Id.Value : ((InstanceId.Vehicle)item.Id).Id.Value).ToImmutableArray(),
            Settlements = world.Settlements.Select(item => item.Id == seller.Id
                ? item with { ParkedVehicles = item.ParkedVehicles.Add(vehicle).OrderBy(id => id.Value).ToImmutableArray() }
                : item).ToImmutableArray(),
            Next = world.Next with { Vehicle = checked(world.Next.Vehicle + 1) }
        };
        using var poor = new CoreSession(atNode.State with { World = parked with { Money = new(0) } });
        var buy = SettlementWarFixture.Submit(poor, new CoreCommand.BuyVehicle(seller.Id, vehicle, new(1000)));
        var rejected = (CommandDecision.Rejected)buy.Receipt.Decision;
        Assert.AreEqual("insufficient_money", rejected.Problems.Single().Code);
        Assert.AreEqual("Wallet", rejected.Problems.Single().Field);
        Assert.IsTrue(buy.Events.IsEmpty);
        Assert.IsNull(poor.Read().Fault);
    }

    [TestMethod]
    public void A_vehicle_damaged_in_combat_sells_for_less_and_joins_the_for_sale_list()
    {
        var content = Content();
        content = content with
        {
            MarketRules = [content.MarketRules[0] with
            {
                VehiclePurchaseLimits = [new(SizeClass.Small, 2), new(SizeClass.Medium, 0)]
            }, ..content.MarketRules.Skip(1)]
        };
        using var session = CombatFixture.SessionWithContent(content, trains: 2);
        var target = SettlementWarFixture.HostileSettlement(session);
        var seller = SettlementWarFixture.Settlement(session);
        var trains = session.State.World.Trains.OrderBy(item => item.Id.Value).ToArray();
        var damagedTrain = trains[0].Id;
        var intactTrain = trains[1].Id;
        using var fought = SettlementWarFixture.MoveTrain(session, target.Node, only: damagedTrain);
        var result = fought.Advance(1);
        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        var damaged = fought.State.World.Trains.Single(item => item.Id == damagedTrain).Vehicles.Single();
        Assert.IsTrue(Instance(fought, damaged).StructureDurability < 1000);
        using var moved = SettlementWarFixture.MoveTrain(fought, seller.Node, only: damagedTrain);
        using var together = SettlementWarFixture.MoveTrain(moved, seller.Node, only: intactTrain);
        var intact = together.State.World.Trains.Single(item => item.Id == intactTrain).Vehicles.Single();
        SettlementWarFixture.Accept(together, new CoreCommand.DisbandTrain(damagedTrain));
        SettlementWarFixture.Accept(together, new CoreCommand.DisbandTrain(intactTrain));

        var damagedQuote = WorldFixture.Success(together.Read().QuoteVehicle(
            new VehicleTradeQuoteRequest(seller.Id, damaged, VehicleTradeAction.Sell)));
        var intactQuote = WorldFixture.Success(together.Read().QuoteVehicle(
            new VehicleTradeQuoteRequest(seller.Id, intact, VehicleTradeAction.Sell)));
        Assert.IsTrue(damagedQuote.Total!.Value.Value < intactQuote.Total!.Value.Value);

        var sale = SettlementWarFixture.Accept(together,
            new CoreCommand.SellVehicle(seller.Id, damaged, new(0))).Events
            .Select(envelope => envelope.Event).OfType<CoreEvent.VehicleTraded>().Single();
        Assert.AreEqual(damagedQuote.Total, sale.ActualAmount);
        Assert.IsTrue(SettlementWarFixture.View(together, seller.Id).VehiclesForSale.Contains(damaged));
    }

    private static CoreContentProjection Content()
    {
        var content = SettlementWarFixture.MoraleRules(
            SettlementWarFixture.Content(armedBuilding: true), coefficient: 0, weaponLoss: .18,
            recovery: .012, threshold: .9);
        return content with
        {
            Designs = content.Designs.Select(design => design.Design switch
            {
                var hash when hash == CombatFixture.ArmedVehicle => design with
                {
                    MaxStructureDurability = 1000, StructureHitWeight = 1,
                    StructuralGoods = [new GoodsQuantity(new GoodId.Voxel(WorldFixture.Material), 10)],
                    Parts = design.Parts.Select(part => part with
                    {
                        HitWeight = 0,
                        Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                            ? (PartCapability)new PartCapability.Weapon(100, 1) : capability).ToImmutableArray()
                    }).ToImmutableArray()
                },
                var hash when hash == CombatFixture.ArmedBuilding => design with
                {
                    StructureHitWeight = 0,
                    Parts = design.Parts.Select(part => part with
                    {
                        HitWeight = 1,
                        Capabilities = part.Capabilities.Select(capability => capability is PartCapability.Weapon
                            ? (PartCapability)new PartCapability.Weapon(10, 1) : capability).ToImmutableArray()
                    }).ToImmutableArray()
                },
                _ => design
            }).ToImmutableArray()
        };
    }

    private static FacilityKey Facility(CoreSession session, SettlementId settlement)
    {
        var state = session.State.World.Settlements.Single(item => item.Id == settlement);
        var building = state.Plots.Select(plot => plot.Building).First(id => id is not null
            && session.State.World.Instances.Single(instance => instance.Id == new InstanceId.Building(id.Value)).Design
            == CombatFixture.BuilderBuilding);
        return new(new BuildingId(building!.Value.Value), CapabilityKind.Construction);
    }

    private static long Cargo(CoreSession session, TrainId train)
        => session.State.World.Inventories.Single(item => item.Id
                == session.State.World.Trains.Single(t => t.Id == train).Inventory)
            .Goods.Where(good => good.Good == ConstructionFixture.Material).Sum(good => good.Quantity);

    private static InstanceState Instance(CoreSession session, VehicleId vehicle)
        => session.State.World.Instances.Single(item => item.Id == new InstanceId.Vehicle(vehicle));
}
