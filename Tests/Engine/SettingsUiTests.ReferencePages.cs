using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SettingsUiTests
{
    [Test]
    public async Task Keys_lists_current_project_bindings_and_about_shows_version_packages_and_report_entry()
    {
        using var undo = (Godot.Collections.Array)InputMap.ActionGetEvents("editor_undo");
        try
        {
            InputMap.ActionEraseEvents("editor_undo");
            using var rebound = new InputEventKey { Keycode = Key.F6 };
            InputMap.ActionAddEvent("editor_undo", rebound);
            var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            {
                Packages = [new(new(new("fixture"), "设置测试内容包", "1.2.3", [], []), new("fixture"), PackageState.Available, [])]
            };
            await WithOverlay(async (_, overlay, input) =>
            {
                await input.Activate(Find<Button>(overlay, "Keys"));
                var rows = overlay.FindChildren("Key_*", "Label", true, false).Cast<Label>().ToArray();
                using var bindings = (Godot.Collections.Array)InputMap.GetActions();
                for (var index = 0; index < bindings.Count; index++)
                {
                    using var value = bindings[index];
                    using var action = value.AsStringName();
                    if (!action.ToString().StartsWith("ui_", StringComparison.Ordinal))
                        Require(rows.Count(row => row.Name == "Key_" + action) == 1, "按键页必须列出全部项目 InputMap 动作。");
                }
                Require(rows.Single(row => row.Name == "Key_editor_undo").Text == "F6", "按键说明应读取实际绑定，不能复制默认键位。");
                foreach (var name in new[] { "Key_console", "Key_close", "Key_console_complete", "Key_console_history" })
                    Require(rows.Any(row => row.Name == name && !string.IsNullOrWhiteSpace(row.Text)),
                        $"按键页必须列出快捷键并提供非空按键说明：{name}。");
                await input.Activate(Find<Button>(overlay, "About"));
                Require(Find<Label>(overlay, "Version").Text == ProjectSettings.GetSetting("application/config/version").AsString(),
                    "关于页应显示实际工程版本。");
                Require(Find<Label>(overlay, "Packages").Text.Contains("设置测试内容包 1.2.3", StringComparison.Ordinal),
                    $"关于页应显示当前可用内容包及版本：text={Find<Label>(overlay, "Packages").Text}，processing={overlay.IsProcessing()}。");
                await input.Activate(Find<Button>(overlay, "ReportIssue"));
                var report = overlay.GetNode<IssueReportPanel>("IssueReportPanel");
                Require(report.Visible, "关于页入口应打开现有报告问题界面。");
                report.Hide();
            }, content);
        }
        finally
        {
            InputMap.ActionEraseEvents("editor_undo");
            for (var index = 0; index < undo.Count; index++)
            {
                using var value = undo[index];
                using var key = value.As<InputEvent>();
                InputMap.ActionAddEvent("editor_undo", key);
            }
        }
    }
}
