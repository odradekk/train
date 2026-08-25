using Godot;

namespace TrainMvp.Domain.Definitions;

[GlobalClass]
public partial class TradeRuleDefinition : Resource
{
    [Export] public string StationId { get; set; } = "";
    [Export] public string GoodId { get; set; } = "";
    [Export] public bool IsBuy { get; set; } = true;
    [Export] public int Threshold { get; set; } = 1;
    [Export] public int TargetHolding { get; set; }

    public TradeRuleDefinition()
    {
    }
}
