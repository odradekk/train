using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Settlements;

internal abstract record FacilityRequest(FacilityKey Facility, int Priority)
{
    internal sealed record Batch(FacilityKey Facility, int Priority, ImmutableArray<GoodsQuantity> Inputs,
        GoodsQuantity? Extracts = null)
        : FacilityRequest(Facility, Priority);
    internal sealed record Work(FacilityKey Facility, int Priority, double MaximumWorkPerSecond,
        double PowerPerWork, double LaborPerWork) : FacilityRequest(Facility, Priority);
}

internal sealed record WorkAllocation(FacilityKey Facility, double WorkPerSecond, ImmutableArray<WorkLimit> Limits);
internal sealed record SettlementResolution(WorldState World, ImmutableArray<long> BasicGrantedUnits,
    ImmutableArray<FacilityKey> AcceptedBatches, ImmutableArray<WorkAllocation> Work,
    ImmutableArray<long> ImprovementGrantedUnits);

internal static class SettlementResources
{
    // Callers submit at most one request per facility capability and cap work at this step's useful rate.
    internal static SettlementResolution Resolve(WorldState world, CoreContentProjection content, SettlementId id,
        double availableLabor, ImmutableArray<GoodsQuantity> basic, ImmutableArray<FacilityRequest> facilities,
        Func<ImmutableArray<long>, ImmutableArray<GoodsQuantity>> improvement)
    {
        var settlement = world.Settlements.Single(s => s.Id == id);
        var inventory = world.Inventories.Single(i => i.Id == settlement.Inventory);
        var quantities = inventory.Goods.ToDictionary(g => g.Good, g => g.Quantity);
        var node = world.Nodes.Single(n => n.Id == settlement.Node);
        var resources = node.Resources.ToDictionary(r => r.Good);
        var resourcesChanged = false;
        var energyRemaining = settlement.Plots.Where(p => p.Building is not null)
            .Sum(p => BuildingCapabilities.Read(world, content, p.Building!.Value).Power) * SimulationTime.StepSeconds;
        var fuelRemaining = settlement.FuelEnergyRemaining;
        var fuels = content.Goods.Where(g => g.Good is GoodId.Voxel && g.FuelEnergyPerUnit is not null
                && !WorldGoods.DefinitionMissing(world, content, g.Good))
            .OrderBy(g => ((GoodId.Voxel)g.Good).Definition.Value, StringComparer.Ordinal).ToArray();
        var batches = ImmutableArray.CreateBuilder<FacilityKey>();
        var work = ImmutableArray.CreateBuilder<WorkAllocation>();
        var basicGranted = Consume(basic);

        foreach (var request in facilities.OrderBy(r => r.Priority).ThenBy(r => r.Facility.Building.Value)
            .ThenBy(r => r.Facility.Capability))
        {
            switch (request)
            {
                case FacilityRequest.Batch batch:
                    if (!batch.Inputs.All(g => Available(g.Good) >= g.Quantity)) break;
                    if (batch.Extracts is { } extracts)
                    {
                        if (WorldGoods.DefinitionMissing(world, content, extracts.Good)
                            || !resources.TryGetValue(extracts.Good, out var resource)
                            || resource.Quantity < extracts.Quantity) break;
                        var remaining = resource.Quantity - extracts.Quantity;
                        if (remaining == 0 && resource.Renewal is ResourceRenewal.NonRenewable) resources.Remove(resource.Good);
                        else resources[resource.Good] = resource with { Quantity = remaining };
                        resourcesChanged = true;
                    }
                    foreach (var input in batch.Inputs) quantities[input.Good] -= input.Quantity;
                    batches.Add(batch.Facility);
                    break;
                case FacilityRequest.Work demand:
                    var rate = demand.MaximumWorkPerSecond;
                    var powerRate = double.PositiveInfinity;
                    var laborRate = double.PositiveInfinity;
                    if (demand.PowerPerWork > 0)
                    {
                        var fuelEnergy = fuelRemaining + fuels.Sum(f => Available(f.Good) * f.FuelEnergyPerUnit!.Value);
                        powerRate = Math.Min(energyRemaining, fuelEnergy) / SimulationTime.StepSeconds / demand.PowerPerWork;
                        rate = Math.Min(rate, powerRate);
                    }
                    if (demand.LaborPerWork > 0)
                    {
                        laborRate = availableLabor / demand.LaborPerWork;
                        rate = Math.Min(rate, laborRate);
                    }
                    var limits = ImmutableArray.CreateBuilder<WorkLimit>();
                    if (powerRate <= rate) limits.Add(WorkLimit.Power);
                    if (laborRate <= rate) limits.Add(WorkLimit.Labor);
                    var energy = rate * demand.PowerPerWork * SimulationTime.StepSeconds;
                    foreach (var fuel in fuels)
                    {
                        if (fuelRemaining >= energy) break;
                        var needed = Math.Ceiling((energy - fuelRemaining) / fuel.FuelEnergyPerUnit!.Value);
                        var available = Available(fuel.Good);
                        var units = needed < available ? checked((long)needed) : available;
                        quantities[fuel.Good] = available - units;
                        fuelRemaining += units * fuel.FuelEnergyPerUnit.Value;
                    }
                    fuelRemaining = Math.Max(0, fuelRemaining - energy);
                    energyRemaining = Math.Max(0, energyRemaining - energy);
                    availableLabor = Math.Max(0, availableLabor - rate * demand.LaborPerWork);
                    work.Add(new(demand.Facility, rate, limits.ToImmutable()));
                    break;
            }
        }

        var improvementGranted = Consume(improvement(basicGranted));
        var resolved = world with
        {
            Nodes = !resourcesChanged ? world.Nodes : world.Nodes.Select(n => n.Id == node.Id
                ? n with { Resources = resources.Values.OrderBy(r => WorldReferences.GoodKey(r.Good), StringComparer.Ordinal).ToImmutableArray() }
                : n).ToImmutableArray(),
            Settlements = world.Settlements.Select(s => s.Id == id ? s with { FuelEnergyRemaining = fuelRemaining } : s).ToImmutableArray(),
            Inventories = world.Inventories.Select(i => i.Id == inventory.Id ? i with
            {
                Goods = quantities.Where(g => g.Value > 0).OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
                    .Select(g => new GoodsQuantity(g.Key, g.Value)).ToImmutableArray()
            } : i).ToImmutableArray()
        };
        return new(resolved, basicGranted, batches.ToImmutable(), work.ToImmutable(), improvementGranted);

        long Available(GoodId good) => WorldGoods.DefinitionMissing(world, content, good)
            ? 0 : quantities.GetValueOrDefault(good);

        ImmutableArray<long> Consume(ImmutableArray<GoodsQuantity> requests)
        {
            var granted = ImmutableArray.CreateBuilder<long>();
            foreach (var request in requests)
            {
                var units = Math.Min(request.Quantity, Available(request.Good));
                if (units > 0) quantities[request.Good] -= units;
                granted.Add(units);
            }
            return granted.ToImmutable();
        }
    }
}
