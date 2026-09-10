namespace Train.Domain.Composites;

public static class SplitMix64
{
    public const ulong AlgorithmVersion = 1;
    public const ulong FracturePurpose = 0x4652414354555245UL;

    public static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    public static ulong FractureScore(ulong seed, ulong transactionId, ulong candidateId)
    {
        var mixed = Mix(seed ^ FracturePurpose);
        mixed = Mix(mixed ^ transactionId);
        return Mix(mixed ^ candidateId);
    }

    public static ulong SelectPrimaryCandidate(ulong seed, ulong transactionId, IReadOnlyList<ulong> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("Fracture ranking requires at least one candidate identity.", nameof(candidates));

        var primary = candidates[0];
        var primaryScore = FractureScore(seed, transactionId, primary);
        for (var index = 1; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var score = FractureScore(seed, transactionId, candidate);
            if (score > primaryScore || (score == primaryScore && candidate < primary))
            {
                primary = candidate;
                primaryScore = score;
            }
        }

        return primary;
    }
}
