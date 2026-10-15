using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentCaptureTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
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
        CollectionAssert.AreEquivalent(new[] { "format_version", "revision", "packages", "designs", "templates", "assembly_displays" }, json.Select(p => p.Key).ToArray());
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
