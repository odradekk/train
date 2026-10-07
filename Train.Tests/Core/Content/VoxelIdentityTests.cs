// Core 内容模块测试。
// 验证体素定义标识的空白、格式、长度和大小写契约。

using Train.Core.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:identity")]
public sealed class VoxelIdentityTests
{
    [TestMethod]
    public void Identities_trim_reject_blank_and_preserve_case()
    {
        Assert.AreEqual(VoxelDefinitionId.Parse("core:iron"), new VoxelDefinitionId("core", "iron"));

        Assert.ThrowsException<ArgumentException>(() => VoxelDefinitionId.Parse(""));
        Assert.ThrowsException<ArgumentException>(() => VoxelDefinitionId.Parse("Iron"));
        Assert.ThrowsException<ArgumentException>(() => new VoxelDefinitionId("Core", "iron"));
        Assert.ThrowsException<ArgumentException>(() => new VoxelDefinitionId("core", "Iron"));
    }
}
