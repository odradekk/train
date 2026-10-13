using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;

namespace Train.Cli.Tests;

[TestClass]
public sealed class PngAssetInspectorTests
{
    [TestMethod]
    public void Valid_png_reports_actual_dimensions()
    {
        var result = Inspect(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAC0lEQVR4nGNgQAcAABIAAXfx+gAAAAAASUVORK5CYII="));
        var success = (AssetInspection.Success)result;
        Assert.AreEqual(new AssetMetadata.Png(2, 2), success.Metadata);
    }

    [TestMethod]
    [DataRow("signature")]
    [DataRow("crc")]
    [DataRow("truncated")]
    [DataRow("short_pixels")]
    [DataRow("long_pixels")]
    [DataRow("idat")]
    [DataRow("filter")]
    [DataRow("dimensions")]
    [DataRow("encoding")]
    public void Invalid_png_is_rejected(string corruption)
    {
        var pixels = new byte[corruption == "short_pixels" ? 17 : corruption == "long_pixels" ? 19 : 18];
        if (corruption == "filter") pixels[0] = 5;
        var png = Png(pixels, corruption == "idat", corruption == "dimensions", corruption == "encoding");
        if (corruption == "signature") png[0] = 0;
        if (corruption == "crc") png[29] ^= 1;
        if (corruption == "truncated") png = png[..^1];
        Assert.AreEqual(AssetInspectionFailure.CannotDecode, ((AssetInspection.Failure)Inspect(png)).Kind);
    }

    [TestMethod]
    public void Glb_is_explicitly_unsupported()
    {
        var result = new PngAssetInspector().Inspect(AssetKind.Glb, new AssetRevision("test"), []);
        Assert.AreEqual(AssetInspectionFailure.UnsupportedContent, ((AssetInspection.Failure)result).Kind);
    }

    private static AssetInspection Inspect(byte[] bytes) =>
        new PngAssetInspector().Inspect(AssetKind.Png, new AssetRevision("test"), bytes.ToImmutableArray());

    private static byte[] Png(byte[] pixels, bool corruptIdat = false, bool invalidDimensions = false,
        bool unsupportedEncoding = false)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, invalidDimensions ? 0u : 2u);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        header[8] = 8;
        header[9] = unsupportedEncoding ? (byte)2 : (byte)6;
        WriteChunk(png, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var encoder = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            encoder.Write(pixels);
        var data = compressed.ToArray();
        if (corruptIdat) data = data[..^2];
        WriteChunk(png, "IDAT"u8, data);
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        png.Write(number);
        png.Write(type);
        png.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in type.ToArray().Concat(data.ToArray()))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        png.Write(number);
    }
}
