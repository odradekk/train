// Domain.Composites 按固定断裂随机键给等值候选排序。
// 通用整数变换由 Core 提供，此文件保留仍在编译的断裂调用所需的用途规则。

namespace Train.Domain.Composites;

/// <summary>
/// 按版本 1 的断裂键计算候选分数并选择主候选。
/// 键依次为种子、事务标识和候选已有的最小部件标识，都是无符号 64 位整数。
/// <see cref="FracturePurpose"/> 是字符串 "FRACTURE" 的 ASCII 字节按大端排列得到的整数。
/// 失败候选不进入随机键，也不分配实体标识；通用混合算法由 Core 提供。
/// </summary>
public static class SplitMix64
{
    public const ulong AlgorithmVersion = 1;
    public const ulong FracturePurpose = 0x4652414354555245UL;

    public static ulong Mix(ulong value) => Train.Core.Simulation.SplitMix64.Mix(value);

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
