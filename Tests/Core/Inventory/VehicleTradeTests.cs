using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class VehicleTradeTests
{
    [TestMethod]
    public void Selling_and_buying_transfer_original_instance_and_preserve_damage_and_old_views()
    {
        using var initial = Parked();
        using var session = Damage(initial, 50, 25);
        var vehicle = Vehicle(session);
        var before = session.State.World;
        var old = session.Read();
        var sellQuote = Quote(session, VehicleTradeAction.Sell);
        Assert.AreEqual(new Money(54), sellQuote.Total); // (100×1/2 + 40×1/4)×0.9
        Assert.IsTrue(sellQuote.Blockers.IsEmpty);
        var sale = Accept(session, new CoreCommand.SellVehicle(Town(session), new(1), new(54)));
        var sold = sale.Events.Select(e => e.Event).OfType<CoreEvent.VehicleTraded>().Single();
        Assert.AreEqual(new CoreEvent.VehicleTraded(new(1), Town(session), VehicleTradeAction.Sell, new(54)), sold);
        Assert.AreEqual(vehicle with { Ownership = SettlementRelation.NonHostile }, Vehicle(session));
        Assert.AreEqual(before with { Money = new(before.Money.Value + 54), Instances = session.State.World.Instances }, session.State.World);
        Assert.AreEqual(SettlementRelation.PlayerOwned, ((Lookup<InstanceView>.Found)old.FindInstance(vehicle.Id)).Value.Ownership);
        Assert.AreEqual(new Money(66), Quote(session, VehicleTradeAction.Buy).Total);
        Accept(session, new CoreCommand.BuyVehicle(Town(session), new(1), new(66)));
        Assert.AreEqual(vehicle, Vehicle(session));
        Assert.AreEqual(new Money(before.Money.Value - 12), session.State.World.Money);
        CollectionAssert.AreEqual(before.Settlements.Single().ParkedVehicles.ToArray(), session.State.World.Settlements.Single().ParkedVehicles.ToArray());
    }

    [TestMethod]
    public void Fractions_are_summed_before_rounding_and_zero_receipt_is_allowed()
    {
        var content = VehicleContent() with
        {
            Designs = VehicleContent().Designs.Select(d => d.Design == WorldFixture.Vehicle ? d with
            {
                MaxStructureDurability = 3, StructuralBaseValue = new(1),
                Parts = d.Parts.Select(p => p with { MaxDurability = 3, BaseValue = new(1) }).ToImmutableArray()
            } : d).ToImmutableArray(),
            MarketRules = [VehicleContent().MarketRules[0] with { VehicleBuyMultiplier = new(10000), VehicleSellMultiplier = new(10000) }]
        };
        using var initial = Parked(content);
        using var session = Damage(initial, 1, 1);
        Assert.AreEqual(new Money(0), Quote(session, VehicleTradeAction.Sell).Total);
        Accept(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)));
        Assert.AreEqual(new Money(1), Quote(session, VehicleTradeAction.Buy).Total);
        Accept(session, new CoreCommand.BuyVehicle(Town(session), new(1), new(1)));
    }

    [TestMethod]
    public void Historical_durability_above_new_maximum_is_valued_as_fully_intact()
    {
        using var session = Parked();
        var changed = Changed(VehicleContent(), '6');
        changed = changed with { Designs = changed.Designs.Select(d => d.Design == WorldFixture.Vehicle ? d with
        { MaxStructureDurability = 10, Parts = d.Parts.Select(p => p with { MaxDurability = 10 }).ToImmutableArray() } : d).ToImmutableArray() };
        Accept(session, new CoreCommand.PublishContent(changed));
        Assert.AreEqual(100L, Vehicle(session).StructureDurability);
        Assert.AreEqual(new Money(126), Quote(session, VehicleTradeAction.Sell).Total);
        Accept(session, new CoreCommand.SellVehicle(Town(session), new(1), new(126)));
        Assert.AreEqual(new Money(154), Quote(session, VehicleTradeAction.Buy).Total);
    }

    [TestMethod]
    public void Purchase_slots_are_occupied_and_released_by_actual_sales()
    {
        using var session = Parked(trains: 2);
        var town = Town(session);
        Assert.AreEqual(1, Capacity(session).Available);
        Accept(session, new CoreCommand.SellVehicle(town, new(1), new(0)));
        Assert.AreEqual(1, Capacity(session).ForSale);
        Assert.AreEqual(0, Capacity(session).Available);
        RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(town, new(2), new(0)), "insufficient_capacity", "Buyer.VehiclePurchaseCapacity");
        Accept(session, new CoreCommand.BuyVehicle(town, new(1), new(1000)));
        Assert.AreEqual(0, Capacity(session).ForSale);
        Assert.AreEqual(1, Capacity(session).Available);
        Accept(session, new CoreCommand.SellVehicle(town, new(2), new(0)));
        CollectionAssert.AreEqual(new[] { new VehicleId(2) }, Settlement(session).VehiclesForSale.ToArray());
    }

    [TestMethod]
    public void Lost_capability_and_reduced_limits_do_not_remove_existing_vehicles()
    {
        using var session = Parked(trains: 2);
        var town = Town(session);
        Accept(session, new CoreCommand.SellVehicle(town, new(1), new(0)));
        var content = Changed(VehicleContent(), '7');
        content = content with { Designs = content.Designs.Select(d => d.Design == WorldFixture.Station ? d with
        { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Where(c => c is not PartCapability.Construction).ToImmutableArray() }).ToImmutableArray() } : d).ToImmutableArray(),
            MarketRules = [content.MarketRules[0] with { VehiclePurchaseLimits = [new(SizeClass.Small, 0)] }] };
        Accept(session, new CoreCommand.PublishContent(content));
        Assert.AreEqual(new VehiclePurchaseCapacity(SizeClass.Small, 1, 0, 0), Capacity(session));
        RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(town, new(2), new(0)), "capability_zero", "Buyer");
        Accept(session, new CoreCommand.BuyVehicle(town, new(1), new(1000)));
        Assert.IsTrue(Settlement(session).VehiclesForSale.IsEmpty);
    }

    [TestMethod]
    public void Missing_definitions_block_both_directions_but_do_not_free_a_slot()
    {
        foreach (var sold in new[] { false, true })
        {
            using var original = Parked();
            if (sold) Accept(original, new CoreCommand.SellVehicle(Town(original), new(1), new(0)));
            var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
            var content = Changed(VehicleContent(), '8') with
            { Goods = VehicleContent().Goods.Where(g => g.Good != Voxel).ToImmutableArray() };
            using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, content, WorldFixture.Options));
            using var session = (CoreSession)candidate.Activate();
            CoreCommand command = sold ? new CoreCommand.BuyVehicle(Town(session), new(1), new(1000))
                : new CoreCommand.SellVehicle(Town(session), new(1), new(0));
            Assert.AreEqual("content_missing", Quote(session, sold ? VehicleTradeAction.Buy : VehicleTradeAction.Sell).Blockers.Single().Code);
            RejectWithoutWorldChange(session, command, "content_missing", "Vehicle");
            Assert.AreEqual(sold ? 1 : 0, Capacity(session).ForSale);
            Assert.AreEqual(sold ? 1 : 0, Settlement(session).VehiclesForSale.Length);
        }
    }

    [TestMethod]
    public void Price_conditions_and_wallet_balance_reject_without_partial_transfer()
    {
        using var session = Parked();
        RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(Town(session), new(1), new(127)), "price_condition_failed", "MinimumReceipt");
        Accept(session, new CoreCommand.SellVehicle(Town(session), new(1), new(126)));
        RejectWithoutWorldChange(session, new CoreCommand.BuyVehicle(Town(session), new(1), new(153)), "price_condition_failed", "MaximumPayment");
        using var poor = new CoreSession(session.State with { World = session.State.World with { Money = new(153) } });
        RejectWithoutWorldChange(poor, new CoreCommand.BuyVehicle(Town(poor), new(1), new(154)), "insufficient_money", "Wallet");
        Assert.AreEqual(new Money(154), Quote(poor, VehicleTradeAction.Buy).Total);
    }

    [TestMethod]
    public void Wallet_overflow_faults_atomically_instead_of_becoming_a_business_rejection()
    {
        using var original = Parked();
        using var session = new CoreSession(original.State with { World = original.State.World with { Money = new(long.MaxValue) } });
        var before = session.State;
        var result = Submit(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)));
        Assert.IsInstanceOfType<CommandDecision.Faulted>(result.Receipt.Decision);
        Assert.AreEqual(before.World, session.State.World);
        Assert.AreEqual(before.Position, session.Read().Position);
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    public void Vehicle_price_overflow_does_not_freeze_quotes_but_faults_submission_atomically()
    {
        foreach (var buy in new[] { false, true })
        {
            using var session = Parked();
            if (buy) Accept(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)));
            var content = Changed(VehicleContent(), '5');
            content = content with { Designs = content.Designs.Select(d => d.Design == WorldFixture.Vehicle ? d with
            { StructuralBaseValue = new(long.MaxValue), Parts = d.Parts.Select(p => p with { BaseValue = new(long.MaxValue) }).ToImmutableArray() } : d).ToImmutableArray() };
            Accept(session, new CoreCommand.PublishContent(content));
            var before = session.State;
            Assert.IsInstanceOfType<CoreResult<VehicleTradeQuote>.Faulted>(session.Read().QuoteVehicle(
                new(Town(session), new(1), buy ? VehicleTradeAction.Buy : VehicleTradeAction.Sell)));
            Assert.AreEqual(before, session.State);
            Assert.IsNull(session.Read().Fault);
            CoreCommand command = buy ? new CoreCommand.BuyVehicle(Town(session), new(1), new(long.MaxValue))
                : new CoreCommand.SellVehicle(Town(session), new(1), new(0));
            var result = Submit(session, command);
            Assert.IsInstanceOfType<CommandDecision.Faulted>(result.Receipt.Decision);
            Assert.IsNotNull(session.Read().Fault);
            Assert.AreEqual(before.World, session.State.World);
            Assert.AreEqual(before.Position, session.Read().Position);
            Assert.IsTrue(result.Events.IsEmpty);
        }
    }

    [TestMethod]
    public void Vehicle_at_another_settlement_is_rejected_before_any_trade()
    {
        using var candidate = WorldFixture.Prepare(WorldFixture.MultiInput, WorldFixture.MultiContent);
        using var session = (CoreSession)candidate.Activate();
        var train = session.State.World.Trains.First();
        var node = ((TrainPosition.Node)train.Position).Id;
        var remote = session.State.World.Settlements.First(s => s.Node != node && s.Relation == SettlementRelation.NonHostile);
        RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(remote.Id, train.Vehicles[0], new(0)),
            "vehicles_not_colocated", "Vehicle.Location");
    }

    [TestMethod]
    public void Trades_require_direction_ownership_and_disbanded_local_vehicle()
    {
        using var session = Session(VehicleContent());
        RejectWithoutWorldChange(session, new CoreCommand.BuyVehicle(Town(session), new(1), new(1000)), "not_for_sale", "Vehicle");
        RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)), "vehicle_in_train", "Vehicle.Details.Train");
        Park(session);
        Accept(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)));
        RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)), "not_owned", "Vehicle");
        RejectWithoutWorldChange(session, new CoreCommand.BuyVehicle(Town(session), new(999), new(1000)), "object_missing", "Vehicle");
    }

    [TestMethod]
    public void Player_owned_and_hostile_settlements_do_not_trade_but_hostile_stock_remains_visible()
    {
        using var initial = Parked();
        Accept(initial, new CoreCommand.SellVehicle(Town(initial), new(1), new(0)));
        foreach (var relation in new[] { SettlementRelation.Hostile, SettlementRelation.PlayerOwned })
        {
            using var session = new CoreSession(initial.State with { World = initial.State.World with
            { Settlements = initial.State.World.Settlements.Select(s => s with { Relation = relation }).ToImmutableArray() } });
            RejectWithoutWorldChange(session, new CoreCommand.BuyVehicle(Town(session), new(1), new(1000)),
                relation == SettlementRelation.Hostile ? "access_denied" : "invalid_state", "Seller");
            RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)),
                relation == SettlementRelation.Hostile ? "access_denied" : "invalid_state", "Buyer");
            var view = Settlement(session);
            if (relation == SettlementRelation.Hostile)
            {
                CollectionAssert.AreEqual(new[] { new VehicleId(1) }, view.VehiclesForSale.ToArray());
                Assert.AreEqual(new VehiclePurchaseCapacity(SizeClass.Small, 1, 1, 0), Capacity(session));
            }
            else
            {
                Assert.IsTrue(view.VehiclesForSale.IsEmpty);
                Assert.IsTrue(view.VehiclePurchaseCapacities.IsEmpty);
            }
        }
    }

    [TestMethod]
    public void Construction_must_be_intact_and_support_the_vehicle_size()
    {
        foreach (var broken in new[] { false, true })
        {
            var content = VehicleContent();
            if (!broken) content = content with { Designs = content.Designs.Select(d => d.Design == WorldFixture.Station ? d with
            { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Select(c => c is PartCapability.Construction construction
                ? construction with { MaximumVehicleSize = null } : c).ToImmutableArray() }).ToImmutableArray() } : d).ToImmutableArray() };
            using var initial = Parked(content);
            using var session = new CoreSession(initial.State with { World = initial.State.World with
            { Instances = initial.State.World.Instances.Select(i => broken && i.Id is InstanceId.Building ? i with
                { Parts = i.Parts.Select(p => p with { Current = 0 }).ToImmutableArray() } : i).ToImmutableArray() } });
            Assert.AreEqual(0, Capacity(session).Available);
            RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)), "capability_zero", "Buyer");
        }
    }

    [TestMethod]
    public void Missing_market_rule_blocks_vehicle_trading_and_quote_has_no_total()
    {
        using var original = Parked();
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved,
            Changed(VehicleContent(), '9') with { MarketRules = [] }, WorldFixture.Options));
        using var session = (CoreSession)candidate.Activate();
        Assert.IsNull(Quote(session, VehicleTradeAction.Sell).Total);
        RejectWithoutWorldChange(session, new CoreCommand.SellVehicle(Town(session), new(1), new(0)), "content_missing", "MarketRule");
    }

    internal static CoreContentProjection VehicleContent()
    {
        var content = Content(duration: 100000);
        return content with
        {
            Designs = content.Designs.Select(d => d.Design == WorldFixture.Station ? d with
            { Parts = d.Parts.Select(p => p with { Capabilities = p.Capabilities.Add(new PartCapability.Construction(1, PlotSize.Small, SizeClass.Small)) }).ToImmutableArray() }
                : d with { StructuralBaseValue = new(100), Parts = d.Parts.Select(p => p with { BaseValue = new(40) }).ToImmutableArray() }).ToImmutableArray(),
            MarketRules = [content.MarketRules[0] with { VehicleBuyMultiplier = new(11000), VehicleSellMultiplier = new(9000),
                VehiclePurchaseLimits = [new(SizeClass.Small, 1)] }]
        };
    }

    internal static CoreSession Parked(CoreContentProjection? content = null, int trains = 1)
    {
        var session = Session(content ?? VehicleContent(), trains);
        Park(session);
        return session;
    }

    private static void Park(CoreSession session)
    {
        var lease = Rent(session, session.State.World.Trains.Length * 30, 100000);
        foreach (var train in session.State.World.Trains.ToArray())
        {
            Service(session, train.Id.Value);
            foreach (var good in Goods) Accept(session, new CoreCommand.UnloadGoods(train.Id, lease.Settlement, lease.Inventory, good, 10));
            Accept(session, new CoreCommand.StopTrain(train.Id));
            Accept(session, new CoreCommand.DisbandTrain(train.Id));
        }
    }

    private static CoreSession Damage(CoreSession session, long structure, long part) => new(session.State with
    { World = session.State.World with { Instances = session.State.World.Instances.Select(i => i.Id == new InstanceId.Vehicle(new(1))
        ? i with { StructureDurability = structure, Parts = i.Parts.Select(p => p with { Current = part }).ToImmutableArray() } : i).ToImmutableArray() } });
    private static SettlementId Town(CoreSession session) => session.State.World.Settlements.Single().Id;
    private static InstanceState Vehicle(CoreSession session) => session.State.World.Instances.Single(i => i.Id == new InstanceId.Vehicle(new(1)));
    private static SettlementView Settlement(CoreSession session) => ((Lookup<SettlementView>.Found)session.Read().FindSettlement(Town(session))).Value;
    private static VehiclePurchaseCapacity Capacity(CoreSession session) => Settlement(session).VehiclePurchaseCapacities.Single(c => c.Size == SizeClass.Small);
    private static VehicleTradeQuote Quote(CoreSession session, VehicleTradeAction action)
        => WorldFixture.Success(session.Read().QuoteVehicle(new(Town(session), new(1), action)));
}
