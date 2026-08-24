using Godot;
using TrainMvp.App;
using TrainMvp.Domain;

namespace TrainMvp.Planning;

public partial class PlanningScreen : Control
{
    private GameSession _session = null!;
    private Label _hud = null!;
    private Label _planSummary = null!;
    private Label _pathSummary = null!;
    private Label _rulePreview = null!;
    private SpinBox _threshold = null!;
    private Label _priceHint = null!;
    private bool _suppressThreshold;

    public event Action? StartLoopRequested;
    public event Action? ResetRequested;

    public void Bind(GameSession session)
    {
        _session = session;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        root.AddChild(top);
        _hud = UiTheme.MakeLabel("", 18, true);
        _hud.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(_hud);
        var reset = UiTheme.MakeButton("重置场景", false);
        reset.Pressed += () => ResetRequested?.Invoke();
        top.AddChild(reset);
        var start = UiTheme.MakeButton("运行一圈");
        start.Pressed += () => StartLoopRequested?.Invoke();
        top.AddChild(start);

        root.AddChild(UiTheme.MakeLabel("先选站组成环线，再为停靠站配置规则，最后运行一圈。", 15));
        root.AddChild(UiTheme.MakeLabel("返回规划会保留圈末状态；只有重置场景才会回到预置失败方案。", 14));

        var body = new HBoxContainer();
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddThemeConstantOverride("separation", 12);
        root.AddChild(body);

        var mapCard = UiTheme.MakeCard();
        mapCard.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        mapCard.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(mapCard);
        var mapBox = new VBoxContainer();
        mapBox.AddThemeConstantOverride("separation", 8);
        mapCard.AddChild(mapBox);
        mapBox.AddChild(UiTheme.MakeLabel("铁路地图", 18, true));
        mapBox.AddChild(BuildMap());

        var side = new VBoxContainer();
        side.CustomMinimumSize = new Vector2(420, 0);
        side.AddThemeConstantOverride("separation", 8);
        body.AddChild(side);

        var planCard = UiTheme.MakeCard();
        side.AddChild(planCard);
        var planBox = new VBoxContainer();
        planCard.AddChild(planBox);
        planBox.AddChild(UiTheme.MakeLabel("当前贸易方案", 18, true));
        _planSummary = UiTheme.MakeLabel("");
        planBox.AddChild(_planSummary);
        _pathSummary = UiTheme.MakeLabel("");
        planBox.AddChild(_pathSummary);

        var ruleCard = UiTheme.MakeCard();
        ruleCard.SizeFlagsVertical = SizeFlags.ExpandFill;
        side.AddChild(ruleCard);
        var ruleBox = new VBoxContainer();
        ruleBox.AddThemeConstantOverride("separation", 6);
        ruleCard.AddChild(ruleBox);
        ruleBox.AddChild(UiTheme.MakeLabel("南市卖粮门槛", 18, true));
        ruleBox.AddChild(UiTheme.MakeLabel("西仓：粮价 ≤ 18，买入到目标 8（预置，本切片不可改环线）。"));
        ruleBox.AddChild(UiTheme.MakeLabel("南市：粮价 ≥ 门槛，卖出到目标 0。把门槛改成不高于现价后即可成交。"));

        var thresholdForm = new VBoxContainer();
        thresholdForm.AddThemeConstantOverride("separation", 4);
        ruleBox.AddChild(thresholdForm);
        var thresholdLabel = UiTheme.MakeLabel("将要保存的门槛");
        thresholdLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        thresholdForm.AddChild(thresholdLabel);
        _threshold = new SpinBox
        {
            MinValue = 1,
            MaxValue = 99,
            Step = 1,
            Rounded = true,
            UpdateOnTextChanged = true,
            CustomMinimumSize = new Vector2(180, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        _threshold.ValueChanged += OnThresholdChanged;
        thresholdForm.AddChild(_threshold);
        _rulePreview = UiTheme.MakeLabel("");
        ruleBox.AddChild(_rulePreview);
        _priceHint = UiTheme.MakeLabel("");
        ruleBox.AddChild(_priceHint);

        Refresh();
    }

    public void Refresh()
    {
        if (_session is null || _hud is null)
        {
            return;
        }

        var state = _session.State;
        var geometry = _session.Engine.DescribeLoop(state.Plan);
        var grain = state.Holding(GoodIds.Grain);
        var coal = state.Holding(GoodIds.Coal);
        var tea = state.Holding(GoodIds.Tea);
        _hud.Text = $"资金 {state.Cash}    货舱 {state.CargoUsed}/{state.Capacity}    粮{grain} 煤{coal} 茶{tea}    整圈 {geometry.TotalTicks} 刻";
        _planSummary.Text = "西仓 → 南市 → 西仓";
        _pathSummary.Text = "完整路径：西仓 → 中枢 → 南市（3 刻），返回 3 刻。中枢只中转。";

        _suppressThreshold = true;
        _threshold.Value = _session.SouthMarketGrainThreshold;
        _suppressThreshold = false;

        var southPrice = _session.Engine.QuotePrice(state, StationIds.SouthMarket, GoodIds.Grain);
        var threshold = _session.SouthMarketGrainThreshold;
        _rulePreview.Text = $"保存后的方案预览：南市卖粮门槛 {threshold}，目标持有量 0。";
        var meets = southPrice >= threshold;
        _priceHint.Text = meets
            ? $"当前南市粮价 {southPrice}，已满足门槛 {threshold}。"
            : $"当前南市粮价 {southPrice}，低于门槛 {threshold}。";
        _priceHint.AddThemeColorOverride("font_color", meets ? new Color(0.118f, 0.357f, 0.184f) : new Color(0.478f, 0.082f, 0.082f));
    }

    private void OnThresholdChanged(double value)
    {
        if (_suppressThreshold)
        {
            return;
        }

        _session.SetSouthMarketGrainThreshold((int)value);
        Refresh();
    }

    private Control BuildMap()
    {
        var grid = new GridContainer
        {
            Columns = 3,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);
        foreach (var station in _session.Scenario.Stations.Values.OrderBy(item => item.GridY).ThenBy(item => item.GridX))
        {
            var isStop = _session.State.Plan.Stops.Contains(station.Id);
            var isHub = station.Id == StationIds.Hub;
            var panel = new PanelContainer
            {
                CustomMinimumSize = new Vector2(150, 82),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            var style = new StyleBoxFlat
            {
                BgColor = isStop
                    ? new Color(0.965f, 0.831f, 0.647f)
                    : isHub
                        ? new Color(0.847f, 0.886f, 0.922f)
                        : new Color(0.937f, 0.902f, 0.847f),
                ContentMarginLeft = 8,
                ContentMarginRight = 8,
                ContentMarginTop = 10,
                ContentMarginBottom = 10
            };
            panel.AddThemeStyleboxOverride("panel", style);
            var box = new VBoxContainer();
            panel.AddChild(box);
            box.AddChild(UiTheme.MakeLabel(station.Name, 18, isStop));
            box.AddChild(UiTheme.MakeLabel(isStop ? "交易停靠站" : isHub ? "本圈中转" : station.Role, 12));
            grid.AddChild(panel);
        }

        return grid;
    }
}
