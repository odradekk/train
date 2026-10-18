using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Sessions;
using Train.Core.World;
using Train.Core.Settlements;
using Train.Core.Trading;

namespace Train.Core.Construction;

internal static class ConstructionCommands
{
    internal static void Validate(JobRequest request, WorldState world, CoreContentProjection content,
        JobFunding funding = JobFunding.OwnInventory)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(funding)) throw new ArgumentException("施工资金来源无效。", nameof(funding));
        if (funding == JobFunding.NpcCommission && request is JobRequest.BuildBuilding or JobRequest.BuildEquipment
                or JobRequest.Dismantle or JobRequest.Repair { Instance: InstanceId.Building }
                or JobRequest.Refit { Instance: InstanceId.Building })
            throw new ArgumentException("委托只支持车辆制造、改装和修理。", nameof(request));
        var (facility, sources, _) = request switch
        {
            JobRequest.BuildVehicle build => (build.Facility, build.Sources, build.Priority),
            JobRequest.BuildBuilding build when build.Plot.Value != 0 => (build.Facility, build.Sources, build.Priority),
            JobRequest.BuildBuilding { Plot.Value: 0 } => throw new ArgumentException("地块编号必须非零。", nameof(request)),
            JobRequest.Refit { Instance: InstanceId.Vehicle vehicle } refit when vehicle.Id.Value != 0
                => (refit.Facility, refit.Sources, refit.Priority),
            JobRequest.Repair { Instance: InstanceId.Vehicle vehicle } repair when vehicle.Id.Value != 0
                => (repair.Facility, repair.Sources, repair.Priority),
            JobRequest.Repair { Instance: InstanceId.Building building } repair when building.Id.Value != 0
                => (repair.Facility, repair.Sources, repair.Priority),
            JobRequest.Dismantle { Instance: InstanceId.Vehicle vehicle } dismantle when vehicle.Id.Value != 0
                => (dismantle.Facility, ImmutableArray<InventoryId>.Empty, dismantle.Priority),
            JobRequest.Repair { Instance: InstanceId.Vehicle { Id.Value: 0 } }
                or JobRequest.Refit { Instance: InstanceId.Vehicle { Id.Value: 0 } }
                or JobRequest.Dismantle { Instance: InstanceId.Vehicle { Id.Value: 0 } }
                => throw new ArgumentException("车辆编号必须非零。", nameof(request)),
            JobRequest.Repair { Instance: InstanceId.Building { Id.Value: 0 } }
                => throw new ArgumentException("建筑编号必须非零。", nameof(request)),
            _ => throw new NotImplementedException($"{request.GetType().Name} 尚未实现。")
        };
        ArgumentNullException.ThrowIfNull(facility);
        if (facility.Building.Value == 0 || facility.Capability != CapabilityKind.Construction || sources.IsDefault
            || sources.Any(s => s.Value == 0))
            throw new ArgumentException("施工请求须提供施工设施和已初始化的来源数组。", nameof(request));
        if (request is JobRequest.BuildVehicle vehicleBuild) VehicleDesignChanges.ValidateInput(vehicleBuild.Design, world, content);
        if (request is JobRequest.BuildBuilding buildingBuild) VehicleDesignChanges.ValidateInput(buildingBuild.Design, world, content);
        if (request is JobRequest.Refit refitting)
            VehicleDesignChanges.Validate(refitting.Instance, refitting.Change, world, content);
    }

    internal static JobQuote Quote(WorldState world, CoreContentProjection content, ulong step, CoreRevision revision,
        JobRequest request, JobFunding funding)
        => Prepare(world, content, step, revision, request, funding, null).Quote;

    private static (WorldState World, JobQuote Quote, ImmutableArray<GoodsQuantity> Returns, InventoryId? ReturnInventory) Prepare(WorldState world, CoreContentProjection content,
        ulong step, CoreRevision revision, JobRequest request, JobFunding funding, Money? maximumPayment)
    {
        var (facility, sources, priority) = request switch
        {
            JobRequest.BuildVehicle build => (build.Facility, build.Sources, build.Priority),
            JobRequest.BuildBuilding build => (build.Facility, build.Sources, build.Priority),
            JobRequest.Refit refit => (refit.Facility, refit.Sources, refit.Priority),
            JobRequest.Repair repair => (repair.Facility, repair.Sources, repair.Priority),
            JobRequest.Dismantle dismantle => (dismantle.Facility, ImmutableArray<InventoryId>.Empty, dismantle.Priority),
            _ => throw new InvalidOperationException("施工请求无效。")
        };
        var prepared = world;
        long work = 0;
        ImmutableArray<GoodsQuantity> required = [];
        ImmutableArray<GoodsQuantity> returns = [];
        InventoryId? returnInventory = null;
        var consumption = ImmutableArray.CreateBuilder<JobMaterialSourceView>();
        var purchases = ImmutableArray.CreateBuilder<GoodsQuantity>();
        Money? materialPayment = funding == JobFunding.OwnInventory ? new(0) : null;
        Money? servicePayment = materialPayment;
        Money? totalPayment = null;
        var settlement = world.Settlements.SingleOrDefault(s => s.Plots.Any(p => p.Building == facility.Building));
        if (settlement is null) return Reject("object_missing", "Facility");
        if (funding == JobFunding.NpcCommission)
        {
            if (settlement.Relation == SettlementRelation.PlayerOwned) return Reject("invalid_state", "Facility");
            if (settlement.Relation == SettlementRelation.Hostile) return Reject("access_denied", "Facility");
        }
        DesignProjection design;
        VehicleId? vehicle = null;
        InstanceState? instance = null;
        if (request is JobRequest.BuildVehicle buildVehicle)
        {
            var input = WorldInputs.NormalizeDesignInput(buildVehicle.Design);
            if (input.InputIdentity != content.Identity) return Reject("content_changed", "Design.InputIdentity");
            design = WorldInputs.NormalizeDesign(input.Design);
            if (design.Kind is not DesignKind.Vehicle || !design.CanConstruct)
                return Reject("invalid_design", "Design");
            prepared = VehicleDesignChanges.AddTarget(world, input);
            if (MissingDesign(prepared, content, design)) return Reject("content_missing", "Design");
        }
        else if (request is JobRequest.BuildBuilding buildBuilding)
        {
            var site = settlement.Plots.SingleOrDefault(p => p.Id == buildBuilding.Plot);
            if (site is null) return Reject("object_missing", "Plot");
            if (site.Building is not null) return Reject("plot_occupied", "Plot");
            if (world.ConstructionJobs.Any(j => j.Plot == buildBuilding.Plot)) return Reject("busy", "Plot.ConstructionJob");
            var input = WorldInputs.NormalizeDesignInput(buildBuilding.Design);
            if (input.InputIdentity != content.Identity) return Reject("content_changed", "Design.InputIdentity");
            design = WorldInputs.NormalizeDesign(input.Design);
            if (design.Kind is not DesignKind.Building || !design.CanConstruct)
                return Reject("invalid_design", "Design");
            prepared = VehicleDesignChanges.AddTarget(world, input);
            if (MissingDesign(prepared, content, design)) return Reject("content_missing", "Design");
            if (!WorldInputs.FitsPlot(((DesignKind.Building)design.Kind).PlotSize, site.Size))
                return Reject("conditions_not_met", "Plot");
        }
        else
        {
            var target = request switch
            {
                JobRequest.Refit targetRefit => targetRefit.Instance,
                JobRequest.Repair repair => repair.Instance,
                JobRequest.Dismantle dismantle => dismantle.Instance,
                _ => throw new InvalidOperationException("施工目标无效。")
            };
            instance = world.Instances.SingleOrDefault(i => i.Id == target);
            if (instance is null) return Reject("object_missing", "Instance");
            if (instance.Ownership != SettlementRelation.PlayerOwned) return Reject("not_owned", "Instance");
            if (ConstructionJobs.InstanceLocked(world, instance.Id)) return Reject("instance_locked", "Instance");
            if (target is InstanceId.Building building)
            {
                if (!settlement.Plots.Any(p => p.Building == building.Id)) return Reject("conditions_not_met", "Instance");
                design = world.Designs.Single(d => d.Design == instance.Design);
                if (instance.StructureDurability <= 0 || design.MaxStructureDurability == 0 && request is not JobRequest.Dismantle)
                    return Reject("instance_destroyed", "Instance");
                if (facility.Building == building.Id) return Reject("invalid_state", "Instance");
                if (world.ConstructionJobs.Any(j => j.Facility.Building == building.Id))
                    return Reject("invalid_state", "Instance.ConstructionJob");
                foreach (var row in settlement.ProductionFacilities.Where(f => f.Facility.Building == building.Id))
                {
                    if (row.Batch is not null) return Reject("invalid_state", "Instance.Batch");
                    if (!row.OutputBuffer.IsEmpty) return Reject("invalid_state", "Instance.OutputBuffer");
                }
            }
            else
            {
                vehicle = ((InstanceId.Vehicle)target).Id;
                if (instance.Details is InstanceDetails.Vehicle { Train: { } trainId })
                {
                    if (request is JobRequest.Dismantle) return Reject("vehicle_in_train", "Instance.Details.Train");
                    var train = world.Trains.Single(t => t.Id == trainId);
                    if (train.Position is not TrainPosition.Node node || node.Id != settlement.Node || train.Activity != TrainActivity.Stopped)
                        return Reject("train_not_stationary_at_node", "Instance.Location");
                }
                else if (!settlement.ParkedVehicles.Contains(vehicle.Value)) return Reject("vehicles_not_colocated", "Instance.Location");
                design = world.Designs.Single(d => d.Design == instance.Design);
                if (instance.StructureDurability <= 0 || design.MaxStructureDurability == 0 && request is not JobRequest.Dismantle)
                    return Reject("instance_destroyed", "Instance");
                if (request is JobRequest.Refit refit)
                {
                    var change = WorldInputs.NormalizeDesignChange(refit.Change);
                    if (VehicleDesignChanges.Check(world, content, instance, change, false) is { } problem) return Result([problem]);
                    if (design.Composition == change.Target.Design.Composition) return Reject("invalid_design", "Change.Target");
                    prepared = VehicleDesignChanges.AddTarget(world, change.Target);
                    design = change.Target.Design;
                }
            }
        }
        DesignKind? tier = request is JobRequest.Dismantle ? null : design.Kind;
        if (ConstructionJobs.Qualification(world, content, settlement.Id, facility, tier, funding) is { } qualification)
            return Result([qualification]);
        if (ConstructionJobs.WorkRate(world, content, facility, tier) <= 0) return Reject("capability_zero", "Facility");
        if (funding == JobFunding.OwnInventory && world.ConstructionJobs.Any(j => j.Facility == facility))
            return Reject("busy", "Facility");
        var ruleId = world.Source.Configuration.Rules.Construction;
        var rule = content.ConstructionRules.SingleOrDefault(r => r.Id == ruleId);
        if (rule is null || content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(ruleId)))
            return Reject("content_missing", "ConstructionRule");
        var marketRule = MarketPrices.Rule(world, content);
        if (funding == JobFunding.NpcCommission && marketRule is null) return Reject("content_missing", "MarketRule");
        var needed = new Dictionary<GoodId, long>();
        if (request is JobRequest.BuildVehicle or JobRequest.BuildBuilding)
        {
            foreach (var good in BillOfMaterials([design])) Add(good.Good, good.Quantity);
            work = checked(design.BillOfMaterialsItemCount * rule.ConstructionWorkPerItem);
        }
        else if (request is JobRequest.Refit refit)
        {
            var change = WorldInputs.NormalizeDesignChange(refit.Change);
            foreach (var good in change.AddedGoods) Add(good.Good, good.Quantity);
            var removed = ConstructionReturns.Calculate(world, content, instance!, change.RemovedItems);
            returns = removed.Goods;
            work = checked(change.ChangedItemCount * rule.RefitWorkPerChangedItem + removed.DamagedParts * rule.RepairWorkPerDamagedPart);
        }
        else if (request is JobRequest.Dismantle)
        {
            var removed = ConstructionReturns.Calculate(world, content, instance!, ConstructionReturns.AllItems(design));
            returns = removed.Goods;
            work = checked(design.BillOfMaterialsItemCount * rule.DeconstructionWorkPerItem + removed.DamagedParts * rule.RepairWorkPerDamagedPart);
        }
        else
        {
            long destroyed = 0, damaged = 0, structure = 0;
            foreach (var part in design.Parts)
            {
                if (InstanceCapabilities.DefinitionMissing(world, content, design.Design, part)) continue;
                var durability = Math.Min(instance!.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability);
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
            var loss = maximum - Math.Min(instance!.StructureDurability, maximum);
            foreach (var good in design.StructuralGoods)
            {
                if (WorldGoods.DefinitionMissing(world, content, good.Good)) continue;
                var quantity = maximum == 0 ? 0 : checked((long)(((Int128)good.Quantity * loss + maximum - 1) / maximum));
                if (quantity > 0) Add(good.Good, quantity);
                structure = checked(structure + quantity);
            }
            work = checked(checked(destroyed + structure) * rule.ConstructionWorkPerItem + damaged * rule.RepairWorkPerDamagedPart);
        }
        if (work == 0 && request is JobRequest.Repair) return Reject("invalid_state", "Instance.Durability");
        required = needed.OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
            .Select(g => new GoodsQuantity(g.Key, g.Value)).ToImmutableArray();
        var inventories = new HashSet<InventoryId>();
        for (var index = 0; index < sources.Length; index++)
        {
            var field = $"Sources[{index}]";
            if (!inventories.Add(sources[index])) return Reject("duplicate_source", field);
            var inventory = world.Inventories.SingleOrDefault(i => i.Id == sources[index]);
            if (inventory is null) return Reject("object_missing", field);
            if (funding == JobFunding.NpcCommission || inventory.Owner != new InventoryOwner.Settlement(settlement.Id))
            {
                if (inventory.Owner is InventoryOwner.Train owner)
                {
                    var train = world.Trains.Single(t => t.Id == owner.Id);
                    var access = InventoryCommands.TrainAccess(world, settlement, train) ?? InventoryCommands.PlatformAccess(world, train);
                    if (access is not null) return Result([access with { Field = field }]);
                }
                else if (funding != JobFunding.NpcCommission || inventory.Owner is not InventoryOwner.Lease lease
                    || !world.Leases.Any(l => l.Id == lease.Id && l.Settlement == settlement.Id))
                    return Reject("access_denied", field);
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
        if (funding == JobFunding.OwnInventory)
        {
            var missing = needed.Where(g => g.Value > 0).OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
                .Select(g => WorldReferences.GoodKey(g.Key)).ToImmutableArray();
            if (!missing.IsEmpty) return Result([new("insufficient_goods", "Sources", null, missing)]);
        }
        if (funding == JobFunding.NpcCommission)
        {
            var pricingWorld = Consume(prepared, consumption);
            var unavailable = ImmutableArray.CreateBuilder<string>();
            long payment = 0;
            foreach (var good in needed.Where(g => g.Value > 0).OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal))
            {
                var inventory = pricingWorld.Inventories.Single(i => i.Id == settlement.Inventory);
                var stock = inventory.Goods.SingleOrDefault(g => g.Good == good.Key)?.Quantity ?? 0;
                var market = new MarketStockTargets(pricingWorld, content, settlement, step);
                if (market.Quantity(good.Key, stock, Math.Min(stock, good.Value), true) < good.Value)
                {
                    unavailable.Add(WorldReferences.GoodKey(good.Key));
                    continue;
                }
                foreach (var (price, quantity) in MarketPrices.Goods(pricingWorld, content, good.Key, true, market, stock, good.Value))
                    payment = checked(payment + price.Value * quantity);
                var purchase = new GoodsQuantity(good.Key, good.Value);
                purchases.Add(purchase);
                pricingWorld = Consume(pricingWorld, [new(settlement.Inventory, [purchase])]);
            }
            if (unavailable.Count > 0)
                return Result([new("insufficient_goods", "Sources", null, unavailable.ToImmutable())]);
            materialPayment = new(payment);
            servicePayment = new(checked(work * marketRule!.CommissionMoneyPerWork.Value));
        }
        if (!returns.IsEmpty)
        {
            var afterConsumption = Consume(prepared, consumption);
            var quantity = returns.Sum(g => g.Quantity);
            var destination = funding == JobFunding.OwnInventory
                ? InventoryStorage.Read(afterConsumption, content, step,
                    afterConsumption.Inventories.Single(i => i.Id == settlement.Inventory))
                : afterConsumption.Inventories.Where(i => i.Owner is InventoryOwner.Lease lease
                        && afterConsumption.Leases.Any(l => l.Id == lease.Id && l.Settlement == settlement.Id))
                    .OrderBy(i => i.Id.Value).Select(i => InventoryStorage.Read(afterConsumption, content, step, i))
                    .FirstOrDefault(i => i.Access == InventoryAccess.ReadWrite && i.Available >= quantity);
            if (destination is null || destination.Access != InventoryAccess.ReadWrite || destination.Available < quantity)
                return Reject("insufficient_capacity", "ReturnInventory.Capacity");
            returnInventory = destination.Id;
        }
        if (world.Next.Job == ulong.MaxValue) return Reject("identity_exhausted", "World.Next.Job");
        totalPayment = new(checked(materialPayment!.Value.Value + servicePayment!.Value.Value));
        if (maximumPayment is { } limit && totalPayment.Value.Value > limit.Value)
            return Reject("price_condition_failed", "MaximumPayment");
        if (totalPayment.Value.Value > world.Money.Value) return Reject("insufficient_money", "Wallet");
        return Result([]);

        bool MissingDesign(WorldState candidate, CoreContentProjection published, DesignProjection value) =>
            !value.MissingReferences.IsEmpty
            || value.Parts.Any(p => InstanceCapabilities.DefinitionMissing(candidate, published, value.Design, p))
            || value.ReturnUnits.Any(u => !u.MissingReferences.IsEmpty)
            || WorldReferences.DesignGoods(value).Any(g => WorldGoods.DefinitionMissing(candidate, published, g.Good));
        void Add(GoodId good, long quantity) => needed[good] = checked(needed.GetValueOrDefault(good) + quantity);
        (WorldState, JobQuote, ImmutableArray<GoodsQuantity>, InventoryId?) Result(ImmutableArray<CoreProblem> problems) => (prepared,
            new(request, funding, revision, work, required, consumption.ToImmutable(), purchases.ToImmutable(),
                materialPayment, servicePayment, totalPayment, problems), returns, returnInventory);
        (WorldState, JobQuote, ImmutableArray<GoodsQuantity>, InventoryId?) Reject(string code, string field) => Result([new(code, field, null, [])]);
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, JobRequest request,
        ulong step, JobFunding funding = JobFunding.OwnInventory, Money? maximumPayment = null)
    {
        var (prepared, quote, returns, returnInventory) = Prepare(world, content, step, new(0), request, funding, maximumPayment);
        if (!quote.Blockers.IsEmpty) return new CommandEffect.Rejected(quote.Blockers);
        var (facility, priority, instance, plot) = request switch
        {
            JobRequest.BuildVehicle build => (build.Facility, build.Priority, (InstanceId?)null, (PlotId?)null),
            JobRequest.BuildBuilding build => (build.Facility, build.Priority, (InstanceId?)null, (PlotId?)build.Plot),
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

    internal static ImmutableArray<GoodsQuantity> BillOfMaterials(IEnumerable<DesignProjection> designs)
        => designs.SelectMany(d => d.BillOfMaterials).GroupBy(g => g.Good)
            .OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
            .Select(g => new GoodsQuantity(g.Key, g.Sum(q => q.Quantity))).ToImmutableArray();

    internal static WorldState Consume(WorldState world, IEnumerable<JobMaterialSourceView> consumption)
        => world with { Inventories = world.Inventories.Select(i => consumption.SingleOrDefault(s => s.Inventory == i.Id) is not { } source ? i : i with
        {
            Goods = i.Goods.Select(g => g with { Quantity = g.Quantity - (source.Goods.SingleOrDefault(t => t.Good == g.Good)?.Quantity ?? 0) })
                .Where(g => g.Quantity > 0).ToImmutableArray()
        }).ToImmutableArray() };

    internal static CommandEffect Pause(WorldState world, CoreContentProjection content, CoreCommand.SetDeconstructionPaused command)
    {
        var job = world.ConstructionJobs.SingleOrDefault(j => j.Id == command.Job);
        if (job is null) return new CommandEffect.Rejected([new("object_missing", "Job", new WorldObjectId.Job(command.Job), [])]);
        if (job.Kind != ConstructionKind.Deconstruction)
            return new CommandEffect.Rejected([new("invalid_state", "Job.Kind", new WorldObjectId.Job(command.Job), [])]);
        if ((job.State == ConstructionState.Paused) == command.Paused) return new CommandEffect.Accepted(content, [], world);
        var state = command.Paused ? ConstructionState.Paused : ConstructionState.Working;
        var changed = job with { State = state, LastActualWorkPerSecond = 0, LastLimits = [], Blockers = [] };
        return new CommandEffect.Accepted(content, [new CoreEvent.ConstructionStateChanged(job.Id, job.State, state, [])],
            world with { ConstructionJobs = world.ConstructionJobs.Select(j => j.Id == job.Id ? changed : j).ToImmutableArray() });
    }
}
