using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldPublicationTests
{
    [TestMethod]
    public void Saved_large_equipment_cannot_change_its_manufactured_good_at_the_same_hash()
    {
        var content = MultiContent;
        var equipment = content.Designs[0] with { Design = new(new string('8', 64)),
            Kind = new DesignKind.LargeEquipment(new GoodId.Voxel(Material)) };
        content = content with { Designs = content.Designs.Add(equipment) };
        var state = State(content: content);
        using var session = new CoreSession(state with { World = state.World with { Designs = state.World.Designs.Add(equipment) } });
        var proposed = Next(content, 'A') with { Designs = content.Designs.Select(d => d.Design == equipment.Design
            ? d with { Kind = new DesignKind.LargeEquipment(new GoodId.Voxel(Ore)) } : d).ToImmutableArray() };
        var problems = Rejected(Publish(session, proposed));
        Assert.IsTrue(problems.Any(p => p.Code == "invalid_state"
            && p.Field.StartsWith($"World.Designs[{equipment.Design.Value}].Kind", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Fault_after_computing_adaptation_does_not_publish_it_and_freezes_the_session()
    {
        var content = Content;
        using var candidate = Prepare(content: content);
        using var session = (CoreSession)candidate.Activate();
        var before = Success(session.Capture(CapturePurpose.Save));
        var proposed = Next(content, 'A') with { Designs = content.Designs.Select(d => d with
            { MaxStructureDurability = 40 }).ToImmutableArray() };
        CommandEffect? computed = null;
        var result = (SubmissionResult.Completed)session.ExecuteCommand(new CoreCommand.PublishContent(proposed),
            CommandSource.AutomationTest, _ =>
            {
                computed = WorldContentPublication.Execute(session.State.World, content, proposed);
                throw new InvalidOperationException("publication transaction fault");
            });
        Assert.IsInstanceOfType<CommandEffect.Accepted>(computed);
        var accepted = (CommandEffect.Accepted)computed!;
        Assert.IsNotNull(accepted.World);
        Assert.IsTrue(accepted.World.Designs.All(d => d.MaxStructureDurability == 40));
        Assert.IsInstanceOfType<CommandDecision.Faulted>(result.Receipt.Decision);
        Assert.AreEqual("publication transaction fault", result.Diagnostic!.Message);
        Assert.IsTrue(result.Events.IsEmpty);
        var diagnostic = Success(session.Capture(CapturePurpose.Diagnostic));
        Assert.AreEqual(World(before), World(diagnostic));
        Assert.AreEqual(before.InputIdentity, diagnostic.InputIdentity);
        Assert.AreEqual(before.Position, diagnostic.Position);
        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(session.Submit(new CoreCommand.PublishContent(proposed), CommandSource.AutomationTest));
        Assert.AreEqual(1, session.ReadRecording().Commands.Length);
    }

    [TestMethod]
    [DataRow("goods")]
    [DataRow("settlement")]
    [DataRow("rule")]
    [DataRow("rule-kind")]
    [DataRow("process")]
    [DataRow("creature")]
    [DataRow("terrain")]
    [DataRow("design")]
    [DataRow("design-good")]
    [DataRow("design-missing")]
    [DataRow("part-missing")]
    public void Removing_required_content_rejects_the_whole_publication_and_records_the_command(string removal)
    {
        var content = MultiContent;
        using var candidate = Prepare(MultiInput, content);
        using var session = candidate.Activate();
        var before = Success(session.Capture(CapturePurpose.Save));
        var proposed = Next(content, 'A');
        proposed = removal switch
        {
            "goods" => proposed with { Goods = [] },
            "settlement" => proposed with { SettlementTypes = [] },
            "rule" => proposed with { RailwayRules = [] },
            "rule-kind" => proposed with { RailwayRules = [],
                MarketRules = proposed.MarketRules.Add(proposed.MarketRules[0] with { Id = Rules.Railway }) },
            "process" => proposed with { Processes = [] },
            "creature" => proposed with { Creatures = [] },
            "terrain" => proposed with { MissingReferences = [new(new CoreDefinitionReference.Terrain(Plain), "candidate")] },
            "design" => proposed with { Designs = proposed.Designs.RemoveAt(0) },
            "design-good" => proposed with { DesignGoods = [] },
            "design-missing" => proposed with { Designs = proposed.Designs.SetItem(0, proposed.Designs[0] with
                { MissingReferences = [new(new CoreDefinitionReference.Voxel(Material), "design")] }) },
            "part-missing" => proposed with { Designs = proposed.Designs.SetItem(0, proposed.Designs[0] with
                { Parts = proposed.Designs[0].Parts.SetItem(0, proposed.Designs[0].Parts[0] with { Missing = true }) }) },
            _ => throw new ArgumentOutOfRangeException(nameof(removal))
        };
        var result = Publish(session, proposed);
        var problems = Rejected(result);
        Assert.IsTrue(problems.All(p => p.Code == "content_missing"));
        if (removal == "settlement")
            foreach (var node in session.Read().World.Nodes.Where(n => n.Occupant is NodeOccupant.Settlement))
            {
                var settlement = ((NodeOccupant.Settlement)node.Occupant).Id;
                Assert.IsTrue(problems.Any(p => p.Field == $"World.Settlements[{settlement.Value}].Type"
                    && p.Subject == new WorldObjectId.Settlement(settlement)));
            }
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.IsNull(result.Diagnostic);
        var after = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(World(before), World(after));
        Assert.AreEqual(before.InputIdentity, after.InputIdentity);
        Assert.AreEqual(before.Position.CompletedSteps, after.Position.CompletedSteps);
        Assert.AreEqual(before.Position.Sequence + 1, after.Position.Sequence);
        Assert.AreEqual(before.Revision.Value + 1, after.Revision.Value);
        var recording = session.ReadRecording();
        Assert.AreEqual(result.Receipt, recording.Commands.Single().Result);
        Assert.AreEqual(before.InputIdentity, recording.Commands.Single().InputAfter);
        var second = Publish(session, Next(content, 'B'));
        Assert.IsInstanceOfType<CommandDecision.Accepted>(second.Receipt.Decision);
        Assert.AreEqual(result.Receipt.Number + 1, second.Receipt.Number);
    }

    [TestMethod]
    public void Publication_reports_all_missing_rule_fields_in_ordinal_order()
    {
        var content = Content;
        using var candidate = Prepare(content: content);
        using var session = candidate.Activate();
        var proposed = Next(content, 'A') with { PopulationRules = [], MarketRules = [], ConstructionRules = [],
            RailwayRules = [], CombatRules = [] };
        var problems = Rejected(Publish(session, proposed));
        foreach (var field in new[] { "Combat", "Construction", "Market", "Population", "Railway" })
            Assert.IsTrue(problems.Any(p => p.Field == "Configuration.Rules." + field), field);
        CollectionAssert.AreEqual(problems.Select(p => p.Field).Order(StringComparer.Ordinal).ToArray(),
            problems.Select(p => p.Field).ToArray());
        Assert.IsTrue(problems.All(p => !p.RelatedIdentities.IsEmpty));
    }

    [TestMethod]
    public void Missing_definitions_at_the_same_field_are_reported_in_ordinal_key_order()
    {
        var content = MultiContent;
        using var candidate = Prepare(MultiInput, content);
        using var session = candidate.Activate();
        var proposed = Next(content, 'A') with { Goods = [], MarketRules = content.MarketRules.Select(rule => rule with
            { GoodPrices = rule.GoodPrices.Reverse().ToImmutableArray() }).ToImmutableArray() };
        var problems = Rejected(Publish(session, proposed));
        var atField = problems.Where(p => p.Field == "Content.MarketRules[fixture:market].GoodPrices").ToArray();
        CollectionAssert.AreEqual(new[] { "voxel:fixture:material", "voxel:fixture:ore" },
            atField.Select(p => p.RelatedIdentities.Single()).ToArray());
    }

    [TestMethod]
    [DataRow("composition")]
    [DataRow("vehicle-size")]
    [DataRow("vehicle-length")]
    [DataRow("building-size")]
    [DataRow("building-kind")]
    [DataRow("building-tags")]
    [DataRow("part-id")]
    [DataRow("part-definition")]
    [DataRow("return-part")]
    public void Same_hash_cannot_change_instance_interpretation(string change)
    {
        var content = MultiContent;
        using var candidate = Prepare(MultiInput, content);
        using var session = candidate.Activate();
        var building = change.StartsWith("building", StringComparison.Ordinal);
        var hash = building ? FactoryBuilding : Vehicle;
        var design = content.Designs.Single(d => d.Design == hash);
        var changed = change switch
        {
            "composition" => design with { Composition = new(new string('1', 64)) },
            "vehicle-size" => design with { Kind = new DesignKind.Vehicle(SizeClass.Medium, 1) },
            "vehicle-length" => design with { Kind = new DesignKind.Vehicle(SizeClass.Small, 2) },
            "building-size" => design with { Kind = ((DesignKind.Building)design.Kind) with { PlotSize = PlotSize.Medium } },
            "building-kind" => design with { Kind = ((DesignKind.Building)design.Kind) with { Type = BuildingKind.Warehouse } },
            "building-tags" => design with { Kind = ((DesignKind.Building)design.Kind) with { FacilityTags = [FacilityTag.Extraction] } },
            "part-id" => design with { Parts = design.Parts.SetItem(0, design.Parts[0] with { Id = new(3) }) },
            "part-definition" => design with { Parts = design.Parts.SetItem(0, design.Parts[0] with { Definition = new CoreDefinitionReference.Voxel(Ore) }) },
            "return-part" => design with { ReturnUnits = design.ReturnUnits.SetItem(0, design.ReturnUnits[0] with { Parts = [new(999)] }) },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var before = Success(session.Capture(CapturePurpose.Save));
        var proposed = Next(content, 'A') with { Designs = content.Designs.Select(d => d.Design == hash ? changed : d).ToImmutableArray() };
        var problems = Rejected(Publish(session, proposed));
        Assert.IsTrue(problems.Any(p => p.Code == "invalid_state" && p.Field.StartsWith($"World.Designs[{hash.Value}]", StringComparison.Ordinal)));
        Assert.AreEqual(World(before), World(Success(session.Capture(CapturePurpose.Save))));
    }

    [TestMethod]
    public void Equal_building_tag_and_part_arrays_in_new_storage_are_compatible_and_platforms_are_recomputed()
    {
        var content = MultiContent;
        using var candidate = Prepare(MultiInput, content);
        using var session = candidate.Activate();
        var before = session.Read();
        var retainedTemplates = JsonNode.Parse(Success(session.Capture(CapturePurpose.Save)).Encode())!["world"]!["settlements"]!.ToJsonString();
        var proposed = Next(content, 'A') with { SettlementTemplates = [], Designs = content.Designs.Select(d => d with
        {
            Kind = d.Kind is DesignKind.Building kind ? kind with { FacilityTags = kind.FacilityTags.ToArray().ToImmutableArray() } : d.Kind,
            Parts = d.Parts.Select(p => p with { Capabilities = d.Design == Station ? [] : p.Capabilities }).ToImmutableArray()
        }).ToImmutableArray() };
        Assert.IsTrue(before.World.Nodes.Any(n => n.Platforms > 0));
        Assert.IsInstanceOfType<CommandDecision.Accepted>(Publish(session, proposed).Receipt.Decision);
        Assert.IsTrue(session.Read().World.Nodes.All(n => n.Platforms == 0));
        Assert.IsTrue(before.World.Nodes.Any(n => n.Platforms > 0));
        var references = Success(session.Capture(CapturePurpose.Save)).References;
        Assert.AreEqual(retainedTemplates, JsonNode.Parse(Success(session.Capture(CapturePurpose.Save)).Encode())!["world"]!["settlements"]!.ToJsonString());
        Assert.IsFalse(references.Templates.IsEmpty);
        foreach (var template in references.Templates)
            Assert.IsTrue(content.SettlementTemplates.Any(source => source.Id == template.SourceTemplate));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Restored_missing_references_can_be_partially_repaired_but_valid_references_cannot_be_removed(bool removeValid)
    {
        var content = MultiContent;
        using var initial = Prepare(MultiInput, content);
        using var original = initial.Activate();
        var saved = Success(original.Capture(CapturePurpose.Save));
        var missing = content with { Goods = content.Goods.Where(g => g.Good != new GoodId.Voxel(Material)
            && g.Good != new GoodId.Voxel(Ore)).ToImmutableArray(), MissingReferences =
            [new(new CoreDefinitionReference.Voxel(Material), "old"), new(new CoreDefinitionReference.Voxel(Ore), "old")] };
        using var restored = Success(CoreFactory.Create().PrepareRestore(saved, missing, Options));
        using var session = restored.Activate();
        var before = Success(session.Capture(CapturePurpose.Save));
        var proposed = Next(content, 'A') with
        {
            Goods = content.Goods.Where(g => g.Good != new GoodId.Voxel(Ore)).ToImmutableArray(),
            MissingReferences = [new(new CoreDefinitionReference.Voxel(Ore), "still missing")],
            RailwayRules = removeValid ? [] : content.RailwayRules
        };
        var result = Publish(session, proposed);
        if (removeValid)
        {
            Rejected(result);
            Assert.AreEqual(World(before), World(Success(session.Capture(CapturePurpose.Save))));
            Assert.AreEqual(before.InputIdentity, session.Read().InputIdentity);
        }
        else
        {
            Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
            var references = Success(session.Capture(CapturePurpose.Save)).References.Definitions;
            Assert.IsTrue(references.Where(r => r.Definition == new CoreDefinitionReference.Voxel(Material)).All(r => !r.WasMissing));
            Assert.IsTrue(references.Any(r => r.Definition == new CoreDefinitionReference.Voxel(Ore) && r.WasMissing));
        }
    }

    [TestMethod]
    public void Existing_missing_definition_does_not_allow_a_new_field_to_reference_it()
    {
        var content = MultiContent;
        using var initial = Prepare(MultiInput, content);
        using var original = initial.Activate();
        var saved = Success(original.Capture(CapturePurpose.Save));
        var missing = content with { Goods = content.Goods.Where(g => g.Good != new GoodId.Voxel(Ore)).ToImmutableArray(),
            MissingReferences = [new(new CoreDefinitionReference.Voxel(Ore), "old")] };
        using var restored = Success(CoreFactory.Create().PrepareRestore(saved, missing, Options));
        using var session = restored.Activate();
        var proposed = Next(missing, 'A') with { RailwayRules = [missing.RailwayRules[0] with { RailGood = Ore }] };
        var problems = Rejected(Publish(session, proposed));
        Assert.IsTrue(problems.Any(p => p.Field == "Content.RailwayRules[fixture:railway].RailGood"));
    }

    [TestMethod]
    public void Previously_missing_directory_entries_without_candidate_markers_are_rejected()
    {
        var content = Content;
        using var initial = Prepare(content: content);
        using var original = initial.Activate();
        var definition = new CoreDefinitionReference.Voxel(Material);
        var missing = content with { Goods = content.Goods.Where(g => g.Good != new GoodId.Voxel(Material)).ToImmutableArray(),
            MissingReferences = [new(definition, "old")] };
        using var restored = Success(CoreFactory.Create().PrepareRestore(Success(original.Capture(CapturePurpose.Save)), missing, Options));
        using var session = restored.Activate();
        var before = Success(session.Capture(CapturePurpose.Save));
        var proposed = Next(missing, 'A') with { MissingReferences = [] };
        var result = Publish(session, proposed);
        var problems = Rejected(result);
        Assert.IsTrue(problems.All(p => p.Code == "content_missing"));
        Assert.IsTrue(problems.Any(p => p.RelatedIdentities.Contains("voxel:fixture:material")));
        var after = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual(World(before), World(after));
        Assert.AreEqual(before.InputIdentity, after.InputIdentity);
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Explicit_missing_markers_survive_publication_save_and_same_projection_restore(bool repaired)
    {
        var content = Content;
        using var initial = Prepare(content: content);
        using var original = initial.Activate();
        var definition = new CoreDefinitionReference.Voxel(Material);
        var missing = content with { Goods = content.Goods.Where(g => g.Good != new GoodId.Voxel(Material)).ToImmutableArray(),
            MissingReferences = [new(definition, "old")] };
        using var restored = Success(CoreFactory.Create().PrepareRestore(Success(original.Capture(CapturePurpose.Save)), missing, Options));
        using var session = restored.Activate();
        var proposed = Next(missing, 'A');
        Assert.IsInstanceOfType<CommandDecision.Accepted>(Publish(session, proposed).Receipt.Decision);
        var saved = Success(CoreFactory.Create().DecodeState(Success(session.Capture(CapturePurpose.Save)).Encode()));
        using var reloaded = Success(CoreFactory.Create().PrepareRestore(saved, proposed, Options));
        Assert.IsTrue(reloaded.MissingReferences.Any(r => r.Reference == definition));
        using var resumed = reloaded.Activate();
        Assert.IsTrue(Success(resumed.Capture(CapturePurpose.Save)).References.Definitions
            .Where(r => r.Definition == definition).All(r => r.WasMissing));
        var current = repaired ? Next(content, 'B') : Next(proposed, 'B');
        Assert.IsInstanceOfType<CommandDecision.Accepted>(Publish(resumed, current).Receipt.Decision);
        Assert.IsTrue(Success(resumed.Capture(CapturePurpose.Save)).References.Definitions
            .Where(r => r.Definition == definition).All(r => r.WasMissing == !repaired));
    }

    [TestMethod]
    public void Lowering_then_raising_durability_preserves_saved_absolute_damage_and_old_snapshots()
    {
        var content = Content;
        var id = new InstanceId.Vehicle(new(1));
        var state = State(content: content);
        using var session = new CoreSession(state with { World = state.World with
        {
            Instances = state.World.Instances.Select(i => i.Id == id ? i with
            {
                StructureDurability = 80,
                Parts = i.Parts.Select(p => p with { Current = 80 }).ToImmutableArray()
            } : i).ToImmutableArray()
        } });
        var retained = session.Read();
        var saved = Success(session.Capture(CapturePurpose.Save));
        var savedText = saved.Encode();
        var original = Instance(retained, id);
        Assert.AreEqual(new DurabilityView(80, 100), original.Structure);
        Assert.AreEqual(new DurabilityView(80, 100), original.Parts[0].Durability);
        var lowered = Next(content, 'A') with { Designs = content.Designs.Select(d => d with
        {
            MaxStructureDurability = 60,
            Parts = d.Parts.Select(p => p with { MaxDurability = 60 }).ToImmutableArray(),
            CanConstruct = false
        }).ToImmutableArray() };
        Assert.IsInstanceOfType<CommandDecision.Accepted>(Publish(session, lowered).Receipt.Decision);
        var low = Instance(session.Read(), id);
        Assert.AreEqual(new DurabilityView(60, 60), low.Structure);
        Assert.AreEqual(new DurabilityView(60, 60), low.Parts[0].Durability);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(Publish(session, Next(content, 'B')).Receipt.Decision);
        Assert.AreEqual(new DurabilityView(80, 100), Instance(session.Read(), id).Structure);
        Assert.AreEqual(new DurabilityView(80, 100), Instance(session.Read(), id).Parts[0].Durability);
        CollectionAssert.AreEqual(original.Parts.ToArray(), Instance(session.Read(), id).Parts.ToArray());
        var stillRetained = Instance(retained, id);
        Assert.AreEqual(original.Structure, stillRetained.Structure);
        Assert.AreEqual(original.Design, stillRetained.Design);
        Assert.AreEqual(original.Composition, stillRetained.Composition);
        Assert.AreEqual(original.Location, stillRetained.Location);
        Assert.AreEqual(original.Details, stillRetained.Details);
        CollectionAssert.AreEqual(original.Parts.ToArray(), stillRetained.Parts.ToArray());
        Assert.AreEqual(savedText, saved.Encode());
    }

    private static CoreContentProjection Next(CoreContentProjection content, char identity) =>
        content with { Identity = new(new string(identity, 64)) };

    private static SubmissionResult.Completed Publish(ICoreSession session, CoreContentProjection content) =>
        (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(content), CommandSource.AutomationTest);

    private static ImmutableArray<CoreProblem> Rejected(SubmissionResult.Completed result)
    {
        Assert.IsInstanceOfType<CommandDecision.Rejected>(result.Receipt.Decision);
        var problems = ((CommandDecision.Rejected)result.Receipt.Decision).Problems;
        Assert.IsFalse(problems.IsEmpty);
        return problems;
    }

    private static string World(ICoreCapture capture) => JsonNode.Parse(capture.Encode())!["world"]!.ToJsonString();
    private static InstanceView Instance(ICoreReadView view, InstanceId id) => ((Lookup<InstanceView>.Found)view.FindInstance(id)).Value;
}
