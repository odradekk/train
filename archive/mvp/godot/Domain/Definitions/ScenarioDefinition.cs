using Godot;

namespace TrainMvp.Domain.Definitions;

[GlobalClass]
public partial class ScenarioDefinition : Resource
{
    [Export] public int StartingCash { get; set; } = 80;
    [Export] public int CargoCapacity { get; set; } = 8;
    [Export] public string StartingStationId { get; set; } = StationIds.WestBarn;
    [Export] public Godot.Collections.Array<GoodDefinition> Goods { get; set; } = [];
    [Export] public Godot.Collections.Array<StationDefinition> Stations { get; set; } = [];
    [Export] public Godot.Collections.Array<RailEdgeDefinition> Edges { get; set; } = [];
    [Export] public TradePlanDefinition PresetPlan { get; set; } = new();

    public ScenarioDefinition()
    {
    }
}
