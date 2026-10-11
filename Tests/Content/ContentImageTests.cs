// Core 内容图像契约：输入像素的所有权在构造时交给不可变图像。
// 预期像素直接来自数据例子，不经解码或修订函数取得。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;

namespace Train.Content.Tests;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
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
