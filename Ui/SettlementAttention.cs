using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal static class SettlementAttention
{
    internal sealed record Snapshot(
        IReadOnlyList<(BuildingId Building, string Text)> Buildings,
        IReadOnlyList<(GoodId Good, string Text)> Goods,
        IReadOnlyList<string> Sites);

    internal static Snapshot Read(ICoreReadView view, IContentView content, SettlementView settlement)
    {
        var buildings = settlement.Facilities.Where(NeedsAttention).GroupBy(item => item.Key.Building)
            .Select(group => (group.Key,
                $"{SettlementBuildingsPage.BuildingName(view, content, group.Key)}停工：" +
                string.Join("；", group.Select(SettlementBuildingsPage.Status).Distinct()))).ToArray();
        var inventory = (view.FindInventory(settlement.Inventory) as Lookup<InventoryView>.Found)?.Value;
        var goods = settlement.Market.Goods.Select(item =>
                (Item: item, Quantity: inventory?.Stacks.FirstOrDefault(stack => stack.Good == item.Good)?.Quantity ?? 0))
            .Where(item => item.Quantity < item.Item.RestockTarget)
            .Select(item => (item.Item.Good,
                $"{SettlementOverviewPage.GoodName(item.Item.Good, content)}低于补货目标：{item.Quantity} / {item.Item.RestockTarget}"))
            .ToArray();
        return new(buildings, goods, SettlementBuildingsPage.ConstructionSites(view, content, settlement).ToArray());
    }

    private static bool NeedsAttention(FacilityView facility) => facility.Enabled &&
        facility.State is not (ProductionState.Running or ProductionState.Disabled);
}
