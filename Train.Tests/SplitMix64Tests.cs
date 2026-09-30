using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Composites;

namespace Train.Tests;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:composites")]
public sealed class SplitMix64Tests
{
    [TestMethod]
    public void Fracture_key_version_1_matches_the_parent_golden_vectors()
    {
        Assert.AreEqual(1UL, SplitMix64.AlgorithmVersion);
        Assert.AreEqual(0x4652414354555245UL, SplitMix64.FracturePurpose);
        Assert.AreEqual(0xd387f4660103bbf6UL, SplitMix64.FractureScore(0, 0, 0));
        Assert.AreEqual(0xec123d5b587d391aUL, SplitMix64.FractureScore(1, 1, 1));
        Assert.AreEqual(0x3b10a1021bbaf607UL, SplitMix64.FractureScore(42, 7, 100));
        Assert.AreEqual(0x94d3bd5f021a1868UL, SplitMix64.FractureScore(42, 7, 101));
        Assert.AreEqual(0x9b12fb6ea89af6baUL, SplitMix64.FractureScore(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue));
    }

    [TestMethod]
    public void Equal_value_ranking_prefers_higher_score_over_smaller_candidate_id()
    {
        const ulong seed = 42;
        const ulong smaller = 2;
        const ulong larger = 30;
        ulong transaction = 0;
        for (ulong tx = 1; tx < 10_000; tx++)
        {
            if (SplitMix64.FractureScore(seed, tx, larger) > SplitMix64.FractureScore(seed, tx, smaller))
            {
                transaction = tx;
                break;
            }
        }

        Assert.AreNotEqual(0UL, transaction, "expected a transaction where the larger candidate scores higher");
        Assert.AreEqual(larger, SplitMix64.SelectPrimaryCandidate(seed, transaction, new[] { smaller, larger }));
        Assert.AreEqual(larger, SplitMix64.SelectPrimaryCandidate(seed, transaction, new[] { larger, smaller }));
        Assert.AreEqual(smaller, SplitMix64.SelectPrimaryCandidate(42, 2, new[] { smaller, larger }));
    }
}
