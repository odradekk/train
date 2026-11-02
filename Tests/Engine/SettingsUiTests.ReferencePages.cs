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
        var undo = InputMap.ActionGetEvents("editor_undo");
        try
        {
            InputMap.ActionEraseEvents("editor_undo");
            InputMap.ActionAddEvent("editor_undo", new InputEventKey { Keycode = Key.F6 });
            var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            {
                Packages = [new(new(new("fixture"), "设置测试内容包", "1.2.3", [], []), new("fixture"), PackageState.Available, [])]
            };
            await WithOverlay(async (_, overlay, input) =>
            {
                await input.Activate(Find<Button>(overlay, "Keys"));
                var rows = overlay.FindChildren("Key_*", "Label", true, false).Cast<Label>().ToArray();
                var bindings = InputMap.GetActions().Where(action => !action.ToString().StartsWith("ui_", StringComparison.Ordinal)).ToArray();
                Require(bindings.All(action => rows.Count(row => row.Name == "Key_" + action) == 1),
                    "按键页必须列出全部项目 InputMap 动作。");
                Require(rows.Single(row => row.Name == "Key_editor_undo").Text == "F6", "按键说明应读取实际绑定，不能复制默认键位。");
                Require(Find<Label>(overlay, "Key_console").Text == "` / ~" && Find<Label>(overlay, "Key_close").Text == "Esc",
                    "按键页必须包含代码中硬编码的控制台和关闭快捷键。");
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
            foreach (var key in undo) InputMap.ActionAddEvent("editor_undo", key);
        }
    }
}
