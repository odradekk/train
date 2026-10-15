using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal static class UiInput
{
    internal static string FormatTime(ulong completedSteps)
    {
        var minutes = completedSteps / (ulong)SimulationTime.StepsPerSecond / 60;
        return $"第{minutes / 1440 + 1}天 {minutes / 60 % 24:00}:{minutes % 60:00}";
    }
}

internal sealed record PanelTarget(HostBinding Binding, ContentGeneration Generation, WorldObjectId Object)
{
    internal bool IsAvailable(HostBinding binding, ContentGeneration generation, ICoreReadView view)
    {
        if (Binding != binding || Generation != generation) return false;
        return Object switch
        {
            WorldObjectId.Node node => view.World.Nodes.Any(item => item.Id == node.Id),
            WorldObjectId.Edge edge => view.World.Edges.Any(item => item.Id == edge.Id),
            WorldObjectId.Train train => view.World.Trains.Any(item => item.Id == train.Id),
            WorldObjectId.Instance instance => view.FindInstance(instance.Id) is Lookup<InstanceView>.Found,
            WorldObjectId.Settlement settlement => view.FindSettlement(settlement.Id) is Lookup<SettlementView>.Found,
            WorldObjectId.Inventory inventory => view.FindInventory(inventory.Id) is Lookup<InventoryView>.Found,
            _ => false
        };
    }
}
