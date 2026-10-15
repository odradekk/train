using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class InventoryTradeTests
{
    internal static readonly GoodId Clothing = new GoodId.Voxel(new("fixture:clothing"));
    internal static readonly GoodId PreparedFood = new GoodId.Voxel(new("fixture:prepared-food"));
    public TestContext TestContext { get; set; } = null!;

    internal static CoreContentProjection Content(long capacity = 1000, long value = 100,
        GoodsPriceParameters? prices = null)
    {
        var content = InventoryFixture.Content();
        return content with
        {
            Goods = content.Goods.Select(g => g with { BaseValue = new(value) }).ToImmutableArray(),
            Designs = content.Designs.Select(d => d with
            {
                Parts = d.Parts.Select(p => p with
                {
                    Capabilities = p.Capabilities.Where(c => c is not PartCapability.Storage)
                        .Append(new PartCapability.Storage(d.Design == WorldFixture.Station ? capacity : Math.Max(1000, capacity))).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray(),
            MarketRules = [content.MarketRules[0] with
            {
                BasicReserveHours = 1d / 3600,
                DefaultGoodsPrices = prices ?? new(new(5000), new(5000), new(20000), new(1000))
            }]
        };
    }

    internal static CoreSession Session(CoreContentProjection? content = null,
        ImmutableArray<GoodsQuantity> local = default, ImmutableArray<GoodsQuantity> cargo = default,
        long money = 10000, SettlementRelation relation = SettlementRelation.NonHostile)
    {
        using var original = InventoryFixture.Session(content ?? Content());
        var population = original.State.World.Settlements.Single().PopulationState;
        Service(original);
        var state = original.State;
        var settlement = state.World.Settlements.Single();
        var train = state.World.Trains.Single();
        return new(state with
        {
            World = state.World with
            {
                Money = new(money),
                Settlements = [settlement with { Relation = relation, Population = 10, PopulationState = population }],
                Nodes = state.World.Nodes.Select(n => n.Occupant is NodeOccupant.Settlement occupant && occupant.Id == settlement.Id
                    ? n with { Occupant = occupant with { Relation = relation } } : n).ToImmutableArray(),
                Instances = state.World.Instances.Select(i => i.Id is InstanceId.Building
                    ? i with { Ownership = relation } : i).ToImmutableArray(),
                Inventories = state.World.Inventories.Select(i => i.Id == settlement.Inventory
                    ? i with { Goods = local.IsDefault ? [] : local }
                    : i.Id == train.Inventory ? i with { Goods = cargo.IsDefault ? [] : cargo } : i).ToImmutableArray()
            }
        });
    }

    [TestMethod]
    public void Documented_midpoints_charge_97_102_108_and_split_199_plus_108()
    {
        using var whole = Session(local: [new(Clothing, 13)]);
        var request = Request(whole, TradeAction.Buy, Clothing, 3);
        var quote = Quote(whole, request);
        AssertQuote(whole, quote, request, 3, 307);
        var before = whole.Read();
        var result = Accept(whole, Command(request, 307));
        Assert.AreEqual(new Money(307), result.Events.Select(e => e.Event).OfType<CoreEvent.GoodsTransferred>().Single().ActualAmount);
        Assert.AreEqual(new Money(9693), whole.Read().Economy.Wallet);
        Assert.AreEqual(13L, ((Lookup<InventoryView>.Found)before.FindInventory(request.LocalInventory)).Value.Stacks.Single().Quantity);
        RejectWithoutWorldChange(whole, Command(request with { Quantity = 1 }, 1000), "insufficient_goods");

        using var split = Session(local: [new(Clothing, 13)]);
        foreach (var expected in new long[] { 97, 102, 108 })
        {
            var one = Request(split, TradeAction.Buy, Clothing, 1);
            Assert.AreEqual(new Money(expected), Quote(split, one).Total);
            Accept(split, Command(one, expected));
        }
        using var twoThenOne = Session(local: [new(Clothing, 13)]);
        var two = Request(twoThenOne, TradeAction.Buy, Clothing, 2);
        Assert.AreEqual(new Money(199), Quote(twoThenOne, two).Total);
        Accept(twoThenOne, Command(two, 199));
        Assert.AreEqual(new Money(108), Quote(twoThenOne, two with { Quantity = 1 }).Total);
    }

    [TestMethod]
    public void Zero_target_costs_exactly_55_and_no_use_does_not_create_demand()
    {
        using var session = Session(local: [new(Voxel, 2)], cargo: [new(Voxel, 1)]);
        var buy = Request(session, TradeAction.Buy, Voxel, 1);
        AssertQuote(session, Quote(session, buy), buy, 1, 55);
        Accept(session, Command(buy, 55));
        var sell = Request(session, TradeAction.Sell, Voxel, 1);
        Assert.AreEqual("insufficient_demand", Quote(session, sell).Blockers.Single().Code);
        RejectWithoutWorldChange(session, Command(sell, 0), "insufficient_demand");
    }

    [TestMethod]
    public void Prices_clamp_before_spread_round_each_unit_and_keep_a_minimum_of_one()
    {
        using var minimum = Session(local: [new(Clothing, 100)]);
        Assert.AreEqual(new Money(55), Quote(minimum, Request(minimum, TradeAction.Buy, Clothing, 1)).Total);
        using var maximum = Session(Content(prices: new(new(5000), new(5000), new(12000), new(1000))),
            cargo: [new(Clothing, 1)]);
        Assert.AreEqual(new Money(108), Quote(maximum, Request(maximum, TradeAction.Sell, Clothing, 1)).Total);
        using var floor = Session(local: [new(Clothing, 9)], cargo: [new(Clothing, 1)]);
        // Midpoint 9.5 gives 102.5, then the 10% spread gives 92.25, floored to 92.
        Assert.AreEqual(new Money(92), Quote(floor, Request(floor, TradeAction.Sell, Clothing, 1)).Total);
        using var zero = Session(Content(value: 0), local: [new(Clothing, 11)], cargo: [new(Clothing, 1)]);
        Assert.AreEqual(new Money(1), Quote(zero, Request(zero, TradeAction.Buy, Clothing, 1)).Total);
        using var zeroSale = Session(Content(value: 0), cargo: [new(Clothing, 1)]);
        Assert.AreEqual(new Money(1), Quote(zeroSale, Request(zeroSale, TradeAction.Sell, Clothing, 1)).Total);
    }

    [TestMethod]
    public void Four_actions_report_request_revision_quantity_total_and_owned_load_shortages()
    {
        using var buy = Session(local: [new(Clothing, 11)]);
        var buyRequest = Request(buy, TradeAction.Buy, Clothing, 1);
        AssertQuote(buy, Quote(buy, buyRequest), buyRequest, 1, 108);
        Assert.IsTrue(Quote(buy, buyRequest).ShortageWarnings.IsEmpty);
        using var sell = Session(local: [new(Clothing, 9)], cargo: [new(Clothing, 1)]);
        var sellRequest = Request(sell, TradeAction.Sell, Clothing, 1);
        AssertQuote(sell, Quote(sell, sellRequest), sellRequest, 1, 92);
        Assert.IsTrue(Quote(sell, sellRequest).ShortageWarnings.IsEmpty);
        var sale = Accept(sell, Command(sellRequest, 92));
        var transfer = sale.Events.Select(e => e.Event).OfType<CoreEvent.GoodsTransferred>().Single();
        Assert.AreEqual(TradeAction.Sell, transfer.Action);
        Assert.AreEqual(new Money(92), transfer.ActualAmount);
        Assert.AreEqual(sell.State.World.Trains.Single().Inventory, transfer.Source);
        Assert.AreEqual(sellRequest.LocalInventory, transfer.Destination);
        Assert.AreEqual(new Money(10092), sell.Read().Economy.Wallet);
        Assert.AreEqual(10L, ReadInventory(sell, sellRequest.LocalInventory).Stacks.Single().Quantity);
        Assert.IsTrue(ReadInventory(sell, transfer.Source).Stacks.IsEmpty);
        using var owned = Session(local: [new(Clothing, 10)], cargo: [new(Clothing, 1)], relation: SettlementRelation.PlayerOwned);
        var load = Request(owned, TradeAction.Load, Clothing, 1);
        var warning = Quote(owned, load);
        AssertQuote(owned, warning, load, 1, 0);
        Assert.AreEqual("restock_shortage", warning.ShortageWarnings.Single().Code);
        var unload = Request(owned, TradeAction.Unload, Clothing, 1);
        AssertQuote(owned, Quote(owned, unload), unload, 1, 0);
        Assert.IsTrue(Quote(owned, unload).ShortageWarnings.IsEmpty);
        var wallet = owned.Read().Economy.Wallet;
        Accept(owned, Command(load));
        Accept(owned, Command(unload));
        Assert.AreEqual(wallet, owned.Read().Economy.Wallet);
    }

    [TestMethod]
    public void Submission_rechecks_stale_quote_and_whole_order_rejections_do_not_mutate_world()
    {
        using var session = Session(local: [new(Clothing, 13)]);
        var request = Request(session, TradeAction.Buy, Clothing, 2);
        Assert.AreEqual(new Money(199), Quote(session, request).Total);
        Accept(session, Command(request with { Quantity = 1 }, 97));
        RejectWithoutWorldChange(session, Command(request, 199), "price_condition_failed");
        RejectWithoutWorldChange(session, Command(request with { Quantity = 3 }, 1000), "insufficient_goods");
        using var capacity = Session(Content(capacity: 30), local: [new(Clothing, 13)], cargo: [new(Voxel, 999)]);
        RejectWithoutWorldChange(capacity, Command(Request(capacity, TradeAction.Buy, Clothing, 2), 1000), "insufficient_capacity");
        using var poor = Session(local: [new(Clothing, 13)], money: 96);
        var poorRequest = Request(poor, TradeAction.Buy, Clothing, 1);
        Assert.AreEqual(0L, Quote(poor, poorRequest).CurrentlyExecutableQuantity);
        RejectWithoutWorldChange(poor, Command(poorRequest, 1000), "insufficient_money");
        using var sale = Session(local: [new(Clothing, 9)], cargo: [new(Clothing, 2)]);
        var sell = Request(sale, TradeAction.Sell, Clothing, 1);
        RejectWithoutWorldChange(sale, Command(sell, 93), "price_condition_failed");
        RejectWithoutWorldChange(sale, Command(sell with { Quantity = 2 }, 0), "insufficient_demand");
        using var full = Session(local: [new(Clothing, 9), new(Voxel, 991)], cargo: [new(Clothing, 1)]);
        RejectWithoutWorldChange(full, Command(Request(full, TradeAction.Sell, Clothing, 1), 0), "insufficient_capacity");
        RejectWithoutWorldChange(full, Command(Request(full, TradeAction.Sell, Clothing, 2), 0), "insufficient_goods");
    }

    [TestMethod]
    public void Leased_storage_cannot_trade_and_missing_market_does_not_block_owned_transfers()
    {
        using var session = Session(cargo: [new(Clothing, 2)]);
        var lease = Rent(session);
        foreach (var action in new[] { TradeAction.Buy, TradeAction.Sell })
        {
            var request = Request(session, action, Clothing, 1) with { LocalInventory = lease.Inventory };
            Assert.AreEqual("invalid_state", Quote(session, request).Blockers.Single().Code);
            RejectWithoutWorldChange(session, Command(request, 1000), "invalid_state");
        }
        using var prepared = Session(local: [new(Clothing, 1)], cargo: [new(Clothing, 1)], relation: SettlementRelation.PlayerOwned);
        using var owned = new CoreSession(prepared.State with { Content = prepared.State.Content with { MarketRules = [] } });
        foreach (var action in new[] { TradeAction.Load, TradeAction.Unload })
        {
            var request = Request(owned, action, Clothing, 1);
            AssertQuote(owned, Quote(owned, request), request, 1, 0);
            Accept(owned, Command(request));
        }
    }

    [TestMethod]
    public void Dynamic_prepared_food_targets_keep_split_orders_equal_and_prevent_buy_sell_profit()
    {
        using var whole = Session(Content(capacity: 12), local: [new(PreparedFood, 12)]);
        using var split = Session(Content(capacity: 12), local: [new(PreparedFood, 12)]);
        var wholeRequest = Request(whole, TradeAction.Buy, PreparedFood, 7);
        var total = Quote(whole, wholeRequest).Total!.Value.Value;
        // Low-end targets are 6,6,5,5,4,4,3; unit payments are 60,69,61,72,62,76,65.
        Assert.AreEqual(465L, total);
        Accept(whole, Command(wholeRequest, total));
        long splitTotal = 0;
        for (var i = 0; i < 7; i++)
        {
            var request = Request(split, TradeAction.Buy, PreparedFood, 1);
            var amount = Quote(split, request).Total!.Value.Value;
            splitTotal += amount;
            Accept(split, Command(request, amount));
        }
        Assert.AreEqual(total, splitTotal);
        Assert.AreEqual(whole.Read().Economy.Wallet, split.Read().Economy.Wallet);
        var resale = Quote(whole, wholeRequest with { Action = TradeAction.Sell });
        Assert.AreEqual("insufficient_demand", resale.Blockers.Single().Code);
        RejectWithoutWorldChange(whole, Command(wholeRequest with { Action = TradeAction.Sell }, 0), "insufficient_demand");
    }

    [TestMethod]
    [DataRow("missing_train", "object_missing", "Train")]
    [DataRow("missing_settlement", "object_missing", "Settlement")]
    [DataRow("missing_inventory", "object_missing", "LocalInventory")]
    [DataRow("owned", "invalid_state", "Settlement")]
    [DataRow("hostile", "access_denied", "LocalInventory")]
    [DataRow("platform", "platform_required", "Train.Position")]
    [DataRow("missing_rule", "content_missing", "MarketRule")]
    [DataRow("missing_good", "content_missing", "Good")]
    public void Trade_qualification_failures_are_shared_by_quote_and_atomic_submission(string scenario, string code, string field)
    {
        foreach (var action in new[] { TradeAction.Buy, TradeAction.Sell })
        {
            using var original = Session(local: [new(Clothing, 11)], cargo: [new(Clothing, 1)],
                relation: scenario == "owned" ? SettlementRelation.PlayerOwned
                    : scenario == "hostile" ? SettlementRelation.Hostile : SettlementRelation.NonHostile);
            if (scenario == "platform") Accept(original, new CoreCommand.StopTrain(new(1)));
            var content = Changed(original.State.Content, '4');
            content = scenario switch
            {
                "missing_rule" => content with { MarketRules = [] },
                "missing_good" => content with { Goods = content.Goods.Where(g => g.Good != Clothing).ToImmutableArray() },
                _ => content
            };
            using var candidate = scenario is "missing_rule" or "missing_good"
                ? WorldFixture.Success(CoreFactory.Create().PrepareRestore(WorldFixture.Success(original.Capture(CapturePurpose.Save)), content, WorldFixture.Options)) : null;
            using var restored = candidate?.Activate();
            var session = restored is null ? original : (CoreSession)restored;
            var request = Request(session, action, Clothing, 1);
            request = scenario switch
            {
                "missing_train" => request with { Train = new(ulong.MaxValue) },
                "missing_settlement" => request with { Settlement = new(ulong.MaxValue) },
                "missing_inventory" => request with { LocalInventory = new(ulong.MaxValue) },
                _ => request
            };
            var quote = Quote(session, request);
            Assert.AreEqual(0L, quote.CurrentlyExecutableQuantity);
            Assert.IsNull(quote.Total);
            Assert.AreEqual(code, quote.Blockers.Single().Code);
            Assert.AreEqual(field, quote.Blockers.Single().Field);
            RejectWithoutWorldChange(session, Command(request, action == TradeAction.Buy ? 1000 : 0), code, field);
        }
    }

    [TestMethod]
    public void Amount_and_wallet_overflow_fault_queries_without_freezing_and_freeze_submission_atomically()
    {
        using var amount = Session(Content(value: long.MaxValue,
            prices: new(new(0), new(10000), new(10000), new(0))), local: [new(Voxel, 2)], money: long.MaxValue);
        AssertOverflow(amount, Request(amount, TradeAction.Buy, Voxel, 2));
        using var wallet = Session(cargo: [new(Clothing, 1)], money: long.MaxValue);
        AssertOverflow(wallet, Request(wallet, TradeAction.Sell, Clothing, 1));
    }

    [TestMethod]
    [TestCategory("integration")]
    public void Full_catalog_and_large_stock_measure_queries_and_reject_extreme_quantity_before_pricing()
    {
        using var session = Session(Content(capacity: 200000, value: 1,
            prices: new(new(0), new(10000), new(10000), new(0))), local: [new(Voxel, 100000)], money: 1000000);
        var request = Request(session, TradeAction.Buy, Voxel, 100000);
        var stopwatch = Stopwatch.StartNew();
        var settlement = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(request.Settlement)).Value;
        stopwatch.Stop();
        TestContext.WriteLine($"FindSettlement: {stopwatch.Elapsed.TotalMilliseconds:F3} ms; catalog={settlement.Market.Goods.Length}; stock=100000");
        Assert.IsTrue(settlement.Market.Goods.Length >= Content().Goods.Length);
        stopwatch.Restart();
        AssertQuote(session, Quote(session, request), request, 100000, 100000);
        stopwatch.Stop();
        TestContext.WriteLine($"QuoteTrade(100000): {stopwatch.Elapsed.TotalMilliseconds:F3} ms");
        stopwatch.Restart();
        Assert.AreEqual("insufficient_goods", Quote(session, request with { Quantity = long.MaxValue }).Blockers.Single().Code);
        stopwatch.Stop();
        TestContext.WriteLine($"QuoteTrade(long.MaxValue), rejected before pricing: {stopwatch.Elapsed.TotalMilliseconds:F3} ms");
        Accept(session, Command(request, 100000));
    }

    private static void AssertOverflow(CoreSession session, TradeQuoteRequest request)
    {
        var before = session.State;
        Assert.IsInstanceOfType<CoreResult<TradeQuote>.Faulted>(session.Read().QuoteTrade(request));
        Assert.AreEqual(before, session.State);
        Assert.IsNull(session.Read().Fault);
        var result = Submit(session, Command(request, request.Action == TradeAction.Buy ? long.MaxValue : 0));
        Assert.IsInstanceOfType<CommandDecision.Faulted>(result.Receipt.Decision);
        Assert.IsNotNull(session.Read().Fault);
        Assert.AreEqual(before.World, session.State.World);
        Assert.AreEqual(before.Position, session.Read().Position);
        Assert.IsTrue(result.Events.IsEmpty);
    }

    private static TradeQuoteRequest Request(CoreSession session, TradeAction action, GoodId good, long quantity)
    {
        var settlement = session.State.World.Settlements.Single();
        return new(session.State.World.Trains.Single().Id, settlement.Id, settlement.Inventory, action, good, quantity);
    }

    private static TradeQuote Quote(CoreSession session, TradeQuoteRequest request)
        => WorldFixture.Success(session.Read().QuoteTrade(request));

    private static CoreCommand Command(TradeQuoteRequest request, long limit = 0) => request.Action switch
    {
        TradeAction.Buy => new CoreCommand.BuyGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity, new(limit)),
        TradeAction.Sell => new CoreCommand.SellGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity, new(limit)),
        TradeAction.Load => new CoreCommand.LoadGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity),
        _ => new CoreCommand.UnloadGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity)
    };

    private static void AssertQuote(CoreSession session, TradeQuote quote, TradeQuoteRequest request, long quantity, long total)
    {
        Assert.AreEqual(request, quote.Request);
        Assert.AreEqual(session.Read().Revision, quote.Revision);
        Assert.AreEqual(quantity, quote.CurrentlyExecutableQuantity);
        Assert.AreEqual(new Money(total), quote.Total);
        Assert.IsTrue(quote.Blockers.IsEmpty);
    }
}
