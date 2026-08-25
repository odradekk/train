using Godot;
using TrainMvp.App;
using TrainMvp.Domain;
using TrainMvp.Report;

namespace TrainMvp.Run;

public partial class RunScreen : Control
{
    private GameSession _session = null!;
    private LoopPlayback? _playback;
    private HSplitContainer _split = null!;
    private Control _worldHost = null!;
    private SubViewport _worldViewport = null!;
    private ReportPanel _report = null!;
    private Control _finished = null!;
    private Control _liveBox = null!;
    private Label _status = null!;
    private Label _location = null!;
    private Label _nextStop = null!;
    private Label _progress = null!;
    private Label _speedState = null!;
    private Label _cash = null!;
    private Label _cargo = null!;
    private Label _stationProgress = null!;
    private Label _lastEvent = null!;
    private Label _currentRule = null!;
    private Button _pause = null!;
    private Button _resume = null!;
    private Button _speed1 = null!;
    private Button _speed4 = null!;
    private Button _speed16 = null!;
    private Button _skip = null!;
    private Button _fast = null!;
    private Button _reset = null!;
    private Node3D _train = null!;
    private readonly Vector3 _leftPos = new(-6f, 0.25f, 0f);
    private readonly Vector3 _midPos = new(0f, 0.25f, 0f);
    private readonly Vector3 _rightPos = new(6f, 0.25f, 0f);
    private string _leftId = StationIds.WestBarn;
    private string _midId = StationIds.Hub;
    private string _rightId = StationIds.SouthMarket;
    private Label3D _leftLabel = null!;
    private Label3D _midLabel = null!;
    private Label3D _rightLabel = null!;
    private bool _openedReport;

    public event Action? ReturnToPlanningRequested;
    public event Action? ResetRequested;

    public LoopPlayback? Playback => _playback;

    public void Bind(GameSession session)
    {
        _session = session;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _split = new HSplitContainer();
        _split.Name = "RunSplit";
        _split.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_split);

        _worldHost = new SubViewportContainer
        {
            Name = "RunWorldHost",
            Stretch = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _split.AddChild(_worldHost);
        _worldViewport = new SubViewport
        {
            OwnWorld3D = true,
            HandleInputLocally = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        _worldHost.AddChild(_worldViewport);
        _worldViewport.AddChild(BuildWorld());

        var side = new MarginContainer
        {
            Name = "RunSidePanel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        side.AddThemeConstantOverride("margin_left", 10);
        side.AddThemeConstantOverride("margin_right", 10);
        side.AddThemeConstantOverride("margin_top", 8);
        side.AddThemeConstantOverride("margin_bottom", 8);
        _split.AddChild(side);

        var scroll = new ScrollContainer
        {
            Name = "RunInfoScroll",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        side.AddChild(scroll);
        var stack = new VBoxContainer();
        stack.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        stack.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(stack);

        _status = NamedLabel("RunStatusLabel", "运行中", 20, true);
        stack.AddChild(_status);
        _reset = NamedButton("ResetButton", "重置场景", false);
        _reset.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _reset.Pressed += () => ResetRequested?.Invoke();
        stack.AddChild(_reset);
        _liveBox = new VBoxContainer
        {
            Name = "LiveRunPanel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _liveBox.AddThemeConstantOverride("separation", 6);
        stack.AddChild(_liveBox);
        _location = NamedLabel("LocationLabel", "");
        _nextStop = NamedLabel("NextStopLabel", "");
        _progress = NamedLabel("ProgressLabel", "");
        _speedState = NamedLabel("SpeedStateLabel", "");
        _cash = NamedLabel("CashLabel", "");
        _cargo = NamedLabel("CargoLabel", "");
        _stationProgress = NamedLabel("StationProgressLabel", "");
        _lastEvent = NamedLabel("LastEventLabel", "");
        _currentRule = NamedLabel("CurrentRuleLabel", "");
        foreach (var label in new[] { _location, _nextStop, _progress, _speedState, _cash, _cargo, _stationProgress, _lastEvent, _currentRule })
        {
            _liveBox.AddChild(label);
        }

        _liveBox.AddChild(BuildControls());

        _report = new ReportPanel();
        _report.Bind(_session);
        _report.Visible = false;
        _report.SizeFlagsVertical = SizeFlags.ExpandFill;
        _report.CustomMinimumSize = new Vector2(0, 240);
        _report.ReturnToPlanningRequested += () => ReturnToPlanningRequested?.Invoke();
        _report.CloseReportRequested += CloseReport;
        stack.AddChild(_report);

        _finished = new VBoxContainer
        {
            Name = "FinishedObservationPanel",
            Visible = false
        };
        _finished.AddThemeConstantOverride("separation", 10);
        _finished.AddChild(UiTheme.MakeLabel("结束观察", 18, true));
        var finishedReturn = UiTheme.MakeButton("返回规划");
        finishedReturn.Name = "FinishedReturnButton";
        finishedReturn.Pressed += () => ReturnToPlanningRequested?.Invoke();
        _finished.AddChild(finishedReturn);
        stack.AddChild(_finished);
        ApplyViewportLayout();
    }

    public void ApplyViewportLayout()
    {
        if (_split is null || _worldHost is null)
        {
            return;
        }

        var size = GetViewport().GetVisibleRect().Size;
        var wide = size.X >= 1800;
        _worldHost.CustomMinimumSize = wide ? new Vector2(820, 620) : new Vector2(400, 280);
        if (_split.FindChild("RunSidePanel", true, false) is Control side)
        {
            side.CustomMinimumSize = new Vector2(wide ? 720 : 400, 0);
            side.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        }

        _split.SplitOffsets = [0];
        if (_report is not null)
        {
            _report.CustomMinimumSize = new Vector2(0, wide ? 640 : 240);
        }
    }

    public void PlayCurrentRun()
    {
        var run = _session.CurrentRun ?? throw new InvalidOperationException("No run to play.");
        _playback = new LoopPlayback(run, _session.State);
        _openedReport = false;
        ApplyViewportLayout();
        _report.Visible = false;
        _finished.Visible = false;
        _liveBox.Visible = true;
        _playback.Start();
        if (_playback.CurrentEvent is TradeEvent)
        {
            _session.BeginRunning();
        }

        RefreshHud();
        RefreshControls();
        UpdateWorld();
    }

    public void CompletePlaybackImmediately()
    {
        _playback?.FastComplete();
        EnsureRunningAfterTrades();
        OpenReportIfFinished();
    }

    public void SyncPresentation()
    {
        RefreshHud();
        RefreshControls();
        UpdateWorld();
    }

    public override void _Process(double delta)
    {
        if (!Visible || _playback is null || _session.Phase is not (AppPhase.Running or AppPhase.Repositioning))
        {
            return;
        }

        _playback.Tick(delta);
        if (_playback.CurrentEvent is TradeEvent)
        {
            _session.BeginRunning();
        }

        RefreshHud();
        RefreshControls();
        UpdateWorld();
        OpenReportIfFinished();
    }

    private Control BuildControls()
    {
        var box = new VBoxContainer
        {
            Name = "RunControlBar"
        };
        box.AddThemeConstantOverride("separation", 6);
        var row1 = new HBoxContainer();
        row1.AddThemeConstantOverride("separation", 8);
        _pause = NamedButton("PauseButton", "暂停", false);
        _pause.Pressed += () =>
        {
            _playback?.Pause();
            RefreshHud();
            RefreshControls();
        };
        _resume = NamedButton("ResumeButton", "继续");
        _resume.Pressed += () =>
        {
            _playback?.Resume();
            RefreshHud();
            RefreshControls();
        };
        _speed1 = NamedButton("Speed1Button", "1×", false);
        _speed1.Pressed += () => SetSpeed(LoopPlayback.Speed1);
        _speed4 = NamedButton("Speed4Button", "4×", false);
        _speed4.Pressed += () => SetSpeed(LoopPlayback.Speed4);
        _speed16 = NamedButton("Speed16Button", "16×", false);
        _speed16.Pressed += () => SetSpeed(LoopPlayback.Speed16);
        foreach (var button in new[] { _pause, _resume, _speed1, _speed4, _speed16 })
        {
            row1.AddChild(button);
        }

        var row2 = new HBoxContainer();
        row2.AddThemeConstantOverride("separation", 8);
        _skip = NamedButton("SkipStopButton", "跳至下一停靠站", false);
        _skip.Pressed += () =>
        {
            _playback?.SkipToNextTradeStop();
            EnsureRunningAfterTrades();
            RefreshHud();
            RefreshControls();
            UpdateWorld();
            OpenReportIfFinished();
        };
        _fast = NamedButton("FastCompleteButton", "快速完成");
        _fast.Pressed += () =>
        {
            _playback?.FastComplete();
            EnsureRunningAfterTrades();
            OpenReportIfFinished();
        };
        row2.AddChild(_skip);
        row2.AddChild(_fast);
        box.AddChild(row1);
        box.AddChild(row2);
        return box;
    }

    private void SetSpeed(int speed)
    {
        _playback?.SetSpeed(speed);
        RefreshHud();
        RefreshControls();
    }

    private void RefreshHud()
    {
        if (_playback is null)
        {
            return;
        }

        var scenario = _session.Scenario;
        var state = _playback.DisplayState;
        var current = _playback.PresentationEvent ?? _playback.CurrentEvent;
        _status.Text = StatusText();
        _location.Text = $"当前位置 {scenario.StationName(state.LocationId)}";
        _nextStop.Text = _playback.NextTradeStopId is { } next
            ? $"下一交易停靠站 {scenario.StationName(next)}"
            : "下一交易停靠站 —";
        _progress.Text = $"整圈进度 {(_playback.LoopProgress * 100):0}%";
        _speedState.Text = _playback.IsPaused ? "速度 暂停" : $"速度 {_playback.Speed}×";
        _cash.Text = $"资金 {state.Cash}";
        _cargo.Text =
            $"货舱 {state.CargoUsed}/{state.Capacity}    粮{state.Holding(GoodIds.Grain)} 煤{state.Holding(GoodIds.Coal)} 茶{state.Holding(GoodIds.Tea)}";
        _stationProgress.Text = "站点进度 " + DescribeProgress();
        var lastTrade = _playback.AppliedEvents.OfType<TradeEvent>().LastOrDefault();
        _lastEvent.Text = "最近事件 " + (lastTrade is null ? DescribeEvent(current) : DescribeRule(lastTrade));
        _currentRule.Text = "当前规则 " + DescribeRule(current);
    }

    private void RefreshControls()
    {
        if (_pause is null)
        {
            return;
        }

        var live = _playback is not null && _playback.CanControl
            && _session.Phase is AppPhase.Running or AppPhase.Repositioning;
        _pause.Visible = live && _playback is { IsPaused: false };
        _resume.Visible = live && _playback is { IsPaused: true };
        _pause.Disabled = !live;
        _resume.Disabled = !live;
        _speed1.Disabled = !live;
        _speed4.Disabled = !live;
        _speed16.Disabled = !live;
        _skip.Disabled = !live;
        _fast.Disabled = !live;
        _reset.Disabled = false;
    }

    private void UpdateWorld()
    {
        if (_playback is not null && _playback.RoutePath.Count >= 2)
        {
            ShowPath(_playback.RoutePath);
            if (_playback.MotionPath.Count >= 2)
            {
                PlaceAlong(_playback.MotionPath, _playback.EventProgress);
                return;
            }
        }

        PlaceTrain(_playback?.DisplayState.LocationId ?? _session.State.LocationId);
    }

    private void EnsureRunningAfterTrades()
    {
        if (_session.Phase == AppPhase.Repositioning
            && _playback?.AppliedEvents.OfType<TradeEvent>().Any() == true)
        {
            _session.BeginRunning();
            GD.Print("PHASE_TRANSITION Repositioning->Running");
        }
    }

    private void OpenReportIfFinished()
    {
        if (_openedReport || _playback is not { IsFinished: true })
        {
            return;
        }

        FinishPlayback();
    }

    private void FinishPlayback()
    {
        if (_session.Phase is AppPhase.Report or AppPhase.FinishedObservation)
        {
            return;
        }

        EnsureRunningAfterTrades();
        if (_session.Phase != AppPhase.Running)
        {
            return;
        }

        _openedReport = true;
        _session.ShowReport();
        GD.Print("PHASE_TRANSITION Running->Report");
        RefreshHud();
        RefreshControls();
        _status.Text = "运行报告";
        _lastEvent.Text = "最近事件 " + (_session.CurrentRun?.Report.NextHint ?? "");
        _finished.Visible = false;
        _liveBox.Visible = false;
        _report.Visible = true;
        _report.ShowCurrentReport();
    }

    private void CloseReport()
    {
        _session.CloseReport();
        _report.Visible = false;
        _liveBox.Visible = false;
        _finished.Visible = true;
        _status.Text = "结束观察";
        _lastEvent.Text = "最近事件 圈已结束，不能再推进。可返回规划继续经营。";
        RefreshControls();
    }

    private string StatusText()
    {
        return _playback?.PresentationEvent switch
        {
            RepositionEvent => "调度空驶",
            TradeEvent trade => trade.IsLoopStart ? "起点交易" : "抵达交易停靠站",
            TravelEvent travel => travel.IsReturn ? "返回起点" : "行驶中",
            TransitEvent => "中转经过",
            ReturnArrivalEvent => "圈结束",
            _ => _session.Phase == AppPhase.Repositioning ? "调度空驶" : "运行中"
        };
    }

    private string DescribeProgress()
    {
        if (_playback is null)
        {
            return "—";
        }

        var parts = new List<string>();
        foreach (var item in _playback.AppliedEvents)
        {
            switch (item)
            {
                case TradeEvent trade:
                    parts.Add($"{_session.Scenario.StationName(trade.StationId)}交易");
                    break;
                case TransitEvent transit:
                    parts.Add($"{_session.Scenario.StationName(transit.StationId)}中转");
                    break;
                case ReturnArrivalEvent:
                    parts.Add("返回");
                    break;
                case RepositionEvent:
                    parts.Add("调度");
                    break;
            }
        }

        return parts.Count == 0 ? "—" : string.Join(" → ", parts);
    }

    private string DescribeEvent(SimEvent? item)
    {
        var scenario = _session.Scenario;
        return item switch
        {
            TradeEvent trade => DescribeRule(trade),
            TravelEvent travel => travel.IsReturn
                ? $"返回{scenario.StationName(travel.ToStationId)}，{travel.Ticks} 刻"
                : $"驶向{scenario.StationName(travel.ToStationId)}，{travel.Ticks} 刻",
            RepositionEvent reposition =>
                $"调度前往{scenario.StationName(reposition.ToStationId)}，不交易，{reposition.Ticks} 刻",
            TransitEvent transit => $"中转经过{scenario.StationName(transit.StationId)}，不交易。",
            ReturnArrivalEvent => "抵达起点，本圈不再交易。",
            LoopFinishedEvent => "圈结束。",
            _ => "—"
        };
    }

    private string DescribeRule(SimEvent? item)
    {
        if (item is not TradeEvent trade)
        {
            return "—";
        }

        if (trade.Rules.Count == 0)
        {
            return $"{_session.Scenario.StationName(trade.StationId)} 无规则。";
        }

        var rule = trade.Rules[0];
        return $"{_session.Scenario.StationName(trade.StationId)} {RunReport.FormatAction(rule.Action)}{_session.Scenario.GoodName(rule.GoodId)} 报价 {rule.QuotedPrice} 期望 {rule.ExpectedQuantity} 实际 {rule.ActualQuantity} {RunReport.FormatOutcome(rule.Outcome)}"
               + (rule.Cause == RuleFailureCause.None ? "" : $" 主因：{RunReport.FormatCause(rule.Cause)}");
    }

    private void ShowPath(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            return;
        }

        _leftId = path[0];
        _rightId = path[^1];
        _midId = path.Count >= 3 ? path[1] : StationIds.Hub;
        _leftLabel.Text = _session.Scenario.StationName(_leftId);
        _midLabel.Text = path.Count >= 3 ? _session.Scenario.StationName(_midId) : "途经";
        _rightLabel.Text = _session.Scenario.StationName(_rightId);
    }

    private void PlaceAlong(IReadOnlyList<string> path, double progress)
    {
        if (path.Count < 2)
        {
            PlaceTrain(path.Count == 0 ? _session.State.LocationId : path[0]);
            return;
        }

        var hops = path.Count - 1;
        var scaled = Math.Clamp(progress, 0, 1) * hops;
        var hop = Math.Min(hops - 1, (int)scaled);
        var t = (float)(scaled - hop);
        _train.Position = PositionOf(path[hop]).Lerp(PositionOf(path[hop + 1]), t);
    }

    private void PlaceTrain(string stationId)
    {
        _train.Position = PositionOf(stationId);
    }

    private Vector3 PositionOf(string stationId)
    {
        if (stationId == _leftId)
        {
            return _leftPos;
        }

        if (stationId == _rightId)
        {
            return _rightPos;
        }

        return _midPos;
    }

    private Node3D BuildWorld()
    {
        var root = new Node3D();
        var camera = new Camera3D
        {
            Position = new Vector3(0f, 8.5f, 11f)
        };
        camera.Transform = camera.Transform.LookingAt(Vector3.Zero, Vector3.Up);
        root.AddChild(camera);
        root.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50f, -30f, 0f) });
        root.AddChild(MakeBox(new Vector3(0f, -0.2f, 0f), new Vector3(16f, 0.2f, 6f), new Color(0.36f, 0.48f, 0.24f)));
        root.AddChild(MakeBox(new Vector3(0f, 0.02f, 0f), new Vector3(14f, 0.06f, 0.45f), new Color(0.22f, 0.22f, 0.22f)));
        var left = MakeLabeledStation(_leftPos, "西仓");
        _leftLabel = left.Label;
        root.AddChild(left.Node);
        var mid = MakeLabeledStation(_midPos, "中枢");
        _midLabel = mid.Label;
        root.AddChild(mid.Node);
        var right = MakeLabeledStation(_rightPos, "南市");
        _rightLabel = right.Label;
        root.AddChild(right.Node);
        _train = MakeBox(PositionOf(StationIds.WestBarn), new Vector3(0.7f, 0.45f, 0.45f), new Color(0.55f, 0.22f, 0.12f));
        root.AddChild(_train);
        return root;
    }

    private static MeshInstance3D MakeBox(Vector3 position, Vector3 size, Color color)
    {
        var mesh = new BoxMesh { Size = size };
        var material = new StandardMaterial3D { AlbedoColor = color };
        var instance = new MeshInstance3D { Mesh = mesh, Position = position };
        instance.SetSurfaceOverrideMaterial(0, material);
        return instance;
    }

    private static (Node3D Node, Label3D Label) MakeLabeledStation(Vector3 position, string name)
    {
        var node = new Node3D { Position = position };
        node.AddChild(MakeBox(new Vector3(0f, 0.15f, 0f), new Vector3(0.9f, 0.7f, 0.9f), new Color(0.8f, 0.71f, 0.54f)));
        var font = new SystemFont();
        font.FontNames = ["Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI"];
        var label = new Label3D
        {
            Text = name,
            Position = new Vector3(0f, 1.1f, 0f),
            FontSize = 48,
            Font = font,
            Modulate = Colors.White
        };
        node.AddChild(label);
        return (node, label);
    }

    private static Label NamedLabel(string name, string text, int size = 16, bool bold = false)
    {
        var label = UiTheme.MakeLabel(text, size, bold);
        label.Name = name;
        return label;
    }

    private static Button NamedButton(string name, string text, bool primary = true)
    {
        var button = UiTheme.MakeButton(text, primary);
        button.Name = name;
        return button;
    }
}
