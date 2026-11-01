using Chickensoft.GoDotTest;
using Godot;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class GameShellPanelTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Fractional_frame_heights_settle_without_repeated_fits()
    {
        foreach (var fraction in new[] { .6f, .4f })
        {
            var owner = new Control { Size = new(960, 863.1f), Theme = GD.Load<Theme>("res://Ui/Theme.tres") };
            TestScene.AddChild(owner);
            PanelContainer? panel = null;
            var changes = 0;
            var frames = new List<float>();
            void LayoutChanged()
            {
                if (panel is not { } current || !GodotObject.IsInstanceValid(current)) return;
                changes++;
                var layout = Find<VBoxContainer>(current, "Layout");
                var scroll = Find<ScrollContainer>(current, "Content");
                frames.Add(current.GetThemeStylebox("panel").GetMinimumSize().Y +
                    layout.GetCombinedMinimumSize().Y - scroll.GetCombinedMinimumSize().Y);
                // 负向验证达到上限后停止布局，让测试报告失败，而不是耗尽引擎队列。
                if (changes == 32) current.Hide();
            }
            try
            {
                var shell = new GameShell { Size = owner.Size, TopInset = 44 };
                owner.AddChild(shell);
                var content = new Control { Size = owner.Size };
                owner.AddChild(content);
                panel = UiStyle.Panel(content, "FractionalPanel", "分数边框", Vector2.Zero, 400, content.Hide, out var body);
                panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { ContentMarginLeft = 12,
                    ContentMarginRight = 12, ContentMarginTop = 12 + fraction, ContentMarginBottom = 12 });
                for (var row = 0; row < 40; row++) body.AddChild(UiStyle.Label($"行 {row}"));
                panel.Resized += LayoutChanged;
                Find<ScrollContainer>(panel, "Content").MinimumSizeChanged += LayoutChanged;
                shell.ShowWorkspace(content);
                var input = new UiTestInput(TestScene);
                await input.Frames(6);
                GD.Print($"Panel fraction={fraction}, layoutChanges={changes}, measuredFrames={string.Join(",", frames)}");
                Require(changes is > 0 and < 32,
                    $"分数 {fraction} 的布局必须在有限布局变化内收敛：changes={changes}，测得外框={string.Join(",", frames)}。");
                var settled = panel.GetGlobalRect();
                var settledChanges = changes;
                await input.Frames(4);
                Require(changes == settledChanges && panel.GetGlobalRect() == settled,
                    "布局收敛后不能在后续帧继续安排 Fit 或改变面板尺寸。");
                var bounds = content.GetGlobalRect();
                Require(settled.Position.Y >= bounds.Position.Y && settled.End.Y <= bounds.End.Y,
                    $"分数高度下停靠面板必须留在分配区域内：panel={settled} area={bounds}。");
            }
            finally { owner.Free(); }
        }
    }

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
