using Godot;
using Train.App;
using Train.App.Notifications;

namespace Train.Ui;

public partial class SettingsOverlay
{
    private void BuildNotifications(VBoxContainer body)
    {
        var table = new GridContainer { Name = "NotificationTable", Columns = 4 };
        var hint = UiStyle.Label("选择每类通知出现时游戏怎么做。通知本身总会显示，实际等级随事件而定。");
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(hint);
        table.AddThemeConstantOverride("v_separation", 2);
        table.AddThemeConstantOverride("h_separation", 16);
        body.AddChild(table);
        foreach (var title in new[] { "通知", "只显示", "降到 1× 速度", "暂停" })
        {
            var label = UiStyle.Label(title);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.HorizontalAlignment = title == "通知" ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            table.AddChild(label);
        }
        foreach (var (title, categories) in new (string, NotificationCategory[])[]
        {
            ("严重", [NotificationCategory.Encounter, NotificationCategory.Destruction, NotificationCategory.RunDestroyed, NotificationCategory.Population]),
            ("警告", [NotificationCategory.RetreatStarted, NotificationCategory.Surrender, NotificationCategory.Relation, NotificationCategory.Construction,
                NotificationCategory.Railway, NotificationCategory.TrainOperation, NotificationCategory.Resource, NotificationCategory.Lease]),
            ("完成", [NotificationCategory.RetreatCompleted, NotificationCategory.LairCleared, NotificationCategory.Founding]),
            ("信息", [NotificationCategory.CombatEnded])
        })
        {
            table.AddChild(UiStyle.Label(title, true));
            for (var column = 0; column < 3; column++) table.AddChild(new Control());
            foreach (var category in categories)
            {
                table.AddChild(UiStyle.Label(NotificationPolicy.Label(category)));
                var group = new ButtonGroup();
                foreach (var action in Enum.GetValues<NotificationAction>())
                {
                    var label = action switch { NotificationAction.None => "只显示", NotificationAction.SlowToOne => "降到 1× 速度", _ => "暂停" };
                    var choice = new CheckBox { Name = $"Notify_{NotificationPolicy.Key(category)}_{action}",
                        ButtonGroup = group, ButtonPressed = _actions[(int)category] == action,
                        CustomMinimumSize = new(24, 24), SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
                        AccessibilityName = NotificationPolicy.Label(category) + "：" + label,
                        TooltipText = NotificationPolicy.Label(category) + "：" + label };
                    choice.Toggled += selected => { if (selected) { _actions[(int)category] = action; Save(); } };
                    table.AddChild(choice);
                }
            }
        }
        body.AddChild(UiStyle.Button("ResetNotifications", "恢复默认", () =>
        {
            _actions = Defaults();
            foreach (var category in Enum.GetValues<NotificationCategory>())
                table.GetNode<CheckBox>($"Notify_{NotificationPolicy.Key(category)}_{_actions[(int)category]}").SetPressedNoSignal(true);
            Save();
        }));
    }
}
