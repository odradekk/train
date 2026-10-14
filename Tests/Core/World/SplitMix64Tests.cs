
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.World;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class SplitMix64Tests
{
    [TestMethod]
    public void Mix_matches_reference_vectors()
    {
        // 参考实现：https://prng.di.unimi.it/splitmix64.c；每个输入作为参考生成器单次调用的初始状态。
        var vectors = new (ulong Input, ulong Expected)[]
        {
            (0x0000000000000000UL, 0xe220a8397b1dcdafUL),
            (0x0000000000000001UL, 0x910a2dec89025cc1UL),
            (0x000000000000002aUL, 0xbdd732262feb6e95UL),
            (0xffffffffffffffffUL, 0xe4d971771b652c20UL),
            (0x9e3779b97f4a7c15UL, 0x6e789e6aa1b965f4UL),
            (0x61c8864680b583ebUL, 0x0000000000000000UL)
        };

        foreach (var (input, expected) in vectors)
            Assert.AreEqual(expected, SplitMix64.Mix(input), $"input=0x{input:x16}");
    }
}
