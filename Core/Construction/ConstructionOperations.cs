using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Railway;
using Train.Core.Settlements;
using Train.Core.World;

namespace Train.Core.Construction;

internal static class ConstructionOperations
{
    internal static ImmutableArray<FacilityRequest> Requests(WorldState world, CoreContentProjection content, SettlementId settlement)
        => world.ConstructionJobs.Where(j => j.Settlement == settlement).GroupBy(j => j.Facility)
            .Select(g => g.MinBy(j => j.Id.Value)!)
            .Where(j => j.State != ConstructionState.Paused && Failure(world, content, j) is null
                && !MissingTarget(world, content, j))
            .Select(j => new FacilityRequest.Work(j.Facility, j.Priority,
                Math.Min(ConstructionJobs.WorkRate(world, content, j.Facility, TargetKind(world, j)),
                    (j.FrozenTotalWork - j.ProgressWork) / SimulationTime.StepSeconds),
                j.FrozenPowerPerWork, j.FrozenLaborPerWork))
            .Where(r => r.MaximumWorkPerSecond > 0).Cast<FacilityRequest>().ToImmutableArray();

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Complete(WorldState world,
        CoreContentProjection content, SettlementId settlement, ImmutableArray<FacilityRequest> requests,
        SettlementResolution resolution, SimulationPosition boundary)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        var heads = world.ConstructionJobs.Where(j => j.Settlement == settlement).GroupBy(j => j.Facility)
            .ToDictionary(g => g.Key, g => g.MinBy(j => j.Id.Value)!.Id);
        double demand = 0, supply = 0, labor = 0;
        foreach (var original in world.ConstructionJobs.Where(j => j.Settlement == settlement).ToArray())
        {
            if (Failure(world, content, original) is { } failure)
            {
                events.Add(new CoreEvent.ConstructionStateChanged(original.Id, original.State, ConstructionState.Failed, [failure]));
                Remove(original);
                continue;
            }
            if (heads[original.Facility] != original.Id) continue;
            if (original.State == ConstructionState.Paused) continue;
            var request = requests.OfType<FacilityRequest.Work>().SingleOrDefault(r => r.Facility == original.Facility);
            var allocation = resolution.Work.SingleOrDefault(a => a.Facility == original.Facility);
            var rate = allocation?.WorkPerSecond ?? 0;
            var capability = ConstructionJobs.WorkRate(world, content, original.Facility, TargetKind(world, original));
            var limits = allocation?.Limits ?? [];
            if (rate == capability)
                limits = limits.Insert(0, WorkLimit.Capability);
            var missing = MissingTarget(world, content, original);
            var blockers = missing
                ? ImmutableArray.Create(new CoreProblem("content_missing", "TargetDesign", new WorldObjectId.Job(original.Id), []))
                : rate == 0 ? limits.Select(l => new CoreProblem(l switch
                    { WorkLimit.Capability => "capability_zero", WorkLimit.Power => "power_unavailable", _ => "labor_unavailable" },
                    "Job", new WorldObjectId.Job(original.Id), [])).ToImmutableArray() : [];
            var progress = Math.Min(original.FrozenTotalWork, original.ProgressWork + rate * SimulationTime.StepSeconds);
            var job = original with
            {
                ProgressWork = progress, State = rate > 0 ? ConstructionState.Working : ConstructionState.ResourceBlocked,
                LastActualWorkPerSecond = rate, LastLimits = limits, Blockers = blockers
            };
            demand += (request?.MaximumWorkPerSecond ?? 0) * original.FrozenPowerPerWork;
            supply += rate * original.FrozenPowerPerWork;
            labor += rate * original.FrozenLaborPerWork;
            if (progress < job.FrozenTotalWork || missing || capability == 0)
            {
                world = world with { ConstructionJobs = world.ConstructionJobs.Select(j => j.Id == job.Id ? job : j).ToImmutableArray() };
                if (original.State != job.State)
                    events.Add(new CoreEvent.ConstructionStateChanged(job.Id, original.State, job.State, job.Blockers));
                continue;
            }
            var design = world.Designs.Single(d => d.Design == job.TargetDesign);
            var returned = job.ReturnInventory is null ? ImmutableArray<GoodsQuantity>.Empty
                : ConstructionReturns.Calculate(world, content, world.Instances.Single(i => i.Id == job.Instance),
                    job.Kind == ConstructionKind.Deconstruction ? ConstructionReturns.AllItems(design) : job.RemovedItems).Goods;
            if (job.ReturnInventory is { } returnInventory)
            {
                var expected = world.Inventories.Single(i => i.Id == returnInventory).Reservations.Single(r => r.Job == job.Id)
                    .ExpectedReturns.ToDictionary(g => g.Good, g => g.Quantity);
                returned = returned.Where(g => expected.ContainsKey(g.Good))
                    .Select(g => g with { Quantity = Math.Min(g.Quantity, expected[g.Good]) }).ToImmutableArray();
            }
            InstanceId? created = null;
            ImmutableArray<CoreEvent> adapted = [];
            if (job.Kind == ConstructionKind.Refit)
            {
                var refit = ApplyRefit(world, content, job, design, boundary);
                world = refit.World;
                adapted = refit.Events;
                events.Add(new CoreEvent.InstanceDesignChanged(job.Instance!, refit.Before, design.Design));
            }
            else if (job.Kind == ConstructionKind.Deconstruction)
            {
                var removed = RemoveTarget(world, content, job, design, boundary);
                world = removed.World;
                adapted = removed.Events;
            }
            else if (job.Instance is not null)
            {
                world = world with { Instances = world.Instances.Select(i => i.Id != job.Instance ? i : i with
                {
                    StructureDurability = design.MaxStructureDurability,
                    Parts = i.Parts.Select(p => InstanceCapabilities.DefinitionMissing(world, content, design.Design,
                        design.Parts.Single(d => d.Id == p.Id)) ? p : p with
                        { Current = design.Parts.Single(d => d.Id == p.Id).MaxDurability }).ToImmutableArray()
                }).ToImmutableArray() };
                if (job.Instance is InstanceId.Building && design.Kind is DesignKind.Building { Type: BuildingKind.Station })
                {
                    var adaptedNodes = AdaptPlatforms(world, content, job.Settlement, boundary);
                    world = adaptedNodes.World;
                    adapted = adaptedNodes.Events;
                }
            }
            else if (job.Plot is not null)
            {
                var placed = PlaceBuilding(world, content, job, design, boundary);
                world = placed.World;
                created = placed.Created;
                adapted = placed.Events;
            }
            else
            {
                if (world.Next.Vehicle == ulong.MaxValue) throw new InvalidOperationException("车辆身份已耗尽。");
                var vehicle = new VehicleId(world.Next.Vehicle);
                created = new InstanceId.Vehicle(vehicle);
                world = world with
                {
                    Next = world.Next with { Vehicle = checked(world.Next.Vehicle + 1) },
                    Instances = world.Instances.Add(new(created, design.Design, SettlementRelation.PlayerOwned,
                        new InstanceDetails.Vehicle(((DesignKind.Vehicle)design.Kind).Size, null), design.MaxStructureDurability,
                        design.Parts.Select(p => new PartDurability(p.Id, p.MaxDurability)).ToImmutableArray())),
                    Settlements = world.Settlements.Select(s => s.Id != settlement ? s : s with
                        { ParkedVehicles = s.ParkedVehicles.Add(vehicle).OrderBy(v => v.Value).ToImmutableArray() }).ToImmutableArray()
                };
            }
            if (job.ReturnInventory is { } destination)
            {
                world = world with { Inventories = world.Inventories.Select(i => i.Id != destination ? i : i with
                {
                    Goods = i.Goods.Concat(returned).GroupBy(g => g.Good)
                        .OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
                        .Select(g => new GoodsQuantity(g.Key, checked(g.Sum(x => x.Quantity)))).ToImmutableArray()
                }).ToImmutableArray() };
            }
            events.Add(new CoreEvent.ConstructionStateChanged(job.Id, original.State, ConstructionState.Completed, []));
            events.Add(new CoreEvent.ConstructionCompleted(job.Id, job.Kind, job.Target(world), created, returned));
            events.AddRange(adapted);
            Remove(job);
        }
        world = world with { Settlements = world.Settlements.Select(s => s.Id != settlement ? s : s with
        { LastConstructionPowerDemand = demand, LastConstructionActualSupply = supply, LastConstructionAllocatedLabor = labor }).ToImmutableArray() };
        return (world, events.ToImmutable());

        void Remove(ConstructionJobState job)
            => world = world with
            {
                ConstructionJobs = world.ConstructionJobs.Where(j => j.Id != job.Id).ToImmutableArray(),
                Inventories = job.ReturnInventory is null ? world.Inventories : world.Inventories.Select(i => i.Id != job.ReturnInventory ? i : i with
                    { Reservations = i.Reservations.Where(r => r.Job != job.Id).ToImmutableArray() }).ToImmutableArray()
            };
    }

    private static (WorldState World, ImmutableArray<CoreEvent> Events) RemoveTarget(WorldState world,
        CoreContentProjection content, ConstructionJobState job, DesignProjection design, SimulationPosition boundary)
    {
        var id = job.Instance!;
        if (id is InstanceId.Vehicle vehicle)
        {
            world = world with
            {
                Instances = world.Instances.Where(i => i.Id != id).ToImmutableArray(),
                Settlements = world.Settlements.Select(s => s.Id != job.Settlement ? s : s with
                    { ParkedVehicles = s.ParkedVehicles.Where(v => v != vehicle.Id).ToImmutableArray() }).ToImmutableArray()
            };
            return (world, []);
        }
        var building = ((InstanceId.Building)id).Id;
        world = world with
        {
            Instances = world.Instances.Where(i => i.Id != id).ToImmutableArray(),
            Settlements = world.Settlements.Select(s => s.Id != job.Settlement ? s : s with
            {
                Plots = s.Plots.Select(p => p.Building != building ? p : p with { Building = null }).ToImmutableArray(),
                ProductionFacilities = s.ProductionFacilities.Where(f => f.Facility.Building != building).ToImmutableArray()
            }).ToImmutableArray()
        };
        if (design.Kind is not DesignKind.Building { Type: BuildingKind.Station }) return (world, []);
        return AdaptPlatforms(world, content, job.Settlement, boundary);
    }

    private static (WorldState World, DesignHash Before, ImmutableArray<CoreEvent> Events) ApplyRefit(
        WorldState world, CoreContentProjection content, ConstructionJobState job, DesignProjection design, SimulationPosition boundary)
    {
        var id = job.Instance!;
        var instance = world.Instances.Single(i => i.Id == id);
        var before = world.Designs.Single(d => d.Design == instance.Design);
        var retained = job.RetainedParts.ToDictionary(p => p.After, p => p.Before);
        var structure = checked((long)(((Int128)design.MaxStructureDurability
            * Math.Min(instance.StructureDurability, before.MaxStructureDurability) + before.MaxStructureDurability - 1)
            / before.MaxStructureDurability));
        var details = instance.Details is InstanceDetails.Vehicle vehicle
            ? vehicle with { Size = ((DesignKind.Vehicle)design.Kind).Size }
            : instance.Details;
        world = world with { Instances = world.Instances.Select(i => i.Id != id ? i : i with
        {
            Design = design.Design, StructureDurability = structure, Details = details,
            Parts = design.Parts.Select(p => new PartDurability(p.Id, retained.TryGetValue(p.Id, out var old)
                ? instance.Parts.Single(d => d.Id == old).Current : p.MaxDurability)).ToImmutableArray()
        }).ToImmutableArray() };
        if (id is not InstanceId.Building) return (world, before.Design, []);
        if (before.Kind is not DesignKind.Building { Type: BuildingKind.Station }
            && design.Kind is not DesignKind.Building { Type: BuildingKind.Station })
            return (world, before.Design, []);
        var adapted = AdaptPlatforms(world, content, job.Settlement, boundary);
        return (adapted.World, before.Design, adapted.Events);
    }

    private static DesignKind? TargetKind(WorldState world, ConstructionJobState job)
        => job.Kind == ConstructionKind.Deconstruction ? null : world.Designs.Single(d => d.Design == job.TargetDesign).Kind;

    private static (WorldState World, InstanceId Created, ImmutableArray<CoreEvent> Events) PlaceBuilding(
        WorldState world, CoreContentProjection content, ConstructionJobState job, DesignProjection design, SimulationPosition boundary)
    {
        if (world.Next.Building == ulong.MaxValue) throw new InvalidOperationException("建筑身份已耗尽。");
        var building = new BuildingId(world.Next.Building);
        var created = new InstanceId.Building(building);
        var settlement = world.Settlements.Single(s => s.Id == job.Settlement);
        var plot = settlement.Plots.Single(p => p.Id == job.Plot);
        var instance = WorldState.CreateInstance(created, design, SettlementRelation.PlayerOwned,
            new InstanceDetails.Building(plot.Id, plot.Size));
        world = world with
        {
            Next = world.Next with { Building = checked(world.Next.Building + 1) },
            Instances = world.Instances.Add(instance).OrderBy(i => i.Id is InstanceId.Building ? 0 : 1)
                .ThenBy(i => i.Id is InstanceId.Building existing ? existing.Id.Value : ((InstanceId.Vehicle)i.Id).Id.Value).ToImmutableArray(),
            Settlements = world.Settlements.Select(s => s.Id != job.Settlement ? s : Occupy(s, plot.Id, building)).ToImmutableArray()
        };
        if (design.Kind is not DesignKind.Building { Type: BuildingKind.Station }) return (world, created, []);
        var adapted = AdaptPlatforms(world, content, job.Settlement, boundary);
        return (adapted.World, created, adapted.Events);
    }

    private static (WorldState World, ImmutableArray<CoreEvent> Events) AdaptPlatforms(WorldState world,
        CoreContentProjection content, SettlementId settlementId, SimulationPosition boundary)
    {
        var settlement = world.Settlements.Single(s => s.Id == settlementId);
        var platforms = WorldState.CountPlatforms(settlement, world.Instances.ToDictionary(i => i.Id), world.Designs.ToDictionary(d => d.Design));
        world = world with
        {
            Nodes = world.Nodes.Select(n => n.Id != settlement.Node ? n : n with { Platforms = platforms }).ToImmutableArray()
        };
        return TrainOperations.AdaptNodes(world, content, boundary);
    }

    private static SettlementState Occupy(SettlementState settlement, PlotId plot, BuildingId building)
    {
        var plots = settlement.Plots.Select(p => p.Id != plot ? p : p with { Building = building }).ToImmutableArray();
        var number = plots.Single(p => p.Id == plot).TemplateNumber;
        var added = settlement.Template.ProductionPlans
            .Where(p => p.PlotNumber == number && p.Capability is CapabilityKind.Production or CapabilityKind.Extraction)
            .Select(p => new ProductionFacilityState(new(building, p.Capability), null, [], 0, [], 0, 0, 0));
        return settlement with
        {
            Plots = plots,
            ProductionFacilities = settlement.ProductionFacilities.AddRange(added)
                .OrderBy(f => f.Facility.Building.Value).ThenBy(f => f.Facility.Capability).ToImmutableArray()
        };
    }

    private static CoreProblem? Failure(WorldState world, CoreContentProjection content, ConstructionJobState job)
    {
        if (job.Instance is { } instanceId)
        {
            var instance = world.Instances.SingleOrDefault(i => i.Id == instanceId);
            if (instance is null) return new("object_missing", "Instance", new WorldObjectId.Instance(instanceId), []);
            if (instance.StructureDurability <= 0)
                return new("instance_destroyed", "Instance", new WorldObjectId.Instance(instance.Id), []);
        }
        return ConstructionJobs.Qualification(world, content, job.Settlement, job.Facility, TargetKind(world, job), job.Funding);
    }

    private static bool MissingTarget(WorldState world, CoreContentProjection content, ConstructionJobState job)
    {
        if (job.Kind is ConstructionKind.Repair or ConstructionKind.Deconstruction) return false;
        var design = world.Designs.Single(d => d.Design == job.TargetDesign);
        return !design.MissingReferences.IsEmpty || design.Parts.Any(p => InstanceCapabilities.DefinitionMissing(world, content, design.Design, p))
            || design.ReturnUnits.Any(u => !u.MissingReferences.IsEmpty)
            || WorldReferences.DesignGoods(design).Any(g => WorldGoods.DefinitionMissing(world, content, g.Good));
    }
}
