using Godot;

namespace TrainMvp.Domain.Definitions;

[GlobalClass]
public partial class RailEdgeDefinition : Resource
{
    [Export] public string StationAId { get; set; } = "";
    [Export] public string StationBId { get; set; } = "";
    [Export] public int Ticks { get; set; }

    public RailEdgeDefinition()
    {
    }
}
