using Chickensoft.GoDotTest;
using Godot;
using Train.App.Notifications;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class GameShellNotificationTests(Node testScene) : TestClass(testScene)
{
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
