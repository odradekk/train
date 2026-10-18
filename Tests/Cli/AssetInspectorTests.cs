using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Engine.Tests.Assets;
using System.Text.Json.Nodes;
using System.Buffers.Binary;
using System.IO.Compression;

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
    public void Glb_embedded_rgb_png_is_supported_and_other_png_encodings_are_explicit()
    {
        Assert.IsFalse(Glb(GlbFixture.Create(png: Png(2))).HasExternalImages);
        Failure(GlbFixture.Create(png: Png(0)), AssetInspectionFailure.UnsupportedContent);
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
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![0]!["componentType"] = 5123), AssetInspectionFailure.UnsupportedContent);
        Failure(GlbFixture.Create(invalidAlpha: "invalid"), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(invalidMaterial: true), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![0]!["count"] = 10000), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(changeJson: root => root["nodes"]![1]!["children"] = new JsonArray(0)), AssetInspectionFailure.CannotDecode);
        Failure([1, 2, 3], AssetInspectionFailure.CannotDecode);
    }

    [TestMethod]
    public void Glb_rejects_invalid_index_and_attribute_references_ranges_and_counts()
    {
        Failure(GlbFixture.Create(changeJson: root => root["meshes"]![0]!["primitives"]![0]!["indices"] = 999), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![1]!["count"] = 4), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![1]!["componentType"] = 5122), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(changeJson: root =>
        {
            root["accessors"]![0]!["count"] = 2;
            root["meshes"]![0]!["primitives"]![0]!["mode"] = 0;
        }), AssetInspectionFailure.CannotDecode); // indices 中的 2 超出两个 POSITION 顶点。
        Failure(GlbFixture.Create(changeJson: root => root["meshes"]![0]!["primitives"]![0]!["attributes"]!["NORMAL"] = 999), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(feature: "tangent", changeJson: root => root["accessors"]![4]!["count"] = 2), AssetInspectionFailure.CannotDecode);
        Failure(GlbFixture.Create(feature: "tangent", changeJson: root => root["accessors"]![4]!["byteOffset"] = 10000), AssetInspectionFailure.CannotDecode);
    }

    [TestMethod]
    public void Glb_rejects_accessor_and_buffer_view_extensions_without_guessing_geometry()
    {
        Failure(GlbFixture.Create(changeJson: root => root["accessors"]![0]!["extensions"] = JsonNode.Parse("{\"unknown\":{}}")), AssetInspectionFailure.UnsupportedContent);
        Failure(GlbFixture.Create(changeJson: root => root["bufferViews"]![0]!["extensions"] = JsonNode.Parse("{\"EXT_meshopt_compression\":{}}")), AssetInspectionFailure.UnsupportedContent);
    }

    private static byte[] Png(byte colorType)
    {
        using var file = new MemoryStream();
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var chunk = new byte[data.Length + 12];
            BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
            data.CopyTo(chunk, 8);
            uint crc = uint.MaxValue;
            foreach (var value in chunk.AsSpan(4, data.Length + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(data.Length + 8), ~crc);
            file.Write(chunk);
        }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 1);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        header[9] = colorType;
        Chunk("IHDR", header);
        using var pixels = new MemoryStream();
        using (var compressed = new ZLibStream(pixels, CompressionMode.Compress, leaveOpen: true))
            compressed.Write(colorType == 2 ? new byte[] { 0, 255, 0, 0 } : new byte[] { 0, 127 });
        Chunk("IDAT", pixels.ToArray());
        Chunk("IEND", []);
        return file.ToArray();
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
