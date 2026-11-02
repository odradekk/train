using System.Collections.Immutable;
using Train.App;
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
            JobQuote? chosen = null;
            var reason = "当地没有可用的修理设施。";
            foreach (var building in settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>())
            {
                var request = new JobRequest.Repair(new(building.Id, CapabilityKind.Construction), instance.Id, [], 0);
                switch (view.GetJobQuote(request, JobFunding.NpcCommission))
                {
                    case CoreResult<JobQuote>.Success { Value: var quote }:
                        if (quote.Blockers.IsEmpty) chosen = quote;
                        else reason = string.Join("\n", quote.Blockers.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, content)));
                        break;
                    case CoreResult<JobQuote>.Failure failure:
                        reason = string.Join("\n", failure.Problems.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, content)));
                        break;
                    case CoreResult<JobQuote>.Faulted:
                        return new([], null, "核心模拟发生故障，无法获取修理报价。");
                }
                if (chosen is not null) break;
            }
            if (chosen is null) return new([], null, reason);
            if (chosen.TotalPayment is not { } payment) return new([], null, "修理报价没有提供金额。");
            jobs.Add(chosen); total = checked(total + payment.Value);
        }
        return new(jobs.ToImmutable(), new(total), jobs.Count == 0 ? "没有损伤，不需要修理" :
            total > view.Economy.Wallet.Value ? "资金不足。" : null);
    }
}
