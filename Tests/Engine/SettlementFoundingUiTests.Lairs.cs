using Chickensoft.GoDotTest;
using Godot;
using Train.App.Notifications;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using static Train.Engine.Tests.SettlementFoundingUiFixture;

namespace Train.Engine.Tests;

public partial class SettlementFoundingUiTests
{
    [Test]
    public async Task Creature_notice_opens_lair_inspector_and_clearance_updates_it()
    {
        var before = World(1, null, creaturePresent: true);
        var after = World(2, null, lairCleared: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(lairName: "棘刺聚落"));
        await Run(setup, async ui =>
        {
            var app = ui.GetParent<Train.App.App>();
            var notices = Find<NotificationPanel>(ui, "NotificationPanel");
            notices.Add(new(NotificationCategory.RetreatStarted, NotificationLevel.Warning, "开始撤退。",
                new WorldLocation.Node(new(1)), new WorldObjectId.CreatureGroup(new(1))), before, app.Host!.Content!);
            await _input.Frames(2);
            await _input.Activate(Find<Button>(Find<Control>(ui, "Toasts"), "Notice"));
            var shell = Find<GameShell>(ui, "World");
            var inspector = shell.InspectorContent!;
            var text = Find<Label>(inspector, "Description").Text;
            Require(shell.SelectedObject == new WorldObjectId.Node(new(4)) &&
                Find<Label>(inspector, "Title").Text == "生物聚落 · 棘刺聚落" &&
                text.Contains("聚落类型：棘刺聚落") && text.Contains("节点上没有生物") &&
                text.Contains("玩家列车") && text.Contains("空节点") && !text.Contains("fixture:"),
                "生物群通知必须按群的当前位置打开生物聚落，说明类型和完整清剿条件。");
            Require(!Find<Button>(inspector, "FoundSettlement").Visible, "生物聚落不能建立聚居地。");
            await UiCapture.CaptureSizes(TestScene, "lair-inspector");
            setup.Session.QueueAdvance(new(1, after.Position, [], null, null), after);
            Require(app.Host.Step(1).Problem is null, "无法发布清剿结果。");
            await _input.Frames(3);
            Require(shell.InspectorContent == inspector && Find<Button>(inspector, "FoundSettlement").Visible &&
                !Find<Label>(inspector, "Description").Text.Contains("聚落类型"),
                "清剿后应在原检查器显示空节点和建立条件。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Missing_lair_presentation_uses_player_facing_text()
    {
        var setup = new HostSimulationTests.HostSetup(World(1, null), EconomyFixture.Content());
        await Run(setup, async ui =>
        {
            await OpenDetail(ui, 3);
            var text = Find<Label>(LatestDetail(ui), "Description").Text;
            Require(text.Contains("聚落类型：资料缺失") && !text.Contains("fixture:") && !text.Contains("Lair"),
                "聚落资料缺失时不得显示内部类型标识。");
        });
        setup.Session.AssertComplete();
    }
}
