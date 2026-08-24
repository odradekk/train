using Godot;

namespace TrainMvp.Domain.Definitions;

[GlobalClass]
public partial class TradePlanDefinition : Resource
{
    [Export] public Godot.Collections.Array<string> StopIds { get; set; } = [];
    [Export] public Godot.Collections.Array<TradeRuleDefinition> Rules { get; set; } = [];

    public TradePlanDefinition()
    {
    }
}
