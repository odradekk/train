
using Train.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Content.Tests;

[TestClass]
public sealed class VoxelIdentityTests
{
    [TestMethod]
    public void Identities_trim_reject_blank_and_preserve_case()
    {
        Assert.AreEqual(VoxelDefinitionId.Parse("core:iron"), new VoxelDefinitionId("core", "iron"));

        Assert.ThrowsExactly<ArgumentException>(() => VoxelDefinitionId.Parse(""));
        Assert.ThrowsExactly<ArgumentException>(() => VoxelDefinitionId.Parse("Iron"));
        Assert.ThrowsExactly<ArgumentException>(() => new VoxelDefinitionId("Core", "iron"));
        Assert.ThrowsExactly<ArgumentException>(() => new VoxelDefinitionId("core", "Iron"));
    }
}
