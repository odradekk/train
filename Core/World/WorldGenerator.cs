using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;
using Train.Core.Inventory;
using Train.Core.Production;
using Train.Core.Settlements;
using Train.Core.Trading;

namespace Train.Core.World;

internal sealed record GeneratedWorld(WorldState State, CoreContentProjection Content);

internal static class WorldGenerator
{
    internal static CoreResult<GeneratedWorld> Generate(WorldGenerationInput input, CoreContentProjection content)
    {
        WorldInputs.ValidateInput(input);
        WorldInputs.ValidateContent(content);
        var budget = new GenerationBudgetCounter(input.Configuration.Budget);
        try
        {
            if (input.GeneratorVersion != "stage1-world-v1") Invalid("GeneratorVersion");
            var railway = input.Configuration.InitialRailway;
            if (railway.MinimumInitialTracks > 2) Invalid("Configuration.InitialRailway.MinimumInitialTracks");
            if (railway.MaximumInitialTracks < 1) Invalid("Configuration.InitialRailway.MaximumInitialTracks");
            input = WorldInputs.NormalizeInput(input, budget);
            content = WorldInputs.NormalizeContent(content);
            ValidateReferences(input, content);
            var types = content.SettlementTypes.Where(t => content.SettlementTemplates.Any(p => p.Type == t.Id)).ToArray();
            if (types.Length == 0) Invalid("Content.SettlementTemplates");
            GenerationFailure? last = null;
            for (ulong world = 0; world <= (ulong)input.Configuration.Budget.MaximumWorldRetries; world++)
            {
                budget.WorldRetry = world;
                budget.Candidate = "";
                var terrain = new WorldTerrain(input, budget, world);
                ImmutableArray<CandidateNode> nodes = [];
                for (ulong attempt = 0; attempt <= (ulong)input.Configuration.Budget.MaximumStageRetries; attempt++)
                {
                    nodes = PlaceNodes(input, terrain, budget, attempt);
                    if (!nodes.IsEmpty) break;
                    budget.Candidate = "";
                    last = new("constraint_unsatisfied", "World.Nodes");
                }
                if (nodes.IsEmpty) continue;
                ImmutableArray<CandidateEdge> edges;
                try { edges = WorldGraph.Build(nodes, terrain, budget); }
                catch (GenerationFailure failure) when (failure.Category == "constraint_unsatisfied") { last = failure; continue; }
                for (ulong attempt = 0; attempt <= (ulong)input.Configuration.Budget.MaximumStageRetries; attempt++)
                {
                    try
                    {
                        var occupants = Occupants(nodes, input, content, types, budget, attempt);
                        var resources = Resources(nodes, input, budget);
                        var tracks = WorldRailway.Build(nodes, edges, occupants, input, budget);
                        var start = Start(nodes, edges, occupants, tracks, input, budget);
                        var state = WorldState.Create(input, content, nodes, edges, occupants, resources, tracks, start);
                        state = InitializeNpcs(state, content, nodes, budget);
                        return new CoreResult<GeneratedWorld>.Success(new(state, content));
                    }
                    catch (GenerationFailure failure) when (failure.Category == "constraint_unsatisfied") { last = failure; }
                }
            }
            throw last ?? new GenerationFailure("constraint_unsatisfied", "World.Nodes");
        }
        catch (GenerationFailure failure) { return new CoreResult<GeneratedWorld>.Failure([budget.Problem(failure)]); }
        catch (Exception exception)
        {
            return new CoreResult<GeneratedWorld>.Faulted(new("internal_error", new(0, 0), "PrepareNew", budget.Stage, null),
                new(exception.Message, exception.StackTrace));
        }
    }

    private static WorldState InitializeNpcs(WorldState world, CoreContentProjection content,
        ImmutableArray<CandidateNode> nodes, GenerationBudgetCounter budget)
    {
        var config = world.Source.Configuration.NpcInitialization;
        if (!config.FillInputsAndLivingTargets && config.OutputReserveHours == 0 && config.MaximumForSaleVehicles == 0)
            return world;

        budget.Stage = "initialization";
        // 补货可能首次引入组装体货物；计算时提供其投影，完成后只冻结实际使用的货物。
        world = world with { DesignGoods = content.DesignGoods };
        foreach (var settlement in world.Settlements.Where(s => s.Relation != SettlementRelation.PlayerOwned))
        {
            var cell = nodes[checked((int)settlement.Node.Value - 1)].Cell;
            budget.Candidate = cell.ToString();
            var inventory = world.Inventories.Single(i => i.Id == settlement.Inventory);
            var stock = inventory.Goods.ToDictionary(g => g.Good, g => g.Quantity);
            var available = InventoryStorage.Settlement(world, content, settlement, 0).Uncommitted;
            if (config.FillInputsAndLivingTargets)
                Fill(new MarketStockTargets(world, content, settlement, 0).Current
                    .Select(g => (g.Good, new BigInteger(g.Quantity))));
            if (config.OutputReserveHours > 0 && available > 0)
            {
                var outputs = ProductionQueries.Potential(world, content, settlement)
                    .Where(p => p.View.State is ProductionState.Running or ProductionState.WaitingForInputs or ProductionState.OutputBlocked)
                    .SelectMany(p => ProductionQueries.Outputs(p.Process!.Details)
                        .Select(g => (g.Good, Rate: p.Rate / p.Process!.WorkRequired, g.Quantity)))
                    .GroupBy(g => g.Good)
                    .Select(g => (g.Key, ReserveQuantity(g.Select(p => (p.Rate, p.Quantity)), config.OutputReserveHours)));
                Fill(outputs);
            }
            world = world with
            {
                Inventories = world.Inventories.Replace(inventory, inventory with
                {
                    Goods = stock.OrderBy(g => WorldReferences.GoodKey(g.Key), StringComparer.Ordinal)
                        .Select(g => new GoodsQuantity(g.Key, g.Value)).ToImmutableArray()
                })
            };
            if (config.MaximumForSaleVehicles > 0 && VehicleTrades.Supports(world, content, settlement, SizeClass.Small))
            {
                var candidates = config.ForSaleVehicleCandidates.Select(hash => world.Designs.Single(d => d.Design == hash))
                    .Where(d => VehicleTrades.Supports(world, content, settlement, ((DesignKind.Vehicle)d.Kind).Size))
                    .OrderBy(d => d.Composition.Value, StringComparer.Ordinal)
                    .ThenBy(d => d.Design.Value, StringComparer.Ordinal).ToArray();
                var rule = content.MarketRules.Single(r => r.Id == world.Source.Configuration.Rules.Market);
                var slots = candidates.Select(d => ((DesignKind.Vehicle)d.Kind).Size).Distinct()
                    .ToDictionary(size => size, size => rule.VehiclePurchaseLimits.FirstOrDefault(l => l.Size == size)?.MaximumForSale ?? 0);
                var capacity = slots.Values.Sum(n => (long)n);
                if (capacity < config.MinimumForSaleVehicles)
                    Invalid("Configuration.NpcInitialization.MinimumForSaleVehicles");
                var count = WorldRandom.Integer(Sample("count", 0), config.MinimumForSaleVehicles,
                    Math.Min(config.MaximumForSaleVehicles, capacity));
                var parked = settlement.ParkedVehicles.ToBuilder();
                var instances = world.Instances.ToBuilder();
                var next = world.Next.Vehicle;
                for (long index = 0; index < count; index++)
                {
                    var eligible = candidates.Where(d => slots[((DesignKind.Vehicle)d.Kind).Size] > 0).ToArray();
                    var design = eligible[(int)WorldRandom.Integer(Sample("choice", (ulong)index), 0, eligible.Length - 1)];
                    var size = ((DesignKind.Vehicle)design.Kind).Size;
                    var id = new VehicleId(next++);
                    parked.Add(id);
                    instances.Add(new(new InstanceId.Vehicle(id), design.Design, settlement.Relation,
                        new InstanceDetails.Vehicle(size, null), design.MaxStructureDurability,
                        design.Parts.Select(p => new PartDurability(p.Id, p.MaxDurability)).ToImmutableArray()));
                    slots[size]--;
                }
                world = world with
                {
                    Settlements = world.Settlements.Replace(settlement, settlement with { ParkedVehicles = parked.ToImmutable() }),
                    Instances = instances.ToImmutable(), Next = world.Next with { Vehicle = next }
                };
            }

            void Fill(IEnumerable<(GoodId Good, BigInteger Quantity)> targets)
            {
                var additions = PopulationStockTargets.Allocate(targets.Select(g =>
                    (g.Good, BigInteger.Max(0, g.Quantity - stock.GetValueOrDefault(g.Good)))), available);
                foreach (var good in additions)
                {
                    stock[good.Good] = checked(stock.GetValueOrDefault(good.Good) + good.Quantity);
                    available -= good.Quantity;
                }
            }

            ulong Sample(string purpose, ulong index) => KeyedRandom.Sample(new(world.Source.WorldSeed,
                "world.stage1.v1.npc.vehicle." + purpose, [cell.X, cell.Y, index]));
        }
        var goods = world.Inventories.SelectMany(i => i.Goods).Select(g => g.Good).ToHashSet();
        goods.UnionWith(world.Designs.SelectMany(WorldReferences.DesignGoods).Select(g => g.Good));
        world = world with { DesignGoods = content.DesignGoods.Where(g => goods.Contains(g.Good)).ToImmutableArray() };
        return world with { DefinitionDependencies = WorldReferences.Dependencies(world, content) };
    }

    private static BigInteger ReserveQuantity(IEnumerable<(double Rate, long Quantity)> outputs, double hours)
    {
        var rates = outputs.ToArray();
        var quantity = rates.Sum(p => p.Rate * p.Quantity) * hours * 3600;
        if (double.IsFinite(quantity)) return new BigInteger(Math.Ceiling(quantity));
        // 超大时长的中间乘积可能溢出；保留二进制比例供容量分配，而不是截断每种货物。
        var (hourUnits, hourExponent) = Binary(hours);
        var terms = rates.Select(p =>
        {
            var (rateUnits, rateExponent) = Binary(p.Rate);
            return (Units: rateUnits * p.Quantity * hourUnits * 3600, Exponent: rateExponent + hourExponent);
        }).ToArray();
        var exponent = terms.Min(p => p.Exponent);
        var numerator = terms.Aggregate(BigInteger.Zero, (sum, p) => sum + (p.Units << (p.Exponent - exponent)));
        if (exponent >= 0) return numerator << exponent;
        var denominator = BigInteger.One << -exponent;
        return BigInteger.DivRem(numerator, denominator, out var remainder) + (remainder.IsZero ? 0 : 1);

        static (BigInteger Units, int Exponent) Binary(double value)
        {
            if (!double.IsFinite(value)) throw new OverflowException("潜在产出速率必须有限。");
            var bits = BitConverter.DoubleToUInt64Bits(value);
            var exponent = (int)((bits >> 52) & 0x7ff);
            var units = (bits & ((1UL << 52) - 1)) | (exponent == 0 ? 0 : 1UL << 52);
            return (new BigInteger(units), exponent == 0 ? -1074 : exponent - 1075);
        }
    }

    private static ImmutableArray<CandidateNode> PlaceNodes(WorldGenerationInput input, WorldTerrain terrain,
        GenerationBudgetCounter budget, ulong attempt)
    {
        budget.Stage = "nodes";
        var config = input.Configuration;
        var width = Math.Ceiling(config.WidthMeters / config.MinimumNodeSpacingMeters);
        var height = Math.Ceiling(config.HeightMeters / config.MinimumNodeSpacingMeters);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width >= (double)ulong.MaxValue || height >= (double)ulong.MaxValue)
            throw new GenerationFailure("budget_exhausted", "Configuration.Budget.MaximumCandidates");
        var nodes = ImmutableArray.CreateBuilder<CandidateNode>();
        for (ulong y = 0; y < (ulong)height; y++)
        for (ulong x = 0; x < (ulong)width; x++)
        {
            var cell = new CellId(x, y);
            budget.Candidate = cell.ToString();
            budget.CandidateCount();
            budget.Work();
            if (Unit("node.presence") >= config.CandidateProbability) continue;
            var left = x * config.MinimumNodeSpacingMeters;
            var top = y * config.MinimumNodeSpacingMeters;
            var position = new MapPoint(left + Unit("node.x") * Math.Min(config.MinimumNodeSpacingMeters, config.WidthMeters - left),
                top + Unit("node.y") * Math.Min(config.MinimumNodeSpacingMeters, config.HeightMeters - top));
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.X > config.WidthMeters
                || position.Y < 0 || position.Y > config.HeightMeters)
                throw new GenerationFailure("config_invalid", "Configuration.WidthMeters");
            var sample = terrain.Sample(position);
            if (sample.View.FeatureIds.Contains("lake")) continue;
            var conflict = false;
            foreach (var node in nodes)
            {
                budget.Work();
                var dx = position.X - node.Position.X;
                var dy = position.Y - node.Position.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < config.MinimumNodeSpacingMeters) { conflict = true; break; }
            }
            if (!conflict) nodes.Add(new(cell, position, sample.View));

            double Unit(string purpose) => WorldRandom.Unit(WorldRandom.Sample(input.WorldSeed, purpose, 2,
                budget.WorldRetry, attempt, x, y));
        }
        return nodes.ToImmutable();
    }

    private static ImmutableArray<GeneratedOccupant> Occupants(ImmutableArray<CandidateNode> nodes,
        WorldGenerationInput input, CoreContentProjection content, SettlementTypeProjection[] types, GenerationBudgetCounter budget, ulong attempt)
    {
        budget.Stage = "occupants";
        var config = input.Configuration;
        var settlements = (int)Math.Floor(nodes.Length * config.SettlementRatio);
        var lairs = (int)Math.Floor(nodes.Length * config.LairRatio);
        if (settlements == 0) throw new GenerationFailure("constraint_unsatisfied", "Configuration.SettlementRatio");
        if (lairs > 0 && config.LairWeights.IsEmpty) Invalid("Configuration.LairWeights");
        var result = Enumerable.Range(0, nodes.Length).Select(_ => (GeneratedOccupant)new GeneratedOccupant.Empty()).ToArray();
        budget.Sort(nodes.Length);
        var order = Enumerable.Range(0, nodes.Length).OrderBy(i => Random("occupant.rank", i)).ThenBy(i => i).ToArray();
        var hostileCount = (int)Math.Floor(settlements * config.HostileSettlementRatio);
        budget.Sort(settlements);
        var hostile = order.Take(settlements).OrderBy(i => Random("settlement.relation", i)).ThenBy(i => i).Take(hostileCount).ToHashSet();
        foreach (var index in order.Take(settlements))
        {
            budget.Candidate = nodes[index].Cell.ToString();
            var preferred = types.Where(t => t.PreferredTerrain.IsEmpty || t.PreferredTerrain.Contains(nodes[index].Terrain.TypeId)).ToArray();
            var type = Weighted(preferred.Length == 0 ? types : preferred, t => t.Weight, Random("settlement.type", index));
            var templates = content.SettlementTemplates.Where(t => t.Type == type.Id).ToArray();
            var template = Weighted(templates, t => t.Weight, Random("settlement.template", index, WorldRandom.Identity(type.Id.Value)));
            var population = WorldRandom.Integer(Random("settlement.population", index, WorldRandom.Identity(type.Id.Value)),
                type.MinimumInitialPopulation, type.MaximumInitialPopulation);
            var platforms = template.Plots.Where(p => p.PrebuiltDesign is not null).Select(p => content.Designs.Single(d => d.Design == p.PrebuiltDesign))
                .Where(d => d.Kind is DesignKind.Building { Type: BuildingKind.Station } && d.MaxStructureDurability > 0)
                .Sum(d => d.Parts.Count(p => p.MaxDurability > 0 && p.Capabilities.Any(c => c is PartCapability.Platform)));
            result[index] = new GeneratedOccupant.Settlement(type, template,
                hostile.Contains(index) ? SettlementRelation.Hostile : SettlementRelation.NonHostile, population, platforms);
        }
        foreach (var index in order.Skip(settlements).Take(lairs))
        {
            budget.Candidate = nodes[index].Cell.ToString();
            var lair = Weighted(config.LairWeights.ToArray(), l => l.Weight, Random("lair.type", index));
            result[index] = new GeneratedOccupant.Lair(lair.Type);
        }
        return result.ToImmutableArray();

        ulong Random(string purpose, int index, params ulong[] identity)
            => WorldRandom.Sample(input.WorldSeed, purpose, 4, budget.WorldRetry, attempt, [nodes[index].Cell.X, nodes[index].Cell.Y, .. identity]);
    }

    private static T Weighted<T>(T[] values, Func<T, int> weight, ulong random)
    {
        var total = values.Sum(v => (long)weight(v));
        var selected = WorldRandom.Integer(random, 0, total - 1);
        foreach (var value in values)
        {
            selected -= weight(value);
            if (selected < 0) return value;
        }
        throw new InvalidOperationException("权重选择没有结果。");
    }

    private static ImmutableArray<ImmutableArray<NodeResourceView>> Resources(ImmutableArray<CandidateNode> nodes,
        WorldGenerationInput input, GenerationBudgetCounter budget)
    {
        budget.Stage = "resources";
        var result = ImmutableArray.CreateBuilder<ImmutableArray<NodeResourceView>>();
        foreach (var node in nodes)
        {
            var resources = ImmutableArray.CreateBuilder<NodeResourceView>();
            foreach (var entry in input.Configuration.Resources)
            {
                budget.Work();
                budget.Candidate = node.Cell + ":" + entry.Good.Value;
                if (!entry.AllowedTerrainTypes.Contains(node.Terrain.TypeId)) continue;
                var identity = WorldRandom.Identity(entry.Good.Value);
                if (WorldRandom.Unit(Random("resource.presence")) >= entry.Probability) continue;
                var reserve = WorldRandom.Integer(Random("resource.reserve"), entry.MinimumReserve, entry.MaximumReserve);
                resources.Add(new(new GoodId.Voxel(entry.Good), reserve, entry.Renewable
                    ? new ResourceRenewal.Renewable(reserve, entry.RecoveryUnitsPerSecond) : new ResourceRenewal.NonRenewable()));

                ulong Random(string purpose) => WorldRandom.Sample(input.WorldSeed, purpose, 5, budget.WorldRetry, 0,
                    [node.Cell.X, node.Cell.Y, .. identity]);
            }
            result.Add(resources.ToImmutable());
        }
        return result.ToImmutable();
    }

    internal static int Start(ImmutableArray<CandidateNode> nodes, ImmutableArray<CandidateEdge> edges,
        ImmutableArray<GeneratedOccupant> occupants, ImmutableArray<int> tracks, WorldGenerationInput input, GenerationBudgetCounter budget)
    {
        budget.Stage = "validation";
        var config = input.Configuration.StartConstraints;
        var roots = Enumerable.Range(0, nodes.Length).Where(i => occupants[i] is GeneratedOccupant.Settlement { Relation: SettlementRelation.NonHostile }).ToArray();
        budget.Sort(roots.Length);
        roots = roots.OrderBy(i => WorldRandom.Sample(input.WorldSeed, "start.rank", 7, budget.WorldRetry, 0, nodes[i].Cell.X, nodes[i].Cell.Y)).ThenBy(i => i).ToArray();
        var traversal = WorldTraversal.Build(nodes.Length, edges.Where((_, i) => tracks[i] > 0).Select(e => (e.A, e.B)), budget);
        var field = "Configuration.StartConstraints";
        foreach (var root in roots)
        {
            budget.Candidate = nodes[root].Cell.ToString();
            if (config.RequireStation && ((GeneratedOccupant.Settlement)occupants[root]).Platforms == 0)
            { field = "Configuration.StartConstraints.RequireStation"; continue; }
            if (config.ExcludeAdjacentLairs && edges.Any(e =>
            {
                budget.Work();
                return (e.A == root && occupants[e.B] is GeneratedOccupant.Lair)
                    || (e.B == root && occupants[e.A] is GeneratedOccupant.Lair);
            }))
            { field = "Configuration.StartConstraints.ExcludeAdjacentLairs"; continue; }
            var reached = traversal.Reachable(root).Nodes;
            if (reached.Count(i => i != root && occupants[i] is GeneratedOccupant.Settlement { Relation: SettlementRelation.NonHostile }) < config.MinimumReachableNonHostileSettlements)
            { field = "Configuration.StartConstraints.MinimumReachableNonHostileSettlements"; continue; }
            if (reached.Count(i => occupants[i] is GeneratedOccupant.Empty) < config.MinimumReachableEmptyNodes)
            { field = "Configuration.StartConstraints.MinimumReachableEmptyNodes"; continue; }
            return root;
        }
        throw new GenerationFailure("constraint_unsatisfied", field);
    }

    private static void ValidateReferences(WorldGenerationInput input, CoreContentProjection content)
    {
        var config = input.Configuration;
        Need(new CoreDefinitionReference.Rule(config.Rules.Population), "Configuration.Rules.Population", content.PopulationRules.Any(r => r.Id == config.Rules.Population));
        Need(new CoreDefinitionReference.Rule(config.Rules.Market), "Configuration.Rules.Market", content.MarketRules.Any(r => r.Id == config.Rules.Market));
        Need(new CoreDefinitionReference.Rule(config.Rules.Construction), "Configuration.Rules.Construction", content.ConstructionRules.Any(r => r.Id == config.Rules.Construction));
        Need(new CoreDefinitionReference.Rule(config.Rules.Railway), "Configuration.Rules.Railway", content.RailwayRules.Any(r => r.Id == config.Rules.Railway));
        Need(new CoreDefinitionReference.Rule(config.Rules.Combat), "Configuration.Rules.Combat", content.CombatRules.Any(r => r.Id == config.Rules.Combat));
        foreach (var band in config.TerrainBands) Need(new CoreDefinitionReference.Terrain(band.Terrain), "Configuration.TerrainBands", true);
        foreach (var resource in config.Resources)
        {
            Need(new CoreDefinitionReference.Voxel(resource.Good), "Configuration.Resources", content.Goods.Any(g => g.Good == new GoodId.Voxel(resource.Good) && g.RawGood));
            foreach (var type in resource.AllowedTerrainTypes) Need(new CoreDefinitionReference.Terrain(type), "Configuration.Resources.AllowedTerrainTypes", true);
        }
        foreach (var lair in config.LairWeights)
            Need(new CoreDefinitionReference.Lair(lair.Type), "Configuration.LairWeights", content.LairTypes.Any(l => l.Id == lair.Type));
        foreach (var template in content.SettlementTemplates)
        {
            Need(new CoreDefinitionReference.Template(template.Id), $"Content.SettlementTemplates[{template.Id.Value}]", true);
            Need(new CoreDefinitionReference.SettlementType(template.Type), $"Content.SettlementTemplates[{template.Id.Value}].Type", content.SettlementTypes.Any(t => t.Id == template.Type));
            foreach (var plot in template.Plots)
                if (plot.PrebuiltDesign is { } design && (FindDesign(design, $"Content.SettlementTemplates[{template.Id.Value}].Plots[{plot.Number}]").Kind is not DesignKind.Building building || !WorldInputs.FitsPlot(building.PlotSize, plot.Size)))
                    Invalid($"Content.SettlementTemplates[{template.Id.Value}].Plots[{plot.Number}]");
            foreach (var building in template.FoundingBuildings)
                if (FindDesign(building.Design, $"Content.SettlementTemplates[{template.Id.Value}].FoundingBuildings").Kind is not DesignKind.Building)
                    Invalid($"Content.SettlementTemplates[{template.Id.Value}].FoundingBuildings");
            foreach (var good in template.SpecialInitialInventory) CheckGood(good.Good, $"Content.SettlementTemplates[{template.Id.Value}].SpecialInitialInventory");
            foreach (var plan in template.ProductionPlans)
            {
                if (template.Plots.SingleOrDefault(p => p.Number == plan.PlotNumber)?.PrebuiltDesign is null)
                    Invalid($"Content.SettlementTemplates[{template.Id.Value}].ProductionPlans");
                Need(new CoreDefinitionReference.Process(plan.Process), $"Content.SettlementTemplates[{template.Id.Value}].ProductionPlans", content.Processes.Any(p => WorldReferences.Matches(p.Details, plan.Process)));
            }
        }
        var commissionField = "Configuration.StartConstraints.RequiredAffordableCommissionDesign";
        if (FindDesign(config.StartConstraints.RequiredAffordableCommissionDesign, commissionField).Kind is not DesignKind.Vehicle) Invalid(commissionField);
        foreach (var hash in config.NpcInitialization.ForSaleVehicleCandidates)
            if (FindDesign(hash, "Configuration.NpcInitialization.ForSaleVehicleCandidates").Kind is not DesignKind.Vehicle)
                Invalid("Configuration.NpcInitialization.ForSaleVehicleCandidates");
        for (var i = 0; i < input.Start.Trains.Length; i++)
        {
            foreach (var hash in input.Start.Trains[i].Vehicles)
                if (FindDesign(hash, $"Start.Trains[{i}].Vehicles").Kind is not DesignKind.Vehicle) Invalid($"Start.Trains[{i}].Vehicles");
            foreach (var good in input.Start.Trains[i].Inventory) CheckGood(good.Good, $"Start.Trains[{i}].Inventory");
        }

        DesignProjection FindDesign(DesignHash hash, string field)
        {
            var design = content.Designs.FirstOrDefault(d => d.Design == hash);
            if (design is null || !design.MissingReferences.IsEmpty || design.Parts.Any(p => p.Missing)) Invalid(field);
            foreach (var part in design!.Parts) Need(part.Definition, field, true);
            if (design.ReturnUnits.Any(u => !u.MissingReferences.IsEmpty)) Invalid(field);
            foreach (var (good, _) in WorldReferences.DesignGoods(design)) CheckGood(good, field);
            return design;
        }

        void Need(CoreDefinitionReference reference, string field, bool exists)
        {
            if (!exists || content.MissingReferences.Any(m => m.Reference == reference)) Invalid(field);
            foreach (var (good, path) in WorldReferences.DependencyGoods(reference, content)) CheckGood(good, path);
            foreach (var (dependency, path, present) in WorldReferences.DependencyDefinitions(reference, content)) Need(dependency, path, present);
        }

        void CheckGood(GoodId good, string field)
        {
            if (!content.Goods.Concat(content.DesignGoods).Any(g => g.Good == good)) Invalid(field);
            if (WorldReferences.GoodReference(good) is { } reference) Need(reference, field, true);
        }
    }

    private static void Invalid(string field) => throw new GenerationFailure("config_invalid", field);
}
