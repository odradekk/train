using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed record VehicleRepairQuote(ImmutableArray<JobQuote> Jobs, Money? Total, string? Reason);

/// <summary>
/// 按车辆取当地付费修理报价，材料全部当地购买，不消耗车上货物。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal static class VehicleRepairs
{
    internal static bool NeedsRepair(InstanceView instance) =>
        instance.Structure.EffectiveCurrent < instance.Structure.Maximum ||
        instance.Parts.Any(part => part.Durability.EffectiveCurrent < part.Durability.Maximum);

    internal static VehicleRepairQuote Quote(IEnumerable<VehicleId> vehicles, SettlementView settlement,
        ICoreReadView view, IContentView content)
    {
        if (settlement.Relation != SettlementRelation.NonHostile)
            return new([], null, "只在非敌对聚居地提供付费修理。");
        var jobs = ImmutableArray.CreateBuilder<JobQuote>();
        long total = 0;
        foreach (var vehicle in vehicles)
        {
            if (view.FindInstance(new InstanceId.Vehicle(vehicle)) is not Lookup<InstanceView>.Found found)
                return new([], null, "车辆已不存在。");
            var instance = found.Value;
            if (!NeedsRepair(instance)) continue;
            var chosen = EconomyPanels.FindConstructionQuote(view, content,
                settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>().Select(building => building.Id),
                JobFunding.NpcCommission, facility => new JobRequest.Repair(facility, instance.Id, [], 0), out var reason);
            if (chosen is null || !chosen.Blockers.IsEmpty) return new([], null, reason);
            if (chosen.TotalPayment is not { } payment) return new([], null, "修理报价没有提供金额。");
            jobs.Add(chosen); total = checked(total + payment.Value);
        }
        return new(jobs.ToImmutable(), new(total), jobs.Count == 0 ? "没有损伤，不需要修理" :
            total > view.Economy.Wallet.Value ? "资金不足。" : null);
    }
}
