using System.Collections.Immutable;
using Train.Core.Construction;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Railway;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Settlements;

internal static class SettlementFounding
{
    private sealed record Plan(SettlementTypeProjection? Type, SettlementTemplateProjection? Template,
        ConstructionRuleProjection? Rule, ImmutableArray<DesignProjection> Designs,
        ImmutableArray<GoodsQuantity> Materials, ImmutableArray<DesignProjection> DesignCatalog,
        ImmutableArray<GoodDefinitionProjection> DesignGoodsCatalog, ImmutableArray<CoreProblem> Blockers);

    internal static CoreResult<SettlementFoundingPreview> Preview(WorldState world, CoreContentProjection content, NodeId node)
    {
        if (node.Value == 0) throw new ArgumentException("节点编号必须非零。", nameof(node));
        var population = PopulationOperations.ReadInputs(world, content);
        var unavailable = PopulationOperations.Problems(world, null, population);
        if (!unavailable.IsEmpty) return new CoreResult<SettlementFoundingPreview>.Failure(unavailable);
        var plan = Prepare(world, content, node);
        var residents = plan.Type?.MinimumInitialPopulation ?? 0;
        var rule = population.Current!;
        var consumption = rule.Needs.Select(need =>
        {
            var good = rule.ConsumptionGoods.OrderBy(g => g.SelectionOrder).First(g => g.Satisfies.Any(c => c.Need == need.Need));
            return new GoodsRateView(good.Good, residents * need.AmountPerPersonSecond
                / good.Satisfies.Single(c => c.Need == need.Need).AmountPerGoodUnit);
        }).OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray();
        return new CoreResult<SettlementFoundingPreview>.Success(new(node, residents, consumption,
            plan.Materials, plan.Rule?.FoundingDurationTicks ?? 0, plan.Blockers));
    }

    private static Plan Prepare(WorldState world, CoreContentProjection content, NodeId id, TrainId? source = null)
    {
        SettlementTypeProjection? type = null;
        SettlementTemplateProjection? template = null;
        ConstructionRuleProjection? rule = null;
        ImmutableArray<DesignProjection> designs = [];
        ImmutableArray<GoodsQuantity> materials = [];
        ImmutableArray<DesignProjection> designCatalog = [];
        ImmutableArray<GoodDefinitionProjection> designGoodsCatalog = [];
        var node = world.Nodes.SingleOrDefault(n => n.Id == id);
        if (node is null) return Reject("object_missing", "Node");
        if (node.Occupant is not NodeOccupant.Empty) return Reject("node_occupied", "Node");
        if (node.FoundingJob is not null) return Reject("busy", "Node.FoundingJob");
        var selected = SettlementSelection.Choose(content, node.Terrain.TypeId,
            KeyedRandom.Sample(new(world.Source.WorldSeed, "settlement.founding.type", [id.Value])),
            kind => KeyedRandom.Sample(new(world.Source.WorldSeed, "settlement.founding.template", [id.Value, .. WorldRandom.Identity(kind.Value)])));
        if (selected is null) return Reject("content_missing", "Content.SettlementTemplates");
        (type, template) = selected.Value;
        if (content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.SettlementType(type.Id)))
            return Reject("content_missing", "SettlementType");
        rule = content.ConstructionRules.SingleOrDefault(r => r.Id == world.Source.Configuration.Rules.Construction);
        if (rule is null || content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(rule.Id)))
            return Reject("content_missing", "ConstructionRule");
        var targets = ImmutableArray.CreateBuilder<DesignProjection>();
        foreach (var building in template.FoundingBuildings)
        {
            var design = content.Designs.SingleOrDefault(d => d.Design == building.Design);
            var plot = template.Plots.SingleOrDefault(p => p.Number == building.PlotNumber);
            if (design?.Kind is not DesignKind.Building kind || plot is null || !WorldInputs.FitsPlot(kind.PlotSize, plot.Size))
                return Reject("invalid_design", "Template.FoundingBuildings");
            targets.Add(design);
        }
        designs = targets.ToImmutable();
        var referenced = designs.SelectMany(WorldReferences.DesignGoods).Select(g => g.Good).ToHashSet();
        designCatalog = world.Designs.Concat(designs).DistinctBy(d => d.Design)
            .OrderBy(d => d.Design.Value, StringComparer.Ordinal).ToImmutableArray();
        designGoodsCatalog = world.DesignGoods.Concat(content.DesignGoods.Where(g => g.Good is GoodId.Assembly && referenced.Contains(g.Good)))
            .DistinctBy(g => g.Good).OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray();
        var prepared = world with { Designs = designCatalog, DesignGoods = designGoodsCatalog };
        foreach (var design in designs)
            if (!design.CanConstruct || VehicleDesignChanges.Missing(prepared, content, design))
                return Reject("content_missing", "Template.FoundingBuildings");
        materials = ConstructionCommands.BillOfMaterials(designs);
        if (source is { } trainId)
        {
            var train = world.Trains.SingleOrDefault(t => t.Id == trainId);
            if (train is null) return Reject("object_missing", "MaterialSource");
            if (train.Vehicles.Any(v => world.Instances.Single(i => i.Id == new InstanceId.Vehicle(v)).Ownership != SettlementRelation.PlayerOwned))
                return Reject("not_owned", "MaterialSource");
            if (train.Position is not TrainPosition.Node position || position.Id != id || train.SpeedMetersPerSecond != 0)
                return Reject("train_not_stationary_at_node", "MaterialSource.Position");
            if (ConstructionJobs.TrainLocked(world, train)) return Reject("instance_locked", "MaterialSource");
            var inventory = world.Inventories.Single(i => i.Id == train.Inventory);
            var missing = materials.Where(g => (inventory.Goods.SingleOrDefault(q => q.Good == g.Good)?.Quantity ?? 0) < g.Quantity)
                .Select(g => WorldReferences.GoodKey(g.Good)).ToImmutableArray();
            if (!missing.IsEmpty) return new(type, template, rule, designs, materials, designCatalog, designGoodsCatalog,
                [new("insufficient_goods", "MaterialSource.Inventory", new WorldObjectId.Train(trainId), missing)]);
        }
        if (world.Next.Job == ulong.MaxValue) return Reject("identity_exhausted", "World.Next.Job");
        if (world.Next.Settlement == ulong.MaxValue) return Reject("identity_exhausted", "World.Next.Settlement");
        return new(type, template, rule, designs, materials, designCatalog, designGoodsCatalog, []);

        Plan Reject(string code, string field) => new(type, template, rule, designs, materials, designCatalog, designGoodsCatalog,
            [new(code, field, new WorldObjectId.Node(id), [])]);
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, CoreCommand.FoundSettlement command)
    {
        var plan = Prepare(world, content, command.Node, command.MaterialSource);
        if (!plan.Blockers.IsEmpty) return new CommandEffect.Rejected(plan.Blockers);
        var source = plan.Template!;
        var template = source with
        {
            Plots = source.Plots.Select(p => p with
                { PrebuiltDesign = source.FoundingBuildings.SingleOrDefault(b => b.PlotNumber == p.Number)?.Design }).ToImmutableArray(),
            SpecialInitialInventory = [], ProductionPlans = []
        };
        var job = new FoundingJobState(new(world.Next.Job), command.Node, new(world.Next.Settlement),
            plan.Type!.Id, template, plan.Type.MinimumInitialPopulation, plan.Rule!.Id, plan.Rule.Revision,
            plan.Rule.FoundingDurationTicks, 0, plan.Materials);
        var inventory = world.Trains.Single(t => t.Id == command.MaterialSource).Inventory;
        var prepared = ConstructionCommands.Consume(world, [new(inventory, plan.Materials)]) with
        {
            FoundingJobs = world.FoundingJobs.Add(job),
            Nodes = world.Nodes.Select(n => n.Id == command.Node ? n with { FoundingJob = job.Id } : n).ToImmutableArray(),
            Next = world.Next with { Job = checked(world.Next.Job + 1), Settlement = checked(world.Next.Settlement + 1) },
            Designs = plan.DesignCatalog,
            DesignGoods = plan.DesignGoodsCatalog
        };
        prepared = prepared with { DefinitionDependencies = WorldReferences.Dependencies(prepared, content) };
        var reference = new OwnedSettlementTemplateReference(job.ReservedSettlementId, source.Id,
            source.Plots.Select(p => new TemplatePlotReference(p.Number, p.Size)).ToImmutableArray());
        return new CommandEffect.Accepted(content, [], prepared, [new WorldObjectId.Job(job.Id)], [reference]);
    }

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState world,
        CoreContentProjection content, SimulationPosition boundary)
    {
        if (world.FoundingJobs.IsEmpty) return (world, []);
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        foreach (var original in world.FoundingJobs)
        {
            var job = original with { ElapsedTicks = checked(original.ElapsedTicks + 1) };
            if (job.ElapsedTicks < job.FrozenDurationTicks)
            {
                world = world with { FoundingJobs = world.FoundingJobs.Select(j => j.Id == job.Id ? job : j).ToImmutableArray() };
                continue;
            }
            var next = world.Next;
            var plotCount = (ulong)job.Template.Plots.Length;
            var buildingCount = (ulong)job.Template.FoundingBuildings.Length;
            if (next.Plot > ulong.MaxValue - plotCount || next.Building > ulong.MaxValue - buildingCount || next.Inventory == ulong.MaxValue)
                throw new InvalidOperationException("聚居地实例身份已耗尽。");
            var settlement = WorldState.CreateSettlement(job.ReservedSettlementId, job.Node, job.Type,
                SettlementRelation.PlayerOwned, job.InitialResidents, job.Template, new(next.Plot), new(next.Building), new(next.Inventory));
            var instances = world.Instances.AddRange(settlement.Plots.Where(p => p.Building is not null).Select(p =>
                WorldState.CreateInstance(new InstanceId.Building(p.Building!.Value),
                    world.Designs.Single(d => d.Design == job.Template.Plots.Single(t => t.Number == p.TemplateNumber).PrebuiltDesign),
                    SettlementRelation.PlayerOwned, new InstanceDetails.Building(p.Id, p.Size))));
            var platforms = WorldState.CountPlatforms(settlement, instances.ToDictionary(i => i.Id), world.Designs.ToDictionary(d => d.Design));
            world = world with
            {
                Settlements = world.Settlements.Add(settlement).OrderBy(s => s.Id.Value).ToImmutableArray(),
                Inventories = world.Inventories.Add(new(settlement.Inventory, new InventoryOwner.Settlement(settlement.Id), [], [])),
                Instances = instances.OrderBy(i => i.Id is InstanceId.Building ? 0 : 1)
                    .ThenBy(i => i.Id is InstanceId.Building b ? b.Id.Value : ((InstanceId.Vehicle)i.Id).Id.Value).ToImmutableArray(),
                Nodes = world.Nodes.Select(n => n.Id == job.Node ? n with
                    { FoundingJob = null, Occupant = new NodeOccupant.Settlement(settlement.Id, SettlementRelation.PlayerOwned), Platforms = platforms } : n).ToImmutableArray(),
                Next = next with { Plot = next.Plot + plotCount, Building = next.Building + buildingCount, Inventory = next.Inventory + 1 },
                FoundingJobs = world.FoundingJobs.Where(j => j.Id != job.Id).ToImmutableArray()
            };
            events.Add(new CoreEvent.SettlementFounded(settlement.Id, job.Node, settlement.Population));
        }
        if (events.Count == 0) return (world, []);
        var adapted = TrainOperations.AdaptNodes(world, content, boundary);
        return (adapted.World, events.ToImmutable().AddRange(adapted.Events));
    }
}
