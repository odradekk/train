using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentAssetTests
{
    [TestMethod]
    public void Front_can_have_a_different_size_and_equal_bytes_share_revision_and_sources()
    {
        var fixture = new ContentFixture();
        var json = JsonNode.Parse(ContentFixture.VoxelJson)!;
        json["appearance"]!["presentation"]!["faces"]!["front"] = "front.png";
        json["appearance"]!["presentation"]!["faces"]!["side"] = "shared/side.png";
        fixture.AddCube(json.ToJsonString());
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/block/front.png", [11, 12], new C.AssetMetadata.Png(64, 64));
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/shared/side.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        using var candidate = fixture.Load();
        var appearance = ((C.ContentLookup<C.VoxelAppearanceProjection>.Found)candidate.View.FindVoxelAppearance(new("core:block"))).Value;
        var cube = (C.VoxelPresentation.Cube)appearance.Presentation;
        Assert.AreEqual(cube.Top, cube.Side);
        Assert.AreNotEqual(cube.Side, cube.Front);
        var asset = ((C.ContentLookup<C.ContentAsset>.Found)candidate.View.FindAsset(cube.Top)).Value;
        CollectionAssert.AreEquivalent(new[] { "resources/voxels/block/tile.png", "resources/voxels/shared/side.png" }, asset.Sources.Select(source => source.Path).ToArray());
    }

    [TestMethod]
    [DataRow(48, 48, false)]
    [DataRow(32, 16, false)]
    [DataRow(0, 0, false)]
    [DataRow(128, 64, true)]
    public void Content_rejects_png_facts_that_violate_field_rules(int width, int height, bool icon)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        var path = "resources/voxels/block/" + (icon ? "icon.png" : "tile.png");
        var field = icon ? "/display/icon" : "/appearance/presentation/faces/top";
        fixture.AddAsset(ContentFixture.BuiltIn, path, icon ? [4, 5, 6] : [1, 2, 3], new C.AssetMetadata.Png(width, height));
        var diagnostics = Rejected(fixture);
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Code == "asset_rule_failed" && diagnostic.Subject is not null &&
            diagnostic.Source?.Path == path && diagnostic.Field == field));
    }

    [TestMethod]
    public void Valid_png_dimensions_have_no_unspecified_upper_bound()
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(8192, 8192));
        using var candidate = fixture.Load();
        Assert.AreEqual(1, candidate.View.Voxels.Length);
    }

    [TestMethod]
    public void Png_diagnostics_identify_each_violated_rule_and_observed_dimensions()
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(48, 16));
        var diagnostics = Rejected(fixture).Where(diagnostic => diagnostic.Field == "/appearance/presentation/faces/top").ToArray();
        Assert.AreEqual(2, diagnostics.Length);
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Message.Contains("正方形", StringComparison.Ordinal) && diagnostic.Message.Contains("48", StringComparison.Ordinal) && diagnostic.Message.Contains("16", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Message.Contains("2 的幂", StringComparison.Ordinal) && diagnostic.Message.Contains("48", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Content_rejects_mismatched_top_side_bottom_sizes()
    {
        var fixture = new ContentFixture();
        var json = JsonNode.Parse(ContentFixture.VoxelJson)!;
        json["appearance"]!["presentation"]!["faces"]!["side"] = "side.png";
        fixture.AddCube(json.ToJsonString());
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/block/side.png", [11, 12], new C.AssetMetadata.Png(64, 64));
        Assert.IsTrue(Rejected(fixture).Any(diagnostic => diagnostic.Code == "asset_rule_failed" && diagnostic.Field.EndsWith("/side", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Static_mesh_uses_glb_facts_and_retains_the_inspected_bytes()
    {
        var fixture = Mesh(ValidModel);
        using var candidate = fixture.Load();
        var appearance = ((C.ContentLookup<C.VoxelAppearanceProjection>.Found)candidate.View.FindVoxelAppearance(new("core:block"))).Value;
        var mesh = (C.VoxelPresentation.Mesh)appearance.Presentation;
        CollectionAssert.AreEqual(new[] { C.MaterialCategory.BareMetal, C.MaterialCategory.Glass }, mesh.SurfaceMaterials.ToArray());
        var asset = ((C.ContentLookup<C.ContentAsset>.Found)candidate.View.FindAsset(mesh.Model)).Value;
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, asset.Bytes.ToArray());
        Assert.AreEqual(ValidModel, asset.Metadata);
    }

    [TestMethod]
    [DataRow("nodes")]
    [DataRow("surfaces")]
    [DataRow("blend")]
    [DataRow("bounds")]
    [DataRow("nonfinite")]
    [DataRow("skeleton")]
    [DataRow("morph")]
    [DataRow("animation")]
    [DataRow("camera")]
    [DataRow("light")]
    [DataRow("external")]
    public void Content_rejects_model_facts_that_violate_static_single_cell_rules(string violation)
    {
        var model = violation switch
        {
            "nodes" => ValidModel with { MeshNodeCount = 2 },
            "surfaces" => ValidModel with { Surfaces = [C.AssetAlphaMode.Opaque] },
            "blend" => ValidModel with { Surfaces = [C.AssetAlphaMode.Opaque, C.AssetAlphaMode.Blend] },
            "bounds" => ValidModel with { Bounds = new(new(-0.251, 0, -0.25), new(0.25, 0.5, 0.25)) },
            "nonfinite" => ValidModel with { Bounds = new(new(double.NaN, 0, -0.25), new(0.25, 0.5, 0.25)) },
            "skeleton" => ValidModel with { HasSkeleton = true },
            "morph" => ValidModel with { HasMorphTargets = true },
            "animation" => ValidModel with { HasAnimation = true },
            "camera" => ValidModel with { HasCamera = true },
            "light" => ValidModel with { HasLight = true },
            "external" => ValidModel with { HasExternalImages = true },
            _ => throw new ArgumentOutOfRangeException(nameof(violation))
        };
        Assert.IsTrue(Rejected(Mesh(model)).Any(diagnostic => diagnostic.Code == "asset_rule_failed" &&
            diagnostic.Source?.Path == "resources/voxels/block/model.glb" && diagnostic.Field ==
            (violation == "surfaces" ? "/appearance/presentation/surface_materials" : "/appearance/presentation/model")));
    }

    [TestMethod]
    public void Model_within_the_documented_bounds_tolerance_is_accepted()
    {
        using var candidate = Mesh(ValidModel with { Bounds = new(new(-0.2504, 0, -0.25), new(0.25, 0.5, 0.25)) }).Load();
        Assert.AreEqual(1, candidate.View.Voxels.Length);
    }

    [TestMethod]
    public void Model_diagnostics_separate_surface_count_from_other_observed_rule_failures()
    {
        var diagnostics = Rejected(Mesh(ValidModel with { MeshNodeCount = 2, Surfaces = [C.AssetAlphaMode.Blend], HasSkeleton = true }));
        Assert.AreEqual(4, diagnostics.Length);
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Field == "/appearance/presentation/surface_materials" &&
            diagnostic.Message.Contains("表面数 1", StringComparison.Ordinal) && diagnostic.Message.Contains("2 项", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Field == "/appearance/presentation/model" && diagnostic.Message.Contains("节点数 2", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Message.Contains("Blend", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(diagnostic => diagnostic.Message.Contains("HasSkeleton=True", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(C.ContentIoFailure.Missing, "file_missing")]
    [DataRow(C.ContentIoFailure.LimitExceeded, "file_too_large")]
    [DataRow(C.ContentIoFailure.LinkedPath, "linked_path")]
    [DataRow(C.ContentIoFailure.Inaccessible, "file_unreadable")]
    public void File_failures_preserve_their_kind_and_asset_location(C.ContentIoFailure failure, string code)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        fixture.SetReadFailure(ContentFixture.BuiltIn, "resources/voxels/block/tile.png", failure);
        Assert.IsTrue(Rejected(fixture).Any(diagnostic => diagnostic.Code == code && diagnostic.Source?.Package.Value == "core" &&
            diagnostic.Source.Path == "resources/voxels/block/tile.png" && diagnostic.Subject is not null));
    }

    [TestMethod]
    [DataRow(C.AssetInspectionFailure.CannotDecode, "asset_decode_failed")]
    [DataRow(C.AssetInspectionFailure.UnsupportedContent, "asset_unsupported")]
    public void Inspection_failure_is_distinct_from_content_rule_failure(C.AssetInspectionFailure failure, string code)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        var result = ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Initial()), fixture, new FailedInspector(failure));
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.All(diagnostic => diagnostic.Code == code));
    }

    private static readonly C.AssetMetadata.Glb ValidModel = new(1, [C.AssetAlphaMode.Opaque, C.AssetAlphaMode.Mask],
        new(new(-0.25, 0, -0.25), new(0.25, 0.5, 0.25)), false, false, false, false, false, false);

    [TestMethod]
    public void Replacing_a_file_during_inspection_does_not_replace_the_fixed_asset_bytes()
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        var result = ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Initial()), fixture, new ChangingInspector(fixture));
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Success>(result, result.ToString());
        using var candidate = ((C.ContentResult<C.IContentCandidate>.Success)result).Value;
        var appearance = ((C.ContentLookup<C.VoxelAppearanceProjection>.Found)candidate.View.FindVoxelAppearance(new("core:block"))).Value;
        var cube = (C.VoxelPresentation.Cube)appearance.Presentation;
        var asset = ((C.ContentLookup<C.ContentAsset>.Found)candidate.View.FindAsset(cube.Top)).Value;
        Assert.AreEqual(cube.Top, cube.Side);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, asset.Bytes.ToArray());
    }

    private static ContentFixture Mesh(C.AssetMetadata.Glb metadata)
    {
        var fixture = new ContentFixture();
        var json = JsonNode.Parse(ContentFixture.VoxelJson)!;
        json["appearance"]!["presentation"] = new JsonObject
        {
            ["kind"] = "mesh", ["model"] = "model.glb", ["surface_materials"] = new JsonArray("bare_metal", "glass")
        };
        fixture.AddCube(json.ToJsonString());
        fixture.AddAsset(ContentFixture.BuiltIn, "resources/voxels/block/model.glb", [7, 8, 9], metadata);
        return fixture;
    }

    private static ImmutableArray<C.ContentDiagnostic> Rejected(ContentFixture fixture)
    {
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result, result.ToString());
        return ((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics;
    }

    private sealed class FailedInspector(C.AssetInspectionFailure failure) : C.IAssetInspector
    {
        public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes) => new C.AssetInspection.Failure(failure, "fixture inspection failure");
    }

    private sealed class ChangingInspector(ContentFixture fixture) : C.IAssetInspector
    {
        public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes)
        {
            if (bytes.SequenceEqual(new byte[] { 1, 2, 3 }))
                fixture.AddFile(ContentFixture.BuiltIn, "resources/voxels/block/tile.png", new byte[] { 8, 9, 10 });
            return fixture.Inspect(kind, revision, bytes);
        }
    }
}
