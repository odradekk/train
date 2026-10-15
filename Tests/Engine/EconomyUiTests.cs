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
            await _input.Enter(Find<LineEdit>(economy, "Quantity"), EconomyFixture.LargeQuantity.ToString());
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
        var after = EconomyFixture.Create(2, tradeTotal: 900);
        CoreProblem[] problems = [new("insufficient_money", "wallet", new WorldObjectId.Settlement(new(1)), ["reason-a"]),
            new("insufficient_capacity", "inventory", new WorldObjectId.Inventory(new(10)), ["reason-b"])];
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        var command = new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food, 1, new(731));
        setup.Session.QueueSubmit(command, CommandSource.UserInterface,
            Completed(1, before, after, new CommandDecision.Rejected([.. problems])), after);
        setup.Session.QueueSubmit(command with { MaximumPayment = new(900) }, CommandSource.UserInterface,
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
            Require(Find<Label>(economy, "Quote").Text.Contains("总额：900"), "拒绝后未按新视图自动报价。");
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            Require(setup.Session.SubmitCount == 2, "再次确认未使用新报价。");
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
            await _input.Choose(Find<OptionButton>(economy, "LocalInventory"), 1);
            await Market(economy);
            await _input.Choose(Find<OptionButton>(economy, "Action"), 2);
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, loaded));
            await _input.Choose(Find<OptionButton>(economy, "Action"), 3);
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            await _input.Activate(Find<Button>(economy, "ConfirmTrade"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, unloaded));
            Require(Find<OptionButton>(economy, "LocalInventory").Selected == 1 && setup.Session.SubmitCount == 2,
                "装卸没有保留租用库存或命令次数错误。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Rent_and_renew_select_rule_multiples_and_preserve_core_price_across_revisions()
    {
        LeaseRequest rent = new LeaseRequest.Rent(new(1), 3, EconomyFixture.LeaseDuration * 2);
        LeaseRequest renew = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 3);
        var quotes = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>
        {
            [rent] = new CoreResult<LeaseQuote>.Success(new(rent, new(1), new(827), 3, [])),
            [new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration)] =
                new CoreResult<LeaseQuote>.Success(new(new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration), new(1), new(419), 3, [])),
            [new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 2)] =
                new CoreResult<LeaseQuote>.Success(new(new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 2), new(1), new(619), 3, [])),
            [renew] = new CoreResult<LeaseQuote>.Success(new(renew, new(1), new(953), 3, []))
        };
        var before = EconomyFixture.Create(leaseQuotes: quotes);
        var changed = EconomyFixture.Create(2, leaseQuotes: new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>(quotes)
        {
            [rent] = new CoreResult<LeaseQuote>.Success(new(rent, new(2), new(109), 3, []))
        });
        var rented = EconomyFixture.Create(3, leaseQuotes: quotes);
        var renewed = EconomyFixture.Create(4);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueAdvance(new(1, changed.Position, [], null, null), changed);
        setup.Session.QueueSubmit(new CoreCommand.AcquireLease(rent, new(827)),
            CommandSource.UserInterface, Completed(1, changed, rented, new CommandDecision.Accepted([], [])), rented);
        setup.Session.QueueSubmit(new CoreCommand.AcquireLease(renew, new(953)),
            CommandSource.UserInterface, Completed(2, rented, renewed, new CommandDecision.Accepted([], [])), renewed);
        await Run(setup, async (ui, economy) =>
        {
            await Page(economy, 3);
            await _input.Enter(Find<LineEdit>(economy, "LeaseCapacity"), "3");
            var multiple = Find<OptionButton>(economy, "LeaseMultiple");
            Require(multiple.ItemCount == 3 && multiple.Selected == 0, "租期倍数没有使用规则上限或默认1倍。");
            await _input.Choose(multiple, 1);
            var duration = Find<Label>(economy, "LeaseDuration").Text;
            Require(duration.Contains("1 游戏日") && duration.Contains("2 游戏日"), "固定租期与本次租期未以游戏日显示。");
            await _input.Activate(Find<Button>(economy, "QuoteLease"));
            Require(Find<Label>(economy, "LeaseQuote").Text.Contains("总额：827"), "租赁未保留核心哨兵总额。");
            await UiCapture.Capture(TestScene, "economy-lease");
            var host = ui.GetParent<Train.App.App>().Host!;
            await _input.ClickAcrossRevision(Find<Button>(economy, "ConfirmLease"), () =>
            {
                Require(host.Step(1).Problem is null, "不能发布租赁确认修订。");
                economy.Refresh(host.GetStatus()!.Binding, changed, host.Content!);
                Require(ReferenceEquals(multiple, Find<OptionButton>(economy, "LeaseMultiple")) && multiple.Selected == 1 &&
                    Find<Label>(economy, "LeaseQuote").Text.Contains("总额：827"), "普通修订重建倍数选择或替换旧报价。");
            });
            await _input.WaitUntil(() => ReferenceEquals(ui.View, rented));
            await _input.Choose(Find<OptionButton>(economy, "Lease"), 1);
            Require(!Find<Label>(economy, "LeaseQuote").Text.Contains("827") && Find<Button>(economy, "ConfirmLease").Disabled,
                "切换续租仍保留新租旧报价。");
            await _input.Choose(Find<OptionButton>(economy, "LeaseMultiple"), 2);
            Require(Find<Label>(economy, "LeaseDuration").Text.Contains("3 游戏日"), "续租未显示本次追加租期。");
            await _input.Activate(Find<Button>(economy, "QuoteLease"));
            Require(Find<Label>(economy, "LeaseQuote").Text.Contains("总额：953"), "续租总额未取核心报价。");
            await _input.Choose(Find<OptionButton>(economy, "LeaseMultiple"), 1);
            Require(!Find<Label>(economy, "LeaseQuote").Text.Contains("953") && Find<Button>(economy, "ConfirmLease").Disabled,
                "改变租期倍数没有清除旧报价。");
            await _input.Choose(Find<OptionButton>(economy, "LeaseMultiple"), 2);
            await _input.Activate(Find<Button>(economy, "QuoteLease"));
            await _input.Activate(Find<Button>(economy, "ConfirmLease"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, renewed));
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Renewal_rechecks_duration_blockers_and_keeps_disabled_selection_with_visible_failures()
    {
        LeaseRequest first = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration);
        LeaseRequest second = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 2);
        LeaseRequest third = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 3);
        var before = EconomyFixture.Create();
        var changed = EconomyFixture.Create(2, leaseQuotes: new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>
        {
            [first] = new CoreResult<LeaseQuote>.Success(new(first, new(2), null, 3,
                [new("insufficient_capacity", "Request.Capacity", null, ["fixture:capacity"])])),
            [second] = new CoreResult<LeaseQuote>.Success(new(second, new(2), null, 3,
                [new("invalid_state", "Request.DurationSteps", null, ["fixture:duration"])])),
            [third] = new CoreResult<LeaseQuote>.Failure([new("invalid_state", "lease", null, ["fixture:failure"])])
        });
        var faulted = EconomyFixture.Create(3, leaseQuotes: new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>
        {
            [first] = new CoreResult<LeaseQuote>.Success(new(first, new(3), new(251), 3, [])),
            [second] = new CoreResult<LeaseQuote>.Faulted(new("fixture:fault", new(0, 3), "QuoteLease", "lease", null), null),
            [third] = new CoreResult<LeaseQuote>.Success(new(third, new(3), new(503), 3, []))
        });
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            await Page(economy, 3);
            await _input.Choose(Find<OptionButton>(economy, "Lease"), 1);
            var multiple = Find<OptionButton>(economy, "LeaseMultiple");
            await _input.Choose(multiple, 1);
            await _input.Activate(Find<Button>(economy, "QuoteLease"));
            var host = ui.GetParent<Train.App.App>().Host!;
            void Advance(Train.Core.ContractSupport.FixedCoreReadView next)
            {
                setup.Session.QueueAdvance(new(1, next.Position, [], null, null), next);
                Require(host.Step(1).Problem is null, "不能发布续租测试修订。");
                economy.Refresh(host.GetStatus()!.Binding, next, host.Content!);
            }
            Advance(changed);
            Require(ReferenceEquals(multiple, Find<OptionButton>(economy, "LeaseMultiple")) && multiple.Selected == 1 &&
                !multiple.IsItemDisabled(0) && multiple.IsItemDisabled(1) && multiple.IsItemDisabled(2),
                "续租未按新修订核心租期阻塞更新，或错误禁用了容量受阻选项。");
            Require(Find<Button>(economy, "QuoteLease").Disabled && Find<Button>(economy, "ConfirmLease").Disabled,
                "已选租期变不可用仍允许报价或确认。");
            var problems = Find<Label>(economy, "LeaseTermProblems").Text;
            Require(problems.Contains("续租后剩余租期不能超过最长租期 3 游戏日") &&
                problems.Contains("Request.DurationSteps") && problems.Contains("fixture:duration") && problems.Contains("fixture:failure"),
                "禁用租期没有显示核心阻塞和Failure原因。");
            Advance(faulted);
            Require(multiple.Selected == 1 && multiple.IsItemDisabled(1) && !multiple.IsItemDisabled(2) &&
                Find<Label>(economy, "LeaseTermProblems").Text.Contains("fixture:fault"), "续租故障原因丢失或修订未恢复可选项。");
            Require(setup.Session.SubmitCount == 0, "不可用续租提交了命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Renewal_groups_identical_duration_problems_and_explains_the_rule_limit()
    {
        var quotes = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>();
        foreach (var multiple in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })
        {
            LeaseRequest request = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * (ulong)multiple);
            quotes.Add(request, new CoreResult<LeaseQuote>.Success(new(request, new(1), new(337), 3, [])));
        }
        foreach (var multiple in new[] { 11, 12 })
        {
            LeaseRequest request = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * (ulong)multiple);
            quotes.Add(request, new CoreResult<LeaseQuote>.Success(new(request, new(1), null, 3,
                [new("invalid_state", "Request.DurationSteps", null, ["fixture:full-horizon"])])));
        }
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseQuotes: quotes),
            EconomyFixture.Content(maxLeaseMultiple: 12));
        await Run(setup, async (_, economy) =>
        {
            await Page(economy, 3);
            await _input.Choose(Find<OptionButton>(economy, "Lease"), 1);
            var multiple = Find<OptionButton>(economy, "LeaseMultiple");
            Require(multiple.ItemCount == 12 && !multiple.IsItemDisabled(9) && multiple.IsItemDisabled(10) && multiple.IsItemDisabled(11),
                "未依据核心结果禁用超期倍数。");
            var problems = Find<Label>(economy, "LeaseTermProblems").Text;
            Require(problems.StartsWith("11、12 倍不可选：续租后剩余租期不能超过最长租期 12 游戏日") &&
                problems.Contains("invalid_state/Request.DurationSteps") && problems.Contains("fixture:full-horizon") &&
                problems.Split('\n').Length == 1, "相同超期原因未合并为一行，或遗漏规则上限和核心问题。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Missing_market_rule_displays_all_problems_and_disables_leasing()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(missingMarketRule: true),
            EconomyFixture.Content(missingMarketRule: true));
        await Run(setup, async (_, economy) =>
        {
            await Page(economy, 3);
            var text = Find<Label>(economy, "LeaseDuration").Text;
            Require(text.Contains("市场规则缺失：fixture:economy") && text.Contains("content_missing/market.rule") &&
                text.Contains("fixture:missing-market") && text.Contains("invalid_state/market.rule") &&
                text.Contains("fixture:invalid-market"), "缺失市场规则的问题未完整显示。");
            Require(Find<OptionButton>(economy, "Lease").Disabled && Find<OptionButton>(economy, "LeaseMultiple").Disabled && !Find<LineEdit>(economy, "LeaseCapacity").Editable &&
                Find<Button>(economy, "QuoteLease").Disabled && Find<Button>(economy, "ConfirmLease").Disabled &&
                Find<Label>(economy, "LeaseQuote").Text.Contains("市场规则缺失"), "缺失市场规则时仍允许租赁或未说明原因。");
            Require(setup.Session.SubmitCount == 0, "缺失市场规则时提交了租赁命令。");
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
            Require(!Find<Label>(economy, "VehicleQuote").Text.Contains("917"), "车辆离开买卖清单后仍显示旧总额。");
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
            var app = ui.GetParent<Train.App.App>();
            Require(app.Saves!.ReturnToMenu() is null, "无法返回主菜单。");
            if (app.Saves.Leaving is not null) app.Saves.ChooseLeave(Train.App.Saves.LeaveChoice.Discard);
            await _input.WaitUntil(() => economy.GetChildCount() == 0);
            Require(app.Host!.GetStatus() is null, "关闭经济窗口时旧对局仍活动。");
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
                economy.Refresh(host.GetStatus()!.Binding, next, host.Content!);
            }
            await Page(economy, 1);
            await _input.Choose(Find<OptionButton>(economy, "Train"), 1, () => Advance(EconomyFixture.Create(2)));
            var good = economy.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First();
            await _input.ClickAcrossRevision(good, () => Advance(changed));
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            Require(Find<Label>(economy, "Quote").Text.Contains("总额：900"), "修订变化吞掉货物选择点击。");
            setup.Session.QueueAdvance(new(1, latest.Position, [], null, null), latest);
            setup.Session.QueueSubmit(new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food, 1, new(900)),
                CommandSource.UserInterface, Completed(1, latest, after, new CommandDecision.Accepted([], [])), after);
            await _input.ClickAcrossRevision(Find<Button>(economy, "ConfirmTrade"), () =>
            {
                Require(host.Step(1).Problem is null, "不能发布价格变动修订。");
                economy.Refresh(host.GetStatus()!.Binding, latest, host.Content!);
                Require(Find<Label>(economy, "Quote").Text.Contains("总额：900"), "普通修订替换了玩家已看到的报价。");
            });
            Require(setup.Session.SubmitCount == 1 && ReferenceEquals(ui.View, after),
                $"跨修订确认没有提交原报价：count={setup.Session.SubmitCount}, revision={ui.View?.Revision}, error={ui.ErrorText}, rect={Find<Button>(economy, "ConfirmTrade").GetGlobalRect()}, quote={Find<Label>(economy, "Quote").Text}。");
            await Page(economy, 2);
            var select = Find<Button>(economy, "SelectBuyVehicle");
            await _input.ClickAcrossRevision(select, () => Advance(EconomyFixture.Create(6)));
            await _input.Activate(Find<Button>(economy, "QuoteVehicle"));
            Require(Find<Label>(economy, "VehicleQuote").Text.Contains("917"), "修订变化吞掉现车选择点击。");
            var vehicleNext = EconomyFixture.Create(7);
            var owned = EconomyFixture.Create(8, vehicle: EconomyFixture.VehicleState.Owned);
            setup.Session.QueueAdvance(new(1, vehicleNext.Position, [], null, null), vehicleNext);
            setup.Session.QueueSubmit(new CoreCommand.BuyVehicle(new(1), new(9), new(917)), CommandSource.UserInterface,
                Completed(2, vehicleNext, owned, new CommandDecision.Accepted([], [])), owned);
            await _input.ClickAcrossRevision(Find<Button>(economy, "ConfirmVehicle"), () =>
            {
                Require(host.Step(1).Problem is null, "不能发布现车确认修订。");
                economy.Refresh(host.GetStatus()!.Binding, vehicleNext, host.Content!);
            });
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
            await _input.Activate(Find<Button>(economy, "QuoteTrade"));
            var text = Find<Label>(economy, "Quote").Text;
            Require(text.Contains("报价不锁定") && text.Contains("当前可执行数量：1") &&
                text.Contains("insufficient_goods/quantity") && text.Contains("fixture:shortage") &&
                text.Contains("content_missing/goods") && text.Contains("fixture:missing"), "报价遗漏可执行数量、短缺风险或不锁定提示。");
            Require(Find<Button>(economy, "ConfirmTrade").Disabled && text.Contains("可执行数量不足"), "数量不足而无Blockers时未说明禁用确认的原因。");
            Require(setup.Session.SubmitCount == 0, "获取风险报价提交了命令。");
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
        await _input.Choose(Find<OptionButton>(economy, "Train"), 1);
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

}
