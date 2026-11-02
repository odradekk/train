using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.App.Notifications;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SettingsUiTests
{
    [Test]
    public async Task All_notification_categories_fit_at_1080p_and_each_row_has_one_response()
    {
        await WithOverlay(async (app, overlay, input) =>
        {
            await input.Activate(Find<Button>(overlay, "Notifications"));
            var table = overlay.FindChildren("NotificationTable", "GridContainer", true, false).Cast<GridContainer>().SingleOrDefault();
            Require(table is not null, "通知应显示为一张响应表。");
            Require(table!.GetChildren().OfType<CheckBox>().Count() ==
                Enum.GetValues<NotificationCategory>().Length * Enum.GetValues<NotificationAction>().Length,
                "每个通知类别应恰有一行，不能遗漏或重复。");
            await input.Frames(3);
            var page = Find<ScrollContainer>(overlay, "NotificationsPage");
            Require(overlay.GetViewportRect().Size == new Vector2(1920, 1080), "通知布局断言必须使用 1920×1080 视口。");
            Require(page.GetGlobalRect().Encloses(table!.GetGlobalRect()) && !page.GetVScrollBar().IsVisibleInTree() &&
                !page.GetHScrollBar().IsVisibleInTree(), "1920×1080 下完整通知表应在可见区域内，且不出现滚动条。");
            foreach (var category in Enum.GetValues<NotificationCategory>())
            {
                var choices = Enum.GetValues<NotificationAction>().Select(action =>
                    Find<CheckBox>(table, $"Notify_{NotificationPolicy.Key(category)}_{action}")).ToArray();
                Require(choices.All(choice => choice.IsVisibleInTree()) && choices.Count(choice => choice.ButtonPressed) == 1,
                    $"通知类别必须完整显示且只有一个响应：{category}。");
            }
            await UiCapture.Capture(TestScene, "settings-notifications");
            foreach (var action in Enum.GetValues<NotificationAction>())
            {
                await input.Activate(Find<CheckBox>(table, "Notify_encounter_" + action));
                Require(app.Notifications.Action(NotificationCategory.Encounter) == action,
                    "真实输入没有立即更新遇敌响应。");
                using var saved = new ConfigFile();
                Require(saved.Load("user://settings.cfg") == Error.Ok && saved.GetValue("notifications", "encounter").AsInt32() == (int)action,
                    "通知响应没有写入设置文件。");
            }
        });
    }

    [Test]
    public async Task Resetting_notifications_restores_all_defaults_and_persists_them()
    {
        await WithOverlay(async (app, overlay, input) =>
        {
            await input.Activate(Find<Button>(overlay, "Notifications"));
            var reset = overlay.FindChildren("ResetNotifications", "Button", true, false).Cast<Button>().SingleOrDefault();
            Require(reset is not null, "通知页必须提供恢复默认。");
            foreach (var category in Enum.GetValues<NotificationCategory>())
                Find<CheckBox>(overlay, $"Notify_{NotificationPolicy.Key(category)}_Pause").ButtonPressed = true;
            await input.Activate(reset!);
            using var saved = new ConfigFile();
            Require(saved.Load("user://settings.cfg") == Error.Ok, "无法读取恢复默认后的设置。");
            foreach (var category in Enum.GetValues<NotificationCategory>())
            {
                var expected = category == NotificationCategory.Encounter ? NotificationAction.SlowToOne : NotificationAction.None;
                Require(app.Notifications.Action(category) == expected &&
                    Find<CheckBox>(overlay, $"Notify_{NotificationPolicy.Key(category)}_{expected}").ButtonPressed &&
                    saved.GetValue("notifications", NotificationPolicy.Key(category)).AsInt32() == (int)expected,
                    $"默认只有遇敌降到 1×，其余只显示；恢复默认后还需保存：{category}。");
                var selected = Enum.GetValues<NotificationAction>().Where(action =>
                    Find<CheckBox>(overlay, $"Notify_{NotificationPolicy.Key(category)}_{action}").ButtonPressed).ToArray();
                Require(selected.Length == 1,
                    $"恢复默认后每行必须恰好选中一个响应：{category}，实际选中={string.Join(", ", selected)}。");
            }
            await input.Click(Find<CheckBox>(overlay, "Notify_encounter_Pause").GetGlobalRect().GetCenter());
            Require(saved.Load("user://settings.cfg") == Error.Ok &&
                app.Notifications.Action(NotificationCategory.Encounter) == NotificationAction.Pause &&
                saved.GetValue("notifications", "encounter").AsInt32() == (int)NotificationAction.Pause,
                $"恢复默认后真实输入选择遇敌暂停必须立即更新策略并写入设置文件：策略={app.Notifications.Action(NotificationCategory.Encounter)}，" +
                $"设置文件={saved.GetValue("notifications", "encounter").AsInt32()}。");
        });
    }
}
