using Chickensoft.GoDotTest;
using Godot;
using Train.App.Measurement;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class MeasurementUiTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public void Docked_shell_keeps_scene_input_available_but_a_workspace_mask_blocks_it()
    {
        var viewport = new SubViewport { Size = new(960, 540) };
        TestScene.AddChild(viewport);
        var shell = new GameShell { Size = new(960, 540) };
        viewport.AddChild(shell);
        try
        {
            shell.ShowList(new Control());
            shell.Inspect(new Train.Core.Contracts.WorldObjectId.Node(new(1)), new Control());
            Require(MeasurementRun.ReadSceneUi(shell).BlockingControls.Length == 0,
                "透明的全屏外壳和停靠区不应算作全屏遮挡。");
            shell.ShowWorkspace(new Control());
            var blockers = MeasurementRun.ReadSceneUi(shell).BlockingControls;
            Require(blockers.Length == 1 && blockers[0].EndsWith("/WorkspaceMask", StringComparison.Ordinal),
                "工作区打开后的全屏遮罩仍须被测量识别。");
            shell.ShowWorkspace(null);
            Require(MeasurementRun.ReadSceneUi(shell).BlockingControls.Length == 0,
                "关闭工作区后应恢复场景可操作状态。");
        }
        finally { viewport.Free(); }
    }

    [Test]
    public void Blocking_controls_keep_tree_order_and_exclude_non_blocking_ui()
    {
        var viewport = new SubViewport { Size = new(320, 240) };
        TestScene.AddChild(viewport);
        var root = new Node();
        viewport.AddChild(root);
        try
        {
            var first = new Control { Name = "First", Size = new(320, 240) };
            root.AddChild(first);
            var branch = new Node();
            root.AddChild(branch);
            var nested = new Node();
            branch.AddChild(nested);
            var second = new Control { Name = "Second", Size = new(640, 480) };
            nested.AddChild(second);
            var third = new Control { Name = "Third", Size = new(320, 240) };
            root.AddChild(third);
            root.AddChild(new Control { Visible = false, Size = new(320, 240) });
            var hiddenParent = new Control { Visible = false };
            root.AddChild(hiddenParent);
            hiddenParent.AddChild(new Control { Size = new(320, 240) });
            root.AddChild(new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new(320, 240) });
            root.AddChild(new Control { Size = new(319, 240) });
            root.AddChild(new Control { Size = new(320, 239) });
            root.AddChild(new MacroMap { Size = new(320, 240) });
            var scene = new Node3D();
            root.AddChild(scene);
            scene.AddChild(new Control { Size = new(320, 240) });

            var result = MeasurementRun.ReadSceneUi(root);

            Require(result.BlockingControls.SequenceEqual(new[]
                { first.GetPath().ToString(), second.GetPath().ToString(), third.GetPath().ToString() }),
                "只应按深度优先顺序报告可见、接收鼠标且覆盖视口的非地图控件，并跳过 Node3D 子树。");
            Require(!result.Modal, "没有窗口时不应报告模态窗口。");
        }
        finally { viewport.Free(); }
    }

    [Test]
    public void Modal_requires_a_visible_exclusive_secondary_window_outside_scene_subtrees()
    {
        var viewport = new SubViewport { Size = new(320, 240), GuiEmbedSubwindows = true };
        TestScene.AddChild(viewport);
        var root = new Node();
        viewport.AddChild(root);
        try
        {
            var window = new Window { Visible = false, Transient = true, Exclusive = true };
            root.AddChild(window);
            var blocker = new Control { Size = new(320, 240) };
            root.AddChild(blocker);
            Require(!MeasurementRun.ReadSceneUi(root).Modal, "隐藏的独占窗口不应算作模态窗口。");

            window.Exclusive = false;
            window.Show();
            Require(!MeasurementRun.ReadSceneUi(root).Modal, "可见但非独占的窗口不应算作模态窗口。");

            window.Exclusive = true;
            var result = MeasurementRun.ReadSceneUi(root);
            Require(result.Modal, "可见的独占子窗口应算作模态窗口。");
            Require(result.BlockingControls.SequenceEqual(new[] { blocker.GetPath().ToString() }),
                "发现模态窗口后仍应读取后续的遮挡控件。");

            window.Hide();
            var scene = new Node3D();
            root.AddChild(scene);
            var nested = new Window { Visible = false, Transient = true, Exclusive = true };
            scene.AddChild(nested);
            nested.Show();
            Require(!MeasurementRun.ReadSceneUi(root).Modal, "Node3D 子树内的窗口不在测量范围内。");
        }
        finally { viewport.Free(); }
    }
}
