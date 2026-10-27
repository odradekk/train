using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldReferencesTests
{
    [TestMethod]
    public void Good_comparer_matches_ordinal_keys_across_kinds_and_identities()
    {
        GoodId[] goods =
        [
            new GoodId.Voxel(new("1:w")), new GoodId.Composite(new("2:c-1")),
            new GoodId.Assembly(new(new string('F', 64))), new GoodId.Voxel(new("1:v")),
            new GoodId.Assembly(new(new string('E', 64))), new GoodId.Composite(new("2:c")),
            new GoodId.Voxel(new("1:v"))
        ];
        foreach (var left in goods)
            foreach (var right in goods)
                Assert.AreEqual(Math.Sign(StringComparer.Ordinal.Compare(WorldReferences.GoodKey(left), WorldReferences.GoodKey(right))),
                    Math.Sign(WorldReferences.GoodComparer.Compare(left, right)));
        CollectionAssert.AreEqual(goods.OrderBy(WorldReferences.GoodKey, StringComparer.Ordinal).ToArray(),
            goods.Order(WorldReferences.GoodComparer).ToArray());
    }
}
