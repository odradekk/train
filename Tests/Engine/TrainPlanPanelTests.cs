using System.Globalization;
using System.Reflection;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class TrainPlanPanelTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Opening_another_train_discards_cancelled_edits_and_submits_only_its_plan()
    {
        var submitted = new List<CoreCommand>();
        await WithPanel(submitted.Add, async (panel, binding, parent, view, content) =>
        {
            var a = Train(view, 7) with { Plan = new(true, [
                new PlanStop.Station(new(999), 60001, [
                    new(new AutomaticGoodsAction.Buy(new(91)), EconomyFixture.Equipment, new(12), 9007199254740993),
                    new(new AutomaticGoodsAction.Sell(new(12)), EconomyFixture.Food, new(11), 4)]),
                new PlanStop.Station(new(1), 120, [new(new AutomaticGoodsAction.Load(), EconomyFixture.Food, new(10), 2)]),
                new PlanStop.Waypoint(new(999))]) };
            var b = Train(view, 8) with { Plan = new(false, [
                new PlanStop.Station(new(1), 61, [
                    new(new AutomaticGoodsAction.Unload(), EconomyFixture.Food, new(11), 3)]),
                new PlanStop.Waypoint(new(1))]) };
            void Open(TrainView train) => panel.Open(new(binding, content.Generation, new WorldObjectId.Train(train.Id)), train, view, content);
            Open(a);
            AssertVisiblePlan(panel, a.Plan!, view, content);
            var first = Rows(panel).GetChild<VBoxContainer>(0);
            var rule = first.GetNode<VBoxContainer>("Rules").GetChild<VBoxContainer>(0);
            Require(Selected(Find<OptionButton>(rule, "Good")).Contains("缺失") &&
                Selected(Find<OptionButton>(rule, "Inventory")).Contains("非本站库存 12"),
                "复杂计划未显示缺失货物和保留的非本站库存。");
            first.GetNode<LineEdit>("Stop/DwellSeconds").Text = "9";
            Find<LineEdit>(rule, "TargetQuantity").Text = "8";
            Find<LineEdit>(rule, "UnitPrice").Text = "7";
            var action = Find<OptionButton>(rule, "Action");
            action.Select(3); action.EmitSignal(OptionButton.SignalName.ItemSelected, 3);
            Find<CheckBox>(panel, "Loop").ButtonPressed = false;
            Find<Button>(panel, "Cancel").EmitSignal(Button.SignalName.Pressed);
            Require(!panel.Visible && submitted.Count == 0, "取消草稿没有关闭面板，或提交了命令。");

            Open(b);
            AssertVisiblePlan(panel, b.Plan!, view, content);
            var bRule = Rows(panel).GetChild<VBoxContainer>(0).GetNode<VBoxContainer>("Rules").GetChild<VBoxContainer>(0);
            Find<OptionButton>(bRule, "Good").Select(0);
            Find<LineEdit>(bRule, "TargetQuantity").Text = "99";
            Find<Button>(panel, "Cancel").EmitSignal(Button.SignalName.Pressed);
            Open(b);
            AssertVisiblePlan(panel, b.Plan!, view, content);
            var bInventory = Find<OptionButton>(bRule, "Inventory");
            Require(bInventory.ItemCount == 3 && Selected(bInventory) == "租用库存 11 · 租约 1",
                "当前计划的租用库存选择不正确，或残留上一个计划的非本站库存选项。");
            Find<Button>(panel, "Apply").EmitSignal(Button.SignalName.Pressed);
            Require(submitted.Count == 1 && submitted[0] is CoreCommand.ReplaceRunPlan && !panel.Visible,
                "不编辑直接应用没有恰好提交一次完整计划并关闭面板。");
            var command = (CoreCommand.ReplaceRunPlan)submitted.Single();
            Require(command.Train == b.Id && command.Plan.Loop == b.Plan!.Loop &&
                command.Plan.Stops.Length == b.Plan.Stops.Length, "提交残留了上一个列车、循环状态或站点。");
            for (var index = 0; index < b.Plan.Stops.Length; index++)
            {
                var expected = b.Plan.Stops[index];
                Require(expected is PlanStop.Station station
                    ? command.Plan.Stops[index] is PlanStop.Station actual && actual.Node == station.Node &&
                        actual.DwellSteps == station.DwellSteps && actual.GoodsRules.SequenceEqual(station.GoodsRules)
                    : command.Plan.Stops[index] == expected,
                    "完整 ReplaceRunPlan 没有保留当前列车的节点、精确步数及有序规则全部字段。");
            }
            Open(a);
            AssertVisiblePlan(panel, a.Plan!, view, content);
            Require(submitted.Count == 1, "重新打开原计划额外提交了命令。");
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task Queries_are_shared_per_station_and_hidden_refresh_preserves_the_pending_revision()
    {
        await WithPanel(_ => { }, async (panel, binding, parent, view, content) =>
        {
            var train = Train(view, 7) with { Plan = new(true, [
                new PlanStop.Station(new(1), 61, [
                    new(new AutomaticGoodsAction.Buy(null), EconomyFixture.Food, new(10), 1),
                    new(new AutomaticGoodsAction.Load(), EconomyFixture.Equipment, new(11), 2)]),
                new PlanStop.Station(new(1), 120, [
                    new(new AutomaticGoodsAction.Sell(new(3)), EconomyFixture.Food, new(12), 4)])]) };
            var counted = DispatchProxy.Create<ICoreReadView, CountingView>();
            var counter = (CountingView)(object)counted;
            counter.Inner = view;
            panel.Open(new(binding, content.Generation, new WorldObjectId.Train(train.Id)), train, counted, content);
            var openReads = counter.SettlementReads;
            counter.SettlementReads = 0;
            counter.Inner = EconomyFixture.Create(2, vehicle: EconomyFixture.VehicleState.Formed);
            panel.Refresh(binding, counted, content);
            var visibleReads = counter.SettlementReads;
            counter.SettlementReads = 0;
            parent.Hide();
            Require(panel.Visible && !panel.IsVisibleInTree(), "隐藏情景没有覆盖祖先隐藏而面板自身 Visible 为真的情况。");
            counter.Inner = EconomyFixture.Create(3, vehicle: EconomyFixture.VehicleState.Formed);
            panel.Refresh(binding, counted, content);
            var hiddenReads = counter.SettlementReads;
            counter.SettlementReads = 0;
            parent.Show();
            void Refresh() => panel.Refresh(binding, counted, content);
            TestScene.GetTree().ProcessFrame += Refresh;
            try { await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame); }
            finally { TestScene.GetTree().ProcessFrame -= Refresh; }
            var reopenedReads = counter.SettlementReads;
            Require(openReads is > 0 and <= 1 && visibleReads is > 0 and <= 1 && hiddenReads == 0 && reopenedReads is > 0 and <= 1,
                $"单个站点的查询预算或隐藏修订处理错误：Open={openReads}，可见新修订={visibleReads}，祖先隐藏={hiddenReads}，恢复首帧={reopenedReads}。");
        });
    }

    private async Task WithPanel(Action<CoreCommand> submit,
        Func<TrainPlanPanel, HostBinding, Control, ICoreReadView, IContentView, Task> run)
    {
        var fixture = new Fixture(TestScene.GetTree());
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Formed);
        var content = EconomyFixture.Content();
        var setup = new HostSimulationTests.HostSetup(view, content);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await FramesUntil(TestScene, () => app.Host!.GetStatus() is not null);
            var parent = new Control();
            var panel = new TrainPlanPanel();
            panel.Initialize(app.Host!, (_, command) => { submit(command); return new([], []); },
                error => throw new InvalidOperationException(error));
            parent.AddChild(panel);
            await fixture.AddToRoot(parent);
            await run(panel, app.Host!.GetStatus()!.Binding, parent, view, content);
        }
        finally { await fixture.Cleanup(); }
    }

    private static TrainView Train(ICoreReadView view, ulong id) => ((Lookup<TrainView>.Found)view.FindTrain(new(id))).Value;
    private static VBoxContainer Rows(TrainPlanPanel panel) => panel.GetNode<VBoxContainer>("Plan/Layout/Body/RowsScroll/Rows");
    private static string Selected(OptionButton choice) => choice.GetItemText(choice.Selected);

    private static void AssertVisiblePlan(TrainPlanPanel panel, RunPlan expected, ICoreReadView view, IContentView content)
    {
        Require(panel.Visible && Find<CheckBox>(panel, "Loop").ButtonPressed == expected.Loop &&
            Rows(panel).GetChildCount() == expected.Stops.Length, "重新打开没有恢复当前循环状态和准确站点数。");
        for (var index = 0; index < expected.Stops.Length; index++)
        {
            var row = Rows(panel).GetChild<VBoxContainer>(index);
            var stop = expected.Stops[index];
            var node = stop is PlanStop.Station station ? station.Node : ((PlanStop.Waypoint)stop).Node;
            var nodeChoice = row.GetNode<OptionButton>("Stop/Node");
            var validNode = view.World.Nodes.Any(item => item.Id == node);
            Require(nodeChoice.ItemCount == view.World.Nodes.Length + (validNode ? 0 : 1) &&
                Selected(nodeChoice) == (validNode ? WorldPanels.NodeName(node, view, content) : $"已失效节点 {node.Value}"),
                "节点选择或上一次失效节点的额外选项没有重置。");
            var nodeLabels = view.World.Nodes.Select(item => WorldPanels.NodeName(item.Id, view, content))
                .Concat(validNode ? [] : new[] { $"已失效节点 {node.Value}" });
            Require(Enumerable.Range(0, nodeChoice.ItemCount).Select(nodeChoice.GetItemText).SequenceEqual(nodeLabels),
                "节点条目顺序或文字不符合当前计划。");
            var rules = stop is PlanStop.Station planned ? planned.GoodsRules : [];
            var kind = row.GetNode<OptionButton>("Stop/Kind");
            Require(kind.Selected == (stop is PlanStop.Station ? 0 : 1) && kind.IsItemDisabled(1) == !rules.IsEmpty &&
                row.GetNode<LineEdit>("Stop/DwellSeconds").Editable == (stop is PlanStop.Station) &&
                row.GetNode<VBoxContainer>("Rules").Visible == (stop is PlanStop.Station) &&
                row.GetNode<Button>("AddRule").Visible == (stop is PlanStop.Station) &&
                row.GetNode<VBoxContainer>("Rules").GetChildCount() == rules.Length, "站点类型、规则数量或相关禁用状态没有重置。");
            if (stop is PlanStop.Station timed)
                Require(row.GetNode<LineEdit>("Stop/DwellSeconds").Text ==
                    (timed.DwellSteps / (decimal)SimulationTime.StepsPerSecond).ToString(CultureInfo.InvariantCulture), "停留时间残留了取消的草稿。");
            for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
            {
                var rule = row.GetNode<VBoxContainer>("Rules").GetChild<VBoxContainer>(ruleIndex);
                var value = rules[ruleIndex];
                var action = value.Action switch { AutomaticGoodsAction.Buy => 0, AutomaticGoodsAction.Sell => 1, AutomaticGoodsAction.Load => 2, _ => 3 };
                var price = value.Action switch { AutomaticGoodsAction.Buy buy => buy.MaximumUnitPrice, AutomaticGoodsAction.Sell sell => sell.MinimumUnitPrice, _ => null };
                var good = Find<OptionButton>(rule, "Good");
                var goodIds = content.Core.Goods.Concat(content.Core.DesignGoods).Select(item => item.Good).Append(value.Good).Distinct().ToArray();
                Require(good.ItemCount == goodIds.Length + 1 && Selected(good).StartsWith(WorldPanels.GoodName(value.Good, content)),
                    "货物选择或旧缺失货物的额外选项没有重置。");
                var goodLabels = new[] { "请选择货物" }.Concat(goodIds.Select(id => WorldPanels.GoodName(id, content) +
                    (content.FindGood(id) is ContentLookup<GoodPresentation>.Found { Value.Missing: false } ? "" : "（缺失，保留身份）")));
                Require(Enumerable.Range(0, good.ItemCount).Select(good.GetItemText).SequenceEqual(goodLabels),
                    "货物条目顺序或文字不符合当前内容和缺失身份。");
                Require(Find<OptionButton>(rule, "Action").GetSelectedId() == action &&
                    Find<LineEdit>(rule, "TargetQuantity").Text == value.TargetQuantity.ToString(CultureInfo.InvariantCulture) &&
                    Find<LineEdit>(rule, "UnitPrice").Text == (price?.Value.ToString(CultureInfo.InvariantCulture) ?? "") &&
                    Find<LineEdit>(rule, "UnitPrice").GetParent<Control>().Visible == (action < 2) &&
                    Find<Label>(rule, "UnitPriceLabel").Text == (action == 0 ? "最高单价" : "最低单价"),
                    "动作、目标数量、空限价或价格列显示状态没有按当前规则重置。");
            }
        }
    }

    private class CountingView : DispatchProxy
    {
        public CountingView() { }
        internal ICoreReadView Inner = null!;
        internal int SettlementReads;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ICoreReadView.FindSettlement)) SettlementReads++;
            return targetMethod.Invoke(Inner, args);
        }
    }
}
