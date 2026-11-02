using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests(Node testScene) : TestClass(testScene)
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
            await _input.Enter(Find<LineEdit>(economy, "Quantity"), EconomyFixture.LargeQuantity.ToString());
            Require(Find<Label>(economy, "Quote").Text.Contains("731"), "报价总额未按核心结果显示。");
            await UiCapture.Capture(TestScene, "economy-market");
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, after));
            Require(setup.Session.SubmitCount == 1, "买入未只提交一次。");
            Require(Find<Label>(economy, "TrainCargo").Text.Contains("9"), "接受后列车库存未刷新。");
            await UiCapture.Capture(TestScene, "economy-inventory");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Rejection_preserves_inputs_reports_all_reasons_and_requires_explicit_retry()
    {
        var before = EconomyFixture.Create();
        var after = EconomyFixture.Create(2, tradeTotal: 900);
        CoreProblem[] problems = [new("insufficient_money", "wallet", new WorldObjectId.Settlement(new(1)), ["reason-a"]),
            new("insufficient_capacity", "inventory", new WorldObjectId.Inventory(new(10)), ["reason-b"])];
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(mapPresentation: true));
        var command = new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food, 1, new(731));
        setup.Session.QueueSubmit(command, CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Rejected([.. problems])), after);
        setup.Session.QueueSubmit(command with { MaximumPayment = new(900) }, CommandSource.UserInterface,
            Completed(2, after, after, new CommandDecision.Accepted([], [])), after);
        await Run(setup, async (ui, economy) =>
        {
            await Market(economy);
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => setup.Session.SubmitCount == 1);
            var result = Find<Label>(economy, "TradeResult").Text;
            Require(result.Contains("资金不足") && result.Contains("可用容量不足") &&
                !result.Contains("insufficient_") && !result.Contains("reason-") &&
                !result.Contains("库存 10") && !result.Contains("聚居地 1") && ui.ErrorText.Length == 0,
                "拒绝原因或位置错误。");
            await UiCapture.Capture(TestScene, "economy-command-rejection");
            Require(Find<LineEdit>(economy, "Quantity").Text == "1", "拒绝丢失数量输入。");
            await _input.Frames(3);
            Require(setup.Session.SubmitCount == 1, "拒绝后自动重试。");
            Require(Find<Label>(economy, "Quote").Text.Contains("总额：900"), "拒绝后未按新视图自动报价。");
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            Require(setup.Session.SubmitCount == 2, "重试未用新报价。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Revision_between_mouse_press_and_release_preserves_selection_and_confirmed_price()
    {
        var before = EconomyFixture.Create();
        var changed = EconomyFixture.Create(3, tradeTotal: 900);
        var latest = EconomyFixture.Create(4, tradeTotal: 1100);
        var after = EconomyFixture.Create(5, foodQuantity: 8);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            var host = ui.GetParent<Train.App.App>().Host!;
            void Advance(Train.Core.ContractSupport.FixedCoreReadView next)
            {
                setup.Session.QueueAdvance(new(1, next.Position, [], null, null), next);
                Require(host.Step(1).Problem is null, "不能发布测试修订。");
                Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, next, host.Content!);
            }
            await Page(economy, 1);
            var goods = economy.FindChildren("SelectGood", "Button", true, false).Cast<Button>().ToArray();
            var good = goods[0];
            await _input.Activate(goods[1]);
            await _input.Enter(Find<LineEdit>(economy, "Quantity"), "1");
            await _input.Choose(Find<OptionButton>(economy, "Train"), 1, () => Advance(EconomyFixture.Create(2)));
            await _input.ClickAcrossRevision(good, () => Advance(changed));
            Require(Find<Label>(economy, "Quote").Text.Contains("总额：900"), "修订变化吞掉货物选择点击。");
            setup.Session.QueueAdvance(new(1, latest.Position, [], null, null), latest);
            setup.Session.QueueSubmit(new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food, 1, new(900)),
                CommandSource.UserInterface, Completed(1, latest, after, new CommandDecision.Accepted([], [])), after);
            await _input.ClickAcrossRevision(Find<Button>(economy, "ConfirmTrade"), () =>
            {
                Require(host.Step(1).Problem is null, "不能发布价格变动修订。");
                Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, latest, host.Content!);
                Require(Find<Label>(economy, "Quote").Text.Contains("总额：900"), "普通修订替换了玩家已看到的报价。");
            });
            Require(setup.Session.SubmitCount == 1 && ReferenceEquals(ui.View, after),
                $"跨修订确认没有提交原报价：count={setup.Session.SubmitCount}, revision={ui.View?.Revision}, error={ui.ErrorText}, rect={Find<Button>(economy, "ConfirmTrade").GetGlobalRect()}, quote={Find<Label>(economy, "Quote").Text}。");
            await Page(economy, 2);
            var select = Find<CheckBox>(economy, "BuyVehicle9");
            await _input.ClickAcrossRevision(select, () => Advance(EconomyFixture.Create(6)));
            Require(select.ButtonPressed && Find<Label>(economy, "PurchaseTotal").Text.Contains("917"), "修订变化吞掉现车选择点击。");
            var vehicleNext = EconomyFixture.Create(7);
            var owned = EconomyFixture.Create(8, vehicle: EconomyFixture.VehicleState.Owned);
            setup.Session.QueueAdvance(new(1, vehicleNext.Position, [], null, null), vehicleNext);
            setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(9), new(917)), CommandSource.UserInterface,
                Completed(2, vehicleNext, owned, new CommandDecision.Accepted([], [])), owned);
            await _input.Activate(Find<Button>(economy, "BuyVehicles"));
            var confirmation = Find<ConfirmationDialog>(economy, "BuyVehiclesConfirmation");
            var confirmedText = confirmation.DialogText;
            Require(confirmation.Visible && confirmedText.Contains("917"), "购买确认未显示玩家已看到的报价。");
            Require(host.Step(1).Problem is null, "不能发布现车确认修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, vehicleNext, host.Content!);
            Require(confirmation.Visible && confirmation.DialogText == confirmedText, "普通修订替换了购买确认中的报价。");
            await _input.Activate(Find<Button>(confirmation, "BuyOnly"), confirmation);
            Require(setup.Session.SubmitCount == 2 && ReferenceEquals(ui.View, owned), "跨修订现车确认没有成交。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Quote_displays_all_shortage_warnings_executable_quantity_and_nonlocking_notice()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(shortage: true, executableQuantity: 1), EconomyFixture.Content());
        await Run(setup, async (_, economy) =>
        {
            await Market(economy);
            await _input.Enter(Find<LineEdit>(economy, "Quantity"), EconomyFixture.LargeQuantity.ToString());
            var text = Find<Label>(economy, "Quote").Text;
            Require(text.Contains("报价不锁定") && text.Contains("当前可执行数量：1") &&
                text.Contains("货物不足") && text.Contains("所需内容缺失") &&
                !text.Contains("insufficient_goods") && !text.Contains("content_missing") &&
                !text.Contains("fixture:shortage") && !text.Contains("fixture:missing"), "报价遗漏可执行数量、短缺风险或不锁定提示。");
            Require(Find<Button>(economy, "ConfirmTrade").Disabled && text.Contains("可执行数量不足"), "数量不足而无Blockers时未说明禁用确认的原因。");
            Require(setup.Session.SubmitCount == 0, "获取风险报价提交了命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Settlement_form_actions_stay_inside_the_default_window()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            var window = ui.GetTree().Root;
            var mode = window.Mode;
            var size = window.Size;
            try
            {
                window.Mode = Window.ModeEnum.Windowed;
                window.Size = new Vector2I(1920, 1080);
                await _input.Frames(2);
                var visible = window.GetVisibleRect().Size;
                Require(visible == new Vector2(1920, 1080), $"默认窗口不是 1920×1080：{visible}。");
                await Market(economy);
                RequireInWindow(Find<Button>(economy, "ConfirmTrade"));
                await Page(economy, 2);
                RequireInWindow(Find<Button>(economy, "BuyVehicles"));
                RequireInWindow(Find<Button>(economy, "FormParked"));
                RequireInWindow(Find<Button>(economy, "SellVehicles"));
                RequireInWindow(Find<Button>(economy, "RepairVehicles"));
                await Page(economy, 3);
                RequireInWindow(Find<Button>(economy, "ConfirmLease"));
                await Page(economy, 7);
                RequireInWindow(Find<Button>(economy, "ConfirmService"));
                RequireInWindow(Find<Button>(economy, "RefreshServiceQuote"));
            }
            finally
            {
                window.Mode = mode;
                window.Size = size;
                await _input.Frames(2);
            }
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Settlement_inspector_confirms_war_and_blocks_map_clicks()
    {
        var setup = new HostSimulationTests.HostSetup(
            EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned), EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            var shell = Find<GameShell>(ui, "*");
            await _input.ClickItem(NodeObjects(ui), FirstSelectableItem(NodeObjects(ui)));
            economy = shell.InspectorContent!;
            await _input.Activate(Find<Button>(economy, "DeclareWar"));
            var confirmation = Find<ConfirmationDialog>(economy, "DeclareWarConfirmation");
            Require(confirmation.Visible && setup.Session.SubmitCount == 0, "宣战没有先展示确认。");
            await _input.Activate(confirmation.GetCancelButton(), confirmation);
            var map = Find<MacroMap>(ui, "MacroMap");
            WorldObjectId? clicked = null;
            map.ObjectClicked += id => clicked = id;
            var editor = ui.GetParent<Train.App.App>().Editor!;
            var error = ui.GetNode<Control>("Root/ErrorPanel");
            if (error.Visible) await _input.Activate(Find<Button>(error, "Close"));
            await _input.Click(economy.GetGlobalRect().Position + new Vector2(8, 8));
            await _input.Frames(3);
            Require(clicked is null && !editor.IsOpen && !error.Visible && setup.Session.SubmitCount == 0,
                "检查器输入穿透到地图、触发了隐藏的改装动作或提交了命令。");
        });
        setup.Session.AssertComplete();
    }

    private static IEnumerable<Button> Named(Node parent, string name) =>
        parent.FindChildren(name, "Button", true, false).Cast<Button>();

    private static void RequireInWindow(Control control)
    {
        var rect = control.GetGlobalRect();
        var window = control.GetViewport().GetVisibleRect();
        Require(control.IsVisibleInTree() && rect.Position.X >= window.Position.X - 1 && rect.Position.Y >= window.Position.Y - 1 &&
            rect.End.X <= window.End.X + 1 && rect.End.Y <= window.End.Y + 1,
            $"{control.Name} 不在默认窗口内：控件 {rect}，窗口 {window}。");
    }

    private async Task Market(Control economy)
    {
        await Page(economy, 1);
        var select = economy.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First();
        await _input.Activate(select);
        await _input.Enter(Find<LineEdit>(economy, "Quantity"), "1");
    }

    private Task Page(Control economy, int index)
    {
        var title = index switch { 1 => "市场", 2 => "车辆", 3 => "仓库", _ => "建造与工地" };
        var tabs = Find<TabContainer>(economy, "Pages");
        return _input.ChooseTab(tabs, Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == title));
    }

}
