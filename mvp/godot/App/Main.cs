using Godot;
using TrainMvp.Domain;
using TrainMvp.Domain.Definitions;
using TrainMvp.Planning;
using TrainMvp.Run;

namespace TrainMvp.App;

public partial class Main : Control
{
    private GameSession _session = null!;
    private PlanningScreen _planning = null!;
    private RunScreen _run = null!;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Theme = UiTheme.Create();
        var paper = new ColorRect
        {
            Color = UiTheme.Paper
        };
        paper.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(paper);

        var resources = ScenarioResourceFactory.CreateApproved();
        var scenario = ScenarioMapper.ToDomain(resources);
        var engine = new SimulationEngine(scenario);
        _session = new GameSession(engine, scenario);

        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        AddChild(margin);

        _planning = new PlanningScreen();
        _planning.Bind(_session);
        _planning.StartLoopRequested += StartLoop;
        _planning.ResetRequested += ResetScenario;
        margin.AddChild(_planning);

        _run = new RunScreen();
        _run.Bind(_session);
        _run.Visible = false;
        _run.ReturnToPlanningRequested += ReturnToPlanning;
        margin.AddChild(_run);

        GD.Print("PLANNING_READY");
        if (WantsLayoutSmoke() || WantsPlayerGrainCoal() || WantsPlayerTwoLoops())
        {
            CallDeferred(nameof(RunVerificationDeferred));
        }
    }

    private void StartLoop()
    {
        _session.StartLoop();
        _planning.Visible = false;
        _run.Visible = true;
        _run.PlayCurrentRun();
    }

    private void ResetScenario()
    {
        _session.ResetScenario();
        _run.Visible = false;
        _planning.Visible = true;
        _planning.Refresh();
    }

    private void ReturnToPlanning()
    {
        _session.ReturnToPlanning();
        _run.Visible = false;
        _planning.Visible = true;
        _planning.Refresh();
    }

    private async void RunVerificationDeferred()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var failures = new List<string>();
        if (WantsLayoutSmoke())
        {
            failures.AddRange(await MeasureLayouts());
        }

        if (WantsPlayerTwoLoops())
        {
            failures.AddRange(await PlayTwoStrategyIterations());
        }

        if (WantsPlayerGrainCoal())
        {
            ResetScenario();
            failures.AddRange(PlayGrainCoalThroughInspector());
        }

        if (failures.Count == 0)
        {
            if (WantsLayoutSmoke())
            {
                GD.Print("LAYOUT_SMOKE_OK");
            }

            if (WantsPlayerTwoLoops())
            {
                GD.Print("PLAYER_TWO_LOOPS_OK");
            }

            if (WantsPlayerGrainCoal())
            {
                GD.Print("PLAYER_GRAIN_COAL_OK");
            }

            GetTree().Quit(0);
            return;
        }

        foreach (var failure in failures)
        {
            GD.PrintErr(failure);
        }

        if (WantsLayoutSmoke())
        {
            GD.Print("LAYOUT_SMOKE_FAIL");
        }

        if (WantsPlayerTwoLoops())
        {
            GD.Print("PLAYER_TWO_LOOPS_FAIL");
        }

        if (WantsPlayerGrainCoal())
        {
            GD.Print("PLAYER_GRAIN_COAL_FAIL");
        }

        GetTree().Quit(1);
    }

    private async Task<List<string>> MeasureLayouts()
    {
        var failures = new List<string>();

        await SetLogicalSize(new Vector2I(1280, 720));
        failures.AddRange(LayoutSmoke.CheckLogicalViewport(GetViewport(), 1280, 720).Select(item => $"1280x720: {item}"));
        failures.AddRange(LayoutSmoke.Check(_planning, _session.Scenario).Select(item => $"1280x720: {item}"));
        SaveShot("planning-layout.png");

        ResetScenario();
        _planning.PlayerJoinAllStations();
        _planning.Refresh();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.CheckTwelveStopLoop(_planning).Select(item => $"12-stop-720: {item}"));
        LayoutSmoke.ScrollLoopRows(_planning, toEnd: false);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.CheckLoopRowReachable(_planning, 0, "top").Select(item => $"12-stop-720-top: {item}"));
        SaveShot("planning-loop-top.png");
        LayoutSmoke.ScrollLoopRows(_planning, toEnd: true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.CheckLoopRowReachable(_planning, 11, "bottom").Select(item => $"12-stop-720-bottom: {item}"));
        SaveShot("planning-loop-bottom.png");

        ResetScenario();
        await SetLogicalSize(new Vector2I(1600, 900));
        failures.AddRange(LayoutSmoke.Check(_planning, _session.Scenario).Select(item => $"1600x900: {item}"));

        ResetScenario();
        await SetLogicalSize(new Vector2I(1920, 1080));
        failures.AddRange(LayoutSmoke.CheckLogicalViewport(GetViewport(), 1920, 1080).Select(item => $"1920x1080: {item}"));
        failures.AddRange(LayoutSmoke.Check(_planning, _session.Scenario).Select(item => $"1920x1080: {item}"));
        failures.AddRange(LayoutSmoke.Check1080UsesSpace(_planning).Select(item => $"1920x1080: {item}"));
        SaveShot("planning-1080.png");

        _planning.PlayerJoinAllStations();
        _planning.Refresh();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.CheckTwelveStopLoop(_planning).Select(item => $"12-stop-1080: {item}"));
        ResetScenario();
        return failures;
    }

    private async Task<List<string>> PlayTwoStrategyIterations()
    {
        var failures = new List<string>();
        await SetLogicalSize(new Vector2I(1920, 1080));
        ResetScenario();
        if (!_session.CanStart)
        {
            failures.Add("preset plan cannot start");
            return failures;
        }

        if (await PressButton("StartLoopButton") is { } startError)
        {
            failures.Add(startError);
            return failures;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.Check1080RunUsesSpace(_run).Select(item => $"1920x1080-run: {item}"));
        SaveShot("run-1080.png");
        _run.CompletePlaybackImmediately();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_session.Phase != AppPhase.Report)
        {
            failures.Add($"after first loop phase is {_session.Phase}");
        }

        var first = _session.CurrentRun?.Report;
        if (first is null || first.Profit != -48 || first.NextHint != "南市卖粮未成交：现价 16 低于门槛 17。")
        {
            failures.Add($"first loop report unexpected: profit={first?.Profit} hint={first?.NextHint}");
        }

        failures.AddRange(LayoutSmoke.Check1080ReportUsesSpace(_run).Select(item => $"1920x1080-report: {item}"));
        SaveShot("report-1080.png");
        if (await PressButton("CloseReportButton") is { } closeError)
        {
            failures.Add(closeError);
            return failures;
        }

        if (_session.Phase != AppPhase.FinishedObservation || _session.CanStart)
        {
            failures.Add("finished observation did not block the old loop");
        }

        failures.AddRange(LayoutSmoke.CheckFinishedObservation(_run).Select(item => $"finished: {item}"));
        SaveShot("finished-1080.png");
        if (await PressButton("FinishedReturnButton") is { } returnError)
        {
            failures.Add(returnError);
            return failures;
        }

        if (_session.Phase != AppPhase.Planning || !_planning.Visible || _run.Visible)
        {
            failures.Add($"return button did not restore planning: phase={_session.Phase}");
        }

        if (_session.State.Cash != 32
            || _session.State.Holding(GoodIds.Grain) != 8
            || _session.State.LocationId != StationIds.WestBarn
            || _session.CurrentRun is null
            || !_session.Plan.Stops.SequenceEqual([StationIds.WestBarn, StationIds.SouthMarket])
            || _session.Plan.RulesAt(StationIds.SouthMarket)[0].Threshold != 17)
        {
            failures.Add(
                $"retained state cash={_session.State.Cash} grain={_session.State.Holding(GoodIds.Grain)} loc={_session.State.LocationId} threshold={_session.Plan.RulesAt(StationIds.SouthMarket)[0].Threshold}");
        }

        _planning.PlayerSelectStation(StationIds.SouthMarket);
        var saveError = _planning.PlayerSaveRule(GoodIds.Grain, TradeAction.Sell, 12, 0);

        if (!string.IsNullOrEmpty(saveError))
        {
            failures.Add($"second-loop save: {saveError}");
            return failures;
        }

        if (await PressButton("StartLoopButton") is { } secondStartError)
        {
            failures.Add(secondStartError);
            return failures;
        }

        _run.CompletePlaybackImmediately();
        var second = _session.CurrentRun?.Report;
        GD.Print($"PLAYER_TWO_LOOPS_FIRST {first?.Profit} SECOND {second?.Profit} PREV {_session.PreviousReport?.Profit}");
        if (second is null || second.Profit != 128)
        {
            failures.Add($"second loop profit {second?.Profit}, expected 128 from retained 8 grain sold at 16");
        }

        if (_session.PreviousReport?.Profit != -48)
        {
            failures.Add($"previous report {_session.PreviousReport?.Profit}, expected -48");
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.Check1080ReportUsesSpace(_run).Select(item => $"1920x1080-report2: {item}"));
        SaveShot("report-second-1080.png");
        return failures;
    }

    private async Task<string?> PressButton(string name)
    {
        var error = LayoutSmoke.PressNamedButton(this, name);
        if (error is not null)
        {
            return error;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        return null;
    }

    private async Task SetLogicalSize(Vector2I size)
    {
        var window = GetWindow();
        window.ContentScaleSize = size;
        window.Size = size;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _planning.ApplyViewportLayout();
        _run.ApplyViewportLayout();
        _planning.Refresh();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void SaveShot(string fileName)
    {
        var shot = OS.GetUserDataDir().PathJoin(fileName);
        var saved = LayoutSmoke.TrySavePlanningShot(GetViewport(), shot);
        if (saved is null)
        {
            GD.Print($"LAYOUT_SMOKE_SHOT_SKIPPED {fileName}");
        }
        else
        {
            GD.Print($"LAYOUT_SMOKE_SHOT {saved}");
        }
    }

    private List<string> PlayGrainCoalThroughInspector()
    {
        var failures = new List<string>();
        _planning.PlayerSelectStation(StationIds.SouthMarket);
        _planning.PlayerLeaveSelected();
        Join(StationIds.NorthMine, failures);
        Join(StationIds.SouthMarket, failures);
        Join(StationIds.SouthFactory, failures);
        Save(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 4, failures);
        Save(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 4, failures);
        Save(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 0, failures);
        Save(StationIds.SouthFactory, GoodIds.Coal, TradeAction.Sell, 20, 0, failures);
        if (!_session.CanStart)
        {
            failures.Add("grain-coal plan cannot start: " + string.Join(" ", _session.Issues.Select(issue => issue.Message)));
            return failures;
        }

        StartLoop();
        _run.CompletePlaybackImmediately();
        var report = _session.CurrentRun?.Report;
        GD.Print($"PLAYER_GRAIN_COAL_PROFIT {report?.Profit}");
        if (report?.Profit != 112)
        {
            failures.Add($"grain-coal profit {report?.Profit}, expected 112");
        }

        return failures;
    }

    private void Join(string stationId, List<string> failures)
    {
        _planning.PlayerSelectStation(stationId);
        _planning.PlayerJoinSelected();
        if (!_session.Plan.Stops.Contains(stationId))
        {
            failures.Add($"join {stationId} failed: {_planning.JoinErrorText}");
        }
    }

    private void Save(string stationId, string goodId, TradeAction action, int threshold, int target, List<string> failures)
    {
        _planning.PlayerSelectStation(stationId);
        var error = _planning.PlayerSaveRule(goodId, action, threshold, target);
        if (!string.IsNullOrEmpty(error))
        {
            failures.Add($"save {stationId} {goodId}: {error}");
        }
    }

    private static bool WantsLayoutSmoke() => OS.GetCmdlineUserArgs().Contains("--layout-smoke");

    private static bool WantsPlayerGrainCoal() => OS.GetCmdlineUserArgs().Contains("--player-grain-coal");

    private static bool WantsPlayerTwoLoops() => OS.GetCmdlineUserArgs().Contains("--player-two-loops");
}
