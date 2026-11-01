using Godot;
using Train.Core.Contracts;

namespace Train.Engine.Tests;

public partial class SettlementInspectorPreview : Node
{
    private Train.App.App? _app;
    private readonly List<Button> _choices = [];
    private Label _status = null!;

    public static void Start(Node root) => root.AddChild(new SettlementInspectorPreview { Name = "SettlementInspectorPreview" });

    public override async void _Ready()
    {
        GetTree().Root.ContentScaleFactor = 1;
        GetTree().Root.Size = new(1920, 1080);
        var hud = new CanvasLayer { Name = "SettlementPreviewHud", Layer = 10 }; AddChild(hud);
        var box = new VBoxContainer { Position = new(16, 96) }; hud.AddChild(box);
        box.AddChild(new Label
        {
            Text = "聚居地检查器固定情景\n生产 App 与界面，Core / Content 为有限契约替身。\n从顶部“节点”列表打开聚居地；每次交易后可重置情景。",
            MouseFilter = Control.MouseFilterEnum.Ignore
        });
        _status = new Label { Name = "PreviewStatus" }; box.AddChild(_status);
        foreach (var (name, text) in new[] { ("Normal", "正常交易"), ("NoTrain", "无车在站"), ("Blocked", "资金与载量不足") })
        {
            var button = new Button { Name = name, Text = text }; box.AddChild(button); _choices.Add(button);
            button.Pressed += async () => await Switch(name);
        }
        await Switch("Normal");
    }

    private async Task Switch(string scenario)
    {
        foreach (var button in _choices) button.Disabled = true;
        try
        {
            if (_app is not null) { _app.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            var noTrain = scenario == "NoTrain";
            var blocked = scenario == "Blocked";
            var before = EconomyFixture.Create(trainAtNode: !noTrain, blocked: blocked, wallet: blocked ? 250 : 1234567);
            var after = EconomyFixture.Create(2, foodQuantity: 14, wallet: 1233867);
            var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(mapPresentation: true));
            if (noTrain)
            {
                var hostile = EconomyFixture.Create(2, trainAtNode: false, relation: SettlementRelation.Hostile);
                setup.Session.QueueSubmit(new CoreCommand.DeclareWar(new(1)), CommandSource.UserInterface,
                    new SubmissionResult.Completed(new(1, before.Position, hostile.Position, new CommandDecision.Accepted([], [])), [], null), hostile);
            }
            else if (!blocked)
                setup.Session.QueueSubmit(new CoreCommand.BuyGoods(new(7), new(1), new(10), EconomyFixture.Food, 10, new(731)),
                    CommandSource.UserInterface,
                    new SubmissionResult.Completed(new(1, before.Position, after.Position, new CommandDecision.Accepted([], [])),
                        [new(after.Position, 0, new CoreEvent.GoodsTransferred(TradeAction.Buy, EconomyFixture.Food, 10,
                            new(10), new(12), new(700), new(7), new(1)))], null), after);
            _app = GD.Load<PackedScene>("res://App/App.tscn").Instantiate<Train.App.App>();
            _app.CreateHost = logger => setup.Create(logger);
            AddChild(_app);
            _app.Host!.AutoPauseOnFocusLoss = false;
            _app.Saves!.SetAutoSaveInterval(null);
            while (_app.Host.IsBusy) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_app.Host.GetStatus() is null) throw new InvalidOperationException("固定情景启动失败。");
            _app.GetNode<Control>("MainMenu").Hide();
            _status.Text = scenario switch { "NoTrain" => "无车在站 · 可打开宣战确认", "Blocked" => "资金与载量不足 · 确认应禁用", _ => "正常交易 · 默认买入10件，实际成交700" };
        }
        catch (Exception error) { GD.PrintErr(error); GetTree().Quit(1); }
        finally { foreach (var button in _choices) button.Disabled = false; }
    }
}
