using Godot;
using TrainMvp.App;
using TrainMvp.Domain;

namespace TrainMvp.Planning;

public partial class PlanningScreen : Control
{
    private GameSession _session = null!;
    private Label _hud = null!;
    private Button _start = null!;
    private MapView _map = null!;
    private Label _planSummary = null!;
    private Label _pathSummary = null!;
    private Label _loopError = null!;
    private ScrollContainer _inspectorScroll = null!;
    private PanelContainer _loopCard = null!;
    private ScrollContainer _loopScroll = null!;
    private VBoxContainer _loopRows = null!;
    private Label _inspectorTitle = null!;
    private Label _inspectorRole = null!;
    private Button _join = null!;
    private Button _leave = null!;
    private Label _joinError = null!;
    private VBoxContainer _ruleRows = null!;
    private OptionButton _good = null!;
    private OptionButton _side = null!;
    private SpinBox _threshold = null!;
    private SpinBox _target = null!;
    private Label _draftPreview = null!;
    private Label _savedPreview = null!;
    private Label _priceHint = null!;
    private Label _formError = null!;
    private Label _capacity = null!;
    private bool _suppressForm;

    public event Action? StartLoopRequested;
    public event Action? ResetRequested;

    public void Bind(GameSession session)
    {
        _session = session;
    }

    public override void _Ready()
    {
        Name = "PlanningScreen";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        var top = new HBoxContainer
        {
            Name = "HudBar",
            CustomMinimumSize = new Vector2(0, 40)
        };
        top.AddThemeConstantOverride("separation", 12);
        root.AddChild(top);
        _hud = UiTheme.MakeLabel("", 17, true, wrap: false);
        _hud.Name = "HudLabel";
        _hud.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(_hud);
        var reset = UiTheme.MakeButton("重置场景", false);
        reset.Name = "ResetButton";
        reset.Pressed += () => ResetRequested?.Invoke();
        top.AddChild(reset);
        _start = UiTheme.MakeButton("运行一圈");
        _start.Name = "StartLoopButton";
        _start.Pressed += () => StartLoopRequested?.Invoke();
        top.AddChild(_start);

        var hint = UiTheme.MakeLabel("先选站组成环线，再为停靠站配置规则，最后运行一圈。", 15, wrap: false);
        hint.Name = "OperationHint";
        root.AddChild(hint);
        root.AddChild(UiTheme.MakeLabel("返回规划会保留圈末状态；重置场景会回到初始资金、市场和预置粮线。", 13));

        var body = new HBoxContainer();
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddThemeConstantOverride("separation", 12);
        root.AddChild(body);

        var mapCard = UiTheme.MakeCard();
        mapCard.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        mapCard.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(mapCard);
        _map = new MapView();
        _map.Bind(_session);
        _map.StationSelected += OnSelectStation;
        mapCard.AddChild(_map);

        _inspectorScroll = new ScrollContainer
        {
            Name = "InspectorScroll",
            CustomMinimumSize = new Vector2(430, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        body.AddChild(_inspectorScroll);
        var side = _inspectorScroll;
        var sideBox = new VBoxContainer();
        sideBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        sideBox.AddThemeConstantOverride("separation", 8);
        side.AddChild(sideBox);
        BuildInspector(sideBox);

        _loopCard = UiTheme.MakeCard();
        _loopCard.Name = "LoopCard";
        _loopCard.CustomMinimumSize = new Vector2(0, 176);
        _loopCard.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        _loopCard.ClipContents = true;
        root.AddChild(_loopCard);
        var loopCard = _loopCard;
        var loopBox = new VBoxContainer();
        loopBox.AddThemeConstantOverride("separation", 6);
        loopCard.AddChild(loopBox);
        loopBox.AddChild(UiTheme.MakeLabel("有序贸易环线与最短路径", 18, true, wrap: false));
        _planSummary = UiTheme.MakeLabel("", 15);
        _planSummary.Name = "PlanSummary";
        loopBox.AddChild(_planSummary);
        _pathSummary = UiTheme.MakeLabel("", 14);
        _pathSummary.Name = "PathSummary";
        loopBox.AddChild(_pathSummary);
        _loopError = UiTheme.MakeLabel("");
        _loopError.Name = "LoopError";
        loopBox.AddChild(_loopError);
        _loopScroll = new ScrollContainer
        {
            Name = "LoopRowsScroll",
            CustomMinimumSize = new Vector2(0, 80),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        loopBox.AddChild(_loopScroll);
        _loopRows = new VBoxContainer();
        _loopRows.Name = "LoopRows";
        _loopRows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _loopScroll.AddChild(_loopRows);

        ApplyViewportLayout();
        Refresh();
    }

    public void ApplyViewportLayout()
    {
        if (_loopCard is null || _loopScroll is null || _map is null || _inspectorScroll is null)
        {
            return;
        }

        var size = GetViewport().GetVisibleRect().Size;
        var tall = size.Y >= 1000;
        _map.CustomMinimumSize = new Vector2(520, tall ? 480 : 300);
        _inspectorScroll.CustomMinimumSize = new Vector2(tall ? 520 : 430, 0);
        _loopCard.CustomMinimumSize = new Vector2(0, tall ? 300 : 176);
        _loopCard.SizeFlagsVertical = tall ? SizeFlags.ExpandFill : SizeFlags.ShrinkEnd;
        _loopScroll.CustomMinimumSize = new Vector2(0, tall ? 200 : 80);
        _loopScroll.SizeFlagsVertical = tall ? SizeFlags.ExpandFill : SizeFlags.Fill;
    }

    public void Refresh()
    {
        if (_session is null || _hud is null)
        {
            return;
        }

        ApplyViewportLayout();
        var state = _session.State;
        var occupancy = state.Plan.BuyTargetOccupancy;
        var ticksText = _session.Engine.TryDescribeLoop(state.Plan, out var geometry, out _)
            ? $"{geometry!.TotalTicks} 刻"
            : "—";
        _hud.Text =
            $"资金 {state.Cash}    货舱 {state.CargoUsed}/{state.Capacity}    买入目标占用 {occupancy}/{state.Capacity}    整圈 {ticksText}    位置 {_session.Scenario.StationName(state.LocationId)}";
        _start.Disabled = !_session.CanStart;
        _map.Refresh();
        RefreshLoop(geometry);
        RefreshInspector();
    }

    private void BuildInspector(VBoxContainer side)
    {
        var card = UiTheme.MakeCard();
        side.AddChild(card);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        card.AddChild(box);
        box.AddChild(UiTheme.MakeLabel("当前贸易方案", 18, true, wrap: false));
        _capacity = UiTheme.MakeLabel("");
        _capacity.Name = "CapacityWarning";
        box.AddChild(_capacity);

        var inspect = UiTheme.MakeCard();
        side.AddChild(inspect);
        var inspectBox = new VBoxContainer();
        inspectBox.AddThemeConstantOverride("separation", 6);
        inspect.AddChild(inspectBox);
        _inspectorTitle = UiTheme.MakeLabel("站点", 18, true, wrap: false);
        _inspectorTitle.Name = "InspectorTitle";
        inspectBox.AddChild(_inspectorTitle);
        _inspectorRole = UiTheme.MakeLabel("");
        inspectBox.AddChild(_inspectorRole);

        var joinRow = new HBoxContainer();
        joinRow.AddThemeConstantOverride("separation", 8);
        inspectBox.AddChild(joinRow);
        _join = UiTheme.MakeButton("加入环线");
        _join.Name = "JoinLoopButton";
        _join.Pressed += OnJoin;
        joinRow.AddChild(_join);
        _leave = UiTheme.MakeButton("移出环线", false);
        _leave.Name = "LeaveLoopButton";
        _leave.Pressed += OnLeave;
        joinRow.AddChild(_leave);
        _joinError = UiTheme.MakeLabel("");
        _joinError.Name = "JoinError";
        inspectBox.AddChild(_joinError);

        inspectBox.AddChild(UiTheme.MakeLabel("站内规则", 16, true, wrap: false));
        _ruleRows = new VBoxContainer();
        inspectBox.AddChild(_ruleRows);

        inspectBox.AddChild(UiTheme.MakeLabel("将要保存的规则", 16, true, wrap: false));
        _good = new OptionButton { CustomMinimumSize = new Vector2(180, 36), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _good.Name = "RuleGoodOption";
        _good.ItemSelected += _ => SyncDraftDefaults(false);
        inspectBox.AddChild(_good);
        _side = new OptionButton { CustomMinimumSize = new Vector2(180, 36), SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        _side.Name = "RuleSideOption";
        _side.ItemSelected += _ => SyncDraftDefaults(false);
        inspectBox.AddChild(_side);

        var thresholdLabel = UiTheme.MakeLabel("价格阈值", wrap: false);
        inspectBox.AddChild(thresholdLabel);
        _threshold = MakeSpin(1, 99, "RuleThresholdSpin");
        inspectBox.AddChild(_threshold);
        var targetLabel = UiTheme.MakeLabel("目标持有量", wrap: false);
        inspectBox.AddChild(targetLabel);
        _target = MakeSpin(0, 8, "RuleTargetSpin");
        inspectBox.AddChild(_target);

        var save = UiTheme.MakeButton("保存规则");
        save.Name = "SaveRuleButton";
        save.Pressed += OnSaveRule;
        inspectBox.AddChild(save);
        _draftPreview = UiTheme.MakeLabel("");
        _draftPreview.Name = "DraftPreview";
        inspectBox.AddChild(_draftPreview);
        _savedPreview = UiTheme.MakeLabel("");
        _savedPreview.Name = "SavedPreview";
        inspectBox.AddChild(_savedPreview);
        _priceHint = UiTheme.MakeLabel("");
        inspectBox.AddChild(_priceHint);
        _formError = UiTheme.MakeLabel("");
        _formError.Name = "FormError";
        inspectBox.AddChild(_formError);
    }

    private static SpinBox MakeSpin(int min, int max, string name)
    {
        var spin = new SpinBox
        {
            Name = name,
            MinValue = min,
            MaxValue = max,
            Step = 1,
            Rounded = true,
            UpdateOnTextChanged = true,
            CustomMinimumSize = new Vector2(180, 36),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        return spin;
    }

    public void PlayerSelectStation(string stationId)
    {
        OnSelectStation(stationId);
    }

    public void PlayerJoinSelected()
    {
        OnJoin();
    }

    public void PlayerLeaveSelected()
    {
        OnLeave();
    }

    public string PlayerSaveRule(string goodId, TradeAction action, int threshold, int target)
    {
        if (_good.ItemCount == 0)
        {
            SyncDraftDefaults(true);
        }

        var goodIndex = -1;
        for (var i = 0; i < _good.ItemCount; i++)
        {
            if (_good.GetItemMetadata(i).AsString() == goodId)
            {
                goodIndex = i;
                break;
            }
        }

        if (goodIndex < 0)
        {
            return "该站没有所选货物。";
        }

        _good.Select(goodIndex);
        SyncDraftDefaults(false);
        var wantBuy = action == TradeAction.Buy;
        var sideIndex = -1;
        for (var i = 0; i < _side.ItemCount; i++)
        {
            if ((_side.GetItemId(i) == 0) == wantBuy)
            {
                sideIndex = i;
                break;
            }
        }

        if (sideIndex < 0)
        {
            return wantBuy ? "无供给站点不能买入。" : "无需求站点不能卖出。";
        }

        _side.Select(sideIndex);
        _threshold.Value = threshold;
        _target.Value = target;
        OnSaveRule();
        return _formError.Text;
    }

    public string FormErrorText => _formError?.Text ?? "";

    public string JoinErrorText => _joinError?.Text ?? "";

    public void PlayerJoinAllStations()
    {
        foreach (var stationId in _session.Scenario.StationOrder)
        {
            if (_session.Plan.Stops.Contains(stationId, StringComparer.Ordinal))
            {
                continue;
            }

            PlayerSelectStation(stationId);
            PlayerJoinSelected();
        }
    }

    private void OnSelectStation(string stationId)
    {
        _session.SelectedStationId = stationId;
        SyncDraftDefaults(true);
        Refresh();
    }

    private void OnJoin()
    {
        _joinError.Text = "";
        if (_session.Plan.Stops.Contains(_session.SelectedStationId, StringComparer.Ordinal))
        {
            _joinError.Text = "交易停靠站不能重复。";
            _joinError.AddThemeColorOverride("font_color", new Color(0.48f, 0.08f, 0.08f));
            return;
        }

        _session.AddStop(_session.SelectedStationId);
        SyncDraftDefaults(true);
        Refresh();
    }

    private void OnLeave()
    {
        _session.RemoveStop(_session.SelectedStationId);
        Refresh();
    }

    private void OnSaveRule()
    {
        _formError.Text = "";
        if (!_session.Plan.Stops.Contains(_session.SelectedStationId, StringComparer.Ordinal))
        {
            _formError.Text = "先把该站加入贸易环线。";
            _formError.AddThemeColorOverride("font_color", new Color(0.48f, 0.08f, 0.08f));
            return;
        }

        var rule = CurrentDraft();
        var issues = TradePlan.ValidateRule(_session.Plan.UpsertRule(rule), _session.Scenario, rule);
        if (issues.Count > 0)
        {
            _formError.Text = issues[0].Message;
            _formError.AddThemeColorOverride("font_color", new Color(0.48f, 0.08f, 0.08f));
            return;
        }

        _session.SaveRule(rule);
        Refresh();
    }

    private TradeRule CurrentDraft()
    {
        var goodId = _good.Selected >= 0 ? _good.GetItemMetadata(_good.Selected).AsString() : GoodIds.Grain;
        var buy = _side.GetSelectedId() == 0;
        return new TradeRule(
            _session.SelectedStationId,
            string.IsNullOrEmpty(goodId) ? GoodIds.Grain : goodId,
            buy ? TradeAction.Buy : TradeAction.Sell,
            Math.Max(1, (int)_threshold.Value),
            (int)_target.Value);
    }

    private void SyncDraftDefaults(bool reloadOptions)
    {
        if (_suppressForm || _good is null)
        {
            return;
        }

        _suppressForm = true;
        var stationId = _session.SelectedStationId;
        var goods = _session.Scenario.TradableGoods(stationId);
        if (reloadOptions)
        {
            _good.Clear();
            foreach (var goodId in goods)
            {
                _good.AddItem(_session.Scenario.GoodName(goodId));
                _good.SetItemMetadata(_good.ItemCount - 1, goodId);
            }

            if (_good.ItemCount > 0)
            {
                _good.Select(0);
            }
        }

        var goodIdSelected = _good.ItemCount == 0 || _good.Selected < 0
            ? GoodIds.Grain
            : _good.GetItemMetadata(_good.Selected).AsString();
        var hasSupply = _session.Scenario.HasSupply(stationId, goodIdSelected);
        var hasDemand = _session.Scenario.HasDemand(stationId, goodIdSelected);
        _side.Clear();
        if (hasSupply)
        {
            _side.AddItem("买入", 0);
        }

        if (hasDemand)
        {
            _side.AddItem("卖出", 1);
        }

        var existing = _session.Plan.RulesAt(stationId).FirstOrDefault(rule => rule.GoodId == goodIdSelected);
        if (existing is not null)
        {
            _side.Select(existing.Action == TradeAction.Buy ? 0 : Math.Max(0, _side.GetItemIndex(1)));
            _threshold.Value = existing.Threshold;
            _target.Value = existing.TargetHolding;
        }
        else
        {
            var buy = hasSupply;
            if (_side.ItemCount > 0)
            {
                _side.Select(0);
                buy = _side.GetSelectedId() == 0;
            }

            var price = _session.Engine.QuotePrice(_session.State, stationId, goodIdSelected);
            _threshold.Value = price;
            _target.Value = buy ? 8 : 0;
        }

        _suppressForm = false;
    }

    private void RefreshLoop(LoopGeometry? geometry)
    {
        var names = _session.Plan.Stops.Select(id => _session.Scenario.StationName(id)).ToArray();
        _planSummary.Text = names.Length == 0
            ? "尚未加入交易停靠站。"
            : string.Join(" → ", names) + (names.Length > 0 ? $" → {names[0]}" : "");
        if (geometry is null)
        {
            _pathSummary.Text = "完整最短路径将在环线有效后显示。";
            _loopError.Text = _session.Issues.FirstOrDefault()?.Message ?? "";
            _loopError.AddThemeColorOverride("font_color", new Color(0.48f, 0.08f, 0.08f));
        }
        else
        {
            _pathSummary.Text = string.Join("；", geometry.Segments.Select(segment =>
                $"{string.Join(" → ", segment.Path.Select(id => _session.Scenario.StationName(id)))}（{segment.Ticks} 刻{(segment.IsReturn ? "，返回" : "")}）"));
            _loopError.Text = _session.CanStart ? "" : string.Join(" ", _session.Issues.Select(issue => issue.Message));
            _loopError.AddThemeColorOverride("font_color", new Color(0.48f, 0.08f, 0.08f));
        }

        foreach (var child in _loopRows.GetChildren())
        {
            _loopRows.RemoveChild(child);
            child.QueueFree();
        }

        for (var i = 0; i < _session.Plan.Stops.Count; i++)
        {
            var index = i;
            var id = _session.Plan.Stops[i];
            var row = new HBoxContainer
            {
                Name = $"LoopRow_{i}"
            };
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(UiTheme.MakeLabel($"{i + 1}. {_session.Scenario.StationName(id)}", 15, wrap: false));
            var up = UiTheme.MakeButton("上移", false);
            up.Name = $"LoopRowUp_{i}";
            up.Pressed += () =>
            {
                _session.MoveStop(index, -1);
                Refresh();
            };
            row.AddChild(up);
            var down = UiTheme.MakeButton("下移", false);
            down.Name = $"LoopRowDown_{i}";
            down.Pressed += () =>
            {
                _session.MoveStop(index, 1);
                Refresh();
            };
            row.AddChild(down);
            var delete = UiTheme.MakeButton("删除", false);
            delete.Name = $"LoopRowDelete_{i}";
            delete.Pressed += () =>
            {
                _session.RemoveStop(id);
                Refresh();
            };
            row.AddChild(delete);
            _loopRows.AddChild(row);
        }
    }

    private void RefreshInspector()
    {
        var station = _session.Scenario.Stations[_session.SelectedStationId];
        _inspectorTitle.Text = station.Name;
        _inspectorRole.Text = station.Role;
        var inLoop = _session.Plan.Stops.Contains(station.Id, StringComparer.Ordinal);
        _join.Disabled = inLoop;
        _leave.Disabled = !inLoop;
        var capacity = _session.State.Capacity;
        _capacity.Text = _session.Plan.HasCapacityCompetition(capacity)
            ? $"容量竞争：买入目标合计 {_session.Plan.BuyTargetOccupancy}，超过 {capacity}。可保存，但货舱会竞争。"
            : $"买入目标占用 {_session.Plan.BuyTargetOccupancy}/{capacity}。";
        _capacity.AddThemeColorOverride("font_color", _session.Plan.HasCapacityCompetition(capacity)
            ? new Color(0.48f, 0.25f, 0.05f)
            : UiTheme.Ink);

        if (_good.ItemCount == 0)
        {
            SyncDraftDefaults(true);
        }

        foreach (var child in _ruleRows.GetChildren())
        {
            _ruleRows.RemoveChild(child);
            child.QueueFree();
        }

        var rules = _session.Plan.RulesAt(station.Id);
        for (var i = 0; i < rules.Count; i++)
        {
            var index = i;
            var rule = rules[i];
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(UiTheme.MakeLabel(
                $"{RunReport.FormatAction(rule.Action)}{_session.Scenario.GoodName(rule.GoodId)} 门槛 {rule.Threshold} 目标 {rule.TargetHolding}",
                14,
                wrap: false));
            var up = UiTheme.MakeButton("上移", false);
            up.Pressed += () =>
            {
                _session.MoveRule(station.Id, index, -1);
                Refresh();
            };
            row.AddChild(up);
            var down = UiTheme.MakeButton("下移", false);
            down.Pressed += () =>
            {
                _session.MoveRule(station.Id, index, 1);
                Refresh();
            };
            row.AddChild(down);
            var delete = UiTheme.MakeButton("删除", false);
            delete.Pressed += () =>
            {
                _session.RemoveRule(station.Id, rule.GoodId);
                Refresh();
            };
            row.AddChild(delete);
            _ruleRows.AddChild(row);
        }

        if (_good.ItemCount == 0)
        {
            _draftPreview.Text = "该站没有可交易货物。";
            return;
        }

        var draft = CurrentDraft();
        _draftPreview.Text =
            $"将要保存：{_session.Scenario.StationName(draft.StationId)} {RunReport.FormatAction(draft.Action)}{_session.Scenario.GoodName(draft.GoodId)}，门槛 {draft.Threshold}，目标 {draft.TargetHolding}。";
        var preview = _session.Plan.UpsertRule(draft);
        _savedPreview.Text = "保存后的方案预览：" + string.Join("；", preview.Rules.Select(rule =>
            $"{_session.Scenario.StationName(rule.StationId)}{RunReport.FormatAction(rule.Action)}{_session.Scenario.GoodName(rule.GoodId)}{rule.Threshold}/{rule.TargetHolding}"));
        var price = _session.Engine.QuotePrice(_session.State, draft.StationId, draft.GoodId);
        var meets = draft.Action == TradeAction.Buy ? price <= draft.Threshold : price >= draft.Threshold;
        _priceHint.Text = meets
            ? $"当前价格 {price}，已满足门槛 {draft.Threshold}。"
            : $"当前价格 {price}，不满足门槛 {draft.Threshold}。";
        _priceHint.AddThemeColorOverride("font_color", meets ? new Color(0.12f, 0.36f, 0.18f) : new Color(0.48f, 0.08f, 0.08f));
    }
}
