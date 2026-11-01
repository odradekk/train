using Station = Train.Core.Contracts.PlanStop.Station;
using Waypoint = Train.Core.Contracts.PlanStop.Waypoint;
using ReplacePlan = Train.Core.Contracts.CoreCommand.ReplaceRunPlan;
using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainPlanWorkspaceTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Long_plan_stays_below_the_wrapped_top_bar()
    {
        var tree = TestScene.GetTree();
        var size = tree.Root.Size;
        var scale = tree.Root.ContentScaleFactor;
        var canvas = tree.Root.ContentScaleSize;
        try
        {
            await WithWorkspace([], async (panel, binding, parent, view, content) =>
            {
                var ui = Find<GameUi>(tree.Root, "GameUi");
                Open(panel, binding, Train(view, 7) with { Plan = new(true,
                    Enumerable.Range(0, 40).Select(_ => (PlanStop)new Station(new(1), 60, [])).ToImmutableArray()) }, view, content);
                float firstHeight = 0;
                foreach (var (windowSize, uiScale) in new[] {
                    (new Vector2I(1920, 1080), 1f), (new Vector2I(1280, 720), 1.25f), (new Vector2I(1280, 720), 1.75f),
                    (new Vector2I(800, 720), 2f), (new Vector2I(960, 1080), 2f) })
                {
                    tree.Root.ContentScaleSize = windowSize.X == 960 ? Vector2I.Zero : canvas;
                    tree.Root.Size = windowSize;
                    tree.Root.ContentScaleFactor = uiScale;
                    await new UiTestInput(TestScene).Frames(6);
                    var bounds = Find<Control>(panel, "Plan").GetGlobalRect();
                    var bar = ui.GetNode<PanelContainer>("Root/World/Bar").GetGlobalRect();
                    var map = Find<TrainPlanMap>(panel, "RouteMap");
                    foreach (var node in view.World.Nodes)
                        Require(new Rect2(Vector2.Zero, map.Size).HasPoint(map.MapToScreen(node.Position)),
                            $"地图尺寸变化后地点被裁掉：{windowSize} × {uiScale}，{map.Size}。");
                    Require(bounds.Position.Y >= bar.End.Y && bounds.End.Y <= tree.Root.GetVisibleRect().End.Y,
                        $"计划超出顶栏下方：{windowSize} × {uiScale}，{bounds}，{bar}。");
                    if (firstHeight == 0) { firstHeight = bar.Size.Y; await UiCapture.Capture(TestScene, "long-plan"); }
                    else
                    {
                        if (windowSize.X == 960) Require(bar.Size.Y > firstHeight, "窄视口顶栏未换行。");
                        foreach (var name in new[] { "Navigation", "ListRegion", "InspectorRegion", "WorkspaceRegion" })
                            Require(Find<Control>(ui, name).GetGlobalRect().Position.Y >= bar.End.Y, $"{name} 侵入顶栏。");
                        Require(((GameShell)ui.World).SceneArea.Position.Y >= bar.End.Y, "场景侵入顶栏。");
                    }
                }
            }, shell: true);
        }
        finally { tree.Root.ContentScaleSize = canvas; tree.Root.Size = size; tree.Root.ContentScaleFactor = scale; }
    }

    [Test]
    public async Task Map_search_and_stop_controls_build_the_ordered_plan()
    {
        var sent = new List<CoreCommand>();
        await WithWorkspace(sent, async (panel, binding, parent, view, content) =>
        {
            Open(panel, binding, Train(view, 7), view, content);
            await MapClick(panel, view, 2);
            Require(Choice(panel, "Kind").Selected == 0, "普通点击类型错误。");
            await MapClick(panel, view, 1, shift: true);
            Require(Choice(panel, "Kind").Selected == 1, "Shift 添加类型错误。");
            await MapClick(panel, view, 3);
            Require(Choice(panel, "Kind").Selected == 1, "空节点类型错误。");
            await Click(Card(panel, 3), "Remove");
            foreach (var id in new ulong[] { 4, 5 })
            {
                await MapClick(panel, view, id);
                Require(Choice(panel, "Kind").Selected == 1, "敌对聚居地或生物聚落只能途经。");
                await Click(Card(panel, 3), "Remove");
            }
            await Click(Card(panel, 2), "MoveUp");
            Require(Choice(panel, "Kind").Selected == 1, "上移没有保留途经点选择。");
            await Click(Card(panel, 2), "MoveUp");
            await Click(Card(panel, 1), "MoveDown");
            await Click(Card(panel, 1), "Select");
            Require(Choice(panel, "Kind").Selected == 1, "排序后编辑对象错误。");
            await EnterText(Field(panel, "Search"), "聚居地");
            var results = Find<VBoxContainer>(panel, "SearchResults");
            await Click(results, "Add1");
            await Click(panel, "Loop");
            await Click(panel, "SaveOnly");
            Require(sent.Count == 1 && sent[0] is ReplacePlan { Plan.Loop: true } command &&
                command.Plan.Stops.Length == 4 && command.Plan.Stops[0] is Station { Node.Value: 1 } &&
                command.Plan.Stops[1] is Waypoint { Node.Value: 1 } &&
                command.Plan.Stops[2] is Station { Node.Value: 2 } &&
                command.Plan.Stops[3] is Station { Node.Value: 1 },
                "保存的站序或循环不符。");
        });
    }

    [Test]
    public async Task Cancel_discards_edits_and_save_preserves_exact_data()
    {
        var sent = new List<CoreCommand>();
        await WithWorkspace(sent, async (panel, binding, parent, view, content) =>
        {
            var input = new UiTestInput(TestScene);
            var first = new AutomaticGoodsRule(new AutomaticGoodsAction.Buy(new(91)), EconomyFixture.Food, new(10), EconomyFixture.LargeQuantity);
            var second = new AutomaticGoodsRule(new AutomaticGoodsAction.Sell(null), EconomyFixture.Equipment, new(10), 4);
            var a = Train(view, 7) with { Plan = new(false, [new Station(new(1), 60001, [first, second])]) };
            var b = Train(view, 8) with { Plan = new(false, [new Station(new(1), 61, [second])]) };
            Open(panel, binding, a, view, content);
            await input.Enter(Field(panel, "DwellSeconds"), "9");
            await input.Enter(Field(Rule(panel, 0), "TargetQuantity"), "8");
            await Click(panel, "Cancel");
            Require(!panel.Visible && sent.Count == 0, "取消没有关闭或意外提交。");
            Open(panel, binding, b, view, content);
            Require(Stops(panel).GetChildCount() == 1 && Rules(panel).GetChildCount() == 1 &&
                Field(Rule(panel, 0), "TargetQuantity").Text == "4" && !Find<CheckBox>(panel, "Loop").ButtonPressed,
                "取消草稿串入另一列车。");
            await Click(panel, "AddRule");
            Require(Rules(panel).GetChildCount() == 2 && Button(panel, "SaveOnly").Disabled, "未完成的新规则应禁止保存。");
            await Click(Rule(panel, 1), "RemoveRule");
            Require(Rules(panel).GetChildCount() == 1 && !Button(panel, "SaveOnly").Disabled, "删除新规则后仍残留问题。");
            await Click(panel, "Cancel");
            Open(panel, binding, a, view, content);
            await Click(Rule(panel, 0), "RuleDown");
            Require(Text(Rule(panel, 0), "RuleSummary").StartsWith("卖出"), "规则未下移。");
            await Click(Rule(panel, 1), "RuleUp");
            Require(Text(Rule(panel, 0), "RuleSummary").StartsWith("买入"), "规则未上移。");
            await Click(Rule(panel, 0), "RuleDown");
            await Click(panel, "SaveOnly");
            Require(sent.Count == 1 && sent[0] is ReplacePlan command && command.Train == a.Id &&
                command.Plan.Stops[0] is Station { DwellSteps: 60001 } actual &&
                actual.GoodsRules.SequenceEqual(new[] { second, first }),
                "保存改变了精确步数或规则。");
        });
    }

    [Test]
    public async Task Rules_show_local_actions_labels_and_summary()
    {
        await WithWorkspace([], async (panel, binding, parent, view, content) =>
        {
            var input = new UiTestInput(TestScene);
            Open(panel, binding, Train(view, 7) with { Plan = new(false, [
                new Station(new(1), 0, [new(new AutomaticGoodsAction.Buy(new(14)), EconomyFixture.Food, new(10), 40)]),
                new Station(new(2), 0, [new(new AutomaticGoodsAction.Load(), EconomyFixture.Food, new(20), 20)])]) }, view, content);
            var rule = Rule(panel, 0);
            Require(ActionIds(rule).SequenceEqual(new[] { 0, 1, 2, 3 }), "有租约时可用动作错误。");
            var labels = rule.FindChildren("*", "Label", true, false).Cast<Label>().Select(label => label.Text).ToArray();
            Require(labels.Contains("货物") && labels.Contains("目标持有量") && labels.Contains("最高单价") &&
                Text(rule, "RuleSummary") == "买入主食，直到车上有 40；单价高于 14 不买", "标签或买入复述错误。");
            await input.Choose(Choice(rule, "Action"), 1);
            Require(Text(Rule(panel, 0), "RuleSummary") == "卖出主食，直到车上剩 40；单价低于 14 不卖",
                "卖出复述未更新。");
            await Click(Card(panel, 1), "Select");
            Require(ActionIds(Rule(panel, 0)).SequenceEqual(new[] { 2, 3 }), "自有地点动作错误。");
            Require(Text(Rule(panel, 0), "RuleSummary").Contains("直到车上有 20"), "装载复述缺少目标。");
            await Click(Card(panel, 0), "Select");
            panel.Refresh(binding, World(revision: 2, expired: true), content);
            Require(ActionIds(Rule(panel, 0)).SequenceEqual(new[] { 0, 1, 2 }), "到期仓库不能卸载。");
        });
    }

    [Test]
    public async Task Problems_block_saving_until_all_are_repaired()
    {
        var sent = new List<CoreCommand>();
        await WithWorkspace(sent, async (panel, binding, parent, view, content) =>
        {
            var input = new UiTestInput(TestScene);
            Open(panel, binding, Train(view, 7) with { Plan = new(false, [
                new Station(new(999), 0, []), new Station(new(1), 0,
                    [new(new AutomaticGoodsAction.Buy(null), new GoodId.Voxel(new("fixture:ore")), new(12), 4)])]) }, view, content);
            Require(Text(Card(panel, 0), "Problems").Length > 0 &&
                Text(panel, "Reason").Length > 0 && Button(panel, "Apply").Disabled && Button(panel, "SaveOnly").Disabled,
                "失效计划未阻止保存。");
            await Click(Card(panel, 1), "Select");
            Require(Text(Rule(panel, 0), "RuleProblems").Length > 0, "规则旁缺少诊断。");
            await Click(Card(panel, 0), "Remove");
            Require(Button(panel, "SaveOnly").Disabled, "规则问题被忽略。");
            var good = Choice(Rule(panel, 0), "Good");
            await input.Choose(good, Enumerable.Range(0, good.ItemCount).Single(index => good.GetItemText(index) == "主食"));
            Require(Button(panel, "SaveOnly").Disabled && Text(Rule(panel, 0), "RuleProblems").Length > 0,
                "修复货物后仍应诊断非本站库存。");
            var inventory = Choice(Rule(panel, 0), "Inventory");
            await input.Choose(inventory, Enumerable.Range(0, inventory.ItemCount).Single(index => inventory.GetItemText(index) == "聚居地库存"));
            Require(!Button(panel, "SaveOnly").Disabled && Text(Rule(panel, 0), "RuleProblems").Length == 0,
                "修复后仍不能保存。");
            await Click(panel, "SaveOnly");
            Require(sent.Count == 1 && !panel.Visible, "修复后的保存错误。");
        });
    }

    [Test]
    public async Task Rejected_save_or_departure_keeps_the_draft()
    {
        var sent = new List<CoreCommand>();
        var reject = "save";
        await WithWorkspace(sent, async (panel, binding, parent, view, content) =>
        {
            var input = new UiTestInput(TestScene);
            Open(panel, binding, Train(view, 7), view, content);
            Require(Button(panel, "Apply").Text == "保存并发车", "停车主按钮错误。");
            await input.Enter(Field(panel, "DwellSeconds"), "9");
            await Click(panel, "Apply");
            Require(sent.Count == 1 && sent[0] is ReplacePlan && panel.Visible &&
                Text(panel, "Reason").Contains("保存被拒") && Field(panel, "DwellSeconds").Text == "9",
                "保存拒绝后丢草稿或发车。");
            reject = "start"; sent.Clear();
            await Click(panel, "Apply");
            Require(sent.Count == 2 && sent[0] is ReplacePlan && sent[1] is CoreCommand.StartTrain && panel.Visible &&
                Text(panel, "Reason").Contains("已保存") && Text(panel, "Reason").Contains("发车被拒") &&
                Field(panel, "DwellSeconds").Text == "9", "发车拒绝后丢草稿或缺少说明。");
            reject = ""; sent.Clear();
            await Click(panel, "Apply");
            Require(sent.Count == 2 && sent[0] is ReplacePlan && sent[1] is CoreCommand.StartTrain && !panel.Visible,
                "接受后的提交顺序错误。");
        }, decide: (command, error) =>
        {
            if (reject == "save" && command is ReplacePlan) { error("保存被拒"); return null; }
            if (reject == "start" && command is CoreCommand.StartTrain) { error("发车被拒"); return null; }
            return new([], []);
        });
    }

    [Test]
    public async Task Running_train_starts_empty_and_only_saves()
    {
        var sent = new List<CoreCommand>();
        await WithWorkspace(sent, async (panel, binding, parent, view, content) =>
        {
            Open(panel, binding, Train(view, 7), view, content);
            Require(Stops(panel).GetChildCount() == 0 && Button(panel, "Apply").Text == "保存" &&
                !Button(panel, "SaveOnly").Visible, "运行中无计划的入口错误。");
            await MapClick(panel, view, 1);
            await Click(panel, "Apply");
            Require(sent.Count == 1 && sent[0] is ReplacePlan && !panel.Visible, "运行中额外发车。");
        }, view: World(running: true));
    }

    [Test]
    public async Task Hidden_refresh_keeps_edits_and_invalid_session_blocks_save()
    {
        await WithWorkspace([], async (panel, binding, parent, view, content) =>
        {
            Open(panel, binding, Train(view, 7), view, content);
            var input = new UiTestInput(TestScene);
            await input.Enter(Field(panel, "DwellSeconds"), "9");
            parent.Hide();
            panel.Refresh(binding, World(revision: 2), content);
            Require(panel.Visible && !panel.IsVisibleInTree(), "未覆盖祖先隐藏。");
            parent.Show();
            panel.Refresh(binding, World(revision: 2), content);
            Require(Field(panel, "DwellSeconds").Text == "9", "刷新丢失草稿。");
            panel.Refresh(new(Guid.NewGuid()), view, content);
            Require(Button(panel, "Apply").Disabled && Button(panel, "SaveOnly").Disabled &&
                Text(panel, "Reason").Length > 0, "对局失效后未禁止保存。");
        });
    }

}
