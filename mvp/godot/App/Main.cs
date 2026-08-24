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
        if (WantsLayoutSmoke() || WantsPlayerGrainCoal())
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

        if (WantsPlayerGrainCoal())
        {
            failures.AddRange(PlayGrainCoalThroughInspector());
        }

        if (failures.Count == 0)
        {
            if (WantsLayoutSmoke())
            {
                GD.Print("LAYOUT_SMOKE_OK");
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

        if (WantsPlayerGrainCoal())
        {
            GD.Print("PLAYER_GRAIN_COAL_FAIL");
        }

        GetTree().Quit(1);
    }

    private async Task<List<string>> MeasureLayouts()
    {
        var failures = new List<string>();
        foreach (var size in new[] { new Vector2I(1280, 720), new Vector2I(1600, 900) })
        {
            GetWindow().Size = size;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var found = LayoutSmoke.Check(_planning, _session.Scenario);
            failures.AddRange(found.Select(item => $"{size.X}x{size.Y}: {item}"));
        }

        RenderingServer.ForceDraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        SaveShot("planning-layout.png");

        _planning.PlayerJoinAllStations();
        _planning.Refresh();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.CheckTwelveStopLoop(_planning).Select(item => $"12-stop: {item}"));

        LayoutSmoke.ScrollLoopRows(_planning, toEnd: false);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.CheckLoopRowReachable(_planning, 0, "top").Select(item => $"12-stop-top: {item}"));
        SaveShot("planning-loop-top.png");

        LayoutSmoke.ScrollLoopRows(_planning, toEnd: true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        failures.AddRange(LayoutSmoke.CheckLoopRowReachable(_planning, 11, "bottom").Select(item => $"12-stop-bottom: {item}"));
        SaveShot("planning-loop-bottom.png");

        ResetScenario();
        return failures;
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
        if (_session.Plan.Stops.Contains(StationIds.SouthMarket))
        {
            failures.Add("leaving 南市 did not remove the stop");
        }

        Join(StationIds.NorthMine, failures);
        Join(StationIds.SouthMarket, failures);
        Join(StationIds.SouthFactory, failures);
        Save(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 4, failures);
        Save(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 4, failures);
        Save(StationIds.SouthMarket, GoodIds.Grain, TradeAction.Sell, 12, 0, failures);
        Save(StationIds.SouthFactory, GoodIds.Coal, TradeAction.Sell, 20, 0, failures);

        var expectedStops = new[]
        {
            StationIds.WestBarn,
            StationIds.NorthMine,
            StationIds.SouthMarket,
            StationIds.SouthFactory
        };
        if (!_session.Plan.Stops.SequenceEqual(expectedStops))
        {
            failures.Add("loop stops are " + string.Join(",", _session.Plan.Stops));
        }

        if (!_session.CanStart)
        {
            failures.Add("grain-coal plan cannot start: " + string.Join(" ", _session.Issues.Select(issue => issue.Message)));
            return failures;
        }

        StartLoop();
        _run.CompletePlaybackImmediately();
        var report = _session.CurrentRun?.Report;
        if (report is null)
        {
            failures.Add("no report after grain-coal run");
            return failures;
        }

        GD.Print($"PLAYER_GRAIN_COAL_PROFIT {report.Profit}");
        foreach (var stop in report.Stops)
        {
            foreach (var rule in stop.Rules)
            {
                GD.Print(
                    $"PLAYER_GRAIN_COAL_RULE {stop.StationId} {rule.GoodId} {rule.Action} {rule.Outcome} {rule.ActualQuantity} {rule.Cause}");
            }
        }

        if (report.Profit != 112)
        {
            failures.Add($"grain-coal profit {report.Profit}, expected 112");
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

    private static bool WantsLayoutSmoke()
    {
        return OS.GetCmdlineUserArgs().Contains("--layout-smoke");
    }

    private static bool WantsPlayerGrainCoal()
    {
        return OS.GetCmdlineUserArgs().Contains("--player-grain-coal");
    }
}
