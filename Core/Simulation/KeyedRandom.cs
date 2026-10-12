
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text;

namespace Train.Core.Simulation;

public sealed record RandomKey(ulong WorldSeed, string Purpose, ImmutableArray<ulong> Components);

public static class KeyedRandom
{
    public const uint AlgorithmVersion = 1;

    public static ImmutableArray<byte> Encode(RandomKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(key.Purpose);
        if (key.Components.IsDefault)
            throw new ArgumentException("随机键分量必须是已初始化的序列。", nameof(key));
        var purpose = Encoding.UTF8.GetBytes(key.Purpose);
        var marker = "TRNG"u8;
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

    public static ulong Sample(RandomKey key) => CombineBytes(Encode(key));

    private static ulong CombineBytes(ImmutableArray<byte> bytes)
    {
        var accumulated = 0UL;
        foreach (var value in bytes)
            accumulated = SplitMix64.Mix(accumulated ^ value);
        return SplitMix64.Mix(accumulated ^ (ulong)bytes.Length);
    }
}
