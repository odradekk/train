using Godot;

namespace TrainMvp.Domain.Definitions;

[GlobalClass]
public partial class StationDefinition : Resource
{
    [Export] public string Id { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    [Export] public int GridX { get; set; }
    [Export] public int GridY { get; set; }
    [Export] public string Role { get; set; } = "";
    [Export] public Godot.Collections.Array<StationGoodDefinition> Markets { get; set; } = [];

    public StationDefinition()
    {
    }
}
