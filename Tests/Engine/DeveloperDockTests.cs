using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class DeveloperDockTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Game_menu_opens_the_console_dock()
    {
        await Run(async (app, console) =>
        {
            await _input.ChooseMenu(Find<MenuButton>(app.GetNode<GameUi>("GameUi"), "GameMenu"), "开发工具");
            Require(console.IsOpen, "游戏菜单的开发工具入口必须打开底栏。");
        });
    }

    [Test]
    public async Task New_log_lines_preserve_the_output_selection()
    {
        await Run(async (app, console) =>
        {
            await _input.PressKey(Key.Quoteleft);
            app.Logger.Info("console", "selection_before", "选中的控制台输出。");
            await _input.Frames(2);
            var output = console.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().Single();
            output.SelectAll();
            var selected = output.GetSelectedText();
            Require(selected.Contains("选中的控制台输出"), "前置条件必须存在实际选区。");
            app.Logger.Info("console", "selection_after", "选区之后的新输出。");
            await _input.Frames(2);
            Require(output.GetSelectedText() == selected, "新日志不能清空或改变现有选区。");
        });
    }

    [Test]
    public async Task Keyboard_completes_selects_subcommands_and_restores_history_drafts()
    {
        await Run(async (app, console) =>
        {
            await _input.PressKey(Key.Quoteleft);
            var edit = Find<LineEdit>(console, "ConsoleInput");
            var suggestions = Find<ItemList>(console, "Suggestions");
            await _input.Enter(edit, "si");
            Require(suggestions.Visible && suggestions.ItemCount == 1 && suggestions.GetItemText(0).Contains("模拟"),
                "输入前缀应显示命令及中文说明。");
            await _input.PressKey(Key.Tab);
            Require(edit.Text == "sim " && edit.HasFocus(), "Tab 必须补全命令并保留输入焦点。");
            await _input.PressKey(Key.Up);
            Require(suggestions.GetItemText(suggestions.GetSelectedItems().Single()).StartsWith("checkpoint"), "上箭头应回绕到最后一个候选。");
            await _input.PressKey(Key.Down);
            Require(suggestions.GetItemText(suggestions.GetSelectedItems().Single()).StartsWith("status"), "下箭头应回绕到第一个候选。");
            await _input.PressKey(Key.Down);
            await _input.PressKey(Key.Tab);
            Require(edit.Text == "sim pause ", "上下键应选择子命令后由 Tab 接受。");
            Require(Find<Label>(console, "CurrentUsage").Text.Contains("sim"), "输入框上方应显示当前命令用法。");
            await _input.PressKey(Key.Enter);
            Require(app.Logger.Threshold == Train.App.Logging.LogLevel.Debug && edit.Text == "" && edit.HasFocus(),
                "执行后应清空输入并能继续输入。");
            Require(console.LogEvents.Any(item => item.EventId == "simulation_paused"), "输入必须真正分发到模拟处理器。");
            await _input.PressKey(Key.Up);
            Require(edit.Text == "sim pause", "无候选时上箭头应取回最近命令。");
            await _input.PressKey(Key.Down);
            Require(edit.Text == "", "历史末尾应恢复空草稿。");
            await _input.Enter(edit, "zz");
            await _input.PressKey(Key.Up);
            Require(edit.Text == "sim pause", "非空草稿也能回看历史。");
            await _input.PressKey(Key.Down);
            Require(edit.Text == "zz", "离开历史应恢复原来的非空草稿。");
            await _input.Enter(edit, "he");
            await _input.PressKey(Key.Escape);
            Require(console.IsOpen && !suggestions.Visible, "Esc 应先收起建议。");
            await _input.PressKey(Key.Escape);
            Require(!console.IsOpen, "再次 Esc 应关闭底栏。");
        });
    }

    [Test]
    public async Task Dragging_the_top_edge_moves_shell_regions_above_the_dock()
    {
        await Run(async (app, console) =>
        {
            var ui = app.GetNode<GameUi>("GameUi");
            var shell = (GameShell)ui.World;
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(Find<ItemList>(shell.ListContent!, "Objects"), 0);
            Find<NotificationPanel>(ui, "NotificationPanel").Add(new(Train.App.Notifications.NotificationCategory.Destruction,
                Train.App.Notifications.NotificationLevel.Severe, "底栏避让检查。", null, null), app.Host!.GetStatus()!.View, app.Host.Content!);
            await _input.PressKey(Key.Quoteleft);
            var previous = shell.BottomInset;
            var grip = Find<Control>(console, "ResizeGrip");
            var start = grip.GetGlobalRect().GetCenter();
            await _input.MouseButton(start, MouseButton.Left, true);
            var transform = TestScene.GetViewport().GetScreenTransform();
            using var motion = new InputEventMouseMotion { Position = transform * (start - new Vector2(0, 90)),
                GlobalPosition = transform * (start - new Vector2(0, 90)), Relative = transform.BasisXform(new(0, -90)), ButtonMask = MouseButtonMask.Left };
            Input.ParseInputEvent(motion);
            await _input.Frames(2);
            await _input.MouseButton(start - new Vector2(0, 90), MouseButton.Left, false);
            Require(Math.Abs(shell.BottomInset - previous - 90) < 1, "底栏高度必须随上沿拖动的距离增加，不能跳到高度上限。");
            var dock = Find<Control>(console, "DeveloperDock").GetGlobalRect();
            foreach (var region in new[] { shell.ListContent!, shell.InspectorContent!, Find<Control>(ui, "Toasts"), Find<Control>(ui, "InformationLayers") })
                Require(region.GetGlobalRect().End.Y <= dock.Position.Y, $"{region.Name} 不应被底栏遮挡。");
            ShellGeometry.Check(ui);
            Require(ui.PanelArea.End.Y <= dock.Position.Y, "普通覆盖面板的可用区域也应让出底栏。");
            await _input.PressKey(Key.Quoteleft);
            Require(shell.BottomInset == 0, "关闭底栏必须恢复外壳高度。");
        });
    }

    [Test]
    public async Task Filters_keep_the_buffer_and_clear_removes_selected_output()
    {
        await Run(async (app, console) =>
        {
            await _input.PressKey(Key.Quoteleft);
            await _input.Activate(Find<Button>(console, "ClearOutput"));
            app.Logger.Debug("console", "filter_debug", "调试筛选样本。");
            app.Logger.Info("console", "filter_info", "信息筛选样本。");
            app.Logger.Warning("console", "filter_warning", "警告筛选样本。");
            app.Logger.Error("console", "filter_error", "错误筛选样本。");
            await _input.Frames(2);
            var output = Find<RichTextLabel>(console, "ConsoleOutput");
            var filter = Find<OptionButton>(console, "OutputLevel");
            Require(output.GetParsedText().Contains("调试筛选样本") && output.GetParsedText().Contains("信息筛选样本") &&
                output.GetParsedText().Contains("警告筛选样本") && output.GetParsedText().Contains("错误筛选样本"), "全部筛选应显示每种级别。");
            await _input.Choose(filter, 1);
            Require(output.GetParsedText().Contains("警告筛选样本") && !output.GetParsedText().Contains("信息筛选样本") &&
                !output.GetParsedText().Contains("调试筛选样本") && output.GetParsedText().Contains("错误筛选样本"), "警告筛选应保留错误并隐藏信息与调试输出。");
            await _input.Choose(filter, 2);
            Require(output.GetParsedText().Contains("信息筛选样本") && !output.GetParsedText().Contains("警告筛选样本"),
                "信息筛选应只显示信息级别。");
            await _input.Choose(filter, 3);
            Require(output.GetParsedText().Contains("调试筛选样本") && !output.GetParsedText().Contains("信息筛选样本"),
                "调试筛选应只显示调试级别。");
            Require(console.LogEvents.Count == 4 && app.Logger.Threshold == Train.App.Logging.LogLevel.Debug,
                "显示筛选不能删除日志或改变写入阈值。");
            string? copied = null;
            console.CopyText = text => copied = text;
            await _input.Activate(Find<Button>(console, "CopyAll"));
            Require(copied is not null && copied.Contains("调试筛选样本") && copied.Contains("[调试]") && copied.Contains("[console]") &&
                !copied.Contains("信息筛选样本") && !copied.Contains("[color="), "复制全部必须提供带元数据的当前可见纯文本。");
            output.SelectAll();
            console.SubmitLine("clear");
            Require(output.GetParsedText().Length == 0 && console.LogEvents.Count == 0, "clear 命令也必须清空带选区的输出。");
        });
    }

    private async Task Run(Func<Train.App.App, GameConsole, Task> check, HostSimulationTests.HostSetup? scenario = null)
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = scenario ?? new HostSimulationTests.HostSetup(EconomyFixture.Create(), EconomyFixture.Content(mapPresentation: true));
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            await _input.Frames(3);
            await check(app, app.GetChildren().OfType<GameConsole>().Single());
        }
        finally { await fixture.Cleanup(); }
    }
}
