using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Engine.Tests.Assets;
using System.Text.Json.Nodes;

namespace Train.Cli.Tests;

[TestClass]
public sealed class AssetInspectorTests
{
    private const string ValidPng = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAC0lEQVR4nGNgQAcAABIAAXfx+gAAAAAASUVORK5CYII=";

    [TestMethod]
    public void Valid_png_reports_actual_dimensions()
    {
        var success = (AssetInspection.Success)Inspect(Convert.FromBase64String(ValidPng));
        Assert.AreEqual(new AssetMetadata.Png(2, 2), success.Metadata);
    }

    [TestMethod]
    public void Corrupted_chunk_crc_is_rejected()
    {
        var bytes = Convert.FromBase64String(ValidPng);
        bytes[29] ^= 1;
        Assert.AreEqual(AssetInspectionFailure.CannotDecode, ((AssetInspection.Failure)Inspect(bytes)).Kind);
    }

    [TestMethod]
    public void Pixel_data_shorter_than_dimensions_is_rejected()
    {
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAC0lEQVR4nGNgQAMAABEAAe4mBk8AAAAASUVORK5CYII=");
        Assert.AreEqual(AssetInspectionFailure.CannotDecode, ((AssetInspection.Failure)Inspect(bytes)).Kind);
    }

    [TestMethod]
    public void Glb_reports_cell_geometry_and_surface_alpha()
    {
        var metadata = Glb(GlbFixture.Create(feature: "cell"));
        Assert.AreEqual(1, metadata.MeshNodeCount);
        CollectionAssert.AreEqual(new[] { AssetAlphaMode.Opaque, AssetAlphaMode.Mask, AssetAlphaMode.Opaque }, metadata.Surfaces.ToArray());
        Assert.IsFalse(metadata.HasSkeleton || metadata.HasMorphTargets || metadata.HasAnimation || metadata.HasCamera || metadata.HasLight || metadata.HasExternalImages);
        Bounds(metadata.Bounds, new(-0.25, 0, -0.125), new(0.125, 0.4, 0.075));
    }

    [TestMethod]
    public void Glb_bounds_use_actual_vertices_and_hierarchy_instead_of_accessor_extrema()
    {
        var metadata = Glb(GlbFixture.Create(transformed: true, changeJson: root =>
        {
            foreach (var accessor in root["accessors"]!.AsArray())
            {
                if (accessor!["type"]!.GetValue<string>() != "VEC3") continue;
                accessor!["min"] = JsonNode.Parse("[999,999,999]");
                accessor["max"] = JsonNode.Parse("[1000,1000,1000]");
            }
        }));
        Bounds(metadata.Bounds, new(-14, 21, 42), new(4, 24, 62));
    }

    [TestMethod]
    public void Glb_matrix_and_accessor_offset_and_stride_are_applied()
    {
        var metadata = Glb(GlbFixture.Create(changeJson: root =>
        {
            root["nodes"]![0]!["matrix"] = JsonNode.Parse("[1,0,0,0,0,1,0,0,0,0,1,0,10,20,30,1]");
            // 每个 POSITION 仅取第二个顶点；步长仍应被解析，不能默认紧密排列。
            foreach (var primitive in root["meshes"]![0]!["primitives"]!.AsArray())
            {
                var accessor = root["accessors"]![primitive!["attributes"]!["POSITION"]!.GetValue<int>()]!;
                accessor["byteOffset"] = 12;
                accessor["count"] = 1;
                primitive!["mode"] = 0;
                primitive.AsObject().Remove("indices");
                root["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!["byteStride"] = 16;
            }
        }));
        Bounds(metadata.Bounds, new(12, 20, 30), new(12, 20, 34));
    }

    [TestMethod]
    public void Glb_interleaved_position_stride_skips_intermediate_vertices()
    {
        var metadata = Glb(GlbFixture.Create(changeJson: root =>
        {
            foreach (var primitive in root["meshes"]![0]!["primitives"]!.AsArray())
            {
                var accessor = root["accessors"]![primitive!["attributes"]!["POSITION"]!.GetValue<int>()]!;
                accessor["count"] = 2;
                primitive!["mode"] = 1;
                primitive.AsObject().Remove("indices");
                root["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!["byteStride"] = 24;
            }
        }));
        Bounds(metadata.Bounds, new(-1, 0, 0), new(0, 3, 5));
    }

    [TestMethod]
    public void Glb_features_are_reported_without_applying_content_policy()
    {
        Assert.IsTrue(Glb(GlbFixture.Create(feature: "skin")).HasSkeleton);
        Assert.IsTrue(Glb(GlbFixture.Create(feature: "morph")).HasMorphTargets);
        Assert.IsTrue(Glb(GlbFixture.Create(feature: "animation")).HasAnimation);
        Assert.IsTrue(Glb(GlbFixture.Create(feature: "camera")).HasCamera);
        Assert.IsTrue(Glb(GlbFixture.Create(feature: "light")).HasLight);
        Assert.IsTrue(Glb(GlbFixture.Create(externalImage: true)).HasExternalImages);
        Assert.AreEqual(2, Glb(GlbFixture.Create(feature: "instances")).MeshNodeCount);
    }

    [TestMethod]
    public void Glb_unsupported_encoding_and_malformed_data_are_distinct()
    {
        Failure(GlbFixture.Create(externalBuffer: true), AssetInspectionFailure.UnsupportedContent);
        Failure(GlbFixture.Create(changeJson: root => root["extensionsRequired"] = new JsonArray("KHR_draco_mesh_compression")), AssetInspectionFailure.UnsupportedContent);
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![0]!["componentType"] = 5123), AssetInspectionFailure.UnsupportedContent);
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![0]!["type"] = "VEC2"), AssetInspectionFailure.UnsupportedContent);
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![0]!["normalized"] = true), AssetInspectionFailure.UnsupportedContent);
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![0]!["sparse"] = new JsonObject()), AssetInspectionFailure.UnsupportedContent);
        Failure(GlbFixture.Create(invalidAlpha: "invalid"), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(invalidMaterial: true), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(changeJson: root => root["nodes"]![1]!["children"] = new JsonArray(0)), AssetInspectionFailure.CannotDecode);
        Failure([1, 2, 3], AssetInspectionFailure.CannotDecode);
    }

    private static AssetMetadata.Glb Glb(ImmutableArray<byte> bytes) =>
        (AssetMetadata.Glb)((AssetInspection.Success)new AssetInspector().Inspect(AssetKind.Glb, new("test"), bytes)).Metadata;

    private static void Failure(ImmutableArray<byte> bytes, AssetInspectionFailure expected) =>
        Assert.AreEqual(expected, ((AssetInspection.Failure)new AssetInspector().Inspect(AssetKind.Glb, new("test"), bytes)).Kind);

    private static void Bounds(ModelBounds actual, LocalPoint minimum, LocalPoint maximum)
    {
        Assert.AreEqual(minimum.X, actual.MinimumMeters.X, 0.0001);
        Assert.AreEqual(minimum.Y, actual.MinimumMeters.Y, 0.0001);
        Assert.AreEqual(minimum.Z, actual.MinimumMeters.Z, 0.0001);
        Assert.AreEqual(maximum.X, actual.MaximumMeters.X, 0.0001);
        Assert.AreEqual(maximum.Y, actual.MaximumMeters.Y, 0.0001);
        Assert.AreEqual(maximum.Z, actual.MaximumMeters.Z, 0.0001);
    }
    private static AssetInspection Inspect(byte[] bytes) =>
        new AssetInspector().Inspect(AssetKind.Png, new AssetRevision("test"), bytes.ToImmutableArray());

}
