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
        _run.ResetRequested += ResetScenario;
        margin.AddChild(_run);

        GD.Print("PLANNING_READY");
        if (WantsLayoutSmoke() || WantsPlayerGrainCoal() || WantsPlayerTwoLoops() || WantsPlayerRunControls()
            || WantsPlayerLocationTimeline() || WantsPlayerRepositionControls())
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
        var failures = new List<string>();
        try
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

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
                failures.AddRange(await PlayGrainCoalThroughInspector());
            }

            if (WantsPlayerRunControls())
            {
                failures.AddRange(await PlayRunControls());
            }

            if (WantsPlayerLocationTimeline())
            {
                failures.AddRange(await PlayLocationTimeline());
            }

            if (WantsPlayerRepositionControls())
            {
                failures.AddRange(await PlayRepositionControls());
            }

            if (failures.Count == 0)
            {
                PrintOkFlags();
                GetTree().Quit(0);
                return;
            }
        }
        catch (Exception exception)
        {
            GD.PrintErr(exception.ToString());
            failures.Add(exception.Message);
        }

        foreach (var failure in failures)
        {
            GD.PrintErr(failure);
        }

        PrintFailFlags();
        GetTree().Quit(1);
    }

    private static void PrintOkFlags()
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

        if (WantsPlayerRunControls())
        {
            GD.Print("PLAYER_RUN_CONTROLS_OK");
        }

        if (WantsPlayerLocationTimeline())
        {
            GD.Print("PLAYER_LOCATION_TIMELINE_OK");
        }

        if (WantsPlayerRepositionControls())
        {
            GD.Print("PLAYER_REPOSITION_CONTROLS_OK");
        }
    }

    private static void PrintFailFlags()
    {
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

        if (WantsPlayerRunControls())
        {
            GD.Print("PLAYER_RUN_CONTROLS_FAIL");
        }

        if (WantsPlayerLocationTimeline())
        {
            GD.Print("PLAYER_LOCATION_TIMELINE_FAIL");
        }

        if (WantsPlayerRepositionControls())
        {
            GD.Print("PLAYER_REPOSITION_CONTROLS_FAIL");
        }
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
        if (_session.CanStart)
        {
            StartLoop();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            RenderingServer.ForceDraw();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            failures.AddRange(LayoutSmoke.Check720RunControls(_run).Select(item => $"720-run: {item}"));
            SaveShot("run-720.png");
            ResetScenario();
        }

        ResetScenario();
        await SetLogicalSize(new Vector2I(1600, 900));
        failures.AddRange(LayoutSmoke.Check(_planning, _session.Scenario).Select(item => $"1600x900: {item}"));

        ResetScenario();
        await SetLogicalSize(new Vector2I(1920, 1080));
        failures.AddRange(LayoutSmoke.CheckLogicalViewport(GetViewport(), 1920, 1080).Select(item => $"1920x1080: {item}"));
        failures.AddRange(LayoutSmoke.Check(_planning, _session.Scenario).Select(item => $"1920x1080: {item}"));
        failures.AddRange(LayoutSmoke.Check1080UsesSpace(_planning).Select(item => $"1920x1080: {item}"));
        SaveShot("planning-1080.png");

        if (_session.CanStart)
        {
            StartLoop();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            RenderingServer.ForceDraw();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            failures.AddRange(LayoutSmoke.Check1080RunSplit(_run).Select(item => $"1920x1080-run: {item}"));
            SaveShot("run-1080.png");
            ResetScenario();
        }

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
        failures.AddRange(LayoutSmoke.Check1080RunSplit(_run).Select(item => $"1920x1080-run: {item}"));
        SaveShot("run-1080.png");
        if (await PressButton("FastCompleteButton") is { } fastError)
        {
            failures.Add(fastError);
            return failures;
        }

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

        if (await PressButton("FastCompleteButton") is { } secondFastError)
        {
            failures.Add(secondFastError);
            return failures;
        }

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

    private async Task<List<string>> PlayGrainCoalThroughInspector()
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

        if (await PressButton("StartLoopButton") is { } startError)
        {
            failures.Add(startError);
            return failures;
        }

        if (await PressButton("FastCompleteButton") is { } fastError)
        {
            failures.Add(fastError);
            return failures;
        }

        var report = _session.CurrentRun?.Report;
        GD.Print($"PLAYER_GRAIN_COAL_PROFIT {report?.Profit}");
        if (report?.Profit != 112)
        {
            failures.Add($"grain-coal profit {report?.Profit}, expected 112");
        }

        return failures;
    }

    private async Task<List<string>> PlayRunControls()
    {
        var failures = new List<string>();
        await SetLogicalSize(new Vector2I(1920, 1080));
        ResetScenario();
        if (await PressButton("StartLoopButton") is { } startError)
        {
            failures.Add(startError);
            return failures;
        }

        var playback = _run.Playback;
        if (playback is null)
        {
            failures.Add("playback missing after start");
            return failures;
        }

        var applied = playback.AppliedEventCount;
        var cash = playback.DisplayState.Cash;
        if (await PressButton("PauseButton") is { } pauseError)
        {
            failures.Add(pauseError);
            return failures;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!playback.IsPaused || playback.AppliedEventCount != applied || playback.DisplayState.Cash != cash)
        {
            failures.Add("pause advanced presentation or economic display");
        }

        if (await PressButton("ResumeButton") is { } resumeError)
        {
            failures.Add(resumeError);
            return failures;
        }

        if (await PressButton("Speed4Button") is { } speed4Error)
        {
            failures.Add(speed4Error);
            return failures;
        }

        if (playback.Speed != LoopPlayback.Speed4)
        {
            failures.Add($"speed 4 became {playback.Speed}");
        }

        if (await PressButton("Speed16Button") is { } speed16Error)
        {
            failures.Add(speed16Error);
            return failures;
        }

        if (playback.Speed != LoopPlayback.Speed16)
        {
            failures.Add($"speed 16 became {playback.Speed}");
        }

        if (await PressButton("Speed1Button") is { } speed1Error)
        {
            failures.Add(speed1Error);
            return failures;
        }

        if (await PressButton("SkipStopButton") is { } skipError)
        {
            failures.Add(skipError);
            return failures;
        }

        if (playback.AppliedEvents.OfType<TradeEvent>().All(trade => trade.StationId != StationIds.SouthMarket))
        {
            failures.Add("skip did not land on the next trade stop");
        }

        if (await PressButton("FastCompleteButton") is { } fastError)
        {
            failures.Add(fastError);
            return failures;
        }

        if (_session.Phase != AppPhase.Report || playback.CanControl)
        {
            failures.Add($"fast complete phase={_session.Phase} canControl={playback.CanControl}");
        }

        if (await PressButton("ResetButton") is { } resetError)
        {
            failures.Add(resetError);
            return failures;
        }

        if (_session.Phase != AppPhase.Planning || _session.State.Cash != 80 || _session.State.Holding(GoodIds.Grain) != 0)
        {
            failures.Add($"reset cash={_session.State.Cash} grain={_session.State.Holding(GoodIds.Grain)} phase={_session.Phase}");
        }

        GD.Print("PLAYER_RUN_CONTROLS_PATH pause/resume/1/4/16/skip/fast/reset");
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

    private async Task<List<string>> PlayLocationTimeline()
    {
        var failures = new List<string>();
        await SetLogicalSize(new Vector2I(1920, 1080));
        ResetScenario();
        if (await PressButton("StartLoopButton") is { } startError)
        {
            failures.Add(startError);
            return failures;
        }

        if (await PressButton("PauseButton") is { } pauseError)
        {
            failures.Add(pauseError);
            return failures;
        }

        var playback = _run.Playback;
        if (playback is null)
        {
            failures.Add("location timeline missing playback");
            return failures;
        }

        var outbound = new List<string>();
        var outboundMotions = new List<string>();
        var returnMotions = new List<string>();
        var seenSouthTrade = false;
        while (true)
        {
            _run.SyncPresentation();
            var location = playback.DisplayState.LocationId;
            var hud = _run.FindChild("LocationLabel", true, false) is Label label ? label.Text : "";
            var motion = string.Join(">", playback.MotionPath);
            var eventName = playback.PresentationEvent?.GetType().Name ?? "none";
            GD.Print($"LOCATION_TIMELINE loc={location} hud={hud} event={eventName} motion={motion}");
            if (hud.Length > 0 && !hud.Contains(_session.Scenario.StationName(location)))
            {
                failures.Add($"HUD {hud} does not match location {location}");
            }

            if (!seenSouthTrade)
            {
                outbound.Add(location);
                if (motion.Length > 0)
                {
                    outboundMotions.Add(motion);
                }

                if (location == StationIds.SouthMarket && playback.CurrentEvent is TradeEvent)
                {
                    seenSouthTrade = true;
                }
            }
            else if (motion.Length > 0)
            {
                returnMotions.Add(motion);
            }

            if (playback.IsFinished)
            {
                break;
            }

            playback.AdvanceEvent();
        }

        GD.Print($"LOCATION_TIMELINE_OUTBOUND {string.Join(">", outbound)}");
        GD.Print($"LOCATION_TIMELINE_MOTIONS {string.Join(",", outboundMotions)} return={string.Join(",", returnMotions)}");

        if (outbound.Count == 0 || outbound[0] != StationIds.WestBarn)
        {
            failures.Add("outbound did not start at 西仓");
        }

        var firstHub = outbound.IndexOf(StationIds.Hub);
        var firstSouth = outbound.IndexOf(StationIds.SouthMarket);
        if (firstHub < 0 || firstSouth < 0 || firstSouth < firstHub)
        {
            failures.Add($"outbound was not 西仓→中枢→南市: {string.Join(">", outbound)}");
        }

        if (outbound.Take(firstHub).Contains(StationIds.SouthMarket))
        {
            failures.Add("outbound showed 南市 before 中枢");
        }

        if (!outboundMotions.Contains("west_barn>hub") || !outboundMotions.Contains("hub>south_market"))
        {
            failures.Add($"outbound motions missing hops: {string.Join(",", outboundMotions)}");
        }

        var southTradeAfterHops = outboundMotions.IndexOf("hub>south_market");
        if (southTradeAfterHops < 0 || outboundMotions.IndexOf("west_barn>hub") < 0
            || outboundMotions.IndexOf("west_barn>hub") > southTradeAfterHops)
        {
            failures.Add("hub>south_market did not follow west_barn>hub before 南市 trade");
        }

        if (!returnMotions.Contains("south_market>hub") || !returnMotions.Contains("hub>west_barn"))
        {
            failures.Add($"return motions missing hops: {string.Join(",", returnMotions)}");
        }

        return failures;
    }

    private async Task<List<string>> PlayRepositionControls()
    {
        var failures = new List<string>();
        await SetLogicalSize(new Vector2I(1920, 1080));
        ResetScenario();
        PrepareRepositionPlan(failures);
        if (failures.Count > 0)
        {
            return failures;
        }

        if (await PressButton("StartLoopButton") is { } startError)
        {
            failures.Add(startError);
            return failures;
        }

        if (_session.Phase != AppPhase.Repositioning)
        {
            failures.Add($"expected Repositioning, got {_session.Phase}");
            return failures;
        }

        GD.Print($"RUN_PHASE {_session.Phase}");
        var report = _session.CurrentRun!.Report;
        var reportSnap = $"{report.Profit}|{report.LoopTicks}|{report.RepositionTicks}|{report.CashAfter}|{report.NextHint}";
        var playback = _run.Playback;
        if (playback is null)
        {
            failures.Add("reposition playback missing");
            return failures;
        }

        if (playback.AppliedEvents.OfType<TradeEvent>().Any())
        {
            failures.Add("reposition start already traded");
        }

        if (await PressButton("PauseButton") is { } pauseError)
        {
            failures.Add(pauseError);
            return failures;
        }

        if (_session.Phase != AppPhase.Repositioning || playback.AppliedEvents.OfType<TradeEvent>().Any())
        {
            failures.Add("pause during reposition traded or left Repositioning");
        }

        if (await PressButton("ResumeButton") is { } resumeError)
        {
            failures.Add(resumeError);
            return failures;
        }

        if (await PressButton("FastCompleteButton") is { } fastError)
        {
            failures.Add(fastError);
            return failures;
        }

        GD.Print($"PHASE_HISTORY {string.Join("->", _session.PhaseHistory)}");
        if (!ContainsPhasePath(_session.PhaseHistory, AppPhase.Repositioning, AppPhase.Running, AppPhase.Report))
        {
            failures.Add($"phase path missing Repositioning->Running->Report: {string.Join("->", _session.PhaseHistory)}");
        }

        if (_session.Phase != AppPhase.Report)
        {
            failures.Add($"fast complete phase={_session.Phase}");
        }

        var after = _session.CurrentRun!.Report;
        var afterSnap = $"{after.Profit}|{after.LoopTicks}|{after.RepositionTicks}|{after.CashAfter}|{after.NextHint}";
        if (afterSnap != reportSnap)
        {
            failures.Add($"report changed during reposition fast-complete {reportSnap} -> {afterSnap}");
        }

        ResetScenario();
        PrepareRepositionPlan(failures);
        if (await PressButton("StartLoopButton") is { } secondStartError)
        {
            failures.Add(secondStartError);
            return failures;
        }

        GD.Print($"RUN_PHASE {_session.Phase}");
        if (await PressButton("SkipStopButton") is { } skipError)
        {
            failures.Add(skipError);
            return failures;
        }

        if (_session.Phase != AppPhase.Running)
        {
            failures.Add($"skip from reposition phase={_session.Phase}, expected Running");
        }

        while (_session.Phase is AppPhase.Running or AppPhase.Repositioning && _run.Playback is { CanControl: true })
        {
            if (await PressButton("SkipStopButton") is { } laterSkipError)
            {
                failures.Add(laterSkipError);
                break;
            }
        }

        GD.Print($"PHASE_HISTORY_SKIP {string.Join("->", _session.PhaseHistory)}");
        if (!ContainsPhasePath(_session.PhaseHistory, AppPhase.Repositioning, AppPhase.Running, AppPhase.Report))
        {
            failures.Add($"skip path missing Repositioning->Running->Report: {string.Join("->", _session.PhaseHistory)}");
        }

        GD.Print("PLAYER_REPOSITION_CONTROLS_PATH pause/resume/fast plus skip-to-fast");
        return failures;
    }

    private void PrepareRepositionPlan(List<string> failures)
    {
        _planning.PlayerSelectStation(StationIds.SouthMarket);
        _planning.PlayerLeaveSelected();
        Join(StationIds.NorthMine, failures);
        if (!MoveNorthMineToFront(failures))
        {
            return;
        }

        Save(StationIds.NorthMine, GoodIds.Coal, TradeAction.Buy, 32, 1, failures);
        Save(StationIds.WestBarn, GoodIds.Grain, TradeAction.Buy, 18, 1, failures);
        if (!_session.CanStart)
        {
            failures.Add("reposition plan cannot start: " + string.Join(" ", _session.Issues.Select(issue => issue.Message)));
        }
    }

    private bool MoveNorthMineToFront(List<string> failures)
    {
        var error = LayoutSmoke.PressNamedButton(this, "LoopRowUp_1");
        if (error is not null)
        {
            failures.Add(error);
            return false;
        }

        if (!_session.Plan.Stops.SequenceEqual([StationIds.NorthMine, StationIds.WestBarn]))
        {
            failures.Add($"move north mine to front failed: {string.Join(">", _session.Plan.Stops)}");
            return false;
        }

        return true;
    }

    private static bool ContainsPhasePath(IReadOnlyList<AppPhase> history, params AppPhase[] expected)
    {
        var index = 0;
        foreach (var phase in history)
        {
            if (phase == expected[index])
            {
                index++;
                if (index == expected.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool WantsLayoutSmoke() => OS.GetCmdlineUserArgs().Contains("--layout-smoke");

    private static bool WantsPlayerGrainCoal() => OS.GetCmdlineUserArgs().Contains("--player-grain-coal");

    private static bool WantsPlayerTwoLoops() => OS.GetCmdlineUserArgs().Contains("--player-two-loops");

    private static bool WantsPlayerRunControls() => OS.GetCmdlineUserArgs().Contains("--player-run-controls");

    private static bool WantsPlayerLocationTimeline() => OS.GetCmdlineUserArgs().Contains("--player-location-timeline");

    private static bool WantsPlayerRepositionControls() => OS.GetCmdlineUserArgs().Contains("--player-reposition-controls");
}
