using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json.Nodes;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class CashFlowTests
{
    [TestMethod]
    public void Historical_goods_and_disbanded_trains_remain_valid_save_references()
    {
        var good = InventoryTradeTests.Clothing;
        using var session = InventoryTradeTests.Session(local: [new(good, 9)], cargo: [new(good, 1)]);
        var town = session.State.World.Settlements.Single();
        Accept(session, new CoreCommand.SellGoods(new(1), town.Id, town.Inventory, good, 1, new(92)));
        Accept(session, new CoreCommand.StopTrain(new(1)));
        Accept(session, new CoreCommand.DisbandTrain(new(1)));
        Assert.IsInstanceOfType<Lookup<TrainView>.Missing>(session.Read().FindTrain(new(1)));
        var captured = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        Assert.IsTrue(captured.References.Definitions.Any(d => d.Definition == new CoreDefinitionReference.Voxel(((GoodId.Voxel)good).Definition)
            && d.Field.StartsWith("World.CashFlows[", StringComparison.Ordinal)));
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(captured.Encode()));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, session.State.Content, WorldFixture.Options));
        using var restored = candidate.Activate();
        Assert.AreEqual(new TrainId(1), restored.Read().Economy.CashFlows.Single().Train);
    }

    [TestMethod]
    public void Full_history_keeps_more_than_128_renewals_in_commit_order()
    {
        var content = Content(108000, rate: 1);
        content = content with { MarketRules = [content.MarketRules[0] with { MaximumLeaseDurationMultiple = 1000 }] };
        using var session = Session(content);
        var lease = Rent(session, 3, 108000);
        for (var index = 0; index < 140; index++)
            Accept(session, new CoreCommand.AcquireLease(new LeaseRequest.Renew(lease.Id, 108000), new(2)));
        var entries = session.Read().Economy.CashFlows;
        Assert.HasCount(141, entries);
        Assert.IsTrue(entries.All(e => e.Kind == CashFlowKind.Rent && e.Amount == -2));
        Assert.IsTrue(entries.Zip(entries.Skip(1)).All(pair => pair.First.Position.Sequence < pair.Second.Position.Sequence));
        var captured = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(captured.Encode()));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, session.State.Content, WorldFixture.Options));
        using var restored = candidate.Activate();
        CollectionAssert.AreEqual(entries.ToArray(), restored.Read().Economy.CashFlows.ToArray());
    }

    [TestMethod]
    public void Save_restores_full_history_and_rejects_the_previous_format()
    {
        using var session = InventoryTradeTests.Session(local: [new(InventoryTradeTests.Clothing, 13)]);
        var town = session.State.World.Settlements.Single();
        Accept(session, new CoreCommand.BuyGoods(new(1), town.Id, town.Inventory, InventoryTradeTests.Clothing, 2, new(199)));
        var captured = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var payload = JsonNode.Parse(captured.Encode())!;
        Assert.AreEqual(24, payload["core_state_format_version"]!.GetValue<int>());
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(captured.Encode()));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, session.State.Content, WorldFixture.Options));
        using var restored = candidate.Activate();
        CollectionAssert.AreEqual(session.Read().Economy.CashFlows.ToArray(), restored.Read().Economy.CashFlows.ToArray());
        payload["core_state_format_version"] = 23;
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(CoreFactory.Create().DecodeState(payload.ToJsonString()));
    }

    [TestMethod]
    [DataRow("amount", "0")]
    [DataRow("amount", "199")]
    [DataRow("quantity", "0")]
    [DataRow("kind", "999")]
    [DataRow("position", "{\"completed_steps\":999999,\"sequence\":0}")]
    public void Restore_rejects_invalid_cash_history(string field, string value)
    {
        using var session = InventoryTradeTests.Session(local: [new(InventoryTradeTests.Clothing, 13)]);
        var town = session.State.World.Settlements.Single();
        Accept(session, new CoreCommand.BuyGoods(new(1), town.Id, town.Inventory, InventoryTradeTests.Clothing, 2, new(199)));
        var payload = JsonNode.Parse(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Encode())!;
        payload["world"]!["cash_flows"]![0]![field] = JsonNode.Parse(value);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(CoreFactory.Create().DecodeState(payload.ToJsonString()));
    }

    [TestMethod]
    public void Goods_payments_keep_actual_signed_amounts_order_positions_and_old_views()
    {
        var good = InventoryTradeTests.Clothing;
        using var session = InventoryTradeTests.Session(local: [new(good, 13)]);
        var town = session.State.World.Settlements.Single();
        var train = session.State.World.Trains.Single();
        var old = session.Read();
        var purchase = Accept(session, new CoreCommand.BuyGoods(train.Id, town.Id, town.Inventory, good, 2, new(199)));
        var afterBuy = session.Read();
        var sale = Accept(session, new CoreCommand.BuyGoods(train.Id, town.Id, town.Inventory, good, 1, new(long.MaxValue)));
        var actual = session.Read().Economy.CashFlows;
        Assert.HasCount(2, actual);
        Assert.AreEqual(new CashFlowEntry(purchase.Receipt.After, CashFlowKind.GoodsPurchase, -199,
            town.Id, train.Id, good, 2), actual[0]);
        var receipt = sale.Events.Select(e => e.Event).OfType<CoreEvent.GoodsTransferred>().Single();
        Assert.AreEqual(new CashFlowEntry(sale.Receipt.After, CashFlowKind.GoodsPurchase, -receipt.ActualAmount.Value,
            town.Id, train.Id, good, 1), actual[1]);
        Assert.IsEmpty(old.Economy.CashFlows);
        Assert.HasCount(1, afterBuy.Economy.CashFlows);
        Assert.AreEqual(session.Read().Economy.Wallet.Value - old.Economy.Wallet.Value, actual.Sum(e => e.Amount));
    }
}
