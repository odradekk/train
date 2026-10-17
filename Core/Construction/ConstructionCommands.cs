using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Construction;

internal static class ConstructionCommands
{
    internal static void Validate(JobRequest request, WorldState world, CoreContentProjection content)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (facility, sources, _) = request switch
        {
            JobRequest.BuildVehicle build => (build.Facility, build.Sources, build.Priority),
            JobRequest.Repair { Instance: InstanceId.Vehicle vehicle } repair when vehicle.Id.Value != 0
                => (repair.Facility, repair.Sources, repair.Priority),
            JobRequest.Repair { Instance: InstanceId.Vehicle } => throw new ArgumentException("车辆编号必须非零。", nameof(request)),
            _ => throw new NotImplementedException($"{request.GetType().Name} 尚未实现。")
        };
        ArgumentNullException.ThrowIfNull(facility);
        if (facility.Building.Value == 0 || facility.Capability != CapabilityKind.Construction || sources.IsDefault
            || sources.Any(s => s.Value == 0))
            throw new ArgumentException("施工请求须提供施工设施和已初始化的来源数组。", nameof(request));
        if (request is not JobRequest.BuildVehicle building) return;
        var input = WorldInputs.NormalizeDesignInput(building.Design);
        if (input.InputIdentity != content.Identity) return;
        var current = content.Designs.SingleOrDefault(d => d.Design == input.Design.Design)
            ?? world.Designs.SingleOrDefault(d => d.Design == input.Design.Design);
        if (current is not null && !WorldJson.SameValue(WorldInputs.NormalizeDesign(input.Design), WorldInputs.NormalizeDesign(current)))
            throw new ArgumentException("同一设计身份的业务投影相互矛盾。", nameof(request));
        foreach (var good in input.AssemblyGoods)
        {
            var existing = content.DesignGoods.SingleOrDefault(g => g.Good == good.Good)
                ?? world.DesignGoods.SingleOrDefault(g => g.Good == good.Good);
            if (existing is not null && !WorldJson.SameValue(
                    existing with { MassTons = WorldInputs.Zero(existing.MassTons) },
                    good with { MassTons = WorldInputs.Zero(good.MassTons) }))
                throw new ArgumentException("同一组装体货物身份的投影相互矛盾。", nameof(request));
        }
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, JobRequest request)
    {
        var (facility, sources, priority) = request switch
        {
            JobRequest.BuildVehicle build => (build.Facility, build.Sources, build.Priority),
            JobRequest.Repair repair => (repair.Facility, repair.Sources, repair.Priority),
            _ => throw new InvalidOperationException("施工请求无效。")
        };
        var settlement = world.Settlements.SingleOrDefault(s => s.Plots.Any(p => p.Building == facility.Building));
        if (settlement is null) return Reject("object_missing", "Facility");
        DesignProjection design;
        VehicleId? vehicle = null;
        var prepared = world;
        if (request is JobRequest.BuildVehicle buildVehicle)
        {
            var input = WorldInputs.NormalizeDesignInput(buildVehicle.Design);
            if (input.InputIdentity != content.Identity) return Reject("content_changed", "Design.InputIdentity");
            design = WorldInputs.NormalizeDesign(input.Design);
            if (design.Kind is not DesignKind.Vehicle || !design.CanConstruct)
                return Reject("invalid_design", "Design");
            prepared = world with
            {
                Designs = world.Designs.Where(d => d.Design != design.Design).Append(design)
                    .OrderBy(d => d.Design.Value, StringComparer.Ordinal).ToImmutableArray(),
                DesignGoods = world.DesignGoods.Concat(input.AssemblyGoods).DistinctBy(g => g.Good)
                    .OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray()
            };
            if (!design.MissingReferences.IsEmpty || design.Parts.Any(p => InstanceCapabilities.DefinitionMissing(prepared, content, design.Design, p))
                || design.ReturnUnits.Any(u => !u.MissingReferences.IsEmpty)
                || WorldReferences.DesignGoods(design).Any(g => WorldGoods.DefinitionMissing(prepared, content, g.Good)))
                return Reject("content_missing", "Design");
        }
        else
        {
            vehicle = ((InstanceId.Vehicle)((JobRequest.Repair)request).Instance).Id;
            var instance = world.Instances.SingleOrDefault(i => i.Id == new InstanceId.Vehicle(vehicle.Value));
            if (instance is null) return Reject("object_missing", "Instance");
            if (instance.Ownership != SettlementRelation.PlayerOwned) return Reject("not_owned", "Instance");
            if (ConstructionJobs.InstanceLocked(world, instance.Id)) return Reject("instance_locked", "Instance");
            if (instance.Details is InstanceDetails.Vehicle { Train: { } trainId })
            {
                var train = world.Trains.Single(t => t.Id == trainId);
                if (train.Position is not TrainPosition.Node node || node.Id != settlement.Node || train.Activity != TrainActivity.Stopped)
                    return Reject("train_not_stationary_at_node", "Instance.Location");
            }
            else if (!settlement.ParkedVehicles.Contains(vehicle.Value)) return Reject("vehicles_not_colocated", "Instance.Location");
            design = world.Designs.Single(d => d.Design == instance.Design);
            if (Math.Min(instance.StructureDurability, design.MaxStructureDurability) <= 0)
                return Reject("instance_destroyed", "Instance");
        }
        var size = ((DesignKind.Vehicle)design.Kind).Size;
        if (ConstructionJobs.Qualification(world, content, settlement.Id, facility, size) is { } qualification)
            return new CommandEffect.Rejected([qualification]);
        if (ConstructionJobs.WorkRate(world, content, facility, size) <= 0) return Reject("capability_zero", "Facility");
        if (world.ConstructionJobs.Any(j => j.Facility == facility)) return Reject("busy", "Facility");
        var ruleId = world.Source.Configuration.Rules.Construction;
        var rule = content.ConstructionRules.SingleOrDefault(r => r.Id == ruleId);
        if (rule is null || content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(ruleId)))
            return Reject("content_missing", "ConstructionRule");
        var needed = new Dictionary<GoodId, long>();
        long work;
        if (vehicle is null)
        {
            foreach (var good in design.BillOfMaterials) Add(good.Good, good.Quantity);
            work = checked(design.BillOfMaterialsItemCount * rule.ConstructionWorkPerItem);
        }
        else
        {
            var instance = world.Instances.Single(i => i.Id == new InstanceId.Vehicle(vehicle.Value));
            long destroyed = 0, damaged = 0, structure = 0;
            foreach (var part in design.Parts)
            {
                if (InstanceCapabilities.DefinitionMissing(world, content, design.Design, part)) continue;
                var durability = Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability);
                if (durability == 0 && part.MaxDurability > 0)
                {
                    if (WorldGoods.DefinitionMissing(world, content, part.RepairGood))
                        return Reject("content_missing", $"Instance.Parts[{part.Id.Value}].RepairGood");
                    Add(part.RepairGood, 1);
                    destroyed++;
                }
                else if (durability < part.MaxDurability) damaged++;
            }
            var maximum = design.MaxStructureDurability;
            var loss = maximum - Math.Min(instance.StructureDurability, maximum);
            foreach (var good in design.StructuralGoods)
            {
                if (WorldGoods.DefinitionMissing(world, content, good.Good)) continue;
                var quantity = maximum == 0 ? 0 : checked((long)(((Int128)good.Quantity * loss + maximum - 1) / maximum));
                if (quantity > 0) Add(good.Good, quantity);
                structure = checked(structure + quantity);
            }
            work = checked(checked(destroyed + structure) * rule.ConstructionWorkPerItem + damaged * rule.RepairWorkPerDamagedPart);
        }
        if (work == 0) return Reject("invalid_state", "Instance.Durability");
        var inventories = new HashSet<InventoryId>();
        var consumption = ImmutableArray.CreateBuilder<JobMaterialSourceView>();
        for (var index = 0; index < sources.Length; index++)
        {
            var field = $"Sources[{index}]";
            if (!inventories.Add(sources[index])) return Reject("duplicate_source", field);
            var inventory = world.Inventories.SingleOrDefault(i => i.Id == sources[index]);
            if (inventory is null) return Reject("object_missing", field);
            if (inventory.Owner != new InventoryOwner.Settlement(settlement.Id))
            {
                if (inventory.Owner is not InventoryOwner.Train owner) return Reject("access_denied", field);
                var train = world.Trains.Single(t => t.Id == owner.Id);
                var access = InventoryCommands.TrainAccess(world, settlement, train) ?? InventoryCommands.PlatformAccess(world, train);
                if (access is not null) return new CommandEffect.Rejected([access with { Field = field }]);
            }
            var taken = ImmutableArray.CreateBuilder<GoodsQuantity>();
            foreach (var good in needed.Keys.OrderBy(WorldReferences.GoodKey, StringComparer.Ordinal).ToArray())
            {
                var quantity = Math.Min(needed[good], inventory.Goods.SingleOrDefault(g => g.Good == good)?.Quantity ?? 0);
                if (quantity == 0) continue;
                taken.Add(new(good, quantity));
                needed[good] -= quantity;
            }
            if (taken.Count > 0) consumption.Add(new(inventory.Id, taken.ToImmutable()));
        }
        if (needed.Any(g => g.Value > 0)) return Reject("insufficient_goods", "Sources");
        if (world.Next.Job == ulong.MaxValue) return Reject("identity_exhausted", "World.Next.Job");
        var invested = consumption.SelectMany(s => s.Goods).GroupBy(g => g.Good)
            .OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
            .Select(g => new GoodsQuantity(g.Key, g.Sum(x => x.Quantity))).ToImmutableArray();
        var job = new ConstructionJobState(new(world.Next.Job), settlement.Id, facility, design.Design, vehicle, priority,
            rule.Id, rule.Revision, work, rule.PowerPerWork, rule.LaborPerWork, invested, consumption.ToImmutable(),
            0, ConstructionState.Working, 0, [], []);
        prepared = prepared with
        {
            ConstructionJobs = prepared.ConstructionJobs.Add(job), Next = prepared.Next with { Job = checked(world.Next.Job + 1) },
            Inventories = prepared.Inventories.Select(i => consumption.SingleOrDefault(s => s.Inventory == i.Id) is not { } source ? i : i with
            {
                Goods = i.Goods.Select(g => g with { Quantity = g.Quantity - (source.Goods.SingleOrDefault(t => t.Good == g.Good)?.Quantity ?? 0) })
                    .Where(g => g.Quantity > 0).ToImmutableArray()
            }).ToImmutableArray()
        };
        prepared = prepared with { DefinitionDependencies = WorldReferences.Dependencies(prepared, content) };
        return new CommandEffect.Accepted(content, [new CoreEvent.ConstructionStarted(job.Read(), JobFunding.OwnInventory, new(0), new(0))],
            prepared, [new WorldObjectId.Job(job.Id)]);

        void Add(GoodId good, long quantity)
        {
            needed[good] = checked(needed.GetValueOrDefault(good) + quantity);
        }
        static CommandEffect.Rejected Reject(string code, string field) => new([new(code, field, null, [])]);
    }
}
