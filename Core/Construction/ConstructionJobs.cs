using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Construction;

internal sealed record ConstructionJobState(JobId Id, SettlementId Settlement, FacilityKey Facility,
    DesignHash TargetDesign, VehicleId? Vehicle, int Priority,
    RuleDefinitionId Rule, SemanticRevision StartedFromRevision, long FrozenTotalWork,
    double FrozenPowerPerWork, double FrozenLaborPerWork, ImmutableArray<GoodsQuantity> InvestedGoods,
    ImmutableArray<JobMaterialSourceView> SourceConsumption, double ProgressWork, ConstructionState State,
    double LastActualWorkPerSecond, ImmutableArray<WorkLimit> LastLimits, ImmutableArray<CoreProblem> Blockers)
{
    [JsonRequired] public JobFunding Funding { get; init; }
    [JsonRequired] public ConstructionKind Kind { get; init; }
    [JsonRequired] public ImmutableArray<PartCorrespondence> RetainedParts { get; init; } = [];
    [JsonRequired] public ImmutableArray<DesignReturnUnit> RemovedItems { get; init; } = [];
    [JsonRequired] public InventoryId? ReturnInventory { get; init; }
    internal ConstructionTarget Target => Vehicle is { } vehicle
        ? new ConstructionTarget.Existing(new InstanceId.Vehicle(vehicle), TargetDesign)
        : new ConstructionTarget.NewVehicle(TargetDesign);
    internal ConstructionJobView Read() => new(Id, Kind, Settlement, Facility, Target, State, Priority,
        FrozenTotalWork, ProgressWork, FrozenTotalWork == 0 ? 0 : ProgressWork / FrozenTotalWork, LastActualWorkPerSecond,
        LastLimits, InvestedGoods, Blockers);
}

internal static class ConstructionJobs
{
    internal static bool InstanceLocked(WorldState world, InstanceId instance)
        => Lock(world, instance) != InstanceLock.None;

    internal static InstanceLock Lock(WorldState world, InstanceId instance)
        => instance is not InstanceId.Vehicle vehicle ? InstanceLock.None
            : world.ConstructionJobs.SingleOrDefault(j => j.Vehicle == vehicle.Id)?.Kind switch
            {
                ConstructionKind.Refit => InstanceLock.UnderRefit,
                ConstructionKind.Repair => InstanceLock.UnderRepair,
                ConstructionKind.Deconstruction => InstanceLock.UnderDeconstruction,
                _ => InstanceLock.None
            };

    internal static bool TrainLocked(WorldState world, TrainState train)
        => train.Vehicles.Any(v => InstanceLocked(world, new InstanceId.Vehicle(v)));

    internal static CoreProblem? Qualification(WorldState world, CoreContentProjection content,
        SettlementId settlementId, FacilityKey facility, SizeClass? size, JobFunding funding = JobFunding.OwnInventory)
    {
        var subject = new WorldObjectId.Instance(new InstanceId.Building(facility.Building));
        var settlement = world.Settlements.SingleOrDefault(s => s.Id == settlementId);
        var instance = world.Instances.SingleOrDefault(i => i.Id == new InstanceId.Building(facility.Building));
        if (settlement is null || instance is null || !settlement.Plots.Any(p => p.Building == facility.Building))
            return new("object_missing", "Facility", subject, []);
        var owner = funding == JobFunding.NpcCommission ? SettlementRelation.NonHostile : SettlementRelation.PlayerOwned;
        if (settlement.Relation != owner || instance.Ownership != owner)
            return new("not_owned", "Facility", subject, []);
        var design = world.Designs.Single(d => d.Design == instance.Design);
        if (Math.Min(instance.StructureDurability, design.MaxStructureDurability) <= 0)
            return new("instance_destroyed", "Facility", subject, []);
        if (!design.Parts.Where(p => !InstanceCapabilities.DefinitionMissing(world, content, design.Design, p))
            .SelectMany(p => p.Capabilities).OfType<PartCapability.Construction>()
            .Any(c => size is null || c.MaximumVehicleSize is { } maximum && maximum >= size))
            return new("conditions_not_met", "Facility", subject, []);
        return null;
    }

    internal static double WorkRate(WorldState world, CoreContentProjection content, FacilityKey facility, SizeClass? size)
    {
        var instance = world.Instances.Single(i => i.Id == new InstanceId.Building(facility.Building));
        var capabilities = InstanceCapabilities.EffectiveParts(world, content, instance)
            .SelectMany(p => p.Capabilities).OfType<PartCapability.Construction>().ToArray();
        return capabilities.Any(c => size is null || c.MaximumVehicleSize is { } maximum && maximum >= size)
            ? capabilities.Sum(c => c.WorkPerSecond) : 0;
    }
}
