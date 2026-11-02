using Chickensoft.GoDotTest;
using Godot;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SettingsUiTests
{
    [Test]
    public async Task Window_mode_is_only_saved_after_confirmation_and_times_out_at_ten_seconds()
    {
        await WithOverlay(async (_, overlay, input) =>
        {
            overlay.SetProcess(false);
            Select(overlay, "WindowMode", 1);
            var prompt = overlay.FindChildren("ModePrompt", "Control", true, false).Cast<Control>().SingleOrDefault();
            Require(prompt is { Visible: true }, "改变窗口模式后必须询问是否保留。");
            Select(overlay, "Quality", 1);
            Require(SavedMode() == 0, "未确认的窗口模式不能被其他设置顺带保存。");
            overlay._Process(9);
            Require(prompt!.Visible && Option(overlay, "WindowMode").Selected == 1, "10 秒之前不能提前恢复窗口模式。");
            overlay._Process(1);
            Require(!prompt.Visible && Option(overlay, "WindowMode").Selected == 0 && SavedMode() == 0,
                "到达 10 秒时必须恢复原模式，且不能写入预览模式。");
            Select(overlay, "WindowMode", 2);
            await input.Activate(Find<Button>(prompt, "KeepMode"));
            Require(!prompt.Visible && SavedMode() == 2, "保留后才应保存新窗口模式。");
            overlay._Process(11);
            Require(Option(overlay, "WindowMode").Selected == 2, "已保留的模式不能在稍后被倒计时恢复。");
        });
    }

    [Test]
    public async Task Window_mode_restores_on_cancel_escape_close_and_removal()
    {
        await WithOverlay(async (_, overlay, input) =>
        {
            overlay.SetProcess(false);
            Select(overlay, "WindowMode", 1);
            var prompt = overlay.FindChildren("ModePrompt", "Control", true, false).Cast<Control>().SingleOrDefault();
            Require(prompt is { Visible: true }, "窗口模式确认未显示。");
            await input.Activate(Find<Button>(prompt!, "RestoreMode"));
            Require(!prompt!.Visible && Option(overlay, "WindowMode").Selected == 0, "恢复按钮没有恢复原模式。");
            Select(overlay, "WindowMode", 1);
            await input.PressKey(Key.Escape);
            Require(overlay.Visible && !prompt.Visible && Option(overlay, "WindowMode").Selected == 0,
                "确认时 Esc 应先恢复模式，保留设置覆盖层。");
            Select(overlay, "WindowMode", 1);
            overlay.Hide();
            Require(!prompt.Visible && Option(overlay, "WindowMode").Selected == 0, "关闭设置必须恢复未确认的模式。");
            overlay.Open();
            Select(overlay, "WindowMode", 2);
            overlay.GetParent().RemoveChild(overlay);
            Require(Option(overlay, "WindowMode").Selected == 0, "覆盖层退出场景树时必须恢复未确认的模式。");
            overlay.Free();
        });
    }

    private static int SavedMode()
    {
        using var config = new ConfigFile();
        Require(config.Load("user://settings.cfg") == Error.Ok, "无法读回设置文件。");
        return config.GetValue("display", "mode").AsInt32();
    }
}
