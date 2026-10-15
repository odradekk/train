using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldGenerationTests
{
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
    [DataRow("default")]
    [DataRow("null_element")]
    [DataRow("null_extract")]
    public void Read_process_goods_collections_reject_uninitialized_or_null_values(string malformed)
    {
        var good = new GoodsQuantity(new GoodId.Voxel(WorldFixture.Material), 1);
        ProcessDetails details = malformed switch
        {
            "default" => new ProcessDetails.Production(new("fixture:process"), default, [good]),
            "null_element" => new ProcessDetails.Recovery(new("fixture:process"), [good], [null!]),
            _ => new ProcessDetails.Extraction(new("fixture:process"), null!)
        };
        var content = WorldFixture.Content with { Processes = [new(details, WorldFixture.Revision, 1, 0, 0, FacilityTag.Assembly, [])] };
        Assert.ThrowsExactly<ArgumentException>(() => CoreFactory.Create().PrepareNew(WorldFixture.Input, content, Options));
    }

    [TestMethod]
    public void Missing_projection_references_return_one_configuration_failure()
    {
        var content = WorldFixture.Content;
        foreach (var (projection, field) in new (CoreContentProjection, string)[]
        {
            (content with { PopulationRules = [] }, "Configuration.Rules.Population"),
            (content with { Designs = [content.Designs[1]] }, "Configuration.StartConstraints.RequiredAffordableCommissionDesign"),
            (content with { Goods = content.Goods.RemoveAt(0) }, "Content.RailwayRules[fixture:railway].RailGood"),
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
            (content with { PopulationRules = [content.PopulationRules[0] with
                { ConsumptionGoods = content.PopulationRules[0].ConsumptionGoods.SetItem(0,
                    content.PopulationRules[0].ConsumptionGoods[0] with { Good = absent }) }] },
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
    public void Commission_and_for_sale_designs_must_be_vehicles()
    {
        var content = WorldFixture.Content;
        var hash = new DesignHash(new string('F', 64));
        var good = new GoodId.Voxel(new("fixture:equipment"));
        content = content with
        {
            Designs = [.. content.Designs, content.Designs[0] with { Design = hash, Kind = new DesignKind.LargeEquipment(good) }],
            Goods = [.. content.Goods, new(good, WorldFixture.Revision, new(1), 1, false, null)]
        };
        foreach (var nonVehicle in new[] { WorldFixture.Station, hash })
        {
            var configuration = WorldFixture.Input.Configuration;
            Failure(WorldFixture.Input with { Configuration = configuration with
                { StartConstraints = configuration.StartConstraints with { RequiredAffordableCommissionDesign = nonVehicle } } },
                content, "config_invalid", "Configuration.StartConstraints.RequiredAffordableCommissionDesign");
            Failure(WorldFixture.Input with { Configuration = configuration with
                { NpcInitialization = configuration.NpcInitialization with { ForSaleVehicleCandidates = [nonVehicle] } } },
                content, "config_invalid", "Configuration.NpcInitialization.ForSaleVehicleCandidates");
        }
    }

    [TestMethod]
    public void Prebuilt_and_founding_designs_must_be_buildings()
    {
        var template = WorldFixture.Content.SettlementTemplates[0];
        foreach (var changed in new[]
        {
            template with { Plots = [template.Plots[0] with { PrebuiltDesign = WorldFixture.Vehicle }] },
            template with { FoundingBuildings = [new(1, WorldFixture.Vehicle)] }
        })
            Failure(WorldFixture.Input, WorldFixture.Content with { SettlementTemplates = [changed] }, "config_invalid",
                changed.FoundingBuildings.IsEmpty ? "Content.SettlementTemplates[fixture:town-template].Plots[1]"
                    : "Content.SettlementTemplates[fixture:town-template].FoundingBuildings");
    }

    [TestMethod]
    [DataRow(PlotSize.Small, "111100")]
    [DataRow(PlotSize.Medium, "011100")]
    [DataRow(PlotSize.Large, "001100")]
    [DataRow(PlotSize.Giant, "000100")]
    [DataRow(PlotSize.SmallStation, "000011")]
    [DataRow(PlotSize.LargeStation, "000001")]
    public void Prebuilt_buildings_follow_the_six_by_six_plot_fit_table(PlotSize buildingSize, string expected)
    {
        PlotSize[] columns = [PlotSize.Small, PlotSize.Medium, PlotSize.Large, PlotSize.Giant, PlotSize.SmallStation, PlotSize.LargeStation];
        for (var index = 0; index < columns.Length; index++)
        {
            var plotSize = columns[index];
            var fits = expected[index] == '1';
            var content = MultiContent;
            var template = content.SettlementTemplates[0];
            var design = content.Designs.Single(d => d.Design == FactoryBuilding);
            content = content with
            {
                Designs = content.Designs.Replace(design, design with
                {
                    Kind = new DesignKind.Building(buildingSize,
                        buildingSize is PlotSize.SmallStation or PlotSize.LargeStation ? BuildingKind.Station : BuildingKind.Production, [])
                }),
                SettlementTemplates = [template with
                    { Plots = template.Plots.SetItem(1, template.Plots[1] with { Size = plotSize }) }]
            };
            if (!fits)
            {
                Failure(WorldFixture.Input, content, "config_invalid",
                    $"Content.SettlementTemplates[{template.Id.Value}].Plots[2]");
                continue;
            }
            using var candidate = Prepare(WorldFixture.Input, content);
            var building = ((Lookup<InstanceView>.Found)candidate.View.FindInstance(new InstanceId.Building(new(2)))).Value;
            Assert.AreEqual(FactoryBuilding, building.Design);
            Assert.AreEqual(new InstanceDetails.Building(new(2), plotSize), building.Details);
        }
    }

    [TestMethod]
    [DataRow(3, 3, "MinimumInitialTracks")]
    [DataRow(0, 0, "MaximumInitialTracks")]
    public void Initial_tracks_without_a_supported_intersection_fail_before_generation(int minimum, int maximum, string field)
    {
        var input = WorldFixture.Input;
        var problem = Failure(input with { Configuration = input.Configuration with
            { InitialRailway = new(3, minimum, maximum, 0) } }, WorldFixture.Content,
            "config_invalid", "Configuration.InitialRailway." + field);
        Assert.AreEqual("configuration", problem.RelatedIdentities[1]);
        Assert.AreEqual("0", problem.RelatedIdentities[4]);
        Assert.AreEqual("0", problem.RelatedIdentities[5]);
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
        content = content with { SettlementTemplates = [content.SettlementTemplates[0] with { Plots = [new(1, PlotSize.SmallStation, null)] }] };
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
        Assert.AreEqual("nodes", exhausted.RelatedIdentities[1]);
        Assert.AreEqual("", exhausted.RelatedIdentities[2]);
        Assert.AreEqual("1", exhausted.RelatedIdentities[3]);
        Assert.AreEqual("4", exhausted.RelatedIdentities[4]);
        var candidates = Failure(WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
            { WidthMeters = 20, Budget = new(1, 1, 1, 1_000_000) } }, WorldFixture.Content, "budget_exhausted", "Configuration.Budget.MaximumCandidates");
        Assert.AreEqual("nodes", candidates.RelatedIdentities[1]);
        Assert.AreEqual("1", candidates.RelatedIdentities[4]);
        var work = Failure(WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
            { Budget = new(1, 1, 100_000, 1) } }, WorldFixture.Content, "budget_exhausted", "Configuration.Budget.MaximumSearchWork");
        Assert.AreEqual("nodes", work.RelatedIdentities[1]);

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
