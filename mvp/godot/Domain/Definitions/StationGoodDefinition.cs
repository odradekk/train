using Godot;

namespace TrainMvp.Domain.Definitions;

[GlobalClass]
public partial class StationGoodDefinition : Resource
{
    [Export] public string GoodId { get; set; } = "";
    [Export] public int Supply { get; set; }
    [Export] public int SupplyCap { get; set; }
    [Export] public int SupplyRestore { get; set; }
    [Export] public int Demand { get; set; }
    [Export] public int DemandCap { get; set; }
    [Export] public int DemandRestore { get; set; }

    public StationGoodDefinition()
    {
    }
}
