using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Construction;

internal sealed record ConstructionJobState(JobId Id, SettlementId Settlement, FacilityKey Facility,
    DesignHash TargetDesign, InstanceId? Instance, PlotId? Plot, int Priority,
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
    internal ConstructionTarget Target(WorldState world)
    {
        if (Plot is { } plot) return new ConstructionTarget.NewBuilding(plot, TargetDesign);
        if (Instance is { } instance) return new ConstructionTarget.Existing(instance, TargetDesign);
        var design = world.Designs.Single(d => d.Design == TargetDesign);
        return design.Kind is DesignKind.LargeEquipment
            ? new ConstructionTarget.NewAssembly(TargetDesign, design.Composition, ReturnInventory!.Value)
            : new ConstructionTarget.NewVehicle(TargetDesign);
    }

    internal ConstructionJobView Read(WorldState world) => new(Id, Kind, Settlement, Facility, Target(world), State, Priority,
        FrozenTotalWork, ProgressWork, FrozenTotalWork == 0 ? 0 : ProgressWork / FrozenTotalWork, LastActualWorkPerSecond,
        LastLimits, InvestedGoods, Blockers);
}

internal static class ConstructionJobs
{
    internal static bool InstanceLocked(WorldState world, InstanceId instance)
        => Lock(world, instance) != InstanceLock.None;

    internal static InstanceLock Lock(WorldState world, InstanceId instance)
        => world.ConstructionJobs.SingleOrDefault(j => j.Instance == instance)?.Kind switch
        {
            ConstructionKind.Refit => InstanceLock.UnderRefit,
            ConstructionKind.Repair => InstanceLock.UnderRepair,
            ConstructionKind.Deconstruction => InstanceLock.UnderDeconstruction,
            _ => InstanceLock.None
        };

    internal static bool TrainLocked(WorldState world, TrainState train)
        => train.Vehicles.Any(v => InstanceLocked(world, new InstanceId.Vehicle(v)));

    internal static CoreProblem? Qualification(WorldState world, CoreContentProjection content,
        SettlementId settlementId, FacilityKey facility, DesignKind? target, JobFunding funding = JobFunding.OwnInventory)
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
        if (InstanceLocked(world, new InstanceId.Building(facility.Building)))
            return new("conditions_not_met", "Facility", subject, []);
        if (!design.Parts.Where(p => !InstanceCapabilities.DefinitionMissing(world, content, design.Design, p))
            .SelectMany(p => p.Capabilities).OfType<PartCapability.Construction>().Any(c => Allows(c, target)))
            return new("conditions_not_met", "Facility", subject, []);
        return null;
    }

    internal static double WorkRate(WorldState world, CoreContentProjection content, FacilityKey facility, DesignKind? target)
    {
        if (InstanceLocked(world, new InstanceId.Building(facility.Building))) return 0;
        var instance = world.Instances.Single(i => i.Id == new InstanceId.Building(facility.Building));
        var capabilities = InstanceCapabilities.EffectiveParts(world, content, instance)
            .SelectMany(p => p.Capabilities).OfType<PartCapability.Construction>().ToArray();
        return capabilities.Any(c => Allows(c, target)) ? capabilities.Sum(c => c.WorkPerSecond) : 0;
    }

    internal static PlotSize Archive(PlotSize size) => size switch
    {
        PlotSize.SmallStation => PlotSize.Large,
        PlotSize.LargeStation => PlotSize.Giant,
        _ => size
    };

    private static bool Allows(PartCapability.Construction capability, DesignKind? target) => target switch
    {
        null or DesignKind.LargeEquipment => true,
        DesignKind.Vehicle vehicle => capability.MaximumVehicleSize is { } maximum && maximum >= vehicle.Size,
        DesignKind.Building building => capability.MaximumBuildingPlotSize >= Archive(building.PlotSize),
        _ => false
    };
}
