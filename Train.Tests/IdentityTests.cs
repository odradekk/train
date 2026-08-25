using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Settlements;

namespace Train.Tests;

[TestClass]
public sealed class IdentityTests
{
    [TestMethod]
    public void Identities_trim_reject_blank_and_preserve_case()
    {
        Assert.AreEqual("MineA", new SettlementId("  MineA  ").Value);
        Assert.AreEqual("Hub", new StationId("Hub").Value);
        Assert.AreEqual("City", new SettlementTypeId("City").Value);
        Assert.AreNotEqual(new BuildingTypeId("mill"), new BuildingTypeId("Mill"));
        Assert.AreEqual(new NaturalResourceTypeId("Iron"), new NaturalResourceTypeId("Iron"));

        Assert.ThrowsException<ArgumentNullException>(() => new SettlementId(null!));
        Assert.ThrowsException<ArgumentException>(() => new StationId(""));
        Assert.ThrowsException<ArgumentException>(() => new SettlementTypeId(" \t "));
        Assert.ThrowsException<ArgumentException>(() => new BuildingTypeId("   "));
        Assert.ThrowsException<ArgumentException>(() => new NaturalResourceTypeId("\n"));
    }
}
