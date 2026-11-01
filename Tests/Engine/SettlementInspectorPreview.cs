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
        var box = new VBoxContainer { Position = new(16, 780) }; hud.AddChild(box);
        box.AddChild(new Label
        {
            Text = "聚居地检查器固定情景\n生产 App 与界面，Core / Content 为有限契约替身。\n从顶部“节点”列表打开聚居地；每次交易后可重置情景。",
            MouseFilter = Control.MouseFilterEnum.Ignore
        });
        _status = new Label { Name = "PreviewStatus" }; box.AddChild(_status);
        foreach (var (name, text) in new[] { ("Normal", "正常交易"), ("NoTrain", "无车在站"), ("Blocked", "资金与载量不足"), ("LongName", "长名称"), ("Multiple", "多列车与最大"), ("Owned", "自有聚居地") })
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
            var multiple = scenario == "Multiple";
            var owned = scenario == "Owned";
            var before = EconomyFixture.Create(trainAtNode: !noTrain, blocked: blocked,
                wallet: blocked ? 250 : multiple ? 1000 : 1234567,
                vehicle: multiple ? EconomyFixture.VehicleState.Formed : EconomyFixture.VehicleState.ForSale,
                tradeQuotes: multiple ? MultipleTrainQuotes() : null, leaseCount: multiple ? 0 : 1);
            if (owned) before = OwnedSettlementInspectorUiTests.View();
            var after = EconomyFixture.Create(2, foodQuantity: 14, wallet: 1233867);
            var setup = new HostSimulationTests.HostSetup(before, owned ? OwnedSettlementInspectorUiTests.Content() :
                EconomyFixture.Content(mapPresentation: true, foodName: scenario == "LongName" ? string.Concat(Enumerable.Repeat("长名称货物", 12)) : "主食"));
            if (noTrain)
            {
                var hostile = EconomyFixture.Create(2, trainAtNode: false, relation: SettlementRelation.Hostile);
                setup.Session.QueueSubmit(new CoreCommand.DeclareWar(new(1)), CommandSource.UserInterface,
                    new SubmissionResult.Completed(new(1, before.Position, hostile.Position, new CommandDecision.Accepted([], [])), [], null), hostile);
            }
            else if (!blocked && !multiple && !owned)
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
            _status.Text = scenario switch
            {
                "NoTrain" => "无车在站 · 可打开宣战确认",
                "Blocked" => "资金与载量不足 · 确认应禁用",
                "Multiple" => "选择列车、切换动作、输入2件或点最大；本情景只查看报价。",
                "Owned" => "自有聚居地 · 查看概况、库存和建筑；生产设置与拆除由引擎测试验证。",
                _ => "正常交易 · 默认买入10件，实际成交700"
            };
        }
        catch (Exception error) { GD.PrintErr(error); GetTree().Quit(1); }
        finally { foreach (var button in _choices) button.Disabled = false; }
    }

    private static IReadOnlyDictionary<TradeQuoteRequest, CoreResult<TradeQuote>> MultipleTrainQuotes()
    {
        var quotes = new Dictionary<TradeQuoteRequest, CoreResult<TradeQuote>>();
        foreach (var train in new ulong[] { 7, 8 })
        foreach (var action in new[] { TradeAction.Buy, TradeAction.Sell })
        foreach (var quantity in new long[] { 1, 2, 3, 4, 10, 20, 40 })
        {
            var request = new TradeQuoteRequest(new(train), new(1), new(10), action, EconomyFixture.Food, quantity);
            var maximum = action == TradeAction.Buy && quantity >= 20;
            var executable = maximum ? quantity == 40 ? 20 : 3 : quantity;
            var total = quantity switch { 10 => action == TradeAction.Buy ? 731 : 431, 20 => 1500, 40 => 3000, 3 => 200, _ => train == 7 ? 50 : 54 };
            quotes.Add(request, new CoreResult<TradeQuote>.Success(new(request, new(1), executable, new(total),
                maximum ? [new(quantity == 40 ? "insufficient_capacity" : "insufficient_money", "quantity", null, [])] : [], [])));
        }
        return quotes;
    }
}
