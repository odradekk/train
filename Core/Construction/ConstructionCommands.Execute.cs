using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Construction;

internal static partial class ConstructionCommands
{
    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, JobRequest request,
        ulong step, JobFunding funding = JobFunding.OwnInventory, Money? maximumPayment = null)
    {
        var (prepared, quote, returns, returnInventory) = Prepare(world, content, step, new(0), request, funding, maximumPayment);
        if (!quote.Blockers.IsEmpty) return new CommandEffect.Rejected(quote.Blockers);
        var (facility, priority, instance, plot) = request switch
        {
            JobRequest.BuildVehicle build => (build.Facility, build.Priority, (InstanceId?)null, (PlotId?)null),
            JobRequest.BuildBuilding build => (build.Facility, build.Priority, (InstanceId?)null, (PlotId?)build.Plot),
            JobRequest.BuildEquipment build => (build.Facility, build.Priority, (InstanceId?)null, (PlotId?)null),
            JobRequest.Refit refit => (refit.Facility, refit.Priority, (InstanceId?)refit.Instance, (PlotId?)null),
            JobRequest.Repair repair => (repair.Facility, repair.Priority, (InstanceId?)repair.Instance, (PlotId?)null),
            JobRequest.Dismantle dismantle => (dismantle.Facility, dismantle.Priority, (InstanceId?)dismantle.Instance, (PlotId?)null),
            _ => throw new InvalidOperationException("施工请求无效。")
        };
        var settlement = world.Settlements.Single(s => s.Plots.Any(p => p.Building == facility.Building));
        var design = request switch
        {
            JobRequest.BuildVehicle build => build.Design.Design,
            JobRequest.BuildBuilding build => build.Design.Design,
            JobRequest.BuildEquipment build => build.Design.Design,
            JobRequest.Refit refit => refit.Change.Target.Design,
            _ => world.Designs.Single(d => d.Design == world.Instances.Single(i => i.Id == instance).Design)
        };
        var rule = content.ConstructionRules.Single(r => r.Id == world.Source.Configuration.Rules.Construction);
        var consumption = quote.SourceConsumption;
        if (!quote.NpcPurchases.IsEmpty) consumption = consumption.Add(new(settlement.Inventory, quote.NpcPurchases));
        var invested = consumption.SelectMany(s => s.Goods).GroupBy(g => g.Good)
            .OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
            .Select(g => new GoodsQuantity(g.Key, g.Sum(x => x.Quantity))).ToImmutableArray();
        var job = new ConstructionJobState(new(world.Next.Job), settlement.Id, facility, design.Design, instance, plot, priority,
            rule.Id, rule.Revision, quote.TotalWork, rule.PowerPerWork, rule.LaborPerWork, invested, consumption,
            0, world.ConstructionJobs.Any(j => j.Facility == facility) ? ConstructionState.Queued : ConstructionState.Working,
            0, [], [])
        {
            Funding = funding,
            Kind = request switch
            {
                JobRequest.Refit => ConstructionKind.Refit,
                JobRequest.Dismantle => ConstructionKind.Deconstruction,
                JobRequest.Repair => ConstructionKind.Repair,
                _ => ConstructionKind.Construction
            },
            RetainedParts = request is JobRequest.Refit r ? WorldInputs.NormalizeDesignChange(r.Change).RetainedParts : [],
            RemovedItems = request is JobRequest.Refit removed ? WorldInputs.NormalizeDesignChange(removed.Change).RemovedItems : [],
            ReturnInventory = returnInventory
        };
        prepared = Consume(prepared, consumption) with
        {
            ConstructionJobs = prepared.ConstructionJobs.Add(job), Next = prepared.Next with { Job = checked(world.Next.Job + 1) },
            Money = new(checked(world.Money.Value - quote.TotalPayment!.Value.Value)),
            CashFlows = quote.TotalPayment.Value.Value == 0 ? prepared.CashFlows : prepared.CashFlows.Add(new(default,
                job.Kind switch
                {
                    ConstructionKind.Refit => CashFlowKind.Refit,
                    ConstructionKind.Repair => CashFlowKind.Repair,
                    ConstructionKind.Deconstruction => CashFlowKind.Deconstruction,
                    _ => CashFlowKind.Construction
                }, -quote.TotalPayment.Value.Value, settlement.Id,
                instance is { } target && world.Instances.Single(i => i.Id == target).Details is InstanceDetails.Vehicle vehicle
                    ? vehicle.Train : null, null, 0)),
            UnassignedServicePayments = new(checked(world.UnassignedServicePayments.Value + quote.ServicePayment!.Value.Value))
        };
        if (job.ReturnInventory is { } destination)
            prepared = prepared with { Inventories = prepared.Inventories.Select(i => i.Id != destination ? i : i with
                { Reservations = i.Reservations.Add(new(job.Id, returns.Sum(g => g.Quantity), returns)) }).ToImmutableArray() };
        prepared = prepared with { DefinitionDependencies = WorldReferences.Dependencies(prepared, content) };
        return new CommandEffect.Accepted(content, [new CoreEvent.ConstructionStarted(job.Read(prepared), funding,
                quote.MaterialPayment!.Value, quote.ServicePayment!.Value)],
            prepared, [new WorldObjectId.Job(job.Id)]);
    }
}
