using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Immutable;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldGenerationTests
{
    internal static CoreSessionOptions Options => new(new("fixture", "1", "Windows", "x64", "10"), false, 1);

    [TestMethod]
    public void Unsupported_generator_version_returns_a_located_failure()
    {
        var result = CoreFactory.Create().PrepareNew(WorldFixture.Input with { GeneratorVersion = "unknown" },
            WorldFixture.Content, Options);
        var failure = (CoreResult<ICoreSessionCandidate>.Failure)result;
        Assert.AreEqual(1, failure.Problems.Length);
        Assert.AreEqual("generation_failed", failure.Problems[0].Code);
        Assert.AreEqual("GeneratorVersion", failure.Problems[0].Field);
        Assert.AreEqual("config_invalid", failure.Problems[0].RelatedIdentities[0]);
    }

    [TestMethod]
    public void Declared_ranges_and_initialized_arrays_are_programming_errors()
    {
        var input = WorldFixture.Input;
        foreach (var configuration in new[]
        {
            input.Configuration with { WidthMeters = double.NaN },
            input.Configuration with { CandidateProbability = 1.01 },
            input.Configuration with { SettlementRatio = .8, LairRatio = .3 },
            input.Configuration with { ElevationNoise = default },
            input.Configuration with { Resources = [null!] },
            input.Configuration with { Budget = new(0, 1, 100, 100) }
        })
            Assert.ThrowsExactly<ArgumentException>(() => CoreFactory.Create().PrepareNew(
                input with { Configuration = configuration }, WorldFixture.Content, Options));
    }

    [TestMethod]
    public void Missing_projection_references_return_one_configuration_failure()
    {
        var content = WorldFixture.Content;
        foreach (var (projection, field) in new (CoreContentProjection, string)[]
        {
            (content with { PopulationRules = [] }, "Configuration.Rules.Population"),
            (content with { Designs = [content.Designs[1]] }, "Configuration.StartConstraints.RequiredAffordableCommissionDesign"),
            (content with { Goods = [] }, "Content.RailwayRules[fixture:railway].RailGood"),
            (content with { SettlementTypes = [] }, "Content.SettlementTemplates[fixture:town-template].Type"),
            (content with { SettlementTemplates = [] }, "Content.SettlementTemplates"),
            (content with { MissingReferences = [new(new CoreDefinitionReference.Template(content.SettlementTemplates[0].Id), "known")] }, "Content.SettlementTemplates[fixture:town-template]")
        })
            Failure(WorldFixture.Input, projection, "config_invalid", field);
    }

    [TestMethod]
    public void Selected_content_dependencies_cannot_hide_known_missing_references()
    {
        var content = WorldFixture.Content;
        var absent = new GoodId.Voxel(new("fixture:absent"));
        var missing = new MissingCoreReference(new CoreDefinitionReference.Voxel(absent.Definition), "known");
        var price = content.MarketRules[0].DefaultGoodsPrices;
        var processId = new ProductionProcessDefinitionId("fixture:process");
        var process = new ProcessProjection(new ProcessDetails.Extraction(processId, new(absent, 1)),
            WorldFixture.Revision, 1, 0, 0, FacilityTag.Extraction, []);
        var planTemplate = content.SettlementTemplates[0] with
            { ProductionPlans = [new(1, CapabilityKind.Extraction, new ProcessRef.Production(processId), false, 0)] };
        foreach (var (projection, field) in new (CoreContentProjection, string)[]
        {
            (content with { RailwayRules = [content.RailwayRules[0] with { RailGood = absent.Definition }] },
                "Content.RailwayRules[fixture:railway].RailGood"),
            (content with { PopulationRules = [content.PopulationRules[0] with { ConsumptionGoods = [new(absent, [], 1)] }] },
                "Content.PopulationRules[fixture:population].ConsumptionGoods"),
            (content with { MarketRules = [content.MarketRules[0] with { GoodPrices = [new(absent, price)] }] },
                "Content.MarketRules[fixture:market].GoodPrices"),
            (content with { SettlementTemplates = [planTemplate], Processes = [process] },
                "Content.Processes[production:fixture:process].Details")
        })
        {
            Failure(WorldFixture.Input, projection, "config_invalid", field);
            var present = projection with { Goods = [.. content.Goods, new(absent, WorldFixture.Revision, new(1), 1, true, null)] };
            using var candidate = Prepare(WorldFixture.Input, present);
            Assert.IsTrue(candidate.References.Definitions.Any(d => d.Definition == missing.Reference && d.Field == field));
            Failure(WorldFixture.Input, present with { MissingReferences = [missing] }, "config_invalid", field);
        }
        var terrain = new TerrainTypeId("fixture:absent-terrain");
        Failure(WorldFixture.Input, content with { SettlementTemplates = [planTemplate],
            Processes = [process with { Details = new ProcessDetails.Extraction(processId, new(new GoodId.Voxel(WorldFixture.Material), 1)),
                AllowedTerrainTypes = [terrain] }],
            MissingReferences = [new(new CoreDefinitionReference.Terrain(terrain), "known")] }, "config_invalid",
            "Content.Processes[production:fixture:process].AllowedTerrainTypes");
        var lairContent = WithLair();
        Failure(WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
            { LairWeights = [new(lairContent.LairTypes[0].Id, 1)] } },
            lairContent with { LairTypes = [lairContent.LairTypes[0] with { Members = [new(new("fixture:absent-creature"), 1)] }] },
            "config_invalid", "Content.LairTypes[fixture:lair].Members");
    }

    [TestMethod]
    public void Large_equipment_commission_design_keeps_its_manufactured_good_reference()
    {
        var content = WorldFixture.Content;
        var hash = new DesignHash(new string('F', 64));
        var good = new GoodId.Voxel(new("fixture:equipment"));
        content = content with { Designs = [.. content.Designs,
            content.Designs[0] with { Design = hash, Kind = new DesignKind.LargeEquipment(good) }] };
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
            { StartConstraints = WorldFixture.Input.Configuration.StartConstraints with { RequiredAffordableCommissionDesign = hash } } };
        Failure(input, content, "config_invalid", "Configuration.StartConstraints.RequiredAffordableCommissionDesign");
        content = content with { Goods = [.. content.Goods, new(good, WorldFixture.Revision, new(1), 1, false, null)] };
        using var candidate = Prepare(input, content);
        Assert.IsTrue(candidate.References.Definitions.Any(d => d.Definition == new CoreDefinitionReference.Voxel(good.Definition)
            && d.Field == $"World.Designs[{hash.Value}].Kind.ManufacturedGood"));
        Failure(input, content with { MissingReferences = [new(new CoreDefinitionReference.Voxel(good.Definition), "known")] },
            "config_invalid", "Configuration.StartConstraints.RequiredAffordableCommissionDesign");
    }

    [TestMethod]
    public void Unsupported_npc_initialization_reports_the_specific_requested_field()
    {
        var input = WorldFixture.Input;
        var npc = input.Configuration.NpcInitialization;
        foreach (var (configuration, field) in new (NpcInitializationConfiguration, string)[]
        {
            (npc with { FillInputsAndLivingTargets = true }, nameof(npc.FillInputsAndLivingTargets)),
            (npc with { OutputReserveHours = 1 }, nameof(npc.OutputReserveHours)),
            (npc with { MaximumForSaleVehicles = 1 }, nameof(npc.MaximumForSaleVehicles))
        })
            Failure(input with { Configuration = input.Configuration with { NpcInitialization = configuration } },
                WorldFixture.Content, "config_invalid", "Configuration.NpcInitialization." + field);
    }

    [TestMethod]
    public void Economic_start_requests_are_validated_but_not_evaluated()
    {
        var input = WorldFixture.Input;
        using var candidate = Prepare(input with
        {
            Start = input.Start with { InitialMoney = new(0) },
            Configuration = input.Configuration with { StartConstraints = input.Configuration.StartConstraints with
                { FullyLoadedPurchaseRoundsPerTrain = int.MaxValue } }
        });
        Assert.AreEqual(1, candidate.View.World.Trains.Length);
    }

    [TestMethod]
    public void Station_requirement_and_reachable_settlement_count_exclude_the_root()
    {
        var input = WorldFixture.Input;
        var content = WorldFixture.Content;
        content = content with { SettlementTemplates = [content.SettlementTemplates[0] with { Plots = [new(1, PlotSize.Station, null)] }] };
        Failure(input, content, "constraint_unsatisfied", "Configuration.StartConstraints.RequireStation");
        using var withoutStation = Prepare(input with { Configuration = input.Configuration with
            { StartConstraints = input.Configuration.StartConstraints with { RequireStation = false } } }, content);
        Assert.AreEqual(0, withoutStation.View.World.Nodes[0].Platforms);
        Failure(input with { Configuration = input.Configuration with { StartConstraints = input.Configuration.StartConstraints with
            { MinimumReachableNonHostileSettlements = 1 } } }, WorldFixture.Content,
            "constraint_unsatisfied", "Configuration.StartConstraints.MinimumReachableNonHostileSettlements");
    }

    [TestMethod]
    public void Reachable_empty_nodes_are_connected_by_built_tracks()
    {
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with { WidthMeters = 100, SettlementRatio = .5,
            StartConstraints = input.Configuration.StartConstraints with { MinimumReachableEmptyNodes = 1 } } };
        using var candidate = Prepare(input);
        var world = candidate.View.World;
        var root = ((TrainPosition.Node)world.Trains[0].Position).Id;
        var reached = Reachable(world, root);
        Assert.IsTrue(world.Nodes.Any(n => reached.Contains(n.Id) && n.Occupant is NodeOccupant.Empty));
        Assert.IsTrue(world.Edges.Any(e => e.Tracks == 0));
    }

    [TestMethod]
    public void Adjacent_lairs_are_excluded_even_when_the_incident_edge_has_no_tracks()
    {
        var input = WorldFixture.Input;
        var content = WithLair();
        input = input with { Configuration = input.Configuration with { WidthMeters = 100, SettlementRatio = .5, LairRatio = .5,
            LairWeights = [new(new("fixture:lair"), 1)],
            StartConstraints = input.Configuration.StartConstraints with { ExcludeAdjacentLairs = false } } };
        using var allowed = Prepare(input, content);
        Assert.IsTrue(allowed.View.World.Edges.Any(e => e.Tracks == 0 &&
            (allowed.View.World.Nodes.Single(n => n.Id == e.Start).Occupant is NodeOccupant.Lair ||
             allowed.View.World.Nodes.Single(n => n.Id == e.End).Occupant is NodeOccupant.Lair)));
        var restricted = input with { Configuration = input.Configuration with { StartConstraints = input.Configuration.StartConstraints with
            { ExcludeAdjacentLairs = true } } };
        var result = CoreFactory.Create().PrepareNew(restricted, content, Options);
        if (result is CoreResult<ICoreSessionCandidate>.Success success)
        {
            using var candidate = success.Value;
            var world = candidate.View.World;
            var root = ((TrainPosition.Node)world.Trains[0].Position).Id;
            Assert.IsFalse(world.Edges.Where(e => e.Start == root || e.End == root).Any(e =>
                world.Nodes.Single(n => n.Id == (e.Start == root ? e.End : e.Start)).Occupant is NodeOccupant.Lair));
        }
        else Assert.AreEqual("constraint_unsatisfied", ((CoreResult<ICoreSessionCandidate>.Failure)result).Problems.Single().RelatedIdentities[0]);
    }

    [TestMethod]
    public void Resources_obey_probability_endpoints_fixed_reserves_and_renewal_limits()
    {
        var input = WorldFixture.Input;
        var resource = new ResourceGenerationEntry(WorldFixture.Material, [WorldFixture.Plain], 1, 7, 7, true, 2, 10, .5);
        using var present = Prepare(input with { Configuration = input.Configuration with { Resources = [resource] } });
        var value = present.View.World.Nodes[0].Resources.Single();
        Assert.AreEqual(new GoodId.Voxel(WorldFixture.Material), value.Good);
        Assert.AreEqual(7L, value.Quantity);
        Assert.AreEqual(new ResourceRenewal.Renewable(7, 2), value.Renewal);
        using var absent = Prepare(input with { Configuration = input.Configuration with { Resources = [resource with { Probability = 0 }] } });
        Assert.AreEqual(0, absent.View.World.Nodes[0].Resources.Length);
        using var finite = Prepare(input with { Configuration = input.Configuration with { Resources = [resource with { Renewable = false }] } });
        Assert.IsInstanceOfType<ResourceRenewal.NonRenewable>(finite.View.World.Nodes[0].Resources.Single().Renewal);
    }

    [TestMethod]
    public void Occupants_use_exact_quotas_and_lairs_do_not_spawn_creature_groups()
    {
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with { WidthMeters = 100, SettlementRatio = .5, LairRatio = .25,
            HostileSettlementRatio = .25, LairWeights = [new(new("fixture:lair"), 1)],
            StartConstraints = input.Configuration.StartConstraints with { ExcludeAdjacentLairs = false } } };
        using var candidate = Prepare(input, WithLair());
        var world = candidate.View.World;
        var count = world.Nodes.Length;
        var settlements = world.Nodes.Count(n => n.Occupant is NodeOccupant.Settlement);
        Assert.AreEqual((int)Math.Floor(count * .5), settlements);
        Assert.AreEqual((int)Math.Floor(count * .25), world.Nodes.Count(n => n.Occupant is NodeOccupant.Lair));
        Assert.AreEqual((int)Math.Floor(settlements * .25), world.Nodes.Count(n => n.Occupant is NodeOccupant.Settlement { Relation: SettlementRelation.Hostile }));
        Assert.AreEqual(0, world.CreatureGroups.Length);
        Assert.IsTrue(candidate.References.Templates.All(t => t.SourceTemplate == WorldFixture.Content.SettlementTemplates[0].Id));
    }

    [TestMethod]
    public void Node_retries_do_not_consume_public_identifiers_and_budgets_include_all_attempts()
    {
        var input = WorldFixture.Input with { WorldSeed = 6 };
        input = input with { Configuration = input.Configuration with { CandidateProbability = .5 } };
        // Seed 6 presence samples are .9623211636979359 then .318845117329815.
        using var candidate = Prepare(input);
        Assert.AreEqual(new NodeId(1), candidate.View.World.Nodes.Single().Id);
        var exhausted = Failure(input with { Configuration = input.Configuration with { CandidateProbability = 0 } },
            WorldFixture.Content, "constraint_unsatisfied", "World.Nodes");
        CollectionAssert.AreEqual(new[] { "constraint_unsatisfied", "nodes", "cell:0,0", "1", "4", "5" }, exhausted.RelatedIdentities.ToArray());
        var candidates = Failure(WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
            { WidthMeters = 20, Budget = new(1, 1, 1, 1_000_000) } }, WorldFixture.Content, "budget_exhausted", "Configuration.Budget.MaximumCandidates");
        Assert.AreEqual("nodes", candidates.RelatedIdentities[1]);
        Assert.AreEqual("1", candidates.RelatedIdentities[4]);
        var work = Failure(WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
            { Budget = new(1, 1, 100_000, 1) } }, WorldFixture.Content, "budget_exhausted", "Configuration.Budget.MaximumSearchWork");
        Assert.AreEqual("nodes", work.RelatedIdentities[1]);
        Assert.AreEqual("1", work.RelatedIdentities[5]);
    }

    [TestMethod]
    public void Failed_preparation_preserves_the_activated_session()
    {
        var factory = CoreFactory.Create();
        using var candidate = ((CoreResult<ICoreSessionCandidate>.Success)factory.PrepareNew(WorldFixture.Input, WorldFixture.Content, Options)).Value;
        using var session = candidate.Activate();
        var before = session.Read();
        var result = factory.PrepareNew(WorldFixture.Input with { GeneratorVersion = "unsupported" }, WorldFixture.Content, Options);
        Assert.IsInstanceOfType<CoreResult<ICoreSessionCandidate>.Failure>(result);
        var after = session.Read();
        Assert.AreEqual(before.World.WorldSeed, after.World.WorldSeed);
        Assert.AreEqual(before.World.RailwayRule, after.World.RailwayRule);
        Assert.AreEqual(before.World.WidthMeters, after.World.WidthMeters);
        Assert.AreEqual(before.World.HeightMeters, after.World.HeightMeters);
        CollectionAssert.AreEqual(before.World.Nodes.ToArray(), after.World.Nodes.ToArray());
        CollectionAssert.AreEqual(before.World.Edges.ToArray(), after.World.Edges.ToArray());
        CollectionAssert.AreEqual(before.World.Trains.ToArray(), after.World.Trains.ToArray());
        Assert.AreEqual(before.Position, after.Position);
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(before.InputIdentity, after.InputIdentity);
    }

    [TestMethod]
    public void Types_without_templates_are_excluded_and_population_uses_the_selected_closed_range()
    {
        var content = WorldFixture.Content;
        var eligible = content.SettlementTypes[0] with { MinimumInitialPopulation = 17, MaximumInitialPopulation = 17 };
        content = content with { SettlementTypes = [eligible,
            eligible with { Id = new("fixture:untemplated"), Weight = int.MaxValue }] };
        using var candidate = Prepare(WorldFixture.Input, content);
        using var session = (CoreSession)candidate.Activate();
        var settlement = session.State.World!.Settlements.Single();
        Assert.AreEqual(eligible.Id, settlement.Type);
        Assert.AreEqual(17L, settlement.Population);
    }

    [TestMethod]
    public void Unordered_inputs_catalogs_and_nested_design_template_leaves_have_identical_results()
    {
        var input = WorldFixture.Input;
        var content = WorldFixture.Content;
        var other = new VoxelDefinitionId("fixture:other");
        content = content with
        {
            Goods = [content.Goods[0], content.Goods[0] with { Good = new GoodId.Voxel(other) }],
            Designs = content.Designs.Select(d => d with { StructuralGoods = [new(new GoodId.Voxel(WorldFixture.Material), 1), new(new GoodId.Voxel(other), 2)] }).ToImmutableArray(),
            SettlementTemplates = [content.SettlementTemplates[0] with
            {
                Plots = [new(1, PlotSize.Station, WorldFixture.Station), new(2, PlotSize.Station, null)],
                SpecialInitialInventory = [new(new GoodId.Voxel(WorldFixture.Material), 3), new(new GoodId.Voxel(other), 4)]
            }]
        };
        input = input with { Configuration = input.Configuration with
        {
            ElevationNoise = [new(10, 0), new(20, 0)],
            TerrainBands = [input.Configuration.TerrainBands[0], input.Configuration.TerrainBands[0] with { MaximumRelief = 2 }],
            Resources = [new(WorldFixture.Material, [WorldFixture.Plain, new("fixture:unused")], 1, 5, 5, false, 0, 1, 0),
                new(other, [WorldFixture.Plain], 0, 5, 5, false, 0, 1, 0)]
        } };
        var permutedInput = input with { Configuration = input.Configuration with
        {
            ElevationNoise = Reverse(input.Configuration.ElevationNoise), TerrainBands = Reverse(input.Configuration.TerrainBands),
            Resources = Reverse(input.Configuration.Resources).Select(r => r with { AllowedTerrainTypes = Reverse(r.AllowedTerrainTypes) }).ToImmutableArray()
        } };
        var permutedContent = content with
        {
            Goods = Reverse(content.Goods), Designs = Reverse(content.Designs).Select(d => d with
            {
                StructuralGoods = Reverse(d.StructuralGoods), Parts = Reverse(d.Parts).Select(p => p with { Capabilities = Reverse(p.Capabilities) }).ToImmutableArray()
            }).ToImmutableArray(),
            SettlementTemplates = Reverse(content.SettlementTemplates).Select(t => t with
                { Plots = Reverse(t.Plots), SpecialInitialInventory = Reverse(t.SpecialInitialInventory) }).ToImmutableArray()
        };
        using var first = Prepare(input, content);
        using var second = Prepare(permutedInput, permutedContent);
        using var firstSession = (CoreSession)first.Activate();
        using var secondSession = (CoreSession)second.Activate();
        Assert.AreEqual(JsonSerializer.Serialize(firstSession.State.World), JsonSerializer.Serialize(secondSession.State.World));
        var left = firstSession.State.World!;
        var right = secondSession.State.World!;
        CollectionAssert.AreEqual(left.Nodes.Select(n => n.Occupant).ToArray(), right.Nodes.Select(n => n.Occupant).ToArray());
        for (var i = 0; i < left.Designs.Length; i++)
        {
            var firstDesign = left.Designs[i];
            var secondDesign = right.Designs[i];
            Assert.AreEqual(firstDesign.Kind.GetType(), secondDesign.Kind.GetType());
            CollectionAssert.AreEqual(firstDesign.StructuralGoods.ToArray(), secondDesign.StructuralGoods.ToArray());
            for (var p = 0; p < firstDesign.Parts.Length; p++)
            {
                Assert.AreEqual(firstDesign.Parts[p].Definition, secondDesign.Parts[p].Definition);
                Assert.AreEqual(firstDesign.Parts[p].RepairGood, secondDesign.Parts[p].RepairGood);
                CollectionAssert.AreEqual(firstDesign.Parts[p].Capabilities.ToArray(), secondDesign.Parts[p].Capabilities.ToArray());
            }
        }
        for (var i = 0; i < left.Settlements.Length; i++)
            CollectionAssert.AreEqual(left.Settlements[i].Template.SpecialInitialInventory.ToArray(),
                right.Settlements[i].Template.SpecialInitialInventory.ToArray());
    }

    [TestMethod]
    public void Train_and_vehicle_input_order_assigns_the_corresponding_instance_numbers()
    {
        var content = WorldFixture.Content;
        var other = new DesignHash(new string('F', 64));
        content = content with { Designs = content.Designs.Add(content.Designs[0] with { Design = other }) };
        var input = WorldFixture.Input with { Start = new(new(0), [new([WorldFixture.Vehicle, other], []), new([other], [])]) };
        using var first = Prepare(input, content);
        using var vehicleReordered = Prepare(input with { Start = input.Start with
            { Trains = [input.Start.Trains[0] with { Vehicles = [other, WorldFixture.Vehicle] }, input.Start.Trains[1]] } }, content);
        var swapped = input with { Start = input.Start with { Trains = [input.Start.Trains[1], input.Start.Trains[0] with { Vehicles = [other, WorldFixture.Vehicle] }] } };
        using var second = Prepare(swapped, content);
        Assert.AreEqual(WorldFixture.Vehicle, ((Lookup<InstanceView>.Found)first.View.FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design);
        Assert.AreEqual(other, ((Lookup<InstanceView>.Found)vehicleReordered.View.FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design);
        Assert.AreEqual(WorldFixture.Vehicle, ((Lookup<InstanceView>.Found)vehicleReordered.View.FindInstance(new InstanceId.Vehicle(new(2)))).Value.Design);
        Assert.AreEqual(other, ((Lookup<InstanceView>.Found)second.View.FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design);
        Assert.AreEqual(WorldFixture.Vehicle, ((Lookup<InstanceView>.Found)second.View.FindInstance(new InstanceId.Vehicle(new(3)))).Value.Design);
    }

    private static ImmutableArray<T> Reverse<T>(ImmutableArray<T> values) => values.Reverse().ToImmutableArray();

    private static ICoreSessionCandidate Prepare(WorldGenerationInput input, CoreContentProjection? content = null)
    {
        var result = CoreFactory.Create().PrepareNew(input, content ?? WorldFixture.Content, Options);
        Assert.IsInstanceOfType<CoreResult<ICoreSessionCandidate>.Success>(result);
        return ((CoreResult<ICoreSessionCandidate>.Success)result).Value;
    }

    private static CoreProblem Failure(WorldGenerationInput input, CoreContentProjection content, string category, string field)
    {
        var result = CoreFactory.Create().PrepareNew(input, content, Options);
        Assert.IsInstanceOfType<CoreResult<ICoreSessionCandidate>.Failure>(result);
        var problem = ((CoreResult<ICoreSessionCandidate>.Failure)result).Problems.Single();
        Assert.AreEqual("generation_failed", problem.Code);
        Assert.AreEqual(category, problem.RelatedIdentities[0]);
        Assert.AreEqual(field, problem.Field);
        return problem;
    }

    private static CoreContentProjection WithLair() => WorldFixture.Content with
    {
        LairTypes = [new(new("fixture:lair"), WorldFixture.Revision, 1, [], 1, 1, 1, 0, 0, 1, new(0))]
    };

    private static HashSet<NodeId> Reachable(WorldSummary world, NodeId root)
    {
        var reached = new HashSet<NodeId> { root };
        var pending = new Queue<NodeId>(reached);
        while (pending.TryDequeue(out var node))
            foreach (var edge in world.Edges.Where(e => e.Tracks > 0))
            {
                if (edge.Start == node && reached.Add(edge.End)) pending.Enqueue(edge.End);
                if (edge.End == node && reached.Add(edge.Start)) pending.Enqueue(edge.Start);
            }
        return reached;
    }
}
