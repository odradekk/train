using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Transfer_actions_only_appear_with_a_lease_and_multiple_leases_require_a_choice()
    {
        var none = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseCount: 0), EconomyFixture.Content());
        await Run(none, async (_, panel) =>
        {
            await Market(panel);
            Require(Find<OptionButton>(panel, "Action").ItemCount == 2, "无租约时只能买入或卖出。");
            Require(!Find<OptionButton>(panel, "TradeLease").IsVisibleInTree(), "无租约时不应显示仓库选择。");
        });
        none.Session.AssertComplete();
        var view = EconomyFixture.Create(leaseCount: 2);
        var multiple = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        multiple.Session.QueueSubmit(new CoreCommand.LoadGoods(new(7), new(1), new(14), EconomyFixture.Food, 1),
            CommandSource.UserInterface, Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
        await Run(multiple, async (_, panel) =>
        {
            await Market(panel);
            await _input.Choose(Find<OptionButton>(panel, "Action"), 2);
            var lease = Find<OptionButton>(panel, "TradeLease");
            Require(lease.IsVisibleInTree() && Find<Button>(panel, "ConfirmTrade").Disabled,
                "多租约时应选择仓库，选择前不能装卸。");
            await _input.Choose(lease, 2);
            await _input.Activate(Find<Button>(panel, "ConfirmTrade"));
            Require(lease.Selected == 2, "装卸后应保留所选仓库。");
        });
        multiple.Session.AssertComplete();
    }
    [Test]
    public async Task Load_and_unload_keep_selected_lease_inventory()
    {
        InventoryView Warehouse(long quantity) => new(new(11), new InventoryOwner.Lease(new(1)), new WorldLocation.Node(new(1)),
            InventoryAccess.ReadWrite, 20, quantity + 1, 2, 17 - quantity, 1.25,
            [new(EconomyFixture.Food, quantity, false), new(EconomyFixture.Equipment, 1, false)], []);
        var before = EconomyFixture.Create(leaseInventory: Warehouse(9));
        var loaded = EconomyFixture.Create(2, foodQuantity: 5, leaseInventory: Warehouse(8));
        var unloaded = EconomyFixture.Create(3, foodQuantity: 4, leaseInventory: Warehouse(9));
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.LoadGoods(new(7), new(1), new(11), EconomyFixture.Food, 1),
            CommandSource.UserInterface, Completed(1, before, loaded, new CommandDecision.Accepted([], [])), loaded);
        setup.Session.QueueSubmit(new CoreCommand.UnloadGoods(new(7), new(1), new(11), EconomyFixture.Food, 1),
            CommandSource.UserInterface, Completed(2, loaded, unloaded, new CommandDecision.Accepted([], [])), unloaded);
        await Run(setup, async (ui, economy) =>
        {
            await Market(economy);
            await _input.Choose(Find<OptionButton>(economy, "Action"), 2);
            Require(Find<Label>(economy, "WarehouseCargo").Text == "仓库中主食：9 · 剩余空间 8", "单份仓库也应显示所选货物数量和剩余空间。");
            Require(Find<Button>(economy, "ConfirmTrade").Text.Contains("· 0"), "仓库装卸应显示免费。");
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, loaded));
            Require(Find<Label>(economy, "WarehouseCargo").Text == "仓库中主食：8 · 剩余空间 9", "装车后仓库货物与可用空间应刷新。");
            await _input.Choose(Find<OptionButton>(economy, "Action"), 3);
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, unloaded));
            Require(setup.Session.SubmitCount == 2,
                "装卸没有保留租用库存或命令次数错误。");
        });
        setup.Session.AssertComplete();
    }

}
