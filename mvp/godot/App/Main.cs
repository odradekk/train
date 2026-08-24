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
}
