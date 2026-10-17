using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Loading;
using Train.Content.Persistence;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentDraftTests
{
    [TestMethod]
    public void Draft_round_trip_preserves_incomplete_specs_invalid_layout_and_raw_text()
    {
        var design = DesignFixtures.Vehicle() with
        {
            Display = new("  ", "\r\n 未完成 \t"),
            Specification = new C.DesignSpecification.Vehicle(null),
            Layout = new([DesignFixtures.Entry("missing"), DesignFixtures.Entry()],
                [new(new(9, 0, 0), 17), new(new(9, 0, 0), -1)], [])
        };
        foreach (var target in new C.DraftTarget[]
        {
            new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(12))),
            new C.DraftTarget.Instance(new K.InstanceId.Building(new(13))),
            new C.DraftTarget.LocalBlueprint(null),
            new C.DraftTarget.UserBlueprint(new("20b0e2d8-9171-419f-a309-57d064023bfb"))
        })
        {
            var saved = Saved(design, target);
            var json = ContentDraftCodec.Encode(saved);
            var decoded = ContentDraftCodec.Decode(json);
            Assert.AreEqual(saved.Id, decoded.Id);
            Assert.AreEqual(saved.Revision, decoded.Revision);
            Assert.AreEqual(target, decoded.Target);
            var value = ((C.DraftPayload.Design)decoded.Payload).Value;
            Assert.AreEqual(design.Display, value.Display);
            Assert.AreEqual(design.Specification, value.Specification);
            CollectionAssert.AreEqual(design.Layout.Palette.ToArray(), value.Layout.Palette.ToArray());
            CollectionAssert.AreEqual(design.Layout.Cells.ToArray(), value.Layout.Cells.ToArray());
            Assert.AreEqual(json, ContentDraftCodec.Encode(decoded));
            using var document = JsonDocument.Parse(json);
            CollectionAssert.AreEqual(new[] { "format_version", "id", "revision", "target", "payload" },
                document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        }
    }

    [TestMethod]
    public void Building_and_functional_composite_drafts_round_trip_without_gameplay_validation()
    {
        foreach (var specification in new C.DesignSpecification[]
        {
            new C.DesignSpecification.Building(null, null, [K.FacilityTag.Assembly]),
            new C.DesignSpecification.FunctionalComposite([new K.PartCapability.Weapon(-1, -2)],
                new(C.RotationAxis.Y, new(0, 0, 0), 180, -180, -1))
        })
        {
            var design = DesignFixtures.Vehicle() with { Specification = specification };
            var saved = Saved(design);
            var json = ContentDraftCodec.Encode(saved);
            Assert.AreEqual(json, ContentDraftCodec.Encode(ContentDraftCodec.Decode(json)));
        }
    }

    [TestMethod]
    public void Default_draft_identity_is_rejected()
    {
        var exception = Assert.ThrowsExactly<DefinitionFormatException>(() =>
            ContentDraftCodec.Encode(Saved(DesignFixtures.Vehicle()) with { Id = default }));
        Assert.AreEqual("invalid_field", exception.Code);
        Assert.AreEqual("/id", exception.Field);
    }

    [TestMethod]
    public void Codec_rejects_unknown_fields_kinds_and_enum_values()
    {
        var json = ContentDraftCodec.Encode(Saved(DesignFixtures.Vehicle()));
        foreach (var mutate in new Action<JsonNode>[]
        {
            root => root["extra"] = true,
            root => root["target"]!["kind"] = "unknown",
            root => root["payload"]!["value"]!["specification"]!["size"] = "unknown",
            root => root["payload"]!["value"]!["layout"]!["extra"] = true
        })
        {
            var root = JsonNode.Parse(json)!;
            mutate(root);
            try { ContentDraftCodec.Decode(root.ToJsonString()); Assert.Fail("应拒绝不可编码字段。"); }
            catch (DefinitionFormatException exception) { Assert.AreEqual("invalid_field", exception.Code); }
            catch (JsonException) { }
        }
        var invalid = DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Vehicle((K.SizeClass)999) };
        Assert.ThrowsExactly<JsonException>(() => ContentDraftCodec.Encode(Saved(invalid)));
    }

    [TestMethod]
    public void Codec_rejects_nonfinite_numbers_and_object_cycles()
    {
        var nonfinite = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.Assembly(new(C.RotationAxis.X, new(0, 0, 0), 0, 1, double.NaN))
        };
        var exception = Assert.ThrowsExactly<DefinitionFormatException>(() => ContentDraftCodec.Encode(Saved(nonfinite)));
        Assert.AreEqual("invalid_field", exception.Code);
        var finite = nonfinite with { Specification = new C.DesignSpecification.Assembly(new(C.RotationAxis.X, new(0, 0, 0), 0, 1, 12345)) };
        var overflow = ContentDraftCodec.Encode(Saved(finite)).Replace("12345", "1e999", StringComparison.Ordinal);
        Assert.AreEqual("invalid_field", Assert.ThrowsExactly<DefinitionFormatException>(() => ContentDraftCodec.Decode(overflow)).Code);
        var backing = new C.NestedDesignItem[1];
        var cycle = DesignFixtures.Vehicle() with
        {
            Layout = new([], [], ImmutableCollectionsMarshal.AsImmutableArray(backing))
        };
        backing[0] = new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, cycle);
        Assert.AreEqual("invalid_field", Assert.ThrowsExactly<DefinitionFormatException>(() => ContentDraftCodec.Encode(Saved(cycle))).Code);
    }

    [TestMethod]
    public void Codec_rejects_null_records_default_arrays_and_null_array_elements()
    {
        var design = DesignFixtures.Vehicle();
        foreach (var invalid in new[]
        {
            design with { Display = null! }, design with { Specification = null! }, design with { Layout = null! },
            design with { Layout = design.Layout with { Cells = default } },
            design with { Layout = design.Layout with { Palette = [null!] } },
            design with { Layout = design.Layout with { NestedItems = [null!] } },
            design with { Layout = design.Layout with { NestedItems = [new C.NestedDesignItem.AssemblyCopy(default, default, null!)] } }
        })
            Assert.AreEqual("invalid_field", Assert.ThrowsExactly<DefinitionFormatException>(() => ContentDraftCodec.Encode(Saved(invalid))).Code);
        var json = ContentDraftCodec.Encode(Saved(design));
        foreach (var mutate in new Action<JsonNode>[]
        {
            root => root["payload"]!["value"]!["display"] = null,
            root => root["payload"]!["value"]!["specification"] = null,
            root => root["payload"]!["value"]!["layout"] = null,
            root => root["payload"]!["value"]!["layout"]!["cells"] = new JsonArray((JsonNode?)null)
        })
        {
            var root = JsonNode.Parse(json)!;
            mutate(root);
            try { ContentDraftCodec.Decode(root.ToJsonString()); Assert.Fail("必须拒绝空结构。"); }
            catch (JsonException) { }
            catch (DefinitionFormatException exception) { Assert.AreEqual("invalid_field", exception.Code); }
        }
    }

    [TestMethod]
    public void Draft_format_is_checked_before_fields_and_duplicate_fields_are_rejected()
    {
        Assert.AreEqual("unsupported_format", Assert.ThrowsExactly<DefinitionFormatException>(() =>
            ContentDraftCodec.Decode("{\"format_version\":2,\"future_field\":true}")).Code);
        var json = ContentDraftCodec.Encode(Saved(DesignFixtures.Vehicle()));
        foreach (var duplicate in new[]
        {
            json.Replace("\"format_version\":1", "\"format_version\":1,\"format_version\":1", StringComparison.Ordinal),
            json.Replace("\"size\":\"small\"", "\"size\":\"small\",\"size\":\"small\"", StringComparison.Ordinal)
        })
            Assert.AreEqual("invalid_json", Assert.ThrowsExactly<DefinitionFormatException>(() => ContentDraftCodec.Decode(duplicate)).Code);
    }

    private static C.SavedContentDraft Saved(C.DesignDraft design, C.DraftTarget? target = null) =>
        new(new(Guid.Parse("16c41dce-4565-4887-bbc3-2a81bb29c5c6")), new(ulong.MaxValue),
            target ?? new C.DraftTarget.LocalBlueprint(null), new C.DraftPayload.Design(design));
}
