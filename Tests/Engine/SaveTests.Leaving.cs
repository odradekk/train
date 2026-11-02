using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests
{
    [Test]
    public async Task Cancelling_a_menu_leave_prompt_returns_to_the_game_without_opening_the_save_list()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = new SaveSetup().Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            owned.Add(app.Saves!.Status!.Identity.Id);
            var ui = app.GetNode<GameUi>("GameUi");
            var panel = ui.GetNode<SavePanel>("Root/SavePanel");
            var closed = new List<bool>();
            foreach (var menu in new[] { "退出游戏", "返回主菜单" })
            {
                await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), menu);
                await _input.WaitUntil(() => UiTestInput.SavePromptVisible(panel, "未保存的更改"));
                if (menu == "退出游戏") await _input.Activate(UiTestInput.SavePromptButton(panel, "Cancel"));
                else await _input.PressKey(Key.Escape);
                closed.Add(!panel.Visible && app.Saves.Leaving is null && app.Host!.GetStatus() is { OverlayPaused: false });
                panel.Hide();
            }
            Require(closed.All(result => result), "取消菜单离开询问后，应关闭仅为询问打开的覆盖层并恢复游戏。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }
}
