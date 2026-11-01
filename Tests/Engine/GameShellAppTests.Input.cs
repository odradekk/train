using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class GameShellAppTests
{
    [Test]
    public async Task Unfocused_nonexclusive_window_leaves_root_escape_to_the_workspace()
    {
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            var auxiliary = new Window { Title = "非独占测试窗口", Exclusive = false, Unfocusable = true,
                Size = new(180, 100), Visible = false };
            TestScene.AddChild(auxiliary);
            try
            {
                auxiliary.Show();
                TestScene.GetTree().Root.GrabFocus();
                await _input.Frames(3);
                Require(auxiliary.Visible && !auxiliary.HasFocus() && shell.WorkspaceContent is not null,
                    "前置条件必须是可见但无焦点的非独占窗口和打开的工作区。");
                await _input.PressKey(Key.Escape);
                Require(shell.WorkspaceContent is null && auxiliary.Visible,
                    "无焦点的非独占窗口不能截走根视口 Esc，工作区应关闭而该窗口保持打开。");
            }
            finally { auxiliary.Hide(); auxiliary.Free(); }
        });
    }

    [Test]
    public async Task Root_escape_closes_the_focused_popup_before_the_workspace()
    {
        await Run(async (ui, shell) =>
        {
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            var workspace = shell.WorkspaceContent;
            var menu = Find<MenuButton>(ui, "GameMenu");
            await _input.Activate(menu);
            var popup = menu.GetPopup();
            Require(popup.Visible && popup.HasFocus(), "弹出菜单应实际打开并持有焦点。");
            await _input.PressKey(Key.Escape);
            Require(!popup.Visible && shell.WorkspaceContent == workspace,
                "从根视口发送的同一次 Esc 应只关闭弹出菜单，不关闭工作区。");
        });
    }

    [Test]
    public async Task Workspace_keeps_the_top_bar_available_to_mouse_input()
    {
        await Run(async (ui, shell) =>
        {
            var host = ui.GetParent<Train.App.App>().Host!;
            host.AutoPauseOnFocusLoss = true;
            host.SetFocused(false);
            await _input.Activate(Find<Button>(ui, "EconomyReports"));
            var workspace = shell.WorkspaceContent;
            await _input.Click(Find<Button>(ui, "Speed2").GetGlobalRect().GetCenter());
            await _input.Frames(3);
            Require(host.GetStatus() is { PlayerPaused: false, Speed: SimulationSpeed.Two } && shell.WorkspaceContent == workspace,
                "工作区打开时，鼠标仍应点到顶栏倍速，且不关闭工作区。");
            await _input.Click(Find<Button>(ui, "Pause").GetGlobalRect().GetCenter());
            await _input.Frames(3);
            Require(host.GetStatus() is { PlayerPaused: true } && shell.WorkspaceContent == workspace,
                "工作区打开时，鼠标仍应点到顶栏暂停。");
        });
    }
}
