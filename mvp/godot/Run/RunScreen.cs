using Godot;
using TrainMvp.App;
using TrainMvp.Domain;
using TrainMvp.Report;

namespace TrainMvp.Run;

public partial class RunScreen : Control
{
    private GameSession _session = null!;
    private ReportPanel _report = null!;
    private Label _status = null!;
    private Label _meters = null!;
    private Label _eventLine = null!;
    private Node3D _train = null!;
    private readonly Dictionary<string, Vector3> _corridor = new()
    {
        [StationIds.WestBarn] = new Vector3(-6f, 0.25f, 0f),
        [StationIds.Hub] = new Vector3(0f, 0.25f, 0f),
        [StationIds.SouthMarket] = new Vector3(6f, 0.25f, 0f)
    };

    private readonly List<VisualStep> _steps = [];
    private int _stepIndex;
    private float _elapsed;
    private Vector3 _from;
    private Vector3 _to;
    private bool _moving;
    private SimulationState? _displayState;

    public event Action? ReturnToPlanningRequested;

    public void Bind(GameSession session)
    {
        _session = session;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var split = new HSplitContainer();
        split.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        split.SplitOffsets = [620];
        AddChild(split);

        var viewHost = new SubViewportContainer
        {
            Stretch = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        split.AddChild(viewHost);
        var viewport = new SubViewport
        {
            OwnWorld3D = true,
            HandleInputLocally = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Size = new Vector2I(640, 720)
        };
        viewHost.AddChild(viewport);
        viewport.AddChild(BuildWorld());

        var side = new MarginContainer();
        side.AddThemeConstantOverride("margin_left", 10);
        side.AddThemeConstantOverride("margin_right", 10);
        side.AddThemeConstantOverride("margin_top", 8);
        side.AddThemeConstantOverride("margin_bottom", 8);
        split.AddChild(side);

        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 8);
        side.AddChild(stack);

        _status = UiTheme.MakeLabel("运行中", 20, true);
        stack.AddChild(_status);
        _meters = UiTheme.MakeLabel("");
        stack.AddChild(_meters);
        _eventLine = UiTheme.MakeLabel("");
        stack.AddChild(_eventLine);

        _report = new ReportPanel();
        _report.Bind(_session);
        _report.Visible = false;
        _report.SizeFlagsVertical = SizeFlags.ExpandFill;
        _report.ReturnToPlanningRequested += () => ReturnToPlanningRequested?.Invoke();
        stack.AddChild(_report);
    }

    public void PlayCurrentRun()
    {
        var run = _session.CurrentRun ?? throw new InvalidOperationException("No run to play.");
        _displayState = _session.State.Clone();
        BuildSteps(run);
        _stepIndex = -1;
        _elapsed = 0;
        _moving = false;
        _report.Visible = false;
        _status.Text = "运行中";
        PlaceTrain(StationIds.WestBarn);
        RefreshMeters();
        AdvanceStep();
    }

    public override void _Process(double delta)
    {
        if (!Visible || _session.Phase != AppPhase.Running)
        {
            return;
        }

        _elapsed += (float)delta;
        var step = CurrentStep();
        if (step is null)
        {
            return;
        }

        if (_moving)
        {
            var t = step.Duration <= 0 ? 1f : Mathf.Clamp(_elapsed / step.Duration, 0f, 1f);
            _train.Position = _from.Lerp(_to, t);
            if (t >= 1f)
            {
                _moving = false;
                AdvanceStep();
            }

            return;
        }

        if (_elapsed >= step.Duration)
        {
            AdvanceStep();
        }
    }

    private void BuildSteps(LoopRunResult run)
    {
        _steps.Clear();
        foreach (var item in run.Events)
        {
            switch (item)
            {
                case TradeEvent trade:
                    _steps.Add(new VisualStep(0.7f, false, Vector3.Zero, Vector3.Zero, () =>
                    {
                        ApplyTradeToDisplay(trade);
                        _status.Text = trade.IsLoopStart ? "起点交易" : "抵达交易停靠站";
                        _eventLine.Text = DescribeTrade(trade);
                        RefreshMeters();
                    }));
                    break;
                case TravelEvent travel:
                    for (var i = 0; i < travel.Path.Count - 1; i++)
                    {
                        var fromId = travel.Path[i];
                        var toId = travel.Path[i + 1];
                        var viaHub = toId == StationIds.Hub;
                        _steps.Add(new VisualStep(0.85f, true, PositionOf(fromId), PositionOf(toId), () =>
                        {
                            _status.Text = travel.IsReturn ? "返回起点" : "行驶中";
                            _eventLine.Text = viaHub
                                ? $"中转经过{_session.Scenario.StationName(StationIds.Hub)}，不交易。"
                                : $"驶向{_session.Scenario.StationName(toId)}";
                        }));
                    }

                    break;
                case ReturnArrivalEvent:
                    _steps.Add(new VisualStep(0.4f, false, Vector3.Zero, Vector3.Zero, () =>
                    {
                        _status.Text = "圈结束";
                        _eventLine.Text = "抵达起点，本圈不再交易。";
                    }));
                    break;
            }
        }
    }

    private void AdvanceStep()
    {
        _stepIndex++;
        _elapsed = 0;
        _moving = false;
        var step = CurrentStep();
        if (step is null)
        {
            FinishPlayback();
            return;
        }

        step.Apply();
        if (step.Moves)
        {
            _from = step.From;
            _to = step.To;
            _train.Position = _from;
            _moving = true;
        }
    }

    private VisualStep? CurrentStep()
    {
        return _stepIndex >= 0 && _stepIndex < _steps.Count ? _steps[_stepIndex] : null;
    }

    private void ApplyTradeToDisplay(TradeEvent trade)
    {
        if (_displayState is null)
        {
            return;
        }

        foreach (var rule in trade.Rules)
        {
            _displayState.Cash += rule.CashDelta;
            var holding = _displayState.Holding(rule.GoodId);
            _displayState.Cargo[rule.GoodId] = rule.Action == TradeAction.Buy
                ? holding + rule.ActualQuantity
                : holding - rule.ActualQuantity;
        }

        _displayState.LocationId = trade.StationId;
    }

    private void FinishPlayback()
    {
        _session.ShowReport();
        _displayState = _session.CurrentRun?.StateAfter;
        RefreshMeters();
        _status.Text = "运行报告";
        _eventLine.Text = _session.CurrentRun?.Report.NextHint ?? "";
        _report.Visible = true;
        _report.ShowCurrentReport();
    }

    private void RefreshMeters()
    {
        var state = _displayState ?? _session.State;
        _meters.Text =
            $"位置 {_session.Scenario.StationName(state.LocationId)}    资金 {state.Cash}    货舱 {state.CargoUsed}/{state.Capacity}    粮{state.Holding(GoodIds.Grain)} 煤{state.Holding(GoodIds.Coal)} 茶{state.Holding(GoodIds.Tea)}";
    }

    private string DescribeTrade(TradeEvent trade)
    {
        if (trade.Rules.Count == 0)
        {
            return $"{_session.Scenario.StationName(trade.StationId)} 无规则。";
        }

        var rule = trade.Rules[0];
        return $"{_session.Scenario.StationName(trade.StationId)} {RunReport.FormatAction(rule.Action)}{_session.Scenario.GoodName(rule.GoodId)} 报价 {rule.QuotedPrice} 期望 {rule.ExpectedQuantity} 实际 {rule.ActualQuantity} {RunReport.FormatOutcome(rule.Outcome)}"
               + (rule.Cause == RuleFailureCause.None ? "" : $" 主因：{RunReport.FormatCause(rule.Cause)}");
    }

    private void PlaceTrain(string stationId)
    {
        _train.Position = PositionOf(stationId);
    }

    private Vector3 PositionOf(string stationId)
    {
        return _corridor.TryGetValue(stationId, out var position) ? position : Vector3.Zero;
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

        var light = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-50f, -30f, 0f)
        };
        root.AddChild(light);

        root.AddChild(MakeBox(new Vector3(0f, -0.2f, 0f), new Vector3(16f, 0.2f, 6f), new Color(0.36f, 0.48f, 0.24f)));
        root.AddChild(MakeBox(new Vector3(0f, 0.02f, 0f), new Vector3(14f, 0.06f, 0.45f), new Color(0.22f, 0.22f, 0.22f)));
        root.AddChild(MakeLabeledStation(StationIds.WestBarn, "西仓"));
        root.AddChild(MakeLabeledStation(StationIds.Hub, "中枢"));
        root.AddChild(MakeLabeledStation(StationIds.SouthMarket, "南市"));

        _train = MakeBox(PositionOf(StationIds.WestBarn), new Vector3(0.7f, 0.45f, 0.45f), new Color(0.55f, 0.22f, 0.12f));
        root.AddChild(_train);
        return root;
    }

    private static MeshInstance3D MakeBox(Vector3 position, Vector3 size, Color color)
    {
        var mesh = new BoxMesh { Size = size };
        var material = new StandardMaterial3D { AlbedoColor = color };
        var instance = new MeshInstance3D
        {
            Mesh = mesh,
            Position = position
        };
        instance.SetSurfaceOverrideMaterial(0, material);
        return instance;
    }

    private Node3D MakeLabeledStation(string stationId, string name)
    {
        var node = new Node3D { Position = PositionOf(stationId) };
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
        return node;
    }

    private sealed record VisualStep(
        float Duration,
        bool Moves,
        Vector3 From,
        Vector3 To,
        Action Apply);
}
