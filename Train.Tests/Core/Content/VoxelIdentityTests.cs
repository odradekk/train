// Core 内容测试验证体素定义标识的格式和大小写约束。
// 从旧混合身份测试中保留独立定义标识契约。

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
