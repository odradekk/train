using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class EconomyUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Buy_preserves_integer_quantity_total_and_refreshes_inventory()
    {
        var before = EconomyFixture.Create();
        var after = EconomyFixture.Create(2, foodQuantity: 9);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food,
            EconomyFixture.LargeQuantity, new(731)), CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Accepted([], [])), after);
        await Run(setup, async (ui, economy) =>
        {
            await Market(economy);
            await Enter(Find<LineEdit>(economy, "Quantity"), EconomyFixture.LargeQuantity.ToString());
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            Require(Find<Label>(economy, "Quote").Text.Contains("731"), "报价总额未按核心结果显示。");
            await UiCapture.Capture(TestScene, "economy-market");
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            Require(setup.Session.SubmitCount == 1, "买入未只提交一次。");
            await Page(economy, 0);
            Require(economy.FindChildren("GoodName", "Label", true, false).Cast<Label>()
                .Any(label => label.IsVisibleInTree() && label.Text.Contains("主食 × 9")), "接受后库存未刷新。");
            await UiCapture.Capture(TestScene, "economy-inventory");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Rejection_preserves_inputs_reports_all_reasons_and_requires_explicit_retry()
    {
        var before = EconomyFixture.Create();
        var after = EconomyFixture.Create(2);
        CoreProblem[] problems = [new("insufficient_money", "wallet", new WorldObjectId.Settlement(new(1)), ["reason-a"]),
            new("insufficient_capacity", "inventory", new WorldObjectId.Inventory(new(10)), ["reason-b"])];
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        var command = new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food, 1, new(731));
        setup.Session.QueueSubmit(command, CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Rejected([.. problems])), after);
        setup.Session.QueueSubmit(command, CommandSource.UserInterface,
            Completed(2, after, after, new CommandDecision.Accepted([], [])), after);
        await Run(setup, async (ui, economy) =>
        {
            await Market(economy);
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => setup.Session.SubmitCount == 1);
            Require(ui.ErrorText.Contains("insufficient_money/wallet") && ui.ErrorText.Contains("insufficient_capacity/inventory") &&
                ui.ErrorText.Contains("reason-a") && ui.ErrorText.Contains("reason-b"), "拒绝原因未完整显示。");
            Require(Find<LineEdit>(economy, "Quantity").Text == "1", "拒绝丢失数量输入。");
            await _input.Frames(3);
            Require(setup.Session.SubmitCount == 1, "拒绝后自动重提命令。");
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            Require(setup.Session.SubmitCount == 2, "显式重新报价确认未重提保留输入。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Load_and_unload_keep_selected_lease_inventory()
    {
        var before = EconomyFixture.Create();
        var loaded = EconomyFixture.Create(2, foodQuantity: 3);
        var unloaded = EconomyFixture.Create(3, foodQuantity: 4);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.LoadGoods(new(7), new(1), new(11), EconomyFixture.Food, 1),
            CommandSource.UserInterface, Completed(1, before, loaded, new CommandDecision.Accepted([], [])), loaded);
        setup.Session.QueueSubmit(new CoreCommand.UnloadGoods(new(7), new(1), new(11), EconomyFixture.Food, 1),
            CommandSource.UserInterface, Completed(2, loaded, unloaded, new CommandDecision.Accepted([], [])), unloaded);
        await Run(setup, async (ui, economy) =>
        {
            await Choose(Find<OptionButton>(economy, "LocalInventory"), 1);
            await Market(economy);
            await Choose(Find<OptionButton>(economy, "Action"), 2);
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, loaded));
            await Choose(Find<OptionButton>(economy, "Action"), 3);
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, unloaded));
            Require(Find<OptionButton>(economy, "LocalInventory").Selected == 1 && setup.Session.SubmitCount == 2,
                "装卸没有保留租用库存或命令次数错误。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Rent_and_renew_preserve_core_amount_and_convert_seconds()
    {
        var before = EconomyFixture.Create();
        var rented = EconomyFixture.Create(2);
        var renewed = EconomyFixture.Create(3);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.AcquireLease(new LeaseRequest.Rent(new(1), 3, 216000), new(419)),
            CommandSource.UserInterface, Completed(1, before, rented, new CommandDecision.Accepted([], [])), rented);
        setup.Session.QueueSubmit(new CoreCommand.AcquireLease(new LeaseRequest.Renew(new(1), 216000), new(419)),
            CommandSource.UserInterface, Completed(2, rented, renewed, new CommandDecision.Accepted([], [])), renewed);
        await Run(setup, async (ui, economy) =>
        {
            await Page(economy, 3);
            await Enter(Find<LineEdit>(economy, "LeaseCapacity"), "3");
            Require(Find<LineEdit>(economy, "LeaseSeconds").Text == "3600", "租期默认秒数错误。");
            await _input.Activate(Find<Button>(economy, "QuoteLease"));
            Require(Find<Label>(economy, "LeaseQuote").Text.Contains("总额：419"), "租赁未显示核心总额。");
            await UiCapture.Capture(TestScene, "economy-lease");
            await _input.Activate(Find<Button>(economy, "ConfirmLease"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, rented));
            await Choose(Find<OptionButton>(economy, "Lease"), 1);
            await _input.Activate(Find<Button>(economy, "QuoteLease"));
            await _input.Activate(Find<Button>(economy, "ConfirmLease"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, renewed));
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Purchased_vehicle_enters_existing_formation_with_same_identity()
    {
        var before = EconomyFixture.Create();
        var owned = EconomyFixture.Create(2, vehicle: EconomyFixture.VehicleState.Owned);
        var formed = EconomyFixture.Create(3, vehicle: EconomyFixture.VehicleState.Formed);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(9), new(917)), CommandSource.UserInterface,
            Completed(1, before, owned, new CommandDecision.Accepted([], [])), owned);
        setup.Session.QueueSubmit(new CoreCommand.FormTrain([new(9)]), CommandSource.UserInterface,
            Completed(2, owned, formed, new CommandDecision.Accepted([new WorldObjectId.Train(new(8)), new WorldObjectId.Inventory(new(13))], [])), formed);
        await Run(setup, async (ui, economy) =>
        {
            await Page(economy, 2);
            await _input.Activate(Find<Button>(economy, "SelectBuyVehicle"));
            await _input.Activate(Find<Button>(economy, "QuoteVehicle"));
            Require(Find<Label>(economy, "VehicleQuote").Text.Contains("917"), "车辆总额未按核心报价显示。");
            await UiCapture.Capture(TestScene, "economy-vehicle");
            await _input.Activate(Find<Button>(economy, "ConfirmVehicle"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, owned));
            await _input.Activate(Find<Button>(economy, "FormPurchased"));
            var formation = ui.GetNode<TrainFormationPanel>("Root/World/TrainFormationPanel");
            var available = Find<ItemList>(formation, "AvailableVehicles");
            Require(available.ItemCount == 1 && available.GetItemText(0).Contains("车辆 9"), "买后原车辆未进入编组候选。");
            await _input.ClickItem(available, 0);
            await _input.Activate(Find<Button>(formation, "AddSelected"));
            Require(Find<ItemList>(formation, "OrderedVehicles").ItemCount == 1,
                $"真实输入未把车辆9加入编组：availableSelected={string.Join(',', available.GetSelectedItems())}，rect={available.GetGlobalRect()}，hover={TestScene.GetTree().Root.GuiGetHoveredControl()?.GetPath()}。");
            await _input.Activate(Find<Button>(formation, "Form"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, formed));
            Require(formed.FindTrain(new(8)) is Lookup<TrainView>.Found purchased &&
                purchased.Value.Vehicles.SequenceEqual(new VehicleId[] { new(9) }) &&
                formed.FindTrain(new(7)) is Lookup<TrainView>.Found existing &&
                existing.Value.Vehicles.SequenceEqual(new VehicleId[] { new(10) }) &&
                formed.FindInstance(new InstanceId.Vehicle(new(9))) is Lookup<InstanceView>.Found
                    { Value.Details: InstanceDetails.Vehicle { Train: { Value: 8 } } },
                "购入车辆没有进入新列车8，或原列车7被替换。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Capacity_blockers_and_stale_bindings_disable_submission_with_both_windows_open()
    {
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned, forSale: 0, purchaseAvailable: 0, blocked: true);
        var content = EconomyFixture.Content();
        var setup = new HostSimulationTests.HostSetup(view, content);
        await Run(setup, async (ui, economy) =>
        {
            await Page(economy, 2);
            Require(Find<Label>(economy, "SoldOut").Text.Contains("售罄") &&
                Find<Label>(economy, "PurchaseCapacity").Text.Contains("收购名额：0"), "售罄或收购名额未展示。");
            await _input.Activate(Find<Button>(economy, "SelectSellVehicle"));
            await _input.Activate(Find<Button>(economy, "QuoteVehicle"));
            Require(Find<Button>(economy, "ConfirmVehicle").Disabled &&
                Find<Label>(economy, "VehicleQuote").Text.Contains("insufficient_capacity"), "收购受阻时不得允许卖车。");
            await Market(economy);
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            Require(Find<Button>(economy, "ConfirmTrade").Disabled &&
                Find<Label>(economy, "Quote").Text.Contains("insufficient_money") &&
                Find<Label>(economy, "Quote").Text.Contains("insufficient_capacity"), "受阻报价未禁止提交或遗漏原因。");
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Fleet"));
            await _input.ClickItem(ui.GetNode<ItemList>("Root/World/WorldPanels/FleetList/Layout/Body/Objects"), 0);
            await _input.Activate(Find<Button>(ui.GetNode<WorldPanels>("Root/World/WorldPanels"), "Inventory"));
            Require(economy.GetNode<Control>("Settlement").IsVisibleInTree() &&
                economy.GetNode<Control>("TrainInventory").IsVisibleInTree(), "聚居地与列车库存窗口未能同时打开。");
            economy.Refresh(new HostBinding(Guid.NewGuid()), view, content);
            Require(Find<Button>(economy, "QuoteTrade").Disabled && !Find<LineEdit>(economy, "Quantity").Editable,
                "旧绑定窗口仍允许操作。");
            var binding = ui.GetParent<Train.App.App>().Host!.GetStatus()!.Binding;
            economy.Refresh(binding, view, EconomyFixture.Content(2));
            Require(Find<Button>(economy, "QuoteTrade").Disabled && Find<Button>(economy, "ConfirmTrade").Disabled,
                "旧内容世代窗口仍允许操作。");
            Require(setup.Session.SubmitCount == 0, "无效窗口提交了命令。");
        });
        setup.Session.AssertComplete();
    }

    private async Task Run(HostSimulationTests.HostSetup setup, Func<GameUi, EconomyPanels, Task> body)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            await _input.ClickItem(ui.GetNode<ItemList>("Root/World/WorldPanels/NodesList/Layout/Body/Objects"), 0);
            await _input.Activate(Find<Button>(ui.GetNode<WorldPanels>("Root/World/WorldPanels"), "Economy"));
            await body(ui, ui.GetNode<EconomyPanels>("Root/World/EconomyPanels"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private async Task Market(EconomyPanels economy)
    {
        await Page(economy, 1);
        await Choose(Find<OptionButton>(economy, "Train"), 1);
        var select = economy.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First();
        await _input.Activate(select);
    }

    private async Task Page(EconomyPanels economy, int index)
    {
        var tabs = Find<TabContainer>(economy, "Pages");
        var bar = tabs.GetTabBar();
        await _input.WaitUntil(() => bar.IsVisibleInTree() && bar.Size.X > 0 && bar.Size.Y > 0 &&
            bar.GetTabRect(index).Size.X > 0 && bar.GetTabRect(index).Size.Y > 0 &&
            bar.GetTabIdxAtPoint(bar.GetTabRect(index).GetCenter()) == index);
        await _input.Frames();
        await _input.Click(bar.GlobalPosition + bar.GetTabRect(index).GetCenter());
        Require(tabs.CurrentTab == index,
            $"实际点击未切换经济页签：global={bar.GetGlobalRect()}，tab={bar.GetTabRect(index)}，viewport={TestScene.GetViewport().GetVisibleRect()}，current={tabs.CurrentTab}，hover={TestScene.GetTree().Root.GuiGetHoveredControl()?.GetPath()}。");
        await _input.Frames();
    }

    private async Task Choose(OptionButton choice, int index)
    {
        Require(choice.IsVisibleInTree(), "下拉选择不可见。");
        choice.GrabFocus();
        await _input.PressKey(Key.Space);
        var popup = choice.GetPopup();
        await _input.WaitUntil(() => popup.Visible && popup.GetFocusedItem() >= 0);
        var focused = popup.GetFocusedItem();
        var direction = focused < index ? Key.Down : Key.Up;
        for (var move = 0; move < Math.Abs(index - focused); move++) await _input.PressKey(direction);
        var beforeEnter = popup.GetFocusedItem();
        await _input.PressKey(Key.Enter);
        Require(choice.Selected == index,
            $"实际输入未选择目标选项：popupVisible={popup.Visible}，initialFocus={focused}，beforeEnterFocus={beforeEnter}，currentFocus={popup.GetFocusedItem()}，selected={choice.Selected}，target={index}。");
    }

    private async Task Enter(LineEdit edit, string value)
    {
        Require(edit.IsVisibleInTree(), "输入框不可见。");
        edit.GrabFocus(); await _input.PressKey(Key.A, true);
        foreach (var digit in value) await _input.PressKey((Key)((long)Key.Key0 + digit - '0'), unicode: digit);
    }

    private static SubmissionResult Completed(ulong number, ICoreReadView before, ICoreReadView after, CommandDecision decision)
        => new SubmissionResult.Completed(new(number, before.Position, after.Position, decision), [], null);
    private static T Find<T>(Node root, string name) where T : Node
        => root.FindChildren(name, typeof(T).Name, true, false).Cast<T>().Single();
}
