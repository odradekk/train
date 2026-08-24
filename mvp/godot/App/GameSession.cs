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
    }

    public SimulationEngine Engine { get; }
    public ScenarioData Scenario { get; }
    public SimulationState State { get; private set; }
    public AppPhase Phase { get; private set; }
    public LoopRunResult? CurrentRun { get; private set; }
    public RunReport? PreviousReport { get; private set; }

    public int SouthMarketGrainThreshold
    {
        get
        {
            var rule = State.Plan.Rules.Single(
                item => item.StationId == StationIds.SouthMarket && item.GoodId == GoodIds.Grain);
            return rule.Threshold;
        }
    }

    public void ResetScenario()
    {
        PreviousReport = null;
        CurrentRun = null;
        State = Engine.CreateInitialState();
        Phase = AppPhase.Planning;
    }

    public void SetSouthMarketGrainThreshold(int threshold)
    {
        if (Phase != AppPhase.Planning)
        {
            throw new InvalidOperationException("The trade plan cannot change while a loop is running.");
        }

        State.Plan = State.Plan.WithThreshold(StationIds.SouthMarket, GoodIds.Grain, threshold);
    }

    public LoopRunResult StartLoop()
    {
        if (Phase != AppPhase.Planning)
        {
            throw new InvalidOperationException("A loop can start only from planning.");
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
}
