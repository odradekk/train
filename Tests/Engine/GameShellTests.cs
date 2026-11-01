using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class GameShellTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public void Inspect_mounts_content_and_records_the_selected_object()
    {
        var shell = new GameShell { Size = new(1920, 1080) };
        TestScene.AddChild(shell);
        try
        {
            var content = new VBoxContainer();
            content.AddChild(new Button { Text = "替身操作" });
            var selected = new WorldObjectId.Train(new(7));

            shell.Inspect(selected, content);

            Require(shell.SelectedObject == selected && shell.InspectorContent == content,
                "检查器应同时挂上内容和记录它的选中对象。");
            Require(shell.IsAncestorOf(content) && content.IsVisibleInTree(),
                "内容应通过真实场景树显示在外壳中。");
        }
        finally { shell.Free(); }
    }

    [Test]
    public void Replacing_content_hides_the_previous_content_and_reopening_does_not_duplicate_it()
    {
        var shell = new GameShell { Size = new(1920, 1080) };
        TestScene.AddChild(shell);
        try
        {
            var first = new Control();
            var second = new Control();
            shell.ShowWorkspace(first);
            shell.ShowWorkspace(second);
            var parent = second.GetParent();
            var count = parent.GetChildCount();
            shell.ShowWorkspace(second);
            Require(!first.IsVisibleInTree() && second.IsVisibleInTree() && shell.WorkspaceContent == second,
                "替换后只能显示当前工作区。");
            Require(second.GetParent() == parent && parent.GetChildCount() == count,
                "重复打开同一内容不能重复挂载或重建。");
        }
        finally { shell.Free(); }
    }

    [Test]
    public void Close_removes_workspace_then_inspector_then_list()
    {
        var shell = new GameShell { Size = new(1920, 1080) };
        TestScene.AddChild(shell);
        try
        {
            var list = new Control();
            var inspector = new Control();
            var workspace = new Control();
            shell.ShowList(list);
            shell.Inspect(new WorldObjectId.Node(new(1)), inspector);
            shell.ShowWorkspace(workspace);
            Require(shell.CloseTop() && !workspace.Visible && inspector.Visible && list.Visible,
                "第一次关闭只能关工作区。");
            Require(shell.CloseTop() && shell.SelectedObject is null && !inspector.Visible && list.Visible,
                "第二次关闭应清除选中对象和检查器，保留列表。");
            Require(shell.CloseTop() && !list.Visible && !shell.CloseTop(),
                "第三次关闭列表，此后由调用者处理地图或菜单。");
        }
        finally { shell.Free(); }
    }

    [Test]
    public async Task Hiding_and_showing_the_shell_preserves_its_contents()
    {
        var shell = new GameShell { Size = new(1920, 1080) };
        TestScene.AddChild(shell);
        try
        {
            var content = new Control();
            var selected = new WorldObjectId.Train(new(7));
            shell.Inspect(selected, content);
            shell.Hide();
            await new UiTestInput(TestScene).Frames();
            Require(!content.IsVisibleInTree() && content.Visible && shell.SelectedObject == selected,
                "整体隐藏不能关闭内容或丢掉选中对象。");
            shell.Show();
            Require(content.IsVisibleInTree() && shell.SelectedObject == selected,
                "恢复外壳应恢复原内容。");
        }
        finally { shell.Free(); }
    }

    [Test]
    public async Task Docked_regions_fit_the_available_height_and_do_not_overlap()
    {
        var shell = new GameShell { Size = new(960, 540), TopInset = 80 };
        TestScene.AddChild(shell);
        try
        {
            var list = new Control();
            var inspector = new Control();
            shell.ShowList(list);
            shell.Inspect(new WorldObjectId.Node(new(1)), inspector);
            shell.BottomInset = 120;
            await new UiTestInput(TestScene).Frames(3);
            var left = list.GetGlobalRect();
            var right = inspector.GetGlobalRect();
            Require(left.Position.Y >= 80 && right.Position.Y >= 80 &&
                left.End.Y <= 420 && right.End.Y <= 420 && left.End.X <= right.Position.X,
                "缩放后的列表和检查器应避开顶栏、底部预留区和彼此。");
            Require(shell.SceneArea.End.Y <= 420 && shell.SceneArea.Position.X >= left.End.X &&
                shell.SceneArea.End.X <= right.Position.X, "场景叠加信息应使用面板之间的剩余区域。");
        }
        finally { shell.Free(); }
    }

    [Test]
    public async Task Clicking_the_workspace_mask_keeps_the_workspace_open()
    {
        var shell = new GameShell();
        shell.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        TestScene.AddChild(shell);
        try
        {
            var content = new PanelContainer();
            content.AddChild(new Button { Text = "替身操作" });
            shell.ShowWorkspace(content);
            var input = new UiTestInput(TestScene);
            await input.Frames(3);
            await input.Click(new(4, 4));
            Require(shell.WorkspaceContent == content && content.IsVisibleInTree(),
                "点击工作区外的遮罩不能关闭当前工作区。");
        }
        finally { shell.Free(); }
    }

    [Test]
    public async Task Escape_closes_one_layer_and_opens_the_menu_only_after_the_list()
    {
        var shell = new GameShell { Size = new(1920, 1080) };
        TestScene.AddChild(shell);
        try
        {
            var menus = 0;
            var overlay = true;
            shell.CloseOverlay = () => { var closed = overlay; overlay = false; return closed; };
            shell.OpenMenu = () => menus++;
            shell.ShowList(new Control());
            shell.Inspect(new WorldObjectId.Node(new(1)), new Control());
            shell.ShowWorkspace(new Control());
            var input = new UiTestInput(TestScene);
            await input.PressKey(Key.Escape);
            Require(!overlay && shell.WorkspaceContent is not null, "Esc 关闭覆盖层时不能同时关闭工作区。");
            await input.PressKey(Key.Escape);
            Require(shell.WorkspaceContent is null && shell.SelectedObject is not null, "第二次 Esc 只关工作区。");
            await input.PressKey(Key.Escape);
            Require(shell.SelectedObject is null && shell.ListContent is not null, "第三次 Esc 只关检查器。");
            await input.PressKey(Key.Escape);
            Require(shell.ListContent is null && menus == 0, "关闭列表的同一次 Esc 不能打开菜单。");
            await input.PressKey(Key.Escape);
            Require(menus == 1, "所有层关闭后，地图上的 Esc 应打开菜单。");
            shell.SceneVisible = () => true;
            await input.PressKey(Key.Escape);
            Require(menus == 1, "局部场景中应把无面板的 Esc 留给原来的场景输入分支。");
        }
        finally { shell.Free(); }
    }
}
