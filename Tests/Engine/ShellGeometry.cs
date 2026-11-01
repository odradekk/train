using Godot;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

internal static class ShellGeometry
{
    internal static void Check(GameUi ui)
    {
        var shell = (GameShell)ui.World;
        var scale = shell.GetTree().Root.ContentScaleFactor;
        var top = Find<Control>(ui, "TopBar");
        var regions = new List<Control> { top };
        if (shell.WorkspaceContent is { } workspace && workspace.IsVisibleInTree()) regions.Add(workspace);
        else
        {
            regions.Add(Find<Control>(shell, "Navigation"));
            if (shell.ListContent is { } list && list.IsVisibleInTree()) regions.Add(list);
            if (shell.InspectorContent is { } inspector && inspector.IsVisibleInTree()) regions.Add(inspector);
            var toasts = Find<Control>(ui, "Toasts");
            if (toasts.IsVisibleInTree() && toasts.GetChildCount() > 0) regions.Add(toasts);
            regions.AddRange(new[] { Find<Control>(ui, "InformationLayers"), Find<Control>(ui, "NodeMinimap") }
                .Where(control => control.IsVisibleInTree()));
        }
        var bounds = ui.GetViewport().GetVisibleRect();
        for (var index = 0; index < regions.Count; index++)
        {
            var current = regions[index];
            var rect = current.GetGlobalRect();
            Require(rect.Size.X > 0 && rect.Size.Y > 0 && rect.Position.X >= bounds.Position.X &&
                rect.Position.Y >= bounds.Position.Y && rect.End.X <= bounds.End.X && rect.End.Y <= bounds.End.Y,
                $"缩放 {scale}：{current.Name} {rect} 超出外壳窗口 {bounds} 或没有可见面积。");
            foreach (var other in regions.Skip(index + 1))
                Require(!rect.Intersects(other.GetGlobalRect()),
                    $"缩放 {scale}：{current.Name} {rect} 与 {other.Name} {other.GetGlobalRect()} 重叠。");
        }
    }

    internal static async Task CheckNavigationAndListText(Node scene, GameUi ui)
    {
        var shell = (GameShell)ui.World;
        foreach (var button in shell.Navigation.GetChildren().OfType<Button>())
        {
            var font = button.GetThemeFont("font");
            var padding = button.GetThemeStylebox("normal").GetMinimumSize().X;
            foreach (var line in button.Text.Split('\n'))
                Require(font.GetStringSize(line, fontSize: button.GetThemeFontSize("font_size")).X <= button.Size.X - padding,
                    $"导航文字被裁掉：{button.Name}，文字={line}，按钮宽={button.Size.X}，内边距={padding}。");
        }
        var body = Find<VBoxContainer>(shell.ListContent!, "Body");
        var hint = body.GetChildren().OfType<Label>().Last();
        var scroll = Find<ScrollContainer>(shell.ListContent!, "Content");
        scroll.EnsureControlVisible(hint);
        await new UiTestInput(scene).Frames(3);
        var area = scroll.GetGlobalRect();
        var horizontal = scroll.GetHScrollBar();
        if (horizontal.IsVisibleInTree()) area.Size -= new Vector2(0, horizontal.Size.Y);
        var rect = hint.GetGlobalRect();
        Require(rect.Position.X >= area.Position.X && rect.End.X <= area.End.X &&
            rect.Position.Y >= area.Position.Y && rect.End.Y <= area.End.Y,
            $"列表底部提示应能完整滚入可见区域，不能被横向滚动条压住：提示={rect}，可见区={area}。");
    }

    internal static async Task CheckBothScales(Node scene, GameUi ui)
    {
        var root = scene.GetTree().Root;
        var input = new UiTestInput(scene);
        try
        {
            foreach (var scale in new[] { 1f, 2f })
            {
                root.ContentScaleFactor = scale;
                await input.Frames(4);
                Check(ui);
                await UiCapture.Capture(scene, $"shell-local-scale-{scale:0}", scale);
            }
        }
        finally { root.ContentScaleFactor = 1; await input.Frames(2); }
    }
}
