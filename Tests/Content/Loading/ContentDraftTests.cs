using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            var json = Encode(saved);
            var decoded = Decode(json);
            Assert.AreEqual(saved.Id, decoded.Id);
            Assert.AreEqual(saved.Revision, decoded.Revision);
            Assert.AreEqual(target, decoded.Target);
            var value = ((C.DraftPayload.Design)decoded.Payload).Value;
            Assert.AreEqual(design.Display, value.Display);
            Assert.AreEqual(design.Specification, value.Specification);
            CollectionAssert.AreEqual(design.Layout.Palette.ToArray(), value.Layout.Palette.ToArray());
            CollectionAssert.AreEqual(design.Layout.Cells.ToArray(), value.Layout.Cells.ToArray());
            Assert.AreEqual(json, Encode(decoded));
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
            new C.DesignSpecification.FunctionalComposite([new K.PartCapability.Weapon(1, 2)],
                new(C.RotationAxis.Y, new(0, 0, 0), -180, 180, 1))
        })
        {
            var design = DesignFixtures.Vehicle() with { Specification = specification };
            var saved = Saved(design);
            var json = Encode(saved);
            Assert.AreEqual(json, Encode(Decode(json)));
        }
    }

    [TestMethod]
    public void Default_draft_identity_is_rejected()
    {
        var result = ContentFactory.Create().EncodeDraft(Saved(DesignFixtures.Vehicle()) with { Id = default });
        Failure(result, "invalid_field");
        Assert.AreEqual("/id", ((C.ContentResult<string>.Failure)result).Diagnostics.Single().Field);
    }

    [TestMethod]
    public void Codec_rejects_unknown_fields_kinds_and_enum_values()
    {
        var json = Encode(Saved(DesignFixtures.Vehicle()));
        foreach (var mutate in new Action<JsonNode>[]
        {
            root => root["extra"] = true,
            root => root["target"]!["kind"] = "unknown",
            root => root["payload"]!["value"]!["simulation"]!["size_class"] = "unknown",
            root => root["payload"]!["value"]!["layout"]!["extra"] = true
        })
        {
            var root = JsonNode.Parse(json)!;
            mutate(root);
            Failure(ContentFactory.Create().DecodeDraft(root.ToJsonString()), "invalid_field");
        }
        var invalid = DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Vehicle((K.SizeClass)999) };
        Failure(ContentFactory.Create().EncodeDraft(Saved(invalid)), "invalid_field");
    }

    [TestMethod]
    public void Codec_rejects_nonfinite_numbers_and_object_cycles()
    {
        var nonfinite = DesignFixtures.Vehicle() with
        {
            Specification = new C.DesignSpecification.Assembly(new(C.RotationAxis.X, new(0, 0, 0), 0, 1, double.NaN))
        };
        Failure(ContentFactory.Create().EncodeDraft(Saved(nonfinite)), "invalid_field");
        var finite = nonfinite with { Specification = new C.DesignSpecification.Assembly(new(C.RotationAxis.X, new(0, 0, 0), 0, 1, 12345)) };
        var overflow = Encode(Saved(finite)).Replace("12345", "1e999", StringComparison.Ordinal);
        Failure(ContentFactory.Create().DecodeDraft(overflow), "invalid_field");
        var backing = new C.NestedDesignItem[1];
        var cycle = DesignFixtures.Vehicle() with
        {
            Layout = new([], [], ImmutableCollectionsMarshal.AsImmutableArray(backing))
        };
        backing[0] = new C.NestedDesignItem.AssemblyCopy(new(0, 0, 0), C.VoxelOrientation.PxPy, cycle);
        Failure(ContentFactory.Create().EncodeDraft(Saved(cycle)), "invalid_field");
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
            design with { Layout = design.Layout with { Cells = [null!] } },
            design with { Specification = new C.DesignSpecification.FunctionalComposite([null!], null) },
            design with { Specification = new C.DesignSpecification.FunctionalComposite(default, null) },
            design with { Specification = new C.DesignSpecification.Building(null, null, default) },
            design with { Layout = design.Layout with { NestedItems = [null!] } },
            design with { Layout = design.Layout with { NestedItems = [new C.NestedDesignItem.AssemblyCopy(default, default, null!)] } }
        })
            Failure(ContentFactory.Create().EncodeDraft(Saved(invalid)), "invalid_field");
        var json = Encode(Saved(design));
        foreach (var mutate in new Action<JsonNode>[]
        {
            root => root["payload"]!["value"]!["display"] = null,
            root => root["payload"]!["value"]!["category"] = null,
            root => root["payload"]!["value"]!["layout"] = null,
            root => root["payload"]!["value"]!["layout"]!["cells"] = new JsonArray((JsonNode?)null)
        })
        {
            var root = JsonNode.Parse(json)!;
            mutate(root);
            Failure(ContentFactory.Create().DecodeDraft(root.ToJsonString()), "invalid_field");
        }
    }

    [TestMethod]
    public void Draft_format_is_checked_before_fields_and_duplicate_fields_are_rejected()
    {
        Failure(ContentFactory.Create().DecodeDraft("{\"format_version\":2,\"future_field\":true}"), "unsupported_format");
        var json = Encode(Saved(DesignFixtures.Vehicle()));
        foreach (var duplicate in new[]
        {
            json.Replace("\"format_version\":1", "\"format_version\":1,\"format_version\":1", StringComparison.Ordinal),
            json.Replace("\"size_class\":\"small\"", "\"size_class\":\"small\",\"size_class\":\"small\"", StringComparison.Ordinal)
        })
            Failure(ContentFactory.Create().DecodeDraft(duplicate), "invalid_json");
    }

    [TestMethod]
    public void V3_nested_shapes_and_paints_round_trip_and_discriminators_ignore_field_order()
    {
        var child = DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Assembly(null) };
        var design = DesignFixtures.Vehicle() with
        {
            Layout = new([DesignFixtures.Entry() with { Shape = new("wedge"), Paint = new("red") }],
                [new(new(0, 0, 0), 0)],
                [new C.NestedDesignItem.AssemblyCopy(new(1, 2, 3), C.VoxelOrientation.PxPy, child),
                 new C.NestedDesignItem.FunctionalCompositeReference(new(2, 3, 4), C.VoxelOrientation.PxPy, new("core:turret"), new("blue"))])
        };
        var json = Encode(Saved(design));
        var decoded = ((C.DraftPayload.Design)Decode(json).Payload).Value;
        CollectionAssert.AreEqual(design.Layout.Palette.ToArray(), decoded.Layout.Palette.ToArray());
        Assert.AreEqual(2, decoded.Layout.NestedItems.Length);
        var copy = (C.NestedDesignItem.AssemblyCopy)decoded.Layout.NestedItems[0];
        Assert.AreEqual(new C.GridPosition(1, 2, 3), copy.At);
        Assert.AreEqual(child.Display, copy.Design.Display);
        Assert.AreEqual(child.Specification, copy.Design.Specification);
        CollectionAssert.AreEqual(child.Layout.Cells.ToArray(), copy.Design.Layout.Cells.ToArray());
        Assert.AreEqual(design.Layout.NestedItems[1], decoded.Layout.NestedItems[1]);
        Assert.AreEqual(json, Encode(Decode(json)));
        var root = JsonNode.Parse(json)!;
        var payload = root["payload"]!.AsObject();
        payload.Remove("kind");
        payload["kind"] = "design";
        Assert.AreEqual(json, Encode(Decode(root.ToJsonString())));
        payload.Remove("kind");
        Failure(ContentFactory.Create().DecodeDraft(root.ToJsonString()), "invalid_field");
        using var loaded = DesignFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var capture = Success(factory.Capture(loaded.View, new([], [], [], [loaded.View.Core.Identity])));
        var captured = JsonNode.Parse(capture.Encode())!;
        captured["local_drafts"] = new JsonArray(root.DeepClone());
        Failure(factory.DecodeCapture(captured.ToJsonString()), "inconsistent_capture");
        Failure(factory.DecodeDraft("{"), "invalid_json");
        foreach (var mutate in new Action<JsonNode>[]
        {
            node => node["id"] = "16C41DCE-4565-4887-BBC3-2A81BB29C5C6",
            node => node["target"]!["id"] = "20B0E2D8-9171-419F-A309-57D064023BFB",
            node => node["payload"]!["value"]!["simulation"]!["size_class"] = "Small"
        })
        {
            var malformed = JsonNode.Parse(json)!;
            mutate(malformed);
            Failure(factory.DecodeDraft(malformed.ToJsonString()), "invalid_field");
        }
        Failure(factory.EncodeDraft(Saved(design, new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(0))))), "invalid_field");
    }

    private static string Encode(C.SavedContentDraft draft) => Success(ContentFactory.Create().EncodeDraft(draft));
    private static C.SavedContentDraft Decode(string json) => Success(ContentFactory.Create().DecodeDraft(json));
    private static T Success<T>(C.ContentResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Success>(result, result.ToString());
        return ((C.ContentResult<T>.Success)result).Value;
    }
    private static void Failure<T>(C.ContentResult<T> result, string code) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Failure>(result, result.ToString());
        Assert.IsTrue(((C.ContentResult<T>.Failure)result).Diagnostics.Any(d => d.Code == code), result.ToString());
    }

    private static C.SavedContentDraft Saved(C.DesignDraft design, C.DraftTarget? target = null) =>
        new(new(Guid.Parse("16c41dce-4565-4887-bbc3-2a81bb29c5c6")), new(ulong.MaxValue),
            target ?? new C.DraftTarget.LocalBlueprint(null), new C.DraftPayload.Design(design));
}
