using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Settlements;
using Train.Core.Trading;
using Train.Core.Tests.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

public sealed partial class InventoryTradeTests
{
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

}
