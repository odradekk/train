using Godot;

namespace TrainMvp.Domain.Definitions;

[GlobalClass]
public partial class GoodDefinition : Resource
{
    [Export] public string Id { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    [Export] public int BasePrice { get; set; }
    [Export] public double Sensitivity { get; set; }
    [Export] public int MinPrice { get; set; }
    [Export] public int MaxPrice { get; set; }

    public GoodDefinition()
    {
    }
}
