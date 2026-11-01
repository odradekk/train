using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainPlanWorkspaceTests
{
    [Test]
    public async Task Search_result_click_survives_a_world_revision()
    {
        var sent = new List<CoreCommand>();
        await WithWorkspace(sent, async (panel, binding, parent, view, content) =>
        {
            Open(panel, binding, Train(view, 7), view, content);
            await EnterText(Field(panel, "Search"), "聚居地");
            var next = World(revision: 2, running: true);
            Require(next.Revision != view.Revision, "场景必须在按下与松开之间推进修订。");
            var input = new UiTestInput(TestScene);
            await input.ClickAcrossRevision(Button(panel, "Add1"), () => panel.Refresh(binding, next, content));
            Require(Stops(panel).GetChildCount() == 1, "修订变化丢失了搜索结果点击。");
            await Click(panel, "Apply");
            Require(sent.Count == 1 && sent[0] is CoreCommand.ReplaceRunPlan { Plan.Stops.Length: 1 } command &&
                command.Plan.Stops[0] is PlanStop.Station { Node.Value: 1 }, "搜索点击没有加入保存的计划。");
        }, view: World(running: true));
    }

    [Test]
    public async Task Place_names_keep_content_names_written_without_chinese_characters()
    {
        await WithWorkspace([], async (panel, binding, parent, view, content) =>
        {
            Open(panel, binding, Train(view, 7) with { Plan = new(false,
                [new PlanStop.Waypoint(new(3)), new PlanStop.Waypoint(new(5))]) }, view, content);
            Require(Button(Card(panel, 0), "Select").Text.Contains("空节点 · Oasis"), "地形内容名称被隐藏。");
            Require(Button(Card(panel, 1), "Select").Text.Contains("生物聚落 · Nest"), "生物聚落类型名称被隐藏。");
            await EnterText(Field(panel, "Search"), "Nest");
            Require(Button(panel, "Add5").Text == "生物聚落 · Nest", "不能用内容名称找到地点。");
        });
    }

    [Test]
    public async Task Leased_inventory_labels_show_capacity_and_expiry()
    {
        foreach (var expired in new[] { false, true })
            await WithWorkspace([], (panel, binding, parent, view, content) =>
            {
                Open(panel, binding, Train(view, 7) with { Plan = new(false, [
                    new PlanStop.Station(new(1), 0, [new(new AutomaticGoodsAction.Load(), EconomyFixture.Food, new(11), 20)])]) }, view, content);
                var inventory = Choice(Rule(panel, 0), "Inventory");
                var label = inventory.GetItemText(inventory.Selected);
                Require(label.Contains("容量 20") && label.Contains(expired ? "已到期" : "第2天 00:00 到期"),
                    "租用仓库标签没有说明容量和到期状态。");
                return Task.CompletedTask;
            }, view: World(expired: expired));
    }
}
