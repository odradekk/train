using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;

namespace Train.Cli.Tests;

[TestClass]
public sealed class PngAssetInspectorTests
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
    public void Glb_is_explicitly_unsupported()
    {
        var result = new PngAssetInspector().Inspect(AssetKind.Glb, new AssetRevision("test"), []);
        Assert.AreEqual(AssetInspectionFailure.UnsupportedContent, ((AssetInspection.Failure)result).Kind);
    }

    private static AssetInspection Inspect(byte[] bytes) =>
        new PngAssetInspector().Inspect(AssetKind.Png, new AssetRevision("test"), bytes.ToImmutableArray());

}
