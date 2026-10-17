using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class AuthoringDraftTests
{
    [TestMethod]
    public void Definition_drafts_preserve_unfinished_fields_missing_references_and_source_text()
    {
        C.DefinitionDraft[] definitions =
        [
            new C.DefinitionDraft.Voxel(new("unfinished", new("  ", "\r\n 草稿\t"), null,
                new(null, null, null, false, null, []), C.AllowedVoxelOrientations.FourWay,
                new C.VoxelCollision.FullBox(), null, new(false, null))),
            new C.DefinitionDraft.SettlementType(new("unfinished", new("", ""), null, false,
                null, [new("missing:terrain")], null, null)),
            new C.DefinitionDraft.SettlementTemplate(new("unfinished", null, null, new("missing:scene"),
                [], [], [], [])),
            new C.DefinitionDraft.Blueprint("unfinished", DesignFixtures.Vehicle() with
            {
                Specification = new C.DesignSpecification.Creature(null, null),
                Layout = new([DesignFixtures.Entry("missing")], [], [])
            })
        ];
        foreach (var definition in definitions)
        foreach (var version in new[] { "", "  source\r\n版本\t ", "not-a-hash" })
        {
            var kind = definition switch
            {
                C.DefinitionDraft.Voxel => C.DefinitionKind.Voxel,
                C.DefinitionDraft.SettlementType => C.DefinitionKind.SettlementType,
                C.DefinitionDraft.SettlementTemplate => C.DefinitionKind.SettlementTemplate,
                _ => C.DefinitionKind.Blueprint
            };
            var saved = Saved(definition, kind, version);
            var json = Success(ContentFactory.Create().EncodeDraft(saved));
            var decoded = Success(ContentFactory.Create().DecodeDraft(json));
            Assert.AreEqual(saved.Target, decoded.Target);
            Assert.AreEqual(saved.Id, decoded.Id);
            Assert.AreEqual(saved.Revision, decoded.Revision);
            Assert.AreEqual(json, Success(ContentFactory.Create().EncodeDraft(decoded)));
            var root = JsonNode.Parse(json)!;
            Assert.AreEqual(1, root["format_version"]!.GetValue<int>());
            Assert.AreEqual("definition", root["payload"]!["kind"]!.GetValue<string>());
            Assert.AreEqual("package_definition", root["target"]!["kind"]!.GetValue<string>());
            Assert.AreEqual(version, root["target"]!["source_version"]!.GetValue<string>());
            if (definition is C.DefinitionDraft.Voxel voxel)
            {
                var actual = ((C.DefinitionDraft.Voxel)((C.DraftPayload.Definition)decoded.Payload).Value).Value;
                Assert.AreEqual(voxel.Value.Display, actual.Display);
                Assert.IsNull(actual.Presentation);
                Assert.IsNull(actual.Simulation!.BaseValue);
                Assert.IsNull(actual.Simulation.MassTons);
                Assert.IsNull(actual.Simulation.MaxDurability);
            }
            if (definition is C.DefinitionDraft.SettlementTemplate template)
            {
                var actual = ((C.DefinitionDraft.SettlementTemplate)((C.DraftPayload.Definition)decoded.Payload).Value).Value;
                Assert.IsNull(actual.Type);
                Assert.IsNull(actual.Weight);
                Assert.AreEqual(template.Value.SceneModel, actual.SceneModel);
            }
            if (definition is C.DefinitionDraft.SettlementType type)
            {
                var actual = ((C.DefinitionDraft.SettlementType)((C.DraftPayload.Definition)decoded.Payload).Value).Value;
                CollectionAssert.AreEqual(type.Value.PreferredTerrain.ToArray(), actual.PreferredTerrain.ToArray());
                Assert.IsNull(actual.Weight);
                Assert.IsNull(actual.MinimumPopulation);
                Assert.IsNull(actual.MaximumPopulation);
            }
            if (definition is C.DefinitionDraft.Blueprint blueprint)
            {
                var actual = ((C.DefinitionDraft.Blueprint)((C.DraftPayload.Definition)decoded.Payload).Value).Value;
                Assert.AreEqual(blueprint.Value.Specification, actual.Specification);
                CollectionAssert.AreEqual(blueprint.Value.Layout.Palette.ToArray(), actual.Layout.Palette.ToArray());
            }
        }
    }

    [TestMethod]
    public void Lair_definition_codec_rejects_both_directions_and_local_library_rejects_package_drafts()
    {
        var lair = new C.DefinitionDraft.LairType(new("den", new("巢穴", ""), null,
            null, [], null, null, null, null, null, null, null));
        Failure(ContentFactory.Create().EncodeDraft(Saved(lair, C.DefinitionKind.LairType)));
        var valid = Saved(new C.DefinitionDraft.Blueprint("unfinished", DesignFixtures.Vehicle()), C.DefinitionKind.Blueprint);
        var root = JsonNode.Parse(Success(ContentFactory.Create().EncodeDraft(valid)))!;
        root["target"]!["definition_kind"] = "lair_type";
        root["payload"]!["definition_kind"] = "lair_type";
        root["payload"]!["value"] = JsonNode.Parse("""{"schema_version":1,"id":"den","display":{"name":"巢穴","description":""}}""");
        Failure(ContentFactory.Create().DecodeDraft(root.ToJsonString()));
        using var loaded = DesignFixtures.Content().Load();
        foreach (var draft in new[]
        {
            valid,
            valid with { Target = new C.DraftTarget.LocalBlueprint(null) },
            valid with { Payload = new C.DraftPayload.Design(DesignFixtures.Vehicle()) }
        })
            Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(ContentFactory.Create().PrepareEdit(
                loaded.View, new C.ContentEdit.SaveLocalDraft(draft)));
    }

    private static C.SavedContentDraft Saved(C.DefinitionDraft value, C.DefinitionKind kind, string version = "snapshot") =>
        new(new(Guid.Parse("16c41dce-4565-4887-bbc3-2a81bb29c5c6")), new(ulong.MaxValue),
            new C.DraftTarget.PackageDefinition(new("core"), kind, "unfinished", new(version)), new C.DraftPayload.Definition(value));

    private static T Success<T>(C.ContentResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Success>(result, result.ToString());
        return ((C.ContentResult<T>.Success)result).Value;
    }

    private static void Failure<T>(C.ContentResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Failure>(result, result.ToString());
        Assert.IsTrue(((C.ContentResult<T>.Failure)result).Diagnostics.Any(d => d.Code == "invalid_field"));
    }
}
