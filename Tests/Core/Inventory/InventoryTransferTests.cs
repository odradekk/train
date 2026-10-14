using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using static Train.Core.Tests.Inventory.InventoryFixture;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class InventoryTransferTests
{
    [TestMethod]
    public void Transfers_keep_three_good_id_kinds_distinct_and_old_views_unchanged()
    {
        using var session = Session();
        var lease = Rent(session, 30);
        Service(session);
        var train = session.State.World.Trains.Single();
        var old = session.Read();
        foreach (var good in Goods)
        {
            var result = Accept(session, new CoreCommand.UnloadGoods(train.Id, lease.Settlement, lease.Inventory, good, 2));
            var transfer = result.Events.Select(e => e.Event).OfType<CoreEvent.GoodsTransferred>().Single();
            Assert.AreEqual(good, transfer.Good);
            Assert.AreEqual(2L, transfer.Quantity);
            Assert.AreEqual(new Money(0), transfer.ActualAmount);
            Assert.AreEqual(train.Inventory, transfer.Source);
            Assert.AreEqual(lease.Inventory, transfer.Destination);
        }
        var local = ReadInventory(session, lease.Inventory);
        Assert.AreEqual(6L, local.Used);
        Assert.AreEqual(3, local.Stacks.Length);
        Assert.IsTrue(local.Stacks.All(s => s.Quantity == 2));
        foreach (var good in Goods)
            Accept(session, new CoreCommand.LoadGoods(train.Id, lease.Settlement, lease.Inventory, good, 1));
        Assert.IsTrue(ReadInventory(session, train.Inventory).Stacks.All(s => s.Quantity == 9));
        Assert.AreEqual(30L, ((Lookup<InventoryView>.Found)old.FindInventory(train.Inventory)).Value.Used);
        Assert.AreEqual(0L, ((Lookup<InventoryView>.Found)old.FindInventory(lease.Inventory)).Value.Used);
    }

    [TestMethod]
    public void Whole_order_rejects_without_mutation_for_platform_stock_and_capacity()
    {
        using var session = Session();
        var lease = Rent(session, 3);
        var train = session.State.World.Trains.Single();
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(train.Id, lease.Settlement, lease.Inventory, Voxel, 2));
        Service(session);
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(train.Id, lease.Settlement, lease.Inventory, Voxel, 11));
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(train.Id, lease.Settlement, lease.Inventory, Voxel, 4));
        Accept(session, new CoreCommand.UnloadGoods(train.Id, lease.Settlement, lease.Inventory, Voxel, 3));
        RejectWithoutWorldChange(session, new CoreCommand.LoadGoods(train.Id, lease.Settlement, lease.Inventory, Voxel, 4));
        Assert.AreEqual(3L, ReadInventory(session, lease.Inventory).Used);
        Assert.AreEqual(27L, ReadInventory(session, train.Inventory).Used);
    }

    [TestMethod]
    public void Nonhostile_npc_inventory_is_unfrozen_but_player_cannot_transfer_directly()
    {
        using var session = Session(Content(localStock: 5));
        Service(session);
        var settlement = session.State.World.Settlements.Single();
        Assert.AreEqual(InventoryAccess.ReadWrite, ReadInventory(session, settlement.Inventory).Access);
        RejectWithoutWorldChange(session, new CoreCommand.LoadGoods(new(1), settlement.Id, settlement.Inventory, Voxel, 1), "not_owned");
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(new(1), settlement.Id, settlement.Inventory, Voxel, 1), "not_owned");
    }

    [TestMethod]
    public void Published_capacity_loss_preserves_goods_and_still_allows_withdrawal()
    {
        using var session = Session();
        var lease = Rent(session);
        Service(session);
        Accept(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Voxel, 7));
        var content = Changed(Content(), '7');
        content = content with { Designs = content.Designs.Select(d => d.Design == WorldFixture.Station ? d with
        { Parts = d.Parts.Select(p => p with { Capabilities = [new PartCapability.Platform(), new PartCapability.Storage(5)] }).ToImmutableArray() } : d).ToImmutableArray() };
        Accept(session, new CoreCommand.PublishContent(content));
        Assert.AreEqual(7L, ReadInventory(session, lease.Inventory).Used);
        Assert.AreEqual(0L, ReadInventory(session, lease.Inventory).Available);
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Voxel, 1));
        Accept(session, new CoreCommand.LoadGoods(new(1), lease.Settlement, lease.Inventory, Voxel, 2));
        Assert.AreEqual(5L, ReadInventory(session, lease.Inventory).Used);
    }

    [TestMethod]
    public void Restore_marks_missing_good_and_preserves_stack_but_rejects_both_transfer_directions()
    {
        using var original = Session();
        var lease = Rent(original);
        Service(original);
        Accept(original, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 2));
        var saved = WorldFixture.Success(original.Capture(CapturePurpose.Save));
        var content = Changed(Content(), '8') with { Goods = Content().Goods.Where(g => g.Good != Composite).ToImmutableArray() };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, content, WorldFixture.Options));
        using var session = (CoreSession)candidate.Activate();
        var stack = ReadInventory(session, lease.Inventory).Stacks.Single();
        Assert.AreEqual(2L, stack.Quantity);
        Assert.IsTrue(stack.DefinitionMissing);
        RejectWithoutWorldChange(session, new CoreCommand.LoadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 1));
        RejectWithoutWorldChange(session, new CoreCommand.UnloadGoods(new(1), lease.Settlement, lease.Inventory, Composite, 1));
    }
}
