using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Loading;
using Train.Content.Persistence;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentCaptureTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    public void Previous_capture_formats_are_rejected(int version)
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var owned = factory.WithSettlementCopies(candidate.View, [Template(41)]);
        var json = JsonNode.Parse(Success(factory.Capture(owned,
            References(owned, "engine") with { Templates = [Template(41)] })).Encode())!;
        json["format_version"] = version;
        var result = factory.DecodeCapture(json.ToJsonString());
        Failure(result, "unsupported_format");
        Assert.IsTrue(((C.ContentResult<C.IContentCapture>.Failure)result).Diagnostics.Any(d => d.Field == "/format_version"));
    }

    [TestMethod]
    public void Capture_round_trips_designs_and_owner_copies_without_derived_state()
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var owned = factory.WithSettlementCopies(candidate.View, [Template(41), Template(42)]);
        var references = References(owned, "engine") with { Templates = [Template(42), Template(41)] };
        var capture = Success(factory.Capture(owned, references));
        var payload = capture.Encode();
        var decoded = Success(factory.DecodeCapture(payload));
        Assert.AreEqual(owned.PersistentRevision, decoded.Revision);
        CollectionAssert.AreEqual(new[] { 41UL, 42UL }, decoded.Templates.Select(t => t.Owner.Value).ToArray());
        Assert.AreEqual(2, decoded.Designs.Length);
        Assert.AreEqual(payload, decoded.Encode());
        Assert.AreEqual(payload, capture.Encode());
        Assert.AreEqual(payload, Success(factory.Capture(owned, references with
        {
            Designs = references.Designs.Reverse().ToImmutableArray(), Templates = references.Templates.Reverse().ToImmutableArray()
        })).Encode());
        var json = JsonNode.Parse(payload)!.AsObject();
        CollectionAssert.AreEquivalent(new[] { "format_version", "revision", "packages", "designs", "templates", "assembly_displays", "local_blueprints", "local_drafts" }, json.Select(p => p.Key).ToArray());
        foreach (var item in json["designs"]!.AsArray())
        {
            CollectionAssert.AreEquivalent(new[] { "hash", "definition" }, item!.AsObject().Select(p => p.Key).ToArray());
        }
    }

    [TestMethod]
    public void Capture_rejects_missing_design_owner_or_plot_pairing()
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var owned = factory.WithSettlementCopies(candidate.View, [Template(41)]);
        var references = References(owned, "engine");
        Failure(factory.Capture(owned, references with { Designs = [new(new string('A', 64))] }), "inconsistent_capture");
        Failure(factory.Capture(owned, references with { Templates = [Template(42)] }), "inconsistent_capture");
        Failure(factory.Capture(owned, references with { Templates = [Template(41) with { Plots = [new(7, K.PlotSize.Small)] }] }), "inconsistent_capture");
    }

    [TestMethod]
    public void Decode_rejects_broken_json_version_hash_and_missing_embedded_scene()
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var owned = factory.WithSettlementCopies(candidate.View, [Template(41)]);
        var payload = Success(factory.Capture(owned, References(owned, "engine") with { Templates = [Template(41)] })).Encode();
        Failure(factory.DecodeCapture("{"), "invalid_json");
        foreach (var mutation in new[] { "version", "hash", "scene", "duplicate" })
        {
            var json = JsonNode.Parse(payload)!;
            switch (mutation)
            {
                case "version": json["format_version"] = 2; json["future_field"] = true; break;
                case "hash": json["designs"]![0]!["hash"] = new string('A', 64); break;
                case "scene": json["designs"] = new JsonArray(); break;
                case "duplicate": json["templates"]!.AsArray().Add(json["templates"]![0]!.DeepClone()); break;
            }
            Failure(factory.DecodeCapture(json.ToJsonString()), mutation == "version" ? "unsupported_format" : "inconsistent_capture");
        }
    }

    [TestMethod]
    public void Decode_reports_full_nested_design_field()
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var json = JsonNode.Parse(Success(factory.Capture(candidate.View, References(candidate.View, "engine"))).Encode())!;
        json["designs"]![0]!["definition"]!["layout"]!["palette"]![0]!["voxel"] = 17;
        var result = factory.DecodeCapture(json.ToJsonString());
        Failure(result, "inconsistent_capture");
        Assert.IsTrue(((C.ContentResult<C.IContentCapture>.Failure)result).Diagnostics.Any(d =>
            d.Field == "/designs/0/definition/layout/palette/0/voxel"));
    }

    [TestMethod]
    public void Local_library_and_incomplete_drafts_round_trip_without_becoming_runtime_designs()
    {
        var fixture = DesignFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var draft = new C.SavedContentDraft(new(Guid.Parse("22222222-2222-4222-8222-222222222222")), new(7),
            new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(12))),
            new C.DraftPayload.Design(DesignFixtures.Vehicle() with { Specification = new C.DesignSpecification.Vehicle(null) }));
        var blueprint = new CapturedBlueprint(new(C.BlueprintLibrary.Local, new("11111111-1111-4111-8111-111111111111")),
            DesignFixtures.Vehicle(), []);
        var references = new K.CoreReferenceManifest([], [], [], [initial.View.Core.Identity]);
        var view = ((ContentView)initial.View).WithCapture(new ContentCapture(new(4), [], [], [], [], [blueprint], [draft]), references);
        var payload = Success(factory.Capture(view, references)).Encode();
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        CollectionAssert.AreEqual(new[] { "format_version", "revision", "packages", "designs", "assembly_displays", "templates", "local_blueprints", "local_drafts" },
            json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(5, json.RootElement.GetProperty("format_version").GetInt32());
        Assert.AreEqual(0, json.RootElement.GetProperty("designs").GetArrayLength());
        var decoded = Success(factory.DecodeCapture(payload));
        Assert.AreEqual(payload, decoded.Encode());
        using var restored = ContentRestoreTests.Restore(fixture, decoded, references);
        var restoredBlueprint = ((C.ContentLookup<C.BlueprintRecord>.Found)restored.View.FindBlueprint(blueprint.Address)).Value;
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(restoredBlueprint.Evaluation);
        Assert.AreEqual(draft.Id, restored.View.LocalDrafts.Single().Id);
        Assert.AreEqual(draft.Revision, restored.View.LocalDrafts.Single().Revision);
        Assert.AreEqual(new C.DesignSpecification.Vehicle(null), ((C.DraftPayload.Design)restored.View.LocalDrafts.Single().Payload).Value.Specification);
        Assert.IsTrue(decoded.ReferencedPackages.Any(p => p.Id.Value == "core"));
        var revised = view.WithCapture(new ContentCapture(new(5), [], [], [], [], [blueprint], [draft with { Revision = new(8) }]), references);
        Assert.AreEqual(view.Core.Identity, revised.Core.Identity);
        var replay = Success(factory.CaptureReplay([view, revised], references));
        var history = Success(factory.DecodeReplay(replay.Encode())).History;
        Assert.AreEqual(2, history.Length);
        Assert.AreEqual(draft.Id, history[1].LocalDrafts.Single().Id);
        Assert.AreEqual(new C.EditRevision(8), history[1].LocalDrafts.Single().Revision);
        Assert.AreNotEqual(history[0].PersistentRevision, history[1].PersistentRevision);
        Assert.IsInstanceOfType<C.ContentLookup<C.BlueprintRecord>.Found>(history[1].FindBlueprint(blueprint.Address));
    }

    [TestMethod]
    public void Capture_sorts_multiple_local_blueprints_and_drafts_and_restores_the_same_order()
    {
        var fixture = DesignFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var firstBlueprint = new CapturedBlueprint(new(C.BlueprintLibrary.Local, new("11111111-1111-4111-8111-111111111111")),
            DesignFixtures.Vehicle(), []);
        var secondBlueprint = firstBlueprint with { Address = new(C.BlueprintLibrary.Local, new("22222222-2222-4222-8222-222222222222")) };
        var firstDraft = new C.SavedContentDraft(new(Guid.Parse("33333333-3333-4333-8333-333333333333")), new(1),
            new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(12))), new C.DraftPayload.Design(DesignFixtures.Vehicle()));
        var secondDraft = firstDraft with
        {
            Id = new(Guid.Parse("44444444-4444-4444-8444-444444444444")),
            Target = new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(13)))
        };
        var references = new K.CoreReferenceManifest([], [], [], [initial.View.Core.Identity]);
        var view = ((ContentView)initial.View).WithCapture(new ContentCapture(new(4), [], [], [], [],
            [secondBlueprint, firstBlueprint], [secondDraft, firstDraft]), references);
        var payload = Success(factory.Capture(view, references)).Encode();
        var json = JsonNode.Parse(payload)!;
        CollectionAssert.AreEqual(new[] { "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222" },
            json["local_blueprints"]!.AsArray().Select(b => b!["id"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(new[] { "33333333-3333-4333-8333-333333333333", "44444444-4444-4444-8444-444444444444" },
            json["local_drafts"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()).ToArray());
        var decoded = Success(factory.DecodeCapture(payload));
        Assert.AreEqual(payload, decoded.Encode());
        using var restored = ContentRestoreTests.Restore(fixture, decoded, references);
        CollectionAssert.AreEqual(new[] { firstDraft.Id, secondDraft.Id }, restored.View.LocalDrafts.Select(d => d.Id).ToArray());
        CollectionAssert.AreEqual(new[] { firstBlueprint.Address, secondBlueprint.Address },
            restored.View.Blueprints.Where(b => b.Address.Library == C.BlueprintLibrary.Local).Select(b => b.Address).ToArray());
    }

    [TestMethod]
    [DataRow("invalid_record")]
    [DataRow("invalid_design")]
    [DataRow("duplicate_id")]
    public void Decode_rejects_malformed_local_blueprints_as_inconsistent_capture(string mutation)
    {
        using var initial = DesignFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var payload = JsonNode.Parse(Success(factory.Capture(initial.View, new([], [], [], [initial.View.Core.Identity]))).Encode())!;
        var blueprint = DesignFixtures.Blueprint("11111111-1111-4111-8111-111111111111");
        blueprint["used_packages"] = new JsonArray();
        blueprint["layout"]!["palette"]![0]!["voxel"] = "core:block";
        var blueprints = payload["local_blueprints"]!.AsArray();
        blueprints.Add(blueprint);
        if (mutation == "invalid_record") blueprints[0] = 17;
        else if (mutation == "invalid_design") blueprint["layout"]!["palette"]![0]!["voxel"] = 17;
        else blueprints.Add(blueprint.DeepClone());
        Failure(factory.DecodeCapture(payload.ToJsonString()), "inconsistent_capture");
    }

    [TestMethod]
    [DataRow("duplicate_id")]
    [DataRow("duplicate_instance")]
    [DataRow("user_target")]
    [DataRow("bad_codec")]
    public void Capture_rejects_invalid_local_draft_records(string mutation)
    {
        using var initial = DesignFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var payload = JsonNode.Parse(Success(factory.Capture(initial.View, new([], [], [], [initial.View.Core.Identity]))).Encode())!;
        var draft = new C.SavedContentDraft(new(Guid.Parse("22222222-2222-4222-8222-222222222222")), new(7),
            new C.DraftTarget.Instance(new K.InstanceId.Vehicle(new(12))), new C.DraftPayload.Design(DesignFixtures.Vehicle()));
        var record = JsonNode.Parse(Success(factory.EncodeDraft(draft)))!;
        var drafts = payload["local_drafts"]!.AsArray();
        drafts.Add(record);
        if (mutation is "duplicate_id" or "duplicate_instance")
        {
            var copy = record.DeepClone();
            if (mutation == "duplicate_instance") copy["id"] = "33333333-3333-4333-8333-333333333333";
            drafts.Add(copy);
        }
        else if (mutation == "user_target") record["target"] = JsonNode.Parse("""{"kind":"user_blueprint","id":null}""");
        else record["payload"]!["kind"] = "definition";
        Failure(factory.DecodeCapture(payload.ToJsonString()), "inconsistent_capture");
    }

    internal static K.OwnedSettlementTemplateReference Template(ulong owner) => new(new(owner), new("core:farm-start"),
        [new(7, K.PlotSize.SmallStation), new(11, K.PlotSize.Medium)]);
    internal static K.CoreReferenceManifest References(C.IContentView view, params string[] blueprints) => new(
        blueprints.Select(id => ((C.ContentLookup<C.BlueprintRecord>.Found)view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:" + id)))).Value)
            .Select(record => ((C.DesignEvaluation.Complete)record.Evaluation).Design).ToImmutableArray(), [], [], [view.Core.Identity]);
    internal static T Success<T>(C.ContentResult<T> result) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Success>(result, result.ToString());
        return ((C.ContentResult<T>.Success)result).Value;
    }
    internal static void Failure<T>(C.ContentResult<T> result, string code) where T : notnull
    {
        Assert.IsInstanceOfType<C.ContentResult<T>.Failure>(result, result.ToString());
        Assert.IsTrue(((C.ContentResult<T>.Failure)result).Diagnostics.Any(d => d.Code == code), result.ToString());
    }
}
