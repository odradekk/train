using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldCaptureTests
{
    [TestMethod]
    public void Published_specifications_round_trip_and_restore_without_changing_the_previous_capture()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        var previous = Capture(session);
        var previousText = previous.Encode();
        var content = WorldFixture.Content with
        {
            Identity = new(new string('A', 64)),
            Designs = WorldFixture.Content.Designs.Select(d => d with
            {
                MaxStructureDurability = 40, MassTons = 2,
                Parts = d.Parts.Select(p => p with { MaxDurability = 30,
                    Capabilities = d.Design == Station ? [] : p.Capabilities }).ToImmutableArray()
            }).ToImmutableArray()
        };
        var publication = (SubmissionResult.Completed)session.Submit(
            new CoreCommand.PublishContent(content), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(publication.Receipt.Decision);
        var captured = Capture(session);
        var decoded = Success(CoreFactory.Create().DecodeState(captured.Encode()));
        Assert.AreEqual(captured.Encode(), decoded.Encode());
        Assert.AreEqual(captured.Checkpoint, decoded.Checkpoint);
        using var restoredCandidate = Success(CoreFactory.Create().PrepareRestore(decoded, content, Options));
        Assert.IsTrue(restoredCandidate.MissingReferences.IsEmpty);
        Assert.IsTrue(restoredCandidate.AdaptationReport.IsEmpty);
        using var restored = restoredCandidate.Activate();
        AssertQueries(session.Read(), restored.Read());
        Assert.AreEqual(captured.Encode(), Capture(restored).Encode());
        Assert.AreEqual(captured.Checkpoint, Capture(restored).Checkpoint);
        Assert.AreEqual(previousText, previous.Encode());

        using var payload = JsonDocument.Parse(captured.Encode());
        var world = payload.RootElement.GetProperty("world");
        foreach (var design in world.GetProperty("designs").EnumerateArray())
        {
            Assert.AreEqual(40L, design.GetProperty("max_structure_durability").GetInt64());
            Assert.AreEqual(2d, design.GetProperty("mass_tons").GetDouble());
            Assert.AreEqual(30L, design.GetProperty("parts")[0].GetProperty("max_durability").GetInt64());
        }
        Assert.IsTrue(session.Read().World.Nodes.All(n => n.Platforms == 0));
        var vehicle = ((Lookup<InstanceView>.Found)restored.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(new DurabilityView(40, 40), vehicle.Structure);
        Assert.AreEqual(new DurabilityView(30, 30), vehicle.Parts[0].Durability);
        using var before = JsonDocument.Parse(previousText);
        Assert.AreEqual(before.RootElement.GetProperty("world").GetProperty("instances").GetRawText(),
            world.GetProperty("instances").GetRawText());
        Assert.AreEqual(before.RootElement.GetProperty("world").GetProperty("settlements").GetRawText(),
            world.GetProperty("settlements").GetRawText());
    }

    [TestMethod]
    public void Publicly_generated_session_runs_the_shared_capture_contract()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        var references = MinimalReferences();
        CaptureScenario.AssertContract(session, WorldFixture.Content.Identity, references, references);
    }

    [TestMethod]
    public void Small_building_on_medium_plot_round_trips_with_the_plot_size()
    {
        var content = MultiContent;
        var template = content.SettlementTemplates[0];
        content = content with { SettlementTemplates = [template with
            { Plots = template.Plots.SetItem(1, template.Plots[1] with { Size = PlotSize.Medium }) }] };
        using var candidate = Prepare(content: content);
        using var original = candidate.Activate();
        var captured = Capture(original);
        var decoded = Success(CoreFactory.Create().DecodeState(captured.Encode()));
        Assert.AreEqual(captured.Encode(), decoded.Encode());
        Assert.AreEqual(captured.Checkpoint, decoded.Checkpoint);
        using var restoredCandidate = Success(CoreFactory.Create().PrepareRestore(decoded, content, Options));
        using var restored = restoredCandidate.Activate();
        AssertQueries(original.Read(), restored.Read());
        var building = ((Lookup<InstanceView>.Found)restored.Read().FindInstance(new InstanceId.Building(new(2)))).Value;
        Assert.AreEqual(FactoryBuilding, building.Design);
        Assert.AreEqual(new InstanceDetails.Building(new(2), PlotSize.Medium), building.Details);
        var recaptured = Capture(restored);
        Assert.AreEqual(captured.Encode(), recaptured.Encode());
        Assert.AreEqual(captured.Checkpoint, recaptured.Checkpoint);
    }

    [TestMethod]
    public void World_round_trip_retains_queries_authoritative_state_and_template_business_data()
    {
        var input = WorldFixture.Input;
        var content = WorldFixture.Content;
        var good = new GoodId.Voxel(WorldFixture.Material);
        var process = new ProcessRef.Production(new("fixture:process"));
        content = content with
        {
            Processes = [new(new ProcessDetails.Production(process.Definition, [new(good, 1)], [new(good, 2)]),
                WorldFixture.Revision, 3, 0, 0, FacilityTag.Assembly, [WorldFixture.Plain])],
            SettlementTemplates = [content.SettlementTemplates[0] with
            {
                FoundingBuildings = [new(1, WorldFixture.Station)], SpecialInitialInventory = [new(good, 13)],
                ProductionPlans = [new(1, CapabilityKind.Production, process, false, 7)]
            }]
        };
        input = input with
        {
            Configuration = input.Configuration with { WidthMeters = 100,
                Resources = [new(WorldFixture.Material, [WorldFixture.Plain], 1, 7, 7, true, 2, 10, .5)] },
            Start = new(new(1234), [new([WorldFixture.Vehicle], [new(good, 11)])])
        };
        using var candidate = Prepare(input, content);
        using var original = candidate.Activate();
        var captured = Capture(original);
        var decoded = Success(CoreFactory.Create().DecodeState(captured.Encode()));
        Assert.AreEqual(captured.Encode(), decoded.Encode());
        Assert.AreEqual(captured.Checkpoint, decoded.Checkpoint);
        using var restoredCandidate = Success(CoreFactory.Create().PrepareRestore(decoded, content, WorldFixture.Options));
        Assert.AreEqual(0, restoredCandidate.MissingReferences.Length);
        using var restored = restoredCandidate.Activate();
        AssertQueries(original.Read(), restored.Read());
        var recaptured = Capture(restored);
        Assert.AreEqual(captured.Encode(), recaptured.Encode());
        Assert.AreEqual(captured.Checkpoint, recaptured.Checkpoint);

        using var payload = JsonDocument.Parse(recaptured.Encode());
        var world = payload.RootElement.GetProperty("world");
        Assert.AreEqual(1234L, world.GetProperty("money").GetInt64());
        Assert.AreEqual(42UL, world.GetProperty("source").GetProperty("world_seed").GetUInt64());
        Assert.AreEqual(2, world.GetProperty("designs").GetArrayLength());
        foreach (var settlement in world.GetProperty("settlements").EnumerateArray())
        {
            Assert.AreEqual(10L, settlement.GetProperty("population").GetInt64());
            var template = settlement.GetProperty("template");
            Assert.AreEqual(13L, template.GetProperty("special_initial_inventory")[0].GetProperty("quantity").GetInt64());
            Assert.AreEqual(1, template.GetProperty("founding_buildings").GetArrayLength());
            Assert.IsFalse(template.GetProperty("production_plans")[0].GetProperty("enabled").GetBoolean());
            Assert.AreEqual(7, template.GetProperty("production_plans")[0].GetProperty("priority").GetInt32());
        }
        Assert.IsTrue(world.GetProperty("inventories").EnumerateArray().Any(i =>
            i.GetProperty("goods").EnumerateArray().Any(g => g.GetProperty("quantity").GetInt64() == 11)));
        Assert.AreEqual((ulong)original.Read().World.Nodes.Length + 1, world.GetProperty("next").GetProperty("node").GetUInt64());
        Assert.AreEqual(2UL, world.GetProperty("next").GetProperty("vehicle").GetUInt64());
    }

    [TestMethod]
    [DataRow(9_007_199_254_740_993L)]
    [DataRow(long.MaxValue)]
    public void Money_is_encoded_as_numbers_and_round_trips_without_losing_integer_precision(long amount)
    {
        var input = WorldFixture.Input;
        input = input with { Start = input.Start with { InitialMoney = new(amount) } };
        var content = WorldFixture.Content;
        content = content with { DesignGoods = [content.Goods[0] with { BaseValue = new(amount) }] };
        using var candidate = Prepare(input, content);
        using var session = candidate.Activate();
        using var payload = JsonDocument.Parse(Capture(session).Encode());
        var world = payload.RootElement.GetProperty("world");
        Assert.AreEqual(JsonValueKind.Number, world.GetProperty("money").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, world.GetProperty("design_goods")[0].GetProperty("base_value").ValueKind);
        var decoded = WorldJson.Read(world);
        Assert.AreEqual(new Money(amount), decoded.Money);
        Assert.AreEqual(new Money(amount), decoded.Source.Start.InitialMoney);
        Assert.AreEqual(new Money(amount), decoded.DesignGoods.Single().BaseValue);
    }

    [TestMethod]
    [DataRow("\"1000\"")]
    [DataRow("{\"value\":1000}")]
    [DataRow("9223372036854775808")]
    [DataRow("-9223372036854775809")]
    public void Invalid_money_encoding_is_reported_as_a_world_format_error(string invalid)
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        payload["world"]!["money"] = JsonNode.Parse(invalid);
        using var document = JsonDocument.Parse(payload.ToJsonString());
        var error = Assert.ThrowsExactly<CoreStateFormatException>(() =>
            WorldJson.Read(document.RootElement.GetProperty("world")));
        Assert.AreEqual("/world", error.Field);
    }

    [TestMethod]
    public void Restoring_a_saved_world_does_not_require_its_generator_to_remain_supported()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        var payload = JsonNode.Parse(Capture(session).Encode())!;
        payload["world"]!["source"]!["generator_version"] = "retired-world-generator";
        var saved = Success(CoreFactory.Create().DecodeState(payload.ToJsonString()));
        using var restoredCandidate = Success(CoreFactory.Create().PrepareRestore(saved, WorldFixture.Content, WorldFixture.Options));
        using var restored = restoredCandidate.Activate();
        AssertQueries(session.Read(), restored.Read());
        using var recaptured = JsonDocument.Parse(Capture(restored).Encode());
        Assert.AreEqual("retired-world-generator", recaptured.RootElement.GetProperty("world").GetProperty("source").GetProperty("generator_version").GetString());
    }

    [TestMethod]
    public void Checkpoint_covers_world_seed_generator_resource_configuration_and_next_numbers()
    {
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with
            { Resources = [new(WorldFixture.Material, [WorldFixture.Plain], 1, 7, 7, true, 2, 10, .5)] } };
        using var candidate = Prepare(input);
        using var session = candidate.Activate();
        var capture = Capture(session);
        foreach (var mutate in new Action<JsonNode>[]
        {
            p => p["world"]!["source"]!["world_seed"] = 43,
            p => p["world"]!["source"]!["generator_version"] = "retired-world-generator",
            p => p["world"]!["source"]!["configuration"]!["resources"]![0]!["regeneration_interval_seconds"] = 20,
            p => p["world"]!["next"]!["vehicle"] = 3
        })
        {
            var payload = JsonNode.Parse(capture.Encode())!;
            mutate(payload);
            var changed = Success(CoreFactory.Create().DecodeState(payload.ToJsonString()));
            Assert.AreNotEqual(capture.Checkpoint.Sha256, changed.Checkpoint.Sha256);
        }
    }

    [TestMethod]
    public void Missing_current_projection_is_reported_without_discarding_saved_world_state()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        var captured = Capture(session);
        var saved = Success(CoreFactory.Create().DecodeState(captured.Encode()));
        var content = WorldFixture.Content with { Goods = [], MissingReferences =
            [new(new CoreDefinitionReference.Voxel(WorldFixture.Material), "Content.Goods")] };
        using var restoredCandidate = Success(CoreFactory.Create().PrepareRestore(saved, content, WorldFixture.Options));
        Assert.IsTrue(restoredCandidate.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Voxel(WorldFixture.Material)
            && m.FieldPath.Contains("World.Designs", StringComparison.Ordinal)));
        Assert.AreEqual(0, restoredCandidate.AdaptationReport.Length);
        using var restored = restoredCandidate.Activate();
        AssertQueries(session.Read(), restored.Read());
        using var before = JsonDocument.Parse(captured.Encode());
        using var after = JsonDocument.Parse(Capture(restored).Encode());
        Assert.AreEqual(before.RootElement.GetProperty("world").GetRawText(), after.RootElement.GetProperty("world").GetRawText());
    }

    [TestMethod]
    public void Missing_parts_in_current_design_projection_are_reported_and_capture_round_trips()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        var saved = Success(CoreFactory.Create().DecodeState(Capture(session).Encode()));
        var content = WorldFixture.Content;
        content = content with { Designs = content.Designs.Select(d => d.Design == WorldFixture.Vehicle
            ? d with { Parts = d.Parts.Select(p => p with { Missing = true }).ToImmutableArray() } : d).ToImmutableArray() };
        using var restoredCandidate = Success(CoreFactory.Create().PrepareRestore(saved, content, WorldFixture.Options));
        Assert.IsTrue(restoredCandidate.MissingReferences.Any(m => m.FieldPath ==
            $"World.Designs[{WorldFixture.Vehicle.Value}].Parts[0].Definition"));
        using var restored = restoredCandidate.Activate();
        var captured = Capture(restored);
        Assert.AreEqual(captured.Encode(), Success(CoreFactory.Create().DecodeState(captured.Encode())).Encode());
    }

    private static CoreReferenceManifest MinimalReferences()
    {
        var definitions = new List<RequiredDefinition>
        {
            new(new CoreDefinitionReference.Rule(WorldFixture.Rules.Combat), null, "Configuration.Rules.Combat", false),
            new(new CoreDefinitionReference.Rule(WorldFixture.Rules.Construction), null, "Configuration.Rules.Construction", false),
            new(new CoreDefinitionReference.Rule(WorldFixture.Rules.Market), null, "Configuration.Rules.Market", false),
            new(new CoreDefinitionReference.Rule(WorldFixture.Rules.Population), null, "Configuration.Rules.Population", false),
            new(new CoreDefinitionReference.Rule(WorldFixture.Rules.Railway), null, "Configuration.Rules.Railway", false),
            new(new CoreDefinitionReference.SettlementType(new("fixture:town")), null, "World.Settlements[1].Type", false),
            new(new CoreDefinitionReference.Terrain(WorldFixture.Plain), null, "Configuration.TerrainBands[fixture:plain].Terrain", false),
            new(new CoreDefinitionReference.Terrain(WorldFixture.Plain), null, "Content.SettlementTypes[fixture:town].PreferredTerrain", false),
            new(new CoreDefinitionReference.Terrain(WorldFixture.Plain), null, "World.Nodes[1].Terrain", false),
            new(new CoreDefinitionReference.Voxel(WorldFixture.Material), null, "Content.RailwayRules[fixture:railway].RailGood", false)
        };
        foreach (var hash in new[] { WorldFixture.Vehicle, WorldFixture.Station })
            foreach (var field in new[] { "Definition", "RepairGood" })
                definitions.Add(new(new CoreDefinitionReference.Voxel(WorldFixture.Material), null,
                    $"World.Designs[{hash.Value}].Parts[0].{field}", false));
        return new([WorldFixture.Vehicle, WorldFixture.Station],
            [new(new(1), new("fixture:town-template"), [new(1, PlotSize.Station)])], definitions.ToImmutableArray(), [WorldFixture.Content.Identity]);
    }

    private static void AssertQueries(ICoreReadView expected, ICoreReadView actual)
    {
        Assert.AreEqual(JsonSerializer.Serialize(expected.World), JsonSerializer.Serialize(actual.World));
        foreach (var node in expected.World.Nodes)
        {
            EqualLookup(expected.FindNode(node.Id), actual.FindNode(node.Id));
            Assert.AreEqual(node.Occupant, actual.World.Nodes.Single(n => n.Id == node.Id).Occupant);
            var location = new WorldLocation.Node(node.Id);
            var expectedLocation = ((Lookup<LocationView>.Found)expected.FindLocation(location)).Value;
            var actualLocation = ((Lookup<LocationView>.Found)actual.FindLocation(location)).Value;
            CollectionAssert.AreEqual(expectedLocation.Instances.ToArray(), actualLocation.Instances.ToArray());
            CollectionAssert.AreEqual(expectedLocation.Trains.ToArray(), actualLocation.Trains.ToArray());
            foreach (var instance in expectedLocation.Instances)
            {
                var before = ((Lookup<InstanceView>.Found)expected.FindInstance(instance)).Value;
                var after = ((Lookup<InstanceView>.Found)actual.FindInstance(instance)).Value;
                Assert.AreEqual(before.Location, after.Location);
                Assert.AreEqual(before.Design, after.Design);
                Assert.AreEqual(before.Details, after.Details);
                Assert.AreEqual(before.Structure, after.Structure);
                CollectionAssert.AreEqual(before.Parts.ToArray(), after.Parts.ToArray());
            }
        }
        foreach (var edge in expected.World.Edges) EqualLookup(expected.FindEdge(edge.Id), actual.FindEdge(edge.Id));
        Assert.IsInstanceOfType<Lookup<NodeView>.Missing>(actual.FindNode(new(ulong.MaxValue)));
    }

    private static void EqualLookup<T>(Lookup<T> expected, Lookup<T> actual) where T : notnull
    {
        Assert.IsInstanceOfType<Lookup<T>.Found>(actual);
        Assert.AreEqual(JsonSerializer.Serialize(((Lookup<T>.Found)expected).Value),
            JsonSerializer.Serialize(((Lookup<T>.Found)actual).Value));
    }

    private static ICoreCapture Capture(ICoreSession session) => Success(session.Capture(CapturePurpose.Save));
}
