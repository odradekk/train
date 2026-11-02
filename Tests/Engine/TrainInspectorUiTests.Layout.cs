using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainInspectorUiTests
{
    [Test]
    public async Task Long_pages_scroll_while_the_main_actions_stay_inside_the_inspector()
    {
        var window = TestScene.GetTree().Root;
        var size = window.Size;
        var scale = window.ContentScaleFactor;
        var view = new InspectorView();
        view.Train = view.Train with { Plan = new(true, Enumerable.Range(0, 40)
            .Select(_ => (PlanStop)new PlanStop.Station(new(1), 60, [])).ToImmutableArray()) };
        try
        {
            window.ContentScaleFactor = 1;
            await WithInspector(view, async (ui, panel, setup) =>
            {
                var input = new UiTestInput(TestScene);
                foreach (var resolution in new[] { new Vector2I(1280, 720), new Vector2I(1920, 1080) })
                {
                    window.Size = resolution; await input.Frames(5);
                    var tabs = Find<TabContainer>(panel, "Pages");
                    for (var page = 0; page < 4; page++)
                    {
                        await input.ChooseTab(tabs, page);
                        var actions = Find<Control>(panel, "Actions").GetGlobalRect();
                        var bounds = panel.GetGlobalRect();
                        Require(Find<Button>(panel, "EditPlan").IsVisibleInTree() && actions.Position.Y >= bounds.Position.Y && actions.End.Y <= bounds.End.Y + 1 &&
                            actions.Position.Y >= tabs.GetGlobalRect().End.Y && tabs.Size.Y > 0,
                            $"页面内容把主操作挤出检查器：{resolution}，第 {page + 1} 页。");
                    }
                    await input.ChooseTab(tabs, 1);
                    var scroll = tabs.GetChild<ScrollContainer>(1);
                    Require(scroll.GetVScrollBar().MaxValue > scroll.GetVScrollBar().Page, "长计划没有独立滚动区。");
                }
            });
        }
        finally { window.Size = size; window.ContentScaleFactor = scale; }
    }
}
