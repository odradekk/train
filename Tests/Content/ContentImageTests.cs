using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;

namespace Train.Content.Tests;

[TestClass]
public sealed class ContentImageTests
{
    [TestMethod]
    public void Image_retains_its_pixels_when_the_input_buffer_changes()
    {
        var pixels = ContentReloadExamples.PixelInput.ToArray();
        var image = new DecodedImage(1, 1, "rgba8", pixels);
        Array.Fill(pixels, (byte)9);

        CollectionAssert.AreEqual(ContentReloadExamples.Image.Pixels.ToArray(), image.Pixels.ToArray());
    }
}
