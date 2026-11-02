using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Notifications;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class GameShellNotificationTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Notification_after_loading_a_save_appears_and_selects_its_object()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var input = new UiTestInput(TestScene);
        Guid? saveId = null;
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy, timeout: ContentPreparationTimeout);
            var host = app.Host!;
            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "通知回归检查的新局请求失败。");
            await FramesUntil(TestScene, () => !host.IsBusy, timeout: ContentPreparationTimeout);
            var created = host.GetStatus()!;
            var saves = app.Saves!;
            saveId = saves.Status!.Identity.Id;
            Require(saves.Save() is null, "通知回归检查的保存请求失败。");
            await FramesUntil(TestScene, () => !saves.HasPendingWork);
            var entry = saves.List().Single(entry => entry.Header.Id == saveId && !entry.Automatic);
            Require(saves.Load(entry) is null, "通知回归检查的读档请求失败。");
            await FramesUntil(TestScene, () => !saves.HasPendingWork && !host.IsBusy, timeout: ContentPreparationTimeout);
            var restored = host.GetStatus()!;
            Require(restored.Binding != created.Binding, "必须在产生通知前真正切换到读档后的会话。");
            var node = restored.View.World.Nodes.First(node =>
                node.Occupant is NodeOccupant.Settlement { Relation: SettlementRelation.NonHostile } &&
                !restored.View.World.Trains.Any(train => train.Position is TrainPosition.Node at && at.Id == node.Id));
            var settlement = ((NodeOccupant.Settlement)node.Occupant).Id;
            var result = host.Submit(restored.Binding, new CoreCommand.DeclareWar(settlement), CommandSource.AutomationTest);
            Require(result is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted },
                "真实核心必须接受宣战并产生关系变化通知。");
            await input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            var shell = ui.GetNode<GameShell>("Root/World");
            var notices = Find<NotificationPanel>(ui, "NotificationPanel");
            var toast = Find<Button>(Find<Control>(ui, "Toasts"), "Notice");
            Require(toast.IsVisibleInTree(), "读档后的新通知必须在屏幕下方实际显示提示。");
            Find<Button>(ui, "Notices").EmitSignal(BaseButton.SignalName.Pressed);
            await input.Frames(2);
            var row = Find<Button>(Find<Control>(notices, "NoticeLog"), "Notice");
            Require(row.IsVisibleInTree() && row.Text == toast.Text, "通知记录中必须实际显示这条新通知。");
            Require(shell.SelectedObject is null && shell.InspectorContent is null, "点击通知前不应已有选中对象或检查器。");
            row.EmitSignal(BaseButton.SignalName.Pressed);
            await input.Frames(2);
            Require(shell.SelectedObject == new WorldObjectId.Settlement(settlement) &&
                shell.InspectorContent == Find<PanelContainer>(ui, "Settlement") && shell.InspectorContent.IsVisibleInTree(),
                "点击通知记录必须在检查器显示该通知相关的聚居地。");
            Require(!app.WorldScenes!.View.Visible, "点击通知记录不能进入局部场景。");
        }
        finally
        {
            await fixture.Cleanup();
            if (saveId is { } id) UiTestInput.DeleteSaves([id]);
        }
    }

    [Test]
    public void Toasts_keep_three_entries_and_critical_notices_do_not_expire()
    {
        var panel = new NotificationPanel { ToastArea = new(0, 0, 800, 600) };
        TestScene.AddChild(panel);
        try
        {
            var view = EconomyFixture.Create();
            var content = EconomyFixture.Content();
            for (var index = 0; index < 4; index++)
                panel.Add(new(NotificationCategory.Construction, NotificationLevel.Information, $"完成 {index}", null, null), view, content);
            var toasts = Find<Control>(panel, "Toasts");
            Require(toasts.GetChildCount() == 3 && panel.UnreadCount == 4, "提示只保留三条，但通知记录和未读数保留全部事件。");
            panel._Process(6.1);
            Require(toasts.GetChildCount() == 0, "普通提示应在六秒后消失。");
            panel.Add(new(NotificationCategory.Destruction, NotificationLevel.Severe, "严重事件", null, null), view, content);
            panel._Process(60);
            Require(toasts.GetChildCount() == 1, "严重提示不能自动消失。");
            panel.ShowLog();
            Require(panel.UnreadCount == 0 && panel.CloseLog() && !panel.CloseLog(), "打开记录清除未读；Esc 只关闭仍打开的通知记录。");
        }
        finally { panel.Free(); }
    }
}
