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
        var bounds = shell.GetGlobalRect();
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
