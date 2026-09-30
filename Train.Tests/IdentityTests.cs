using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Voxels;
using Train.Domain.Settlements;

namespace Train.Tests;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:identity")]
public sealed class IdentityTests
{
    [TestMethod]
    public void Identities_trim_reject_blank_and_preserve_case()
    {
        Assert.AreEqual("MineA", new SettlementId("  MineA  ").Value);
        Assert.AreEqual("Hub", new StationId("Hub").Value);
        Assert.AreEqual("City", new SettlementTypeId("City").Value);
        Assert.AreNotEqual(new BuildingTypeId("mill"), new BuildingTypeId("Mill"));
        Assert.AreEqual(VoxelDefinitionId.Parse("core:iron"), new VoxelDefinitionId("core", "iron"));

        Assert.ThrowsException<ArgumentNullException>(() => new SettlementId(null!));
        Assert.ThrowsException<ArgumentException>(() => new StationId(""));
        Assert.ThrowsException<ArgumentException>(() => new SettlementTypeId(" \t "));
        Assert.ThrowsException<ArgumentException>(() => new BuildingTypeId("   "));
        Assert.ThrowsException<ArgumentException>(() => VoxelDefinitionId.Parse(""));
        Assert.ThrowsException<ArgumentException>(() => VoxelDefinitionId.Parse("Iron"));
        Assert.ThrowsException<ArgumentException>(() => new VoxelDefinitionId("Core", "iron"));
        Assert.ThrowsException<ArgumentException>(() => new VoxelDefinitionId("core", "Iron"));
    }
}
