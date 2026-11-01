using Chickensoft.GoDotTest;
using Godot;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class GameShellPanelTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Docked_panel_restores_its_width_when_returning_to_standalone_owner()
    {
        var owner = new Control { Size = new(1920, 1080) };
        TestScene.AddChild(owner);
        try
        {
            var shell = new GameShell { Size = owner.Size };
            owner.AddChild(shell);
            var content = new Control { Size = owner.Size };
            owner.AddChild(content);
            var panel = UiStyle.Panel(content, "Panel", "原内容", new(60, 80), 650, content.Hide, out var body);
            body.AddChild(UiStyle.Label("内容"));
            var input = new UiTestInput(TestScene);
            await input.Frames(4);
            var standaloneWidth = panel.Size.X;
            shell.ShowWorkspace(content);
            await input.Frames(4);
            Require(panel.Size.X > standaloneWidth, "面板应实际使用较宽的工作区。");
            shell.ShowWorkspace(null);
            content.Reparent(owner);
            content.Size = owner.Size;
            content.Show();
            await input.Frames(4);
            Require(panel.Size.X >= standaloneWidth && panel.GetGlobalRect().End.X <= owner.Size.X,
                "停靠后再独立打开应恢复原宽度，不能退化成内容的最小宽度。");
        }
        finally { owner.Free(); }
    }
}
