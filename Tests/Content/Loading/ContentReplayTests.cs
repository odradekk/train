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
    public void Replay_restores_fixed_definitions_layout_appearance_and_asset_sources_after_installation_changes()
    {
        var fixture = WorldContentFixtures.Content();
        using var candidate = fixture.Load();
        var factory = ContentFactory.Create();
        var original = factory.WithSettlementCopies(candidate.View, [Template(41)]);
        var references = References(original, "engine") with { Templates = [Template(41)] };
        var appearance = Found(original.FindVoxelAppearance(new("core:machine")));
        var cube = (C.VoxelPresentation.Cube)appearance.Presentation;
        var asset = Found(original.FindAsset(cube.Top));
        var layout = Found(original.FindSettlementLayout(new(41)));
        var saved = Success(factory.Capture(original, references)).Encode();
        var replay = Success(factory.CaptureReplay([original], references));
        Change(fixture, "resources/voxels/machine.voxel.json", json => json["simulation"]!["max_durability"] = 2);
        fixture.RemoveEntry(ContentFixture.BuiltIn, TemplatePath);
        fixture.RemoveEntry(ContentFixture.BuiltIn, "resources/blueprints/engine.blueprint.json");
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/machine/tile.png", [21, 22], new C.AssetMetadata.Png(64, 64));
        candidate.Dispose();

        var decoded = Success(factory.DecodeReplay(replay.Encode()));
        var restored = decoded.History.Single();
        Assert.AreEqual(original.Core.Identity, restored.Core.Identity);
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
        foreach (var mutation in new[] { "version", "hash", "bytes", "missing", "metadata" })
        {
            var json = JsonNode.Parse(payload)!;
            switch (mutation)
            {
                case "version": json["format_version"] = 2; break;
                case "hash": json["blobs"]![0]!["hash"] = new string('A', 64); break;
                case "bytes": json["blobs"]![0]!["bytes"] = Convert.ToBase64String([255, 254]); break;
                case "missing": json["blobs"]!.AsArray().RemoveAt(0); break;
                case "metadata": json["history"]![0]!["source"]!["inspections"]![0]!["metadata"]!["Width"] = "invalid"; break;
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
}
