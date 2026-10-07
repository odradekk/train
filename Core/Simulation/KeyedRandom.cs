// Train.Core.Simulation 定义带用途与稳定身份的无状态随机键。
// 具体用途负责各身份分量的意义，本模块固定基础编码和 SplitMix64 组合。

using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text;

namespace Train.Core.Simulation;

/// <summary>表示世界种子、稳定用途名和按用途契约排列的无符号身份分量。</summary>
/// <param name="WorldSeed">完整 ulong 范围内的世界种子，包括零。</param>
/// <param name="Purpose">非空用途名；UTF-8 编码，不进行大小写折叠或 Unicode 归一化。</param>
/// <param name="Components">允许零的 ulong 序列；每个用途固定其数量、单位及顺序，不能使用枚举序号。</param>
/// <remarks>当前基础只接收无符号 64 位分量；新增用途需要自己的布局与固定向量，不预置世界生成或战斗用途。</remarks>
/// <example>fixture.resource-choice 的 (事务号 6, 实体 ID 7) 与 (7, 6) 是不同的完整键。</example>
public sealed record RandomKey(ulong WorldSeed, string Purpose, ImmutableArray<ulong> Components);

/// <summary>计算随机键的规范字节和无状态 64 位结果。</summary>
/// <remarks>算法版本与完整字节布局见 DeterminismDesign.md；不使用运行时哈希。</remarks>
public static class KeyedRandom
{
    /// <summary>基础键编码和组合算法的第一版；用途字段的语义另由用途契约解释。</summary>
    public const uint AlgorithmVersion = 1;

    /// <summary>计算版本、种子、用途和全部身份分量的无歧义规范字节。</summary>
    /// <param name="key">完整随机键；不读取或消耗任何随机流。</param>
    /// <returns>按明确位宽、小端序和 UTF-8 长度界定的不可变字节。</returns>
    /// <exception cref="ArgumentException">用途为空或分量数组未初始化。</exception>
    public static ImmutableArray<byte> Encode(RandomKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(key.Purpose);
        if (key.Components.IsDefault)
            throw new ArgumentException("随机键分量必须是已初始化的序列。", nameof(key));
        var purpose = Encoding.UTF8.GetBytes(key.Purpose);
        var marker = "TRNG"u8;
        // 标记、版本、种子和两个长度；每个分量为 UInt64。
        var headerSize = marker.Length + sizeof(uint) * 3 + sizeof(ulong);
        var bytes = new byte[checked(headerSize + purpose.Length + key.Components.Length * sizeof(ulong))];
        marker.CopyTo(bytes);
        var offset = marker.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), AlgorithmVersion);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), key.WorldSeed);
        offset += sizeof(ulong);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint)purpose.Length);
        offset += sizeof(uint);
        purpose.CopyTo(bytes.AsSpan(offset));
        offset += purpose.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), (uint)key.Components.Length);
        offset += sizeof(uint);
        foreach (var component in key.Components)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), component);
            offset += sizeof(ulong);
        }
        return ImmutableArray.CreateRange(bytes);
    }

    /// <summary>计算只由完整键决定的 64 位随机结果。</summary>
    /// <param name="key">用途和身份均已固定的完整随机键。</param>
    /// <returns>完整 ulong 范围内的确定结果；调用顺序不影响后续调用。</returns>
    /// <remarks>没有随机状态或外部副作用；先编码完整键，再组合全部字节。</remarks>
    /// <exception cref="ArgumentException">用途为空或分量数组未初始化。</exception>
    public static ulong Sample(RandomKey key) => CombineBytes(Encode(key));

    /// <summary>按算法 1 累计混合每个键字节，最后混合总字节数。</summary>
    private static ulong CombineBytes(ImmutableArray<byte> bytes)
    {
        var accumulated = 0UL;
        foreach (var value in bytes)
            accumulated = SplitMix64.Mix(accumulated ^ value);
        return SplitMix64.Mix(accumulated ^ (ulong)bytes.Length);
    }
}
