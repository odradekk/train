using TrainMvp.Domain;

namespace TrainMvp.App;

public enum AppPhase
{
    Planning,
    Running,
    Report
}

public sealed class GameSession
{
    public GameSession(SimulationEngine engine, ScenarioData scenario)
    {
        Engine = engine;
        Scenario = scenario;
        State = engine.CreateInitialState();
        Phase = AppPhase.Planning;
        SelectedStationId = StationIds.WestBarn;
    }

    public SimulationEngine Engine { get; }
    public ScenarioData Scenario { get; }
    public SimulationState State { get; private set; }
    public AppPhase Phase { get; private set; }
    public LoopRunResult? CurrentRun { get; private set; }
    public RunReport? PreviousReport { get; private set; }
    public string SelectedStationId { get; set; }

    public TradePlan Plan => State.Plan;

    public IReadOnlyList<PlanIssue> Issues => TradePlan.Validate(State.Plan, Scenario);

    public bool CanStart => Phase == AppPhase.Planning && TradePlan.CanStart(State.Plan, Scenario);

    public void ResetScenario()
    {
        PreviousReport = null;
        CurrentRun = null;
        State = Engine.CreateInitialState();
        SelectedStationId = StationIds.WestBarn;
        Phase = AppPhase.Planning;
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
        Phase = AppPhase.Running;
        return result;
    }

    public void ShowReport()
    {
        if (CurrentRun is null)
        {
            throw new InvalidOperationException("No finished loop is available.");
        }

        Phase = AppPhase.Report;
    }

    public void ReturnToPlanning()
    {
        if (CurrentRun is not null)
        {
            State = CurrentRun.StateAfter;
        }

        Phase = AppPhase.Planning;
    }

    private void EnsurePlanning()
    {
        if (Phase != AppPhase.Planning)
        {
            throw new InvalidOperationException("The trade plan cannot change while a loop is running.");
        }
    }
}
