using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class CompositeContentTests
{
    [TestMethod]
    public void Builtin_style_composites_commit_through_the_existing_reloader()
    {
        var (reloader, logger) = CompositeFixtures.Load();
        using (logger)
        {
            Assert.IsTrue(reloader.Composites.Current.TryGet(HollowShellFixture.DefinitionId, out var composite));
            Assert.IsTrue(reloader.Composites.Current.TryGet(HollowShellFixture.ArchetypeId, out var archetype));
            Assert.AreEqual("core:dev-hollow-shell", composite.Definition.ArchetypeId.Value);
            Assert.AreEqual(VehicleSizeClass.Small, composite.Definition.SizeClass);
            Assert.AreEqual(VoxelTags.IdentityCore, archetype.Archetype.IdentityRetention.RequiredCoreTag);
            Assert.AreEqual(2, archetype.Archetype.Construction.ContactAnchorCount);
        }
    }

    [TestMethod]
    public void Formal_fixture_json_loads_168_cells_and_separate_contact_bindings()
    {
        var (reloader, logger) = CompositeFixtures.Load(CompositeFixtures.FormalFixturePackage());
        using (logger)
        {
            Assert.IsTrue(reloader.Composites.Current.TryGet(HollowShellFixture.DefinitionId, out var composite));
            Assert.AreEqual(168, composite.Definition.Voxels.Count);
            Assert.AreEqual(HollowShellFixture.CoreCell, composite.Definition.Voxels.Single(item => item.DefinitionId.LocalId == "identity-core").Cell);
            Assert.AreEqual(2, composite.Definition.Contacts.Count);
            Assert.AreEqual(1.0, composite.Definition.Contacts[0].X);
            Assert.AreEqual(-0.75, composite.Definition.Contacts[0].Y);
            Assert.AreEqual(1.5, composite.Definition.Contacts[0].Z);
            Assert.AreEqual(HollowShellFixture.CoreCell, composite.Definition.Contacts[0].BoundCell);
            Assert.AreEqual(HollowShellFixture.RearBoundCell, composite.Definition.Contacts[1].BoundCell);
            Assert.AreNotEqual(
                (composite.Definition.Contacts[0].X, composite.Definition.Contacts[0].Y, composite.Definition.Contacts[0].Z),
                ((double)composite.Definition.Contacts[0].BoundCell.X,
                    (double)composite.Definition.Contacts[0].BoundCell.Y,
                    (double)composite.Definition.Contacts[0].BoundCell.Z));
            Assert.AreNotEqual(-0.75, composite.Definition.Contacts[0].BoundCell.Y);
        }
    }

    [TestMethod]
    public void Unknown_fields_and_schema_violations_reject_built_in_composites()
    {
        var cases = new[]
        {
            "{\"schema_version\":1,\"archetypes\":[],\"composites\":[],\"extra\":1}",
            "{\"schema_version\":2,\"archetypes\":[" + CompositeFixtures.Archetype() + "],\"composites\":[" + CompositeFixtures.Composite() + "]}",
            "{\"schema_version\":1,\"composites\":[" + CompositeFixtures.Composite() + "]}"
        };

        foreach (var json in cases)
        {
            using var session = LoggerSession.Create();
            var source = ContentFixtures.ValidIron();
            source.AddCompositeFile("core", "bad.composites.json", json);
            var reloader = ContentFixtures.Create(source, session.Logger);
            Assert.AreEqual(ContentReloadStatus.Rejected, reloader.Reload().Status, json);
        }
    }

    [TestMethod]
    public void Duplicate_composite_ids_invalidate_the_owning_package()
    {
        using var session = LoggerSession.Create();
        var source = ContentFixtures.ValidIron();
        source.AddCompositeFile("core", "a.composites.json", CompositeFixtures.File(null, CompositeFixtures.Archetype(), CompositeFixtures.Composite()));
        source.AddCompositeFile("core", "b.composites.json", CompositeFixtures.File(null, "", CompositeFixtures.Composite()));
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Rejected, reloader.Reload().Status);
    }

    [TestMethod]
    public void Illegal_mod_composites_are_isolated_without_rejecting_built_in()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.VehiclePackage();
        source.AddMod("broken", "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        source.AddCompositeFile(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            "composites/bad.composites.json",
            "{\"schema_version\":1,\"extra\":true}",
            "memory://mods/broken/resources");
        var reloader = ContentFixtures.Create(source, session.Logger);
        var result = reloader.Reload();
        Assert.IsTrue(result.IsCommitted);
        Assert.IsTrue(reloader.Composites.Current.TryGet(HollowShellFixture.DefinitionId, out _));
        Assert.IsFalse(reloader.Composites.Current.TryGet(
            new CompositeDefinitionId("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "bad"),
            out _));
    }

    [TestMethod]
    public void Candidate_drops_a_mod_whose_composite_references_are_unresolved()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.VehiclePackage();
        source.AddMod("dangling", "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff");
        source.AddCompositeFile(
            "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff",
            "composites/car.composites.json",
            CompositeFixtures.File(
                null,
                CompositeFixtures.Archetype("mod-car"),
                CompositeFixtures.Composite(id: "mod-car", archetype: "mod-car", voxels: CompositeFixtures.Voxel(0, 0, 0, "missing-part"))),
            "memory://mods/dangling/resources");
        var reloader = ContentFixtures.Create(source, session.Logger);
        var result = reloader.Reload();
        Assert.IsTrue(result.IsCommitted);
        Assert.IsFalse(reloader.Composites.Current.TryGet(
            new CompositeDefinitionId("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff", "mod-car"),
            out _));
        Assert.IsTrue(reloader.Composites.Current.TryGet(HollowShellFixture.DefinitionId, out _));
    }

    [TestMethod]
    public void Player_blueprint_uses_file_uuid_namespace_and_in_file_display_name()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.VehiclePackage();
        var uuid = "cccccccc-dddd-4eee-8fff-000000000001";
        source.AddBlueprint(
            uuid,
            CompositeFixtures.File(
                "我的手写蓝图",
                CompositeFixtures.Archetype("player-car"),
                CompositeFixtures.Composite(
                    id: "player-car",
                    archetype: "core:dev-hollow-shell",
                    voxels: string.Join(",", CompositeFixtures.Voxel(0, 0, 0, "core:vehicle-shell"), CompositeFixtures.Voxel(1, 0, 0, "core:identity-core")),
                    contacts: string.Join(",", CompositeFixtures.Contact(0, -0.75, 0.5, 0, 0, 0), CompositeFixtures.Contact(1, -0.75, 0.5, 1, 0, 0)))));
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        Assert.IsTrue(reloader.Composites.Current.TryGet(new CompositeDefinitionId(uuid, "player-car"), out var entry));
        Assert.AreEqual("我的手写蓝图", entry.FileDisplayName);
        Assert.AreEqual(HollowShellFixture.ArchetypeId, entry.Definition.ArchetypeId);
    }

    [TestMethod]
    public void Semantic_revision_ignores_layout_and_excludes_display_and_target_revisions()
    {
        using var session = LoggerSession.Create();
        var source = CompositeFixtures.VehiclePackage();
        var reloader = ContentFixtures.Create(source, session.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var baseline = reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision;
        var baselineArchetype = reloader.Composites.Current.Archetypes[HollowShellFixture.ArchetypeId].SemanticRevision;

        source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            """
            {"schema_version":1,"archetypes":[{"id":"dev-hollow-shell","construction":{"contact_anchor_count":2,"contacts_must_be_distinct":true,"contacts_must_bind_existing_parts":true,"require_declared_size":true,"require_nonempty_six_connected":true},"identity_retention":{"require_core_tag":"identity_core"}}],"composites":[{"id":"dev-hollow-shell","name":"开发空心壳","description":"测试车辆","archetype":"dev-hollow-shell","size_class":"small","voxels":[{"definition":"identity-core","orientation":"px_py","x":1,"y":0,"z":0},{"definition":"vehicle-shell","orientation":"px_py","x":0,"y":0,"z":0},{"definition":"vehicle-shell","orientation":"px_py","x":2,"y":0,"z":0}],"contacts":[{"x":1,"y":-0.75,"z":1.5,"cell":{"x":1,"y":0,"z":0}},{"x":2,"y":-0.75,"z":1.5,"cell":{"x":2,"y":0,"z":0}}]}]}
            """);
        var rearranged = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements, composites: reloader.Composites).Reload();
        Assert.IsFalse(rearranged.Changed);
        Assert.AreEqual(baseline, reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision);
        Assert.AreEqual(baselineArchetype, reloader.Composites.Current.Archetypes[HollowShellFixture.ArchetypeId].SemanticRevision);

        source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            CompositeFixtures.File("只改显示名", CompositeFixtures.Archetype(), CompositeFixtures.Composite(name: "新名字", description: "新说明")));
        var display = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements, composites: reloader.Composites).Reload();
        Assert.IsTrue(display.Changed);
        Assert.AreEqual(baseline, reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision);

        source.AddVoxelFile("core", "voxels/vehicle-shell.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("vehicle-shell", "壳体", "壳体说明", value: 20, tagsJson: "[]")));
        var dependency = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements, composites: reloader.Composites).Reload();
        Assert.IsTrue(dependency.Changed);
        Assert.AreEqual(baseline, reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision);

        source.AddCompositeFile(
            "core",
            "composites/dev-hollow-shell.composites.json",
            CompositeFixtures.File("只改显示名", CompositeFixtures.Archetype(), CompositeFixtures.Composite(size: "medium")));
        var semantic = ContentFixtures.Create(source, session.Logger, catalog: reloader.Catalog, settlements: reloader.Settlements, composites: reloader.Composites).Reload();
        Assert.IsTrue(semantic.Changed);
        Assert.AreNotEqual(baseline, reloader.Composites.Current.Composites[HollowShellFixture.DefinitionId].SemanticRevision);
    }
}
