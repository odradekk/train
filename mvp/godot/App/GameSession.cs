using TrainMvp.Domain;

namespace TrainMvp.App;

public enum AppPhase
{
    Planning,
    Repositioning,
    Running,
    Report,
    FinishedObservation
}

public sealed class GameSession
{
    private readonly List<AppPhase> _phaseHistory = [];

    public GameSession(SimulationEngine engine, ScenarioData scenario)
    {
        Engine = engine;
        Scenario = scenario;
        State = engine.CreateInitialState();
        SelectedStationId = StationIds.WestBarn;
        SetPhase(AppPhase.Planning);
    }

    public SimulationEngine Engine { get; }
    public ScenarioData Scenario { get; }
    public SimulationState State { get; private set; }
    public AppPhase Phase { get; private set; }
    public IReadOnlyList<AppPhase> PhaseHistory => _phaseHistory;
    public LoopRunResult? CurrentRun { get; private set; }
    public RunReport? PreviousReport { get; private set; }
    public string SelectedStationId { get; set; }

    public TradePlan Plan => State.Plan;

    public IReadOnlyList<PlanIssue> Issues => TradePlan.Validate(State.Plan, Scenario);

    public bool CanStart => Phase == AppPhase.Planning && TradePlan.CanStart(State.Plan, Scenario);

    public bool CanReturnToPlanning => Phase is AppPhase.Report or AppPhase.FinishedObservation;

    public void ResetScenario()
    {
        PreviousReport = null;
        CurrentRun = null;
        State = Engine.CreateInitialState();
        SelectedStationId = StationIds.WestBarn;
        _phaseHistory.Clear();
        SetPhase(AppPhase.Planning);
    }

    public void ReplacePlan(TradePlan plan)
    {
        EnsurePlanning();
        State.Plan = plan;
    }

    public void AddStop(string stationId)
    {
        EnsurePlanning();
        State.Plan = State.Plan.AddStop(stationId);
    }

    public void RemoveStop(string stationId)
    {
        EnsurePlanning();
        State.Plan = State.Plan.RemoveStop(stationId);
    }

    public void MoveStop(int index, int delta)
    {
        EnsurePlanning();
        State.Plan = State.Plan.MoveStop(index, delta);
    }

    public void SaveRule(TradeRule rule)
    {
        EnsurePlanning();
        State.Plan = State.Plan.UpsertRule(rule);
    }

    public void RemoveRule(string stationId, string goodId)
    {
        EnsurePlanning();
        State.Plan = State.Plan.RemoveRule(stationId, goodId);
    }

    public void MoveRule(string stationId, int index, int delta)
    {
        EnsurePlanning();
        State.Plan = State.Plan.MoveRule(stationId, index, delta);
    }

    public LoopRunResult StartLoop()
    {
        if (!CanStart)
        {
            throw new InvalidOperationException("The current trade plan cannot start.");
        }

        var result = Engine.RunLoop(State);
        PreviousReport = CurrentRun?.Report;
        CurrentRun = result;
        SetPhase(result.Report.RepositionTicks > 0 ? AppPhase.Repositioning : AppPhase.Running);
        return result;
    }

    public void BeginRunning()
    {
        if (Phase == AppPhase.Repositioning)
        {
            SetPhase(AppPhase.Running);
        }
    }

    public void ShowReport()
    {
        if (CurrentRun is null || Phase != AppPhase.Running)
        {
            throw new InvalidOperationException(
                Phase == AppPhase.Repositioning
                    ? "Report is only available after running has begun."
                    : "No finished loop is available.");
        }

        SetPhase(AppPhase.Report);
    }

    public void CloseReport()
    {
        if (Phase != AppPhase.Report)
        {
            throw new InvalidOperationException("No open report to close.");
        }

        SetPhase(AppPhase.FinishedObservation);
    }

    public void ReturnToPlanning()
    {
        if (!CanReturnToPlanning)
        {
            throw new InvalidOperationException("Return to planning is only available after a report.");
        }

        if (CurrentRun is not null)
        {
            State = CurrentRun.StateAfter;
        }

        SetPhase(AppPhase.Planning);
    }

    private void SetPhase(AppPhase phase)
    {
        Phase = phase;
        _phaseHistory.Add(phase);
    }

    private void EnsurePlanning()
    {
        if (Phase != AppPhase.Planning)
        {
            throw new InvalidOperationException("The trade plan cannot change while a loop is running.");
        }
    }
}
