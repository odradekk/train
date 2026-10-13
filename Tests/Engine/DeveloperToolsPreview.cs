using Godot;
using Train.App.Development;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.Engine.Tests;

public partial class DeveloperToolsPreview : Node
{
    private Train.App.App? _app;
    private DeveloperToolsFixture? _fixture;
    private readonly Label _status = new() { Name = "PreviewStatus" };
    private readonly Label _message = new() { Name = "PreviewMessage" };
    private readonly Label _leave = new() { Name = "LeaveStatus" };
    private readonly List<Button> _actions = [];
    public static void Start(Node root) => root.AddChild(new DeveloperToolsPreview { Name = "DeveloperToolsPreview" });

    public override void _Ready()
    {
        try
        {
            _fixture = new(ProjectSettings.GlobalizePath("res://TestResults/developer-tools-preview"));
            _app = GD.Load<PackedScene>("res://App/App.tscn").Instantiate<Train.App.App>();
            _app.CreateHost = _fixture.CreateHost;
            _app.CreateScenes = () => _fixture.Scenes;
            AddChild(_app);
            _app.GetNode<Control>("MainMenu").Hide();
            _app.Host!.AutoPauseOnFocusLoss = false;
            _app.Saves!.SetAutoSaveInterval(null);
            var hud = new CanvasLayer { Name = "DeveloperPreviewHud", Layer = 10 };
            AddChild(hud);
            var box = new VBoxContainer { Position = new(16, 16) };
            hud.AddChild(box);
            box.AddChild(new Label
            {
                Text = "开发检查预览 · 同份生产App / DeveloperTools / DeveloperInspector\n" +
                    "Core、Content、Scenes均为有限契约替身；不是生产世界或真实内容工厂。\n" +
                    "普通会话：只支持0→1→2，到达步2后停止。对象：节点1、列车1。\n" +
                    "重放：打开ZIP→单步至1:0→到位置2:0→导出普通存档→经存档面板导入和读档。\n" +
                    "可重新打开同一ZIP重复验证。图层开启后Scenes替身提供两条彩色线。\n" +
                    "ZIP起点0:0，录制截止2:0，无命令；中间及截止检查点为有限固定样本。\n" +
                    $"复现包：{_fixture.ReplayPath}\n默认导出：{_fixture.ExportPath}",
                MouseFilter = Control.MouseFilterEnum.Ignore
            });
            var row = new HBoxContainer();
            box.AddChild(row);
            Action(row, "打开开发窗口", "OpenInspector", () =>
                _app.GetChildren().OfType<GameConsole>().Single().SubmitLine("dev open"));
            Action(row, "运行至步2", "UntilStep2", () => Show(_app.Development!.StartCondition(new DeveloperCondition.AtStep(2))));
            Action(row, "打开固定复现包", "OpenFixtureReplay", () => Show(_app.Development!.OpenReplay(_fixture.ReplayPath)));
            var second = new HBoxContainer();
            box.AddChild(second);
            Action(second, "重放单步", "ReplayStep", () => Show(_app.Development!.StartSteps(1)));
            Action(second, "重放至2:0", "ReplayToEnd", () => Show(_app.Development!.AdvanceTo(new(2, 0))));
            Action(second, "导出至默认路径", "ExportFixtureBoundary", () => Show(_app.Development!.ExportReplayBoundary(_fixture.ExportPath)));
            Action(second, "打开存档面板", "OpenSaves", () => _app.GetNode<MainMenu>("MainMenu").GetChildren().OfType<SavePanel>().Single().Open());
            var third = new HBoxContainer();
            box.AddChild(third);
            Action(third, "进入节点场景", "EnterFixtureScene", () => _app.WorldScenes!.Open(DeveloperToolsFixture.Target));
            Action(third, "返回地图", "ReturnFixtureMap", () => _app.WorldScenes!.ReturnToMap());
            Action(third, "放弃旧会话并继续", "DiscardLeave", () => _app.Saves!.ChooseLeave(Train.App.Saves.LeaveChoice.Discard));
            Action(third, "取消会话切换", "CancelLeave", () => _app.Saves!.ChooseLeave(Train.App.Saves.LeaveChoice.Cancel));
            box.AddChild(_leave);
            box.AddChild(_status);
            box.AddChild(_message);
        }
        catch (Exception error) { GD.PrintErr(error); GetTree().Quit(1); }
    }

    public override void _Process(double delta)
    {
        if (_app?.Host?.GetStatus() is not { } status) return;
        foreach (var action in _actions) action.Disabled = _app.Development!.IsBusy && _app.Saves!.Leaving is null;
        _leave.Text = _app.Saves!.Leaving is null ? "" : "离开保护等待确认：可放弃旧会话或取消切换。";
        _status.Text = $"{(status.IsReplay ? "只读重放" : "普通会话")} · {status.View.Position.CompletedSteps}:{status.View.Position.Sequence}" +
            $" · 普通核心推进 {_fixture!.InitialSession.AdvanceCount} 次 · 原会话释放 {_fixture.InitialSession.DisposeCount} 次" +
            $"\n场景线数 {_app.WorldScenes?.Display?.DisplayedFrame?.DebugLines.Length ?? 0} · {_app.Development!.LastMessage}";
    }

    private void Show(Train.App.Saves.SaveFailure? failure) => _message.Text = failure is null ? "操作已请求。" : $"{failure.Code}：{failure.Message}";
    private void Action(HBoxContainer row, string text, string name, System.Action action)
    {
        var button = new Button { Text = text, Name = name };
        row.AddChild(button);
        button.Pressed += action;
        _actions.Add(button);
    }
}
