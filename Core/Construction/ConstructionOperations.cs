using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Settlements;
using Train.Core.World;

namespace Train.Core.Construction;

internal static class ConstructionOperations
{
    internal static ImmutableArray<FacilityRequest> Requests(WorldState world, CoreContentProjection content, SettlementId settlement)
        => world.ConstructionJobs.Where(j => j.Settlement == settlement).GroupBy(j => j.Facility)
            .Select(g => g.MinBy(j => j.Id.Value)!)
            .Where(j => Failure(world, content, j) is null
                && !MissingTarget(world, content, j))
            .Select(j => new FacilityRequest.Work(j.Facility, j.Priority,
                Math.Min(ConstructionJobs.WorkRate(world, content, j.Facility, Size(world, j)),
                    (j.FrozenTotalWork - j.ProgressWork) / SimulationTime.StepSeconds),
                j.FrozenPowerPerWork, j.FrozenLaborPerWork))
            .Where(r => r.MaximumWorkPerSecond > 0).Cast<FacilityRequest>().ToImmutableArray();

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Complete(WorldState world,
        CoreContentProjection content, SettlementId settlement, ImmutableArray<FacilityRequest> requests, SettlementResolution resolution)
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
            var request = requests.OfType<FacilityRequest.Work>().SingleOrDefault(r => r.Facility == original.Facility);
            var allocation = resolution.Work.SingleOrDefault(a => a.Facility == original.Facility);
            var rate = allocation?.WorkPerSecond ?? 0;
            var capability = ConstructionJobs.WorkRate(world, content, original.Facility, Size(world, original));
            var limits = allocation?.Limits ?? [];
            if (rate == capability)
                limits = limits.Insert(0, WorkLimit.Capability);
            var blockers = MissingTarget(world, content, original)
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
            if (progress < job.FrozenTotalWork)
            {
                world = world with { ConstructionJobs = world.ConstructionJobs.Select(j => j.Id == job.Id ? job : j).ToImmutableArray() };
                if (original.State != job.State)
                    events.Add(new CoreEvent.ConstructionStateChanged(job.Id, original.State, job.State, job.Blockers));
                continue;
            }
            var design = world.Designs.Single(d => d.Design == job.TargetDesign);
            InstanceId? created = null;
            if (job.RepairVehicle is { } repair)
            {
                world = world with { Instances = world.Instances.Select(i => i.Id != new InstanceId.Vehicle(repair) ? i : i with
                {
                    StructureDurability = design.MaxStructureDurability,
                    Parts = i.Parts.Select(p => InstanceCapabilities.DefinitionMissing(world, content, design.Design,
                        design.Parts.Single(d => d.Id == p.Id)) ? p : p with
                        { Current = design.Parts.Single(d => d.Id == p.Id).MaxDurability }).ToImmutableArray()
                }).ToImmutableArray() };
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
            events.Add(new CoreEvent.ConstructionStateChanged(job.Id, original.State, ConstructionState.Completed, []));
            events.Add(new CoreEvent.ConstructionCompleted(job.Id, job.Kind, job.Target, created, []));
            Remove(job);
        }
        world = world with { Settlements = world.Settlements.Select(s => s.Id != settlement ? s : s with
        { LastConstructionPowerDemand = demand, LastConstructionActualSupply = supply, LastConstructionAllocatedLabor = labor }).ToImmutableArray() };
        return (world, events.ToImmutable());

        void Remove(ConstructionJobState job)
            => world = world with { ConstructionJobs = world.ConstructionJobs.Where(j => j.Id != job.Id).ToImmutableArray() };
    }

    private static SizeClass Size(WorldState world, ConstructionJobState job)
        => ((DesignKind.Vehicle)world.Designs.Single(d => d.Design == job.TargetDesign).Kind).Size;

    private static CoreProblem? Failure(WorldState world, CoreContentProjection content, ConstructionJobState job)
    {
        if (job.RepairVehicle is { } vehicle)
        {
            var instance = world.Instances.SingleOrDefault(i => i.Id == new InstanceId.Vehicle(vehicle));
            if (instance is null) return new("object_missing", "Instance", new WorldObjectId.Instance(new InstanceId.Vehicle(vehicle)), []);
            if (Math.Min(instance.StructureDurability, world.Designs.Single(d => d.Design == instance.Design).MaxStructureDurability) <= 0)
                return new("instance_destroyed", "Instance", new WorldObjectId.Instance(instance.Id), []);
        }
        return ConstructionJobs.Qualification(world, content, job.Settlement, job.Facility, Size(world, job), job.Funding);
    }

    private static bool MissingTarget(WorldState world, CoreContentProjection content, ConstructionJobState job)
    {
        if (job.RepairVehicle is not null) return false;
        var design = world.Designs.Single(d => d.Design == job.TargetDesign);
        return !design.MissingReferences.IsEmpty || design.Parts.Any(p => InstanceCapabilities.DefinitionMissing(world, content, design.Design, p))
            || design.ReturnUnits.Any(u => !u.MissingReferences.IsEmpty)
            || WorldReferences.DesignGoods(design).Any(g => WorldGoods.DefinitionMissing(world, content, g.Good));
    }
}
