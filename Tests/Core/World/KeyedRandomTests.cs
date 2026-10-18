
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.World;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class KeyedRandomTests
{
    // 参考算法：https://prng.di.unimi.it/splitmix64.c；键布局与组合协议见 KeyedRandom 的公开契约和实现。
    public static IEnumerable<object[]> Vectors()
    {
        yield return [new RandomKey(0, "fixture.empty", []),
            "54524E470100000000000000000000000D000000666978747572652E656D70747900000000", 1155599494716839220UL];
        yield return [new RandomKey(42, "fixture.resource-choice", [6, 7]),
            "54524E47010000002A0000000000000017000000666978747572652E7265736F757263652D63686F6963650200000006000000000000000700000000000000", 8204153891108266487UL];
        yield return [new RandomKey(42, "fixture.resource-choice", [7, 6]),
            "54524E47010000002A0000000000000017000000666978747572652E7265736F757263652D63686F6963650200000007000000000000000600000000000000", 13822885226203741682UL];
        yield return [new RandomKey(ulong.MaxValue, "fixture.boundary", [0, ulong.MaxValue]),
            "54524E4701000000FFFFFFFFFFFFFFFF10000000666978747572652E626F756E64617279020000000000000000000000FFFFFFFFFFFFFFFF", 16262244345581814768UL];
        yield return [new RandomKey(0, "资源", [0]),
            "54524E4701000000000000000000000006000000E8B584E6BA90010000000000000000000000", 16127836127881110599UL];
        yield return [new RandomKey(42, "fixture.resource-choice/other", [6, 7]),
            "54524E47010000002A000000000000001D000000666978747572652E7265736F757263652D63686F6963652F6F746865720200000006000000000000000700000000000000", 400061384819256086UL];
        yield return [new RandomKey(0, "ab", [99]),
            "54524E47010000000000000000000000020000006162010000006300000000000000", 2448275292056966062UL];
        yield return [new RandomKey(0, "a", [98, 99]),
            "54524E4701000000000000000000000001000000610200000062000000000000006300000000000000", 6438997189913525080UL];
        yield return [new RandomKey(0, "\u00e9", []),
            "54524E4701000000000000000000000002000000C3A900000000", 7225363803161898347UL];
        yield return [new RandomKey(0, "e\u0301", []),
            "54524E470100000000000000000000000300000065CC8100000000", 12780317014525921434UL];
        yield return [new RandomKey(42, "combat.hit.v1", [1, 100, 0, 7, 0]),
            "54524E47010000002A000000000000000D000000636F6D6261742E6869742E76310500000001000000000000006400000000000000000000000000000007000000000000000000000000000000", 6267333097098766625UL];
        yield return [new RandomKey(42, "combat.hit.v1", [1, 100, 1, 2, 0]),
            "54524E47010000002A000000000000000D000000636F6D6261742E6869742E76310500000001000000000000006400000000000000010000000000000002000000000000000000000000000000", 5513475949424332455UL];
        yield return [new RandomKey(0, "combat.hit.v1", [ulong.MaxValue, ulong.MaxValue, 0, 1, int.MaxValue]),
            "54524E470100000000000000000000000D000000636F6D6261742E6869742E763105000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00000000000000000100000000000000FFFFFF7F00000000", 10738802885146978975UL];
    }

    [TestMethod]
    [DynamicData(nameof(Vectors), DynamicDataSourceType.Method)]
    public void Encode_matches_independent_byte_vectors(object key, string expectedHex, ulong _)
    {
        CollectionAssert.AreEqual(Convert.FromHexString(expectedHex), KeyedRandom.Encode((RandomKey)key).ToArray());
    }

    [TestMethod]
    [DynamicData(nameof(Vectors), DynamicDataSourceType.Method)]
    public void Sample_matches_independent_unsigned_vectors(object key, string _, ulong expected)
    {
        Assert.AreEqual(expected, KeyedRandom.Sample((RandomKey)key));
    }

    [TestMethod]
    public void Calls_in_a_different_order_do_not_consume_random_state()
    {
        var first = new RandomKey(42, "fixture.resource-choice", [6, 7]);
        var other = new RandomKey(42, "fixture.resource-choice", [7, 6]);
        Assert.AreEqual(8204153891108266487UL, KeyedRandom.Sample(first));
        Assert.AreEqual(13822885226203741682UL, KeyedRandom.Sample(other));
        Assert.AreEqual(8204153891108266487UL, KeyedRandom.Sample(first));
    }

    [TestMethod]
    public void Empty_purpose_is_rejected_instead_of_sharing_an_unnamed_stream()
    {
        var key = new RandomKey(0, "", []);
        Assert.ThrowsExactly<ArgumentException>(() => KeyedRandom.Encode(key));
        Assert.ThrowsExactly<ArgumentException>(() => KeyedRandom.Sample(key));
    }

    [TestMethod]
    public void Uninitialized_components_are_rejected_instead_of_being_an_empty_sequence()
    {
        var key = new RandomKey(0, "fixture.empty", default(ImmutableArray<ulong>));
        Assert.ThrowsExactly<ArgumentException>(() => KeyedRandom.Encode(key));
        Assert.ThrowsExactly<ArgumentException>(() => KeyedRandom.Sample(key));
    }
}
