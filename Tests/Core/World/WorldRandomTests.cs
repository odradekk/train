using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.World;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldRandomTests
{
    // 固定值由独立 Python struct + SplitMix64 参考实现计算，不调用生产代码。
    [TestMethod]
    [DataRow("noise.elevation", 1, 2811670032273776233UL)]
    [DataRow("noise.moisture", 1, 18275838931852893589UL)]
    [DataRow("noise.river", 1, 10195221282573318484UL)]
    [DataRow("node.presence", 2, 8838129325567775039UL)]
    [DataRow("node.x", 2, 5431682671347202232UL)]
    [DataRow("node.y", 2, 10442403272029465360UL)]
    [DataRow("occupant.rank", 4, 6740367066334911342UL)]
    [DataRow("settlement.relation", 4, 2828140202050531420UL)]
    [DataRow("settlement.type", 4, 13307594827417692352UL)]
    [DataRow("settlement.template", 4, 12904437039502354479UL)]
    [DataRow("settlement.population", 4, 6804495863583086515UL)]
    [DataRow("lair.type", 4, 13850703617931429917UL)]
    [DataRow("resource.presence", 5, 11832410515883875486UL)]
    [DataRow("resource.reserve", 5, 11179390923893662347UL)]
    [DataRow("rail.cycle", 6, 8715095948868530376UL)]
    [DataRow("rail.tracks", 6, 3236087425112613175UL)]
    [DataRow("start.rank", 7, 12502784646848428110UL)]
    public void PurposeSamplesMatchIndependentGolden(string purpose, int stage, ulong expected)
    {
        ulong[] components = purpose switch
        {
            "noise.elevation" or "noise.moisture" or "noise.river" => [0x4020000000000000, 5, 8],
            "settlement.template" or "settlement.population" => [5, 8, .. WorldRandom.Identity("core:town")],
            "resource.presence" or "resource.reserve" => [5, 8, .. WorldRandom.Identity("core:iron")],
            "rail.cycle" or "rail.tracks" => [5, 8, 9, 13],
            _ => [5, 8]
        };
        Assert.AreEqual(expected, WorldRandom.Sample(42, purpose, stage, 2, 3, components));
    }

    [TestMethod]
    public void KeysIsolateStagesRetriesCandidatesAndCallOrder()
    {
        var expected = WorldRandom.Sample(42, "node.x", 2, 2, 3, 5, 8);
        ulong[] different = [
            WorldRandom.Sample(43, "node.x", 2, 2, 3, 5, 8),
            WorldRandom.Sample(42, "node.y", 2, 2, 3, 5, 8),
            WorldRandom.Sample(42, "node.x", 3, 2, 3, 5, 8),
            WorldRandom.Sample(42, "node.x", 2, 3, 3, 5, 8),
            WorldRandom.Sample(42, "node.x", 2, 2, 4, 5, 8),
            WorldRandom.Sample(42, "node.x", 2, 2, 3, 8, 5)];
        Assert.IsTrue(different.All(value => value != expected));
        Assert.AreEqual(expected, WorldRandom.Sample(42, "node.x", 2, 2, 3, 5, 8));
    }

    [TestMethod]
    public void IdentityUsesUtf8LengthAndBytes()
    {
        CollectionAssert.AreEqual(new ulong[] { 3, 232, 189, 166 }, WorldRandom.Identity("车"));
        CollectionAssert.AreEqual(new ulong[] { 0 }, WorldRandom.Identity(""));
        Assert.AreEqual(0UL, WorldRandom.Signed(0));
        Assert.AreEqual(1UL, WorldRandom.Signed(-1));
        Assert.AreEqual(2UL, WorldRandom.Signed(1));
        Assert.AreEqual(ulong.MaxValue, WorldRandom.Signed(long.MinValue));
    }

    [TestMethod]
    public void SampleConversionsHonorClosedIntegerAndHalfOpenUnitBounds()
    {
        Assert.AreEqual(0d, WorldRandom.Unit(0));
        Assert.AreEqual(1 - 1d / 9007199254740992d, WorldRandom.Unit(ulong.MaxValue));
        Assert.AreEqual(3L, WorldRandom.Integer(0, 3, 7));
        Assert.AreEqual(7L, WorldRandom.Integer(ulong.MaxValue, 3, 7));
        Assert.AreEqual(-4L, WorldRandom.Integer(ulong.MaxValue, -4, -4));
    }
}
