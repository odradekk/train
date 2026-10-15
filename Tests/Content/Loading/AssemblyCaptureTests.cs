using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class AssemblyCaptureTests
{
    [TestMethod]
    public void Capture_keeps_assembly_display_design_without_instance_references()
    {
        var fixture = Content();
        using var candidate = fixture.Load();
        var factory = ContentFactory.Create();
        var blueprint = ((C.ContentLookup<C.BlueprintRecord>.Found)candidate.View.FindBlueprint(
            new(C.BlueprintLibrary.Packages, new("core:assembly")))).Value;
        var evaluation = (C.DesignEvaluation.Complete)blueprint.Evaluation;
        var capture = Success(factory.Capture(candidate.View, EmptyReferences(candidate.View)));
        CollectionAssert.Contains(capture.Designs.ToArray(), evaluation.Design);
        var json = JsonNode.Parse(capture.Encode())!;
        Assert.AreEqual(4, json["format_version"]!.GetValue<int>());
        Assert.AreEqual(evaluation.Composition.Value, json["assembly_displays"]![0]!["composition"]!.GetValue<string>());
        Assert.AreEqual(evaluation.Design.Value, json["assembly_displays"]![0]!["design"]!.GetValue<string>());
        Assert.AreEqual(capture.Encode(), Success(factory.DecodeCapture(capture.Encode())).Encode());
    }

    [TestMethod]
    public void Decode_rejects_missing_duplicate_or_mismatched_assembly_display_records()
    {
        using var candidate = Content().Load();
        var factory = ContentFactory.Create();
        var payload = Success(factory.Capture(candidate.View, EmptyReferences(candidate.View))).Encode();
        foreach (var mutation in new[] { "missing", "duplicate", "composition", "design" })
        {
            var json = JsonNode.Parse(payload)!;
            var displays = json["assembly_displays"]!.AsArray();
            switch (mutation)
            {
                case "missing": displays.Clear(); break;
                case "duplicate": displays.Add(displays[0]!.DeepClone()); break;
                case "composition": displays[0]!["composition"] = new string('A', 64); break;
                case "design": displays[0]!["design"] = new string('A', 64); break;
            }
            Failure(factory.DecodeCapture(json.ToJsonString()), "inconsistent_capture");
        }
    }

    [TestMethod]
    public void Capture_preserves_root_functional_definition_identity_and_rejects_missing_identity()
    {
        var fixture = DesignFixtures.Content();
        var functional = DesignFixtures.Blueprint("functional");
        functional["category"] = "large_equipment";
        functional["simulation"] = JsonNode.Parse("""{"behaviors":{"control":{}}}""");
        WorldContentFixtures.AddBlueprint(fixture, functional);
        using var candidate = fixture.Load();
        var factory = ContentFactory.Create();
        var payload = Success(factory.Capture(candidate.View, References(candidate.View, "functional"))).Encode();
        var json = JsonNode.Parse(payload)!;
        Assert.AreEqual("core:functional", json["designs"]![0]!["composite"]!.GetValue<string>());
        Assert.AreEqual(payload, Success(factory.DecodeCapture(payload)).Encode());
        json["designs"]![0]!.AsObject().Remove("composite");
        Failure(factory.DecodeCapture(json.ToJsonString()), "inconsistent_capture");
    }

    private static ContentFixture Content()
    {
        var fixture = DesignFixtures.Content();
        var assembly = DesignFixtures.Blueprint("assembly");
        assembly["category"] = "large_equipment";
        assembly.Remove("simulation");
        WorldContentFixtures.AddBlueprint(fixture, assembly);
        return fixture;
    }

    private static K.CoreReferenceManifest EmptyReferences(C.IContentView view) => new([], [], [], [view.Core.Identity]);
}
