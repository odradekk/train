using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class CreatureRestoreTests
{
    [TestMethod]
    public void Restore_and_reload_project_current_definitions_without_replacing_frozen_revisions()
    {
        var fixture = Package();
        using var initial = fixture.Load();
        var creature = initial.View.Core.Creatures.Single();
        var lair = initial.View.Core.LairTypes.Single();
        var references = Manifest(initial.View, creature.Revision, lair.Revision, false);
        var saved = Success(ContentFactory.Create().Capture(initial.View, references));
        WorldContentFixtures.Change(fixture, "resources/blueprints/scout.blueprint.json", json =>
        {
            json["simulation"]!["behaviors"]!["creature"]!["hp"] = 9;
            json["display"]!["name"] = "新侦察";
        });
        WorldContentFixtures.Change(fixture, "resources/lair-types/nest.lair-type.json", json =>
        {
            json["spawn_interval_seconds"] = 45;
            json["display"]!["name"] = "新巢穴";
        });
        using var reloaded = Reload(fixture, initial.View, references);
        using var restored = Restore(fixture, saved, references);
        foreach (var view in new[] { reloaded.View, restored.View })
        {
            var current = view.Core.Creatures.Single();
            var currentLair = view.Core.LairTypes.Single();
            Assert.AreEqual(9, current.HitPoints);
            Assert.AreEqual(45, currentLair.SpawnIntervalSeconds);
            Assert.AreNotEqual(creature.Revision, current.Revision);
            Assert.AreNotEqual(lair.Revision, currentLair.Revision);
            Assert.AreEqual("新侦察", ((C.ContentLookup<C.CreatureAppearanceProjection>.Found)view.FindCreature(creature.Id)).Value.Display.Name);
            Assert.AreEqual("新巢穴", ((C.ContentLookup<C.ContentDisplay>.Found)view.FindDefinitionDisplay(
                new K.CoreDefinitionReference.Lair(lair.Id))).Value.Text.Name);
            Assert.IsTrue(view.Core.MissingReferences.IsEmpty);
        }
        Assert.AreEqual(creature.Revision, references.Definitions[0].FrozenRevision);
        Assert.AreEqual(lair.Revision, references.Definitions[1].FrozenRevision);
    }

    [TestMethod]
    public void Reload_rejects_a_newly_missing_creature_or_lair_and_keeps_one_that_was_already_missing()
    {
        var fixture = Package();
        using var initial = fixture.Load();
        var references = Manifest(initial.View, null, null, false);
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/lair-types/nest.lair-type.json");
        var rejected = ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(initial.View, references)), fixture, fixture);
        Failure(rejected, "reference_missing");
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)rejected).Diagnostics.Any(diagnostic =>
            diagnostic.Stage == "reload" && diagnostic.Field == "node.lair"));
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/scout.blueprint.json");
        rejected = ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(initial.View, references)), fixture, fixture);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)rejected).Diagnostics.Any(diagnostic =>
            diagnostic.Field == "raid.creature" && diagnostic.Code == "reference_missing"));

        var absent = DesignFixtures.Content();
        WorldContentFixtures.AddBlueprint(absent, WorldContentFixtures.Scene());
        using var basis = absent.Load();
        var retained = Manifest(basis.View, null, null, true);
        var capture = Success(ContentFactory.Create().Capture(basis.View, retained));
        using var restored = Restore(absent, capture, retained);
        Assert.IsInstanceOfType<C.ContentLookup<C.CreatureAppearanceProjection>.Missing>(restored.View.FindCreature(new("core:scout")));
        Assert.IsInstanceOfType<C.ContentLookup<C.LairAppearanceProjection>.Missing>(restored.View.FindLair(new("core:nest")));
        using var stillMissing = Reload(absent, restored.View, retained);
        Assert.IsInstanceOfType<C.ContentLookup<C.CreatureAppearanceProjection>.Missing>(stillMissing.View.FindCreature(new("core:scout")));
        absent.AddFile(ContentFixture.BuiltIn, "resources/blueprints/scout.blueprint.json", Creature().ToJsonString());
        absent.AddFile(ContentFixture.BuiltIn, "resources/lair-types/nest.lair-type.json", Lair().ToJsonString());
        using var recovered = Reload(absent, restored.View, retained);
        Assert.IsInstanceOfType<C.ContentLookup<C.CreatureAppearanceProjection>.Found>(recovered.View.FindCreature(new("core:scout")));
        Assert.IsInstanceOfType<C.ContentLookup<C.LairAppearanceProjection>.Found>(recovered.View.FindLair(new("core:nest")));
        Assert.IsTrue(recovered.View.Core.MissingReferences.IsEmpty);
    }

    [TestMethod]
    public void Replay_keeps_historical_creature_and_lair_appearance_after_installed_content_changes()
    {
        var fixture = Package();
        using var initial = fixture.Load();
        var references = Manifest(initial.View, null, null, false);
        var replay = Success(ContentFactory.Create().CaptureReplay([initial.View], references)).Encode();
        WorldContentFixtures.Change(fixture, "resources/blueprints/scout.blueprint.json", json => json["display"]!["name"] = "新侦察");
        WorldContentFixtures.Change(fixture, "resources/blueprints/scene.blueprint.json", json => json["display"]!["name"] = "新场景");
        using var current = fixture.Load();
        Assert.AreEqual("新侦察", ((C.ContentLookup<C.CreatureAppearanceProjection>.Found)current.View.FindCreature(new("core:scout"))).Value.Display.Name);
        var history = Success(ContentFactory.Create().DecodeReplay(replay)).History.Single();
        var creature = ((C.ContentLookup<C.CreatureAppearanceProjection>.Found)history.FindCreature(new("core:scout"))).Value;
        var lair = ((C.ContentLookup<C.LairAppearanceProjection>.Found)history.FindLair(new("core:nest"))).Value;
        Assert.AreEqual("侦察", creature.Display.Name);
        Assert.AreEqual(8, history.Core.Creatures.Single().HitPoints);
        Assert.AreEqual("测试", lair.SceneModel.Display.Name);
        Assert.AreEqual("新场景", ((C.ContentLookup<C.LairAppearanceProjection>.Found)current.View.FindLair(new("core:nest"))).Value.SceneModel.Display.Name);
    }

    private static ContentFixture Package()
    {
        var fixture = DesignFixtures.Content();
        WorldContentFixtures.AddBlueprint(fixture, WorldContentFixtures.Scene());
        fixture.AddFile(ContentFixture.BuiltIn, "resources/blueprints/scout.blueprint.json", Creature().ToJsonString());
        fixture.AddFile(ContentFixture.BuiltIn, "resources/lair-types/nest.lair-type.json", Lair().ToJsonString());
        return fixture;
    }

    private static K.CoreReferenceManifest Manifest(C.IContentView view, K.SemanticRevision? creature, K.SemanticRevision? lair, bool wasMissing) => new([], [],
    [
        new(new K.CoreDefinitionReference.Creature(new("core:scout")), creature, "raid.creature", wasMissing),
        new(new K.CoreDefinitionReference.Lair(new("core:nest")), lair, "node.lair", wasMissing)
    ], [view.Core.Identity]);

    private static C.IContentCandidate Reload(ContentFixture fixture, C.IContentView basis, K.CoreReferenceManifest references) =>
        Success(ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Reload(basis, references)), fixture, fixture));

    private static C.IContentCandidate Restore(ContentFixture fixture, C.IContentCapture capture, K.CoreReferenceManifest references) =>
        Success(ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Restore(capture, references)), fixture, fixture));

    private static JsonObject Creature() => JsonNode.Parse("""
        {"schema_version":3,"id":"scout","category":"creature",
        "simulation":{"behaviors":{"creature":{"hp":8,"speed":1.5,"retreat_threshold":0.25,"raid_duration":12}}},
        "layout":{"palette":[{"voxel":"scenery"}],"cells":[[0,0,0,0]]},
        "display":{"name":"侦察","description":"测试生物"}}
        """)!.AsObject();

    private static JsonObject Lair() => JsonNode.Parse("""
        {"schema_version":1,"id":"nest","display":{"name":"巢穴","description":"测试聚落"},
        "generation_weight":2,"members":[{"creature":"scout","weight":1}],"maximum_members":4,
        "spawn_interval_seconds":30,"raid_interval_seconds":60,"roaming_radius_hops":1,"raid_radius_hops":2,
        "rail_damage_work_seconds":15,"clearance_reward":10,"scene_model":"scene"}
        """)!.AsObject();
}
