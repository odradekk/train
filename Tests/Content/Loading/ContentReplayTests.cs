using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using static Train.Content.Tests.Loading.ContentRestoreTests;
using static Train.Content.Tests.Loading.WorldContentFixtures;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentReplayTests
{
    [TestMethod]
    public void Version_one_replay_content_with_plot_sizes_is_rejected()
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var json = JsonNode.Parse(Success(factory.CaptureReplay([candidate.View], References(candidate.View, "engine"))).Encode())!;
        json["format_version"] = 1;
        var result = factory.DecodeReplay(json.ToJsonString());
        Failure(result, "unsupported_format");
        Assert.IsTrue(((C.ContentResult<C.IContentReplayCapture>.Failure)result).Diagnostics.Any(d => d.Field == "/format_version"));
    }

    [TestMethod]
    public void Replay_restores_fixed_definitions_layout_appearance_and_asset_sources_after_installation_changes()
    {
        var fixture = WorldContentFixtures.Content();
        ProcessLoadingTests.Add(fixture, ProcessLoadingTests.Process("extraction"));
        using var candidate = fixture.Load();
        var factory = ContentFactory.Create();
        var original = factory.WithSettlementCopies(candidate.View, [Template(41)]);
        var references = References(original, "engine") with { Templates = [Template(41)] };
        var appearance = Found(original.FindVoxelAppearance(new("core:machine")));
        var cube = (C.VoxelPresentation.Cube)appearance.Presentation;
        var asset = Found(original.FindAsset(cube.Top));
        var layout = Found(original.FindSettlementLayout(new(41)));
        var saved = Success(factory.Capture(original, references)).Encode();
        var process = original.Core.Processes.Single();
        var replay = Success(factory.CaptureReplay([original], references));
        fixture.RemoveEntry(ContentFixture.BuiltIn, ProcessLoadingTests.Path);
        Change(fixture, "resources/voxels/machine.voxel.json", json => json["simulation"]!["max_durability"] = 2);
        fixture.RemoveEntry(ContentFixture.BuiltIn, TemplatePath);
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/engine.blueprint.json");
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/machine/tile.png", [21, 22], new C.AssetMetadata.Png(64, 64));
        candidate.Dispose();

        var decoded = Success(factory.DecodeReplay(replay.Encode()));
        var restored = decoded.History.Single();
        Assert.AreEqual(original.Core.Identity, restored.Core.Identity);
        Assert.AreEqual(process.Revision, restored.Core.Processes.Single().Revision);
        Assert.AreEqual(process.Details, restored.Core.Processes.Single().Details);
        Assert.AreEqual(original.Generation, restored.Generation);
        Assert.AreEqual(original.PersistentRevision, restored.PersistentRevision);
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(Found(restored.FindDesign(references.Designs.Single())));
        Assert.AreEqual(saved, Success(factory.Capture(restored, references)).Encode());
        var restoredLayout = Found(restored.FindSettlementLayout(new(41)));
        Assert.AreEqual(layout.SourceTemplate, restoredLayout.SourceTemplate);
        Assert.AreEqual(layout.Display, restoredLayout.Display);
        Assert.AreEqual(layout.SceneModel.Design, restoredLayout.SceneModel.Design);
        CollectionAssert.AreEqual(layout.Plots.ToArray(), restoredLayout.Plots.ToArray());
        var restoredAppearance = Found(restored.FindVoxelAppearance(new("core:machine")));
        Assert.AreEqual(appearance.AllowedOrientations, restoredAppearance.AllowedOrientations);
        Assert.AreEqual(appearance.Collision, restoredAppearance.Collision);
        Assert.AreEqual(appearance.Behaviors, restoredAppearance.Behaviors);
        var restoredCube = (C.VoxelPresentation.Cube)restoredAppearance.Presentation;
        Assert.AreEqual(cube.Material, restoredCube.Material);
        Assert.AreEqual(cube.TileSpan, restoredCube.TileSpan);
        CollectionAssert.AreEqual(cube.Shapes.ToArray(), restoredCube.Shapes.ToArray());
        Assert.AreEqual(cube.Top, restoredCube.Top);
        Assert.AreEqual(cube.Side, restoredCube.Side);
        Assert.AreEqual(cube.Bottom, restoredCube.Bottom);
        Assert.AreEqual(cube.Front, restoredCube.Front);
        var restoredAsset = Found(restored.FindAsset(cube.Top));
        CollectionAssert.AreEqual(asset.Bytes.ToArray(), restoredAsset.Bytes.ToArray());
        Assert.AreEqual(asset.Metadata, restoredAsset.Metadata);
        CollectionAssert.AreEqual(asset.Sources.ToArray(), restoredAsset.Sources.ToArray());
        Assert.AreEqual(replay.Encode(), decoded.Encode());
    }

    [TestMethod]
    public void Replay_keeps_same_input_views_in_history_order_with_distinct_persistent_revisions()
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var initial = candidate.View;
        var owned = factory.WithSettlementCopies(initial, [Template(41)]);
        Assert.AreEqual(initial.Core.Identity, owned.Core.Identity);
        Assert.AreNotEqual(initial.PersistentRevision, owned.PersistentRevision);
        var references = References(owned, "engine") with { Templates = [Template(41)] };
        var replay = Success(factory.DecodeReplay(Success(factory.CaptureReplay([owned, initial, owned], references)).Encode()));
        Assert.AreEqual(3, replay.History.Length);
        CollectionAssert.AreEqual(new[] { owned.PersistentRevision, initial.PersistentRevision, owned.PersistentRevision },
            replay.History.Select(view => view.PersistentRevision).ToArray());
        Assert.IsInstanceOfType<C.ContentLookup<C.SettlementLayoutProjection>.Found>(replay.History[0].FindSettlementLayout(new(41)));
        Assert.IsInstanceOfType<C.ContentLookup<C.SettlementLayoutProjection>.NotFound>(replay.History[1].FindSettlementLayout(new(41)));
        Assert.IsInstanceOfType<C.ContentLookup<C.SettlementLayoutProjection>.Found>(replay.History[2].FindSettlementLayout(new(41)));
    }

    [TestMethod]
    public void Replay_includes_disposed_unactivated_candidates_and_preserves_distinct_input_order()
    {
        var fixture = WorldContentFixtures.Content();
        using var firstCandidate = fixture.Load();
        var first = firstCandidate.View;
        Change(fixture, "resources/voxels/machine.voxel.json", json => json["simulation"]!["max_durability"] = 2);
        using var secondCandidate = fixture.Load();
        var second = secondCandidate.View;
        Assert.AreNotEqual(first.Core.Identity, second.Core.Identity);
        firstCandidate.Dispose();
        secondCandidate.Dispose();
        var references = References(first, "engine") with { Inputs = [first.Core.Identity, second.Core.Identity] };
        var factory = ContentFactory.Create();
        var replay = Success(factory.DecodeReplay(Success(factory.CaptureReplay([second, first], references)).Encode()));
        CollectionAssert.AreEqual(new[] { second.Core.Identity, first.Core.Identity }, replay.History.Select(view => view.Core.Identity).ToArray());
        Assert.AreEqual(2L, replay.History[0].Core.Designs.Single(d => d.Design == references.Designs.Single()).Parts.Single().MaxDurability);
        Assert.AreEqual(5L, replay.History[1].Core.Designs.Single(d => d.Design == references.Designs.Single()).Parts.Single().MaxDurability);
        Failure(factory.CaptureReplay([first], references), "inconsistent_capture");
        Failure(factory.CaptureReplay([first], references with { Inputs = [first.Core.Identity], Designs = [new(new string('A', 64))] }), "inconsistent_capture");
        Failure(factory.CaptureReplay([first], references with { Inputs = [first.Core.Identity], Templates = [Template(41)] }), "inconsistent_capture");
    }

    [TestMethod]
    public void Decode_replay_rejects_broken_json_unsupported_version_and_corrupted_or_missing_blobs()
    {
        using var candidate = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var payload = Success(factory.CaptureReplay([candidate.View], References(candidate.View, "engine"))).Encode();
        Failure(factory.DecodeReplay("{"), "invalid_json");
        foreach (var mutation in new[] { "version", "hash", "bytes", "missing", "metadata", "input" })
        {
            var json = JsonNode.Parse(payload)!;
            switch (mutation)
            {
                case "version": json["format_version"] = 3; json["future_field"] = true; break;
                case "input": json["history"]![0]!["input"] = new string('A', 64); break;
                case "hash": json["blobs"]![0]!["hash"] = new string('A', 64); break;
                case "bytes": json["blobs"]![0]!["bytes"] = Convert.ToBase64String([255, 254]); break;
                case "missing": json["blobs"]!.AsArray().RemoveAt(0); break;
                case "metadata": json["history"]![0]!["source"]!["inspections"]![0]!["metadata"]!["width"] = "invalid"; break;
            }
            Failure(factory.DecodeReplay(json.ToJsonString()), mutation == "version" ? "unsupported_format" : "inconsistent_capture");
        }
    }

    [TestMethod]
    public void Replay_preserves_glb_inspection_and_deduplicates_shared_bytes_across_views()
    {
        var fixture = DesignFixtures.Content();
        DesignFixtures.AddMeshVoxel(fixture);
        using var candidate = fixture.Load();
        var factory = ContentFactory.Create();
        var appearance = (C.VoxelPresentation.Mesh)Found(candidate.View.FindVoxelAppearance(new("core:model"))).Presentation;
        var asset = Found(candidate.View.FindAsset(appearance.Model));
        var capture = Success(factory.CaptureReplay([candidate.View, candidate.View], References(candidate.View)));
        var payload = capture.Encode();
        var json = JsonNode.Parse(payload)!;
        var inspections = json["history"]![0]!["source"]!["inspections"]!.AsArray();
        var modelInspection = inspections.Single(i => i!["metadata_kind"]!.GetValue<string>() == "glb")!;
        Assert.AreEqual("glb", modelInspection["kind"]!.GetValue<string>());
        var metadata = modelInspection["metadata"]!;
        CollectionAssert.AreEquivalent(new[] { "mesh_node_count", "surfaces", "bounds", "has_skeleton", "has_morph_targets", "has_animation", "has_camera", "has_light", "has_external_images" }, metadata.AsObject().Select(p => p.Key).ToArray());
        Assert.AreEqual("opaque", metadata["surfaces"]![0]!.GetValue<string>());
        Assert.AreEqual(-0.25, metadata["bounds"]!["minimum_meters"]!["x"]!.GetValue<double>());
        Assert.AreEqual(0.5, metadata["bounds"]!["maximum_meters"]!["y"]!.GetValue<double>());
        var png = inspections.First(i => i!["metadata_kind"]!.GetValue<string>() == "png")!;
        Assert.AreEqual("png", png["kind"]!.GetValue<string>());
        CollectionAssert.AreEquivalent(new[] { "width", "height" }, png["metadata"]!.AsObject().Select(p => p.Key).ToArray());
        var hashes = json["blobs"]!.AsArray().Select(blob => blob!["hash"]!.GetValue<string>()).ToArray();
        Assert.AreEqual(hashes.Length, hashes.Distinct().Count());
        var restored = Success(factory.DecodeReplay(payload)).History[1];
        var recovered = Found(restored.FindAsset(appearance.Model));
        CollectionAssert.AreEqual(asset.Bytes.ToArray(), recovered.Bytes.ToArray());
        var expected = (C.AssetMetadata.Glb)asset.Metadata;
        var actual = (C.AssetMetadata.Glb)recovered.Metadata;
        Assert.AreEqual(expected.Bounds, actual.Bounds);
        Assert.AreEqual(expected.MeshNodeCount, actual.MeshNodeCount);
        CollectionAssert.AreEqual(expected.Surfaces.ToArray(), actual.Surfaces.ToArray());
        metadata["surfaces"]![0] = "unknown_alpha_mode";
        var invalid = factory.DecodeReplay(json.ToJsonString());
        Failure(invalid, "inconsistent_capture");
        Assert.IsTrue(((C.ContentResult<C.IContentReplayCapture>.Failure)invalid).Diagnostics.Any(d =>
            d.Field.StartsWith("/history/0/source/inspections/", StringComparison.Ordinal) && d.Field.EndsWith("/metadata/surfaces/0", StringComparison.Ordinal)));

    }

    [TestMethod]
    public void Replay_restores_missing_mod_placeholder_and_its_missing_references()
    {
        var fixture = ModContent();
        using var original = fixture.Load();
        var blueprint = Found(original.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new(ModId + ":engine"))));
        var design = ((C.DesignEvaluation.Complete)blueprint.Evaluation).Design;
        var references = new K.CoreReferenceManifest([design], [], [], [original.View.Core.Identity]);
        var factory = ContentFactory.Create();
        var capture = Success(factory.Capture(original.View, references));
        fixture.RemoveEntry(ContentFixture.Mods, "equipment");
        using var restored = Restore(fixture, capture, references);
        var partial = (C.DesignEvaluation.Partial)Found(restored.View.FindDesign(design));
        var replayReferences = references with { Inputs = [restored.View.Core.Identity] };
        var replay = Success(factory.CaptureReplay([restored.View], replayReferences));
        restored.Dispose();
        var replayed = Success(factory.DecodeReplay(replay.Encode())).History.Single();
        Assert.AreEqual(restored.View.Core.Identity, replayed.Core.Identity);
        Assert.AreEqual(capture.Revision, replayed.PersistentRevision);
        var replayedPartial = (C.DesignEvaluation.Partial)Found(replayed.FindDesign(design));
        Assert.AreEqual(design, replayedPartial.CorePlaceholder!.Design);
        CollectionAssert.AreEqual(partial.CorePlaceholder!.MissingReferences.ToArray(), replayedPartial.CorePlaceholder.MissingReferences.ToArray());
        Assert.AreEqual(partial.Scene.EditLocks.Length, replayedPartial.Scene.EditLocks.Length);
        for (var index = 0; index < partial.Scene.EditLocks.Length; index++)
        {
            var expected = partial.Scene.EditLocks[index];
            var actual = replayedPartial.Scene.EditLocks[index];
            Assert.AreEqual(expected.Reference, actual.Reference);
            Assert.AreEqual(expected.Package, actual.Package);
            CollectionAssert.AreEqual(expected.BlockedOperations.ToArray(), actual.BlockedOperations.ToArray());
        }
        var part = replayedPartial.CorePlaceholder.Parts.Single();
        Assert.IsTrue(part.Missing);
        Assert.AreEqual(partial.CorePlaceholder.Parts.Single().Id, part.Id);
        Assert.AreEqual(0L, part.MaxDurability);
        Assert.AreEqual(Success(factory.Capture(restored.View, replayReferences)).Encode(), Success(factory.Capture(replayed, replayReferences)).Encode());
    }
    [TestMethod]
    public void Replay_reports_full_history_path_when_rebuilding_from_incomplete_source_facts()
    {
        using var initial = WorldContentFixtures.Content().Load();
        var factory = ContentFactory.Create();
        var json = JsonNode.Parse(Success(factory.CaptureReplay([initial.View], References(initial.View))).Encode())!;
        json["history"]![0]!["source"]!["files"] = new JsonArray();
        var result = factory.DecodeReplay(json.ToJsonString());
        Failure(result, "inconsistent_capture");
        Assert.IsTrue(((C.ContentResult<C.IContentReplayCapture>.Failure)result).Diagnostics.Any(d =>
            d.Field == "/history/0/source/files"));
    }

    [TestMethod]
    public void Replay_rejects_fabricated_and_omitted_missing_definitions()
    {
        var fixture = ModContent();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var blueprint = Found(initial.View.FindBlueprint(new(C.BlueprintLibrary.Packages, new(ModId + ":engine"))));
        var design = ((C.DesignEvaluation.Complete)blueprint.Evaluation).Design;
        var references = new K.CoreReferenceManifest([design], [], [], [initial.View.Core.Identity]);
        var saved = Success(factory.Capture(initial.View, references));
        fixture.RemoveEntry(ContentFixture.Mods, "equipment");
        using var restored = Restore(fixture, saved, references);
        Assert.IsTrue(restored.View.Core.MissingReferences.Any(m =>
            m.Reference == new K.CoreDefinitionReference.Voxel(new(ModId + ":motor"))));
        var payload = Success(factory.CaptureReplay([restored.View], references with { Inputs = [restored.View.Core.Identity] })).Encode();
        foreach (var omit in new[] { false, true })
        {
            var json = JsonNode.Parse(payload)!;
            if (omit) json["history"]![0]!["missing"] = new JsonArray();
            else json["history"]![0]!["missing"]!.AsArray().Add(JsonNode.Parse("""{"kind":"voxel","id":"core:block","field":"fabricated"}"""));
            Failure(factory.DecodeReplay(json.ToJsonString()), "inconsistent_capture");
        }
    }

    [TestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void Replay_preserves_rejected_mod_with_nonfinite_model_bounds(double coordinate)
    {
        var fixture = ModContent();
        var voxel = JsonNode.Parse(ContentFixture.VoxelJson)!;
        voxel["id"] = "model";
        voxel["appearance"]!["presentation"] = JsonNode.Parse("""{"kind":"mesh","model":"model.glb","surface_materials":["bare_metal"]}""");
        fixture.AddFile(ContentFixture.Mods, "equipment/resources/voxels/model.voxel.json", voxel.ToJsonString());
        fixture.AddAsset(ContentFixture.Mods, "equipment/resources/voxels/model/icon.png", [22, 23], new C.AssetMetadata.Png(128, 128));
        fixture.AddAsset(ContentFixture.Mods, "equipment/resources/voxels/model/model.glb", [7, 8, 9],
            new C.AssetMetadata.Glb(1, [C.AssetAlphaMode.Opaque], new(new(coordinate, 0, 0), new(0.25, 0.5, 0.25)), false, false, false, false, false, false));
        using var initial = fixture.Load();
        var before = initial.View.Packages.Single(p => p.Metadata?.Id.Value == ModId);
        Assert.AreEqual(C.PackageState.Invalid, before.State);
        Assert.IsTrue(before.Diagnostics.Any(d => d.Code == "asset_rule_failed" && d.Field == "/appearance/presentation/model"));
        var factory = ContentFactory.Create();
        var replay = Success(factory.DecodeReplay(Success(factory.CaptureReplay([initial.View], References(initial.View))).Encode()));
        var after = replay.History.Single().Packages.Single(p => p.Metadata?.Id.Value == ModId);
        Assert.AreEqual(before.State, after.State);
        CollectionAssert.AreEqual(before.Diagnostics.ToArray(), after.Diagnostics.ToArray());
    }

}
